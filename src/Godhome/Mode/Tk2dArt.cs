using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace Godhome;

/// <summary>
/// [Godhome] 借游戏的 tk2d 动作帧（Boss 的各种姿势），给同一个 Boss 的不同形态雕不一样的石像。
///
/// 动作帧在 StreamingAssets/aa/.../tk2dcollections_assets_&lt;组&gt;.bundle 里：每个精灵集合是一个预制体
/// （例如 Assets/Collections/Hornet Enemies/Lace Cln Data/Lace Cln.prefab，上面挂 tk2dSpriteCollectionData，
/// 图集贴图 atlas0.png 也在同一个包里）。和 SanctumArt 一样走游戏的 Addressables（ProvideResource，和游戏共用、带引用计数），
/// 不自己开包、不改游戏文件、不带任何美术。只在黑幕下雕石像时借，雕完马上还（StatueCarver 雕出来的是我们自己的贴图）。
/// 字段见反编译的 TeamCherry.TK2D（reference/TeamCherry.TK2D/tk2dSpriteCollectionData.cs、tk2dSpriteDefinition）。
///
/// 每次搭建借自己的一份（Lease，Codex 审查 v0.6.3 #1/#2）：
/// - 引用计数归零时包按 UnloadAsync(true) 卸载、从包里取出的东西跟着销毁，所以谁借的谁还，
///   过期的搭建任务只还自己那份，不会把新任务正在雕的包卸掉；
/// - 句柄一拿到就登记在借用里，协程中途没了也能由 ReleaseAll（离开大厅、回标题）还掉；还在加载的等加载完再还。
/// </summary>
internal static class Tk2dArt
{
    /// <summary>一次搭建借的动作帧包。用完调 Release（可以重复调）。</summary>
    internal sealed class Lease
    {
        internal readonly int Id;
        internal readonly Dictionary<string, Group> Groups = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<AsyncOperationHandle<IAssetBundleResource>> Handles = new();
        internal bool Released;

        internal Lease(int id) => Id = id;
    }

    internal sealed class Group
    {
        public AssetBundle Bundle = null!;
        public string[] Names = Array.Empty<string>();
        public readonly Dictionary<string, tk2dSpriteCollectionData?> Collections = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly HashSet<Lease> active = new();
    private static Dictionary<string, IResourceLocation>? locations;
    private static readonly HashSet<string> warned = new();
    private static readonly Regex BundleKey = new(@"^tk2dcollections_assets_(.+?)(_[0-9a-f]{32})?\.bundle$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static int nextId;

    /// <summary>还有借着没还的（包括还在加载途中的）。</summary>
    internal static bool Holding => active.Count > 0;

    internal static string Note { get; private set; } = "";

    internal static Lease Begin()
    {
        var l = new Lease(++nextId);
        active.Add(l);
        return l;
    }

    /// <summary>借这些组的动作帧包（这次借用里已经有的跳过）。alive 返回 false 或这次借用已经还了，就把取到的句柄全部还掉。</summary>
    internal static IEnumerator Load(Lease lease, IEnumerable<string> groups, Func<bool> alive)
    {
        if (lease.Released)
        {
            yield break;
        }
        float t0 = Time.realtimeSinceStartup;
        IndexLocations();
        var pending = new List<(string Name, AsyncOperationHandle<IAssetBundleResource> H)>();
        var missing = new List<string>();
        foreach (string g in groups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (lease.Groups.ContainsKey(g))
            {
                continue;
            }
            if (locations == null || !locations.TryGetValue(g, out IResourceLocation? loc))
            {
                missing.Add(g);
                continue;
            }
            try
            {
                AsyncOperationHandle<IAssetBundleResource> h = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(loc);
                // 一拿到就登记：协程停在半路也能由 Release / ReleaseAll 还掉。
                lease.Handles.Add(h);
                pending.Add((g, h));
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 动作帧包加载出错 / tk2d bundle load failed to start {g}: {e.Message}");
            }
        }
        if (pending.Count == 0 && missing.Count == 0)
        {
            yield break;
        }
        float until = Time.realtimeSinceStartup + 30f;
        while (!lease.Released && alive() && pending.Any(p => p.H.IsValid() && !p.H.IsDone) && Time.realtimeSinceStartup < until)
        {
            yield return null;
        }
        if (lease.Released)
        {
            yield break;
        }
        if (!alive())
        {
            Release(lease, "过期 / stale");
            yield break;
        }
        int ok = 0;
        foreach ((string name, AsyncOperationHandle<IAssetBundleResource> h) in pending)
        {
            AssetBundle? bundle = null;
            string[]? names = null;
            try
            {
                if (h.IsValid() && h.IsDone && h.Status == AsyncOperationStatus.Succeeded && h.Result?.GetAssetBundle() is AssetBundle b)
                {
                    // 名单读成功了才算借到（Codex 审查 v0.6.3 低 #2）。
                    names = b.GetAllAssetNames();
                    bundle = b;
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 动作帧包读取出错 / tk2d bundle read failed {name}: {e.Message}");
            }
            if (bundle == null || names == null)
            {
                missing.Add(name);
                continue;
            }
            lease.Groups[name] = new Group { Bundle = bundle, Names = names };
            ok++;
        }
        Note = $"动作帧 / tk2d: 借到 / loaded {ok}，{(Time.realtimeSinceStartup - t0):0.00}s" + (missing.Count > 0 ? $"；找不到 / missing: {string.Join(", ", missing)}" : "");
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
    }

    /// <summary>
    /// 取一帧要雕的东西：组（包名里的那段，例如 laceboss）、集合名（例如 Lace Cln）、帧名（例如 lace_engarde0000）。
    /// 找不到返回 null（日志只记一次）。返回的贴图属于借来的包，只能在这次借用还掉之前用。
    /// </summary>
    internal static StatueCarver.Source? Get(Lease? lease, string group, string collection, string sprite, string key)
    {
        if (lease == null || lease.Released || !lease.Groups.TryGetValue(group, out Group? g) || g.Bundle == null)
        {
            return null;
        }
        if (!g.Collections.TryGetValue(collection, out tk2dSpriteCollectionData? coll))
        {
            coll = null;
            string suffix = "/" + collection.ToLowerInvariant() + ".prefab";
            string? path = g.Names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            try
            {
                GameObject? go = path != null ? g.Bundle.LoadAsset<GameObject>(path) : null;
                coll = go != null ? go.GetComponent<tk2dSpriteCollectionData>() : null;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 读精灵集合出错 / tk2d collection load failed {group}/{collection}: {e.Message}");
            }
            g.Collections[collection] = coll;
            if (coll == null)
            {
                Warn($"{group}/{collection}", "找不到精灵集合 / tk2d collection not found");
            }
        }
        if (coll == null || coll.spriteDefinitions == null)
        {
            return null;
        }
        // 直接按名字找，不走 inst / GetSpriteIdByName（那会初始化平台数据、建字典）。
        tk2dSpriteDefinition? def = coll.spriteDefinitions.FirstOrDefault(d => d != null && d.name == sprite);
        if (def == null || def.positions == null || def.uvs == null || def.indices == null || def.positions.Length == 0)
        {
            Warn($"{group}/{collection}/{sprite}", "找不到动作帧 / tk2d sprite not found");
            return null;
        }
        Texture? tex = null;
        int mid = def.materialId;
        if (coll.textures != null && mid >= 0 && mid < coll.textures.Length)
        {
            tex = coll.textures[mid];
        }
        if (tex == null && coll.materials != null && mid >= 0 && mid < coll.materials.Length && coll.materials[mid] != null)
        {
            tex = coll.materials[mid].mainTexture;
        }
        if (tex == null && def.material != null)
        {
            tex = def.material.mainTexture;
        }
        if (tex == null)
        {
            Warn($"{group}/{collection}/{sprite}#tex", "动作帧没有贴图 / tk2d sprite has no texture");
            return null;
        }
        var verts = new Vector2[def.positions.Length];
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = def.positions[i];
            min = Vector2.Min(min, verts[i]);
            max = Vector2.Max(max, verts[i]);
        }
        var b = new Bounds((min + max) / 2f, max - min);
        return new StatueCarver.Source(key, sprite, tex, verts, def.uvs, def.indices, b, coll.premultipliedAlpha);
    }

    private static void Warn(string key, string what)
    {
        if (warned.Add(key))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 动作帧 / tk2d art: {what}: {key}");
        }
    }

