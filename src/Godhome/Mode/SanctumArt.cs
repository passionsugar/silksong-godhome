using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 圣所的美术：运行时从游戏自己的图集包里借精灵（StreamingAssets/aa/.../sprites/_atlases/*.spriteatlas.bundle）。
/// 不带任何美术文件，也不改游戏文件。
///
/// 加载走游戏的 Addressables：在目录里找到图集包的位置（IResourceLocation），用 ResourceManager.ProvideResource 取。
/// 图集包的加载缓存只按包路径算（AssetBundleProvider.CreateCacheKeyForLocation → IdCacheKey，见反编译的
/// Unity.ResourceManager），和游戏加载场景依赖时是同一份、带引用计数，所以不会和游戏抢同一个包
/// （自己 AssetBundle.LoadFromFile 会让游戏随后加载同一个包失败）。
/// 离开大厅以后释放（大厅场景已经卸载、精灵不再被引用）。
/// </summary>
internal static class SanctumArt
{
    private sealed class Atlas
    {
        public AsyncOperationHandle<IAssetBundleResource> Handle;
        public SpriteAtlas[] Atlases = Array.Empty<SpriteAtlas>();
        public readonly Dictionary<string, Sprite?> Sprites = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Atlas> loaded = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, IResourceLocation>? locations;
    private static readonly HashSet<string> warned = new();

    /// <summary>这次加载的说明（日志、巡视报告用）。</summary>
    internal static string Note { get; private set; } = "";

    /// <summary>找不到的精灵个数（巡视报告用）。</summary>
    internal static int MissingSprites { get; private set; }

    internal static int LoadedCount => loaded.Count;

    /// <summary>还借着图集（已加载的，或者还在加载途中的）。</summary>
    internal static bool Holding => loaded.Count > 0 || inflight > 0;

    // 每次 ReleaseAll 换一代（Codex 审查 v0.6.0 #2）：加载途中被放掉的那一代，取回来的句柄由加载它的协程自己放掉，
    // 不写进缓存；每个 ProvideResource 拿到的引用只放一次。
    private static int generation;
    private static int inflight;

    internal static bool Has(string atlas) => loaded.TryGetValue(atlas, out Atlas? a) && a.Atlases.Length > 0;

    /// <summary>
    /// 加载这些图集（已经加载的跳过）。等全部加载完（最多 30 秒）以后，alive 返回 false（离开大厅、任务过期）
    /// 或者期间已经 ReleaseAll 过，就把这次取到的句柄全部放掉，什么都不留下。
    /// </summary>
    internal static IEnumerator Load(IEnumerable<string> atlases, Func<bool> alive)
    {
        int gen = generation;
        float t0 = Time.realtimeSinceStartup;
        IndexLocations();
        var pending = new List<(string Name, AsyncOperationHandle<IAssetBundleResource> H)>();
        var missing = new List<string>();
        foreach (string name in atlases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Has(name))
            {
                continue;
            }
            if (locations == null || !locations.TryGetValue(name, out IResourceLocation? loc))
            {
                missing.Add(name);
                continue;
            }
            try
            {
                pending.Add((name, Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(loc)));
                inflight++;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 图集加载出错 / atlas load failed to start {name}: {e.Message}");
            }
        }
        float until = Time.realtimeSinceStartup + 30f;
        while (pending.Any(p => p.H.IsValid() && !p.H.IsDone) && Time.realtimeSinceStartup < until)
        {
            yield return null;
        }
        inflight -= pending.Count;
        if (gen != generation || !alive())
        {
            foreach ((_, AsyncOperationHandle<IAssetBundleResource> h) in pending)
            {
                SafeRelease(h);
            }
            GodhomePlugin.Log.LogInfo($"[Godhome] 图集加载已过期，放掉这次取到的 {pending.Count} 个 / atlas load went stale, released its {pending.Count} handle(s)");
            yield break;
        }
        int ok = 0;
        foreach ((string name, AsyncOperationHandle<IAssetBundleResource> h) in pending)
        {
            if (loaded.ContainsKey(name))
            {
                // 同一代里别的加载先放进去了：这一份多出来的引用放掉。
                SafeRelease(h);
                continue;
            }
            var a = new Atlas { Handle = h };
            try
            {
                if (h.IsValid() && h.IsDone && h.Status == AsyncOperationStatus.Succeeded && h.Result?.GetAssetBundle() is AssetBundle b)
                {
                    a.Atlases = b.LoadAllAssets<SpriteAtlas>();
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 图集读取出错 / atlas read failed {name}: {e.Message}");
            }
            if (a.Atlases.Length == 0)
            {
                missing.Add(name);
                SafeRelease(h);
                continue;
            }
            loaded[name] = a;
            ok++;
        }
        Note = $"图集 / atlases: 新加载 / loaded {ok}，共 / total {loaded.Count}，{(Time.realtimeSinceStartup - t0):0.00}s" + (missing.Count > 0 ? $"；找不到 / missing: {string.Join(", ", missing)}" : "");
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
    }

    private static void SafeRelease(AsyncOperationHandle<IAssetBundleResource> h)
    {
        try
        {
            if (h.IsValid())
            {
                Addressables.ResourceManager.Release(h);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 释放图集出错 / atlas release failed: {e.Message}");
        }
    }

    /// <summary>取一个精灵（图集名是文件名去掉 .spriteatlas.bundle，精灵名是原名）。找不到返回 null，日志只记一次。</summary>
    internal static Sprite? Get(string atlas, string name)
    {
        if (!loaded.TryGetValue(atlas, out Atlas? a))
        {
            Warn($"{atlas}/*", "图集没加载 / atlas not loaded");
            return null;
        }
        // 缓存里记着“找不到”（null）就直接返回，不重复查、不重复计数（Codex 审查 v0.6.0 #17）；销毁了的克隆重新取。
        if (a.Sprites.TryGetValue(name, out Sprite? s) && (s is null || s != null))
        {
            return s;
        }
        foreach (SpriteAtlas sa in a.Atlases)
        {
            if (sa == null)
            {
                continue;
            }
            // GetSprite 返回一份克隆（名字带 (Clone)），释放时销毁。
            s = sa.GetSprite(name);
            if (s != null)
            {
                s.name = name;
                a.Sprites[name] = s;
                return s;
            }
        }
        a.Sprites[name] = null;
        MissingSprites++;
        Warn($"{atlas}/{name}", "找不到精灵 / sprite not found");
        return null;
    }

    private static void Warn(string key, string what)
    {
        if (warned.Add(key))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 圣所美术 / sanctum art: {what}: {key}");
        }
    }

    /// <summary>释放全部图集（离开大厅、回标题以后调用：用到这些精灵的物体已经跟着大厅场景销毁）。</summary>
    internal static void ReleaseAll(string why)
    {
        // 还在加载途中的那些归它们自己的协程放（见 Load）：这里换一代，它们回来后就知道作废了。
        generation++;
        if (loaded.Count == 0)
        {
            return;
        }
        int n = loaded.Count;
        foreach (Atlas a in loaded.Values)
        {
            foreach (Sprite? s in a.Sprites.Values)
            {
                if (s != null)
                {
                    Object.Destroy(s);
                }
            }
            SafeRelease(a.Handle);
        }
        loaded.Clear();
        MissingSprites = 0;
        GodhomePlugin.Log.LogInfo($"[Godhome] 圣所图集已释放 / sanctum atlases released ({n}, {why}{(inflight > 0 ? $"，还有 {inflight} 个在加载途中，回来后自己放 / {inflight} still loading, released on arrival" : "")})");
    }

    /// <summary>
    /// 在 Addressables 目录里找每个图集包的位置。键是包名（例如 weave_gate.spriteatlas_&lt;hash&gt;.bundle），
    /// 用“.spriteatlas”前面那段（到路径分隔符为止）当图集名。只做一次。
    /// 先建在局部字典里，整个扫完才发布（Codex 审计 0.10.3：以前中途出错时半张表留在静态字典里，Count > 0 以后再也不重扫）；
    /// 出错就不发布，下次再扫。
    /// </summary>
    private static void IndexLocations()
    {
        if (locations != null && locations.Count > 0)
        {
            return;
        }
        var found = new Dictionary<string, IResourceLocation>(StringComparer.OrdinalIgnoreCase);
        int keys = 0;
        try
        {
            foreach (IResourceLocator locator in Addressables.ResourceLocators)
            {
                foreach (object key in locator.Keys)
                {
                    if (key is not string k)
                    {
                        continue;
                    }
                    keys++;
                    int i = k.IndexOf(".spriteatlas", StringComparison.OrdinalIgnoreCase);
                    if (i <= 0)
                    {
                        continue;
                    }
                    int start = Math.Max(k.LastIndexOf('/', i - 1), k.LastIndexOf('\\', i - 1)) + 1;
                    string name = k.Substring(start, i - start);
                    if (name.Length == 0 || found.ContainsKey(name))
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
                        found[name] = bl;
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 查图集目录出错，这次不用，下次重扫 / atlas catalog scan failed; not using it, will rescan next time（已找到 / found {found.Count}）: {e.Message}");
            return;
        }
        locations = found;
        GodhomePlugin.Log.LogInfo($"[Godhome] 图集目录 / atlas catalog: {locations.Count} 个图集包 / atlas bundles（扫了 {keys} 个键 / keys）");
    }
}