    /// <summary>还掉一次借用（可以重复调）。还在加载的句柄等加载完再还。</summary>
    internal static void Release(Lease? lease, string why)
    {
        if (lease == null || lease.Released)
        {
            return;
        }
        lease.Released = true;
        active.Remove(lease);
        int n = lease.Handles.Count;
        foreach (AsyncOperationHandle<IAssetBundleResource> h in lease.Handles)
        {
            SafeRelease(h);
        }
        lease.Handles.Clear();
        lease.Groups.Clear();
        if (n > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 动作帧包已还 / tk2d bundles released ({n}, {why})");
        }
    }

    /// <summary>还掉所有借用（离开大厅、回标题）。</summary>
    internal static void ReleaseAll(string why)
    {
        foreach (Lease l in active.ToList())
        {
            Release(l, why);
        }
    }

    private static void SafeRelease(AsyncOperationHandle<IAssetBundleResource> h)
    {
        try
        {
            if (!h.IsValid())
            {
                return;
            }
            if (h.IsDone)
            {
                Addressables.ResourceManager.Release(h);
            }
            else
            {
                // 还在加载：等它加载完再还这一份引用（不在半路释放进行中的操作）。
                h.Completed += op =>
                {
                    try
                    {
                        if (op.IsValid())
                        {
                            Addressables.ResourceManager.Release(op);
                        }
                    }
                    catch (Exception e)
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 释放动作帧包出错 / tk2d bundle release failed: {e.Message}");
                    }
                };
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 释放动作帧包出错 / tk2d bundle release failed: {e.Message}");
        }
    }

    /// <summary>在 Addressables 目录里找每个 tk2dcollections_assets_&lt;组&gt; 包的位置。整个扫完才算数（中途出错下次重扫）。</summary>
    private static void IndexLocations()
    {
        if (locations != null)
        {
            return;
        }
        var found = new Dictionary<string, IResourceLocation>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (IResourceLocator locator in Addressables.ResourceLocators)
            {
                foreach (object key in locator.Keys)
                {
                    if (key is not string k || k.IndexOf("tk2dcollections_assets_", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    string leaf = k.Substring(Math.Max(k.LastIndexOf('/'), k.LastIndexOf('\\')) + 1);
                    Match m = BundleKey.Match(leaf);
                    if (!m.Success || found.ContainsKey(m.Groups[1].Value))
                    {
                        continue;
                    }
                    if (!locator.Locate(k, typeof(IAssetBundleResource), out IList<IResourceLocation> locs) || locs == null)
                    {
                        continue;
                    }
                    IResourceLocation? bl = locs.FirstOrDefault(l => l != null && typeof(IAssetBundleResource).IsAssignableFrom(l.ResourceType));
                    if (bl != null)
                    {
                        found[m.Groups[1].Value] = bl;
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 查动作帧包目录出错 / tk2d catalog scan failed: {e.Message}");
            return;
        }
        if (found.Count > 0)
        {
            locations = found;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 动作帧包目录 / tk2d catalog: {found.Count} 个组 / groups");
    }
}
