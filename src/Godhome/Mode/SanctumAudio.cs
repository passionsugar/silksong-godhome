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
/// [Godhome] 圣所的音效：运行时从游戏自己的音效包里借（不带任何音频文件，也不改游戏文件）。
///
/// 拉杆用游戏骨柄拉杆（Lever 组件，ant_17、bone_east_11 等场景）的受击音效：
/// hitSound.Clip = Assets/Audio/SFX/Props/bone_switch.wav，音高 0.95–1.05，音量 1（UnityPy 读场景数据），
/// 在 sfxstatic_assets_areabone.bundle 里。和图集一样走 Addressables 的 ProvideResource（和游戏共用、带引用计数），
/// 待在大厅期间一直借着（音效在包里，包还掉音效就没了），离开大厅还掉。
/// 播放照游戏的 Lever.Hit：AudioEvent.SpawnAndPlayOneShot（游戏默认的音源预制体和混音组）。
/// </summary>
internal static class SanctumAudio
{
    private const string Group = "sfxstatic_assets_areabone";
    private const string LeverClipPath = "Assets/Audio/SFX/Props/bone_switch.wav";

    private static IResourceLocation? location;

    // 每次加载借的那一份引用一取到就登记在这里（加载途中被硬停也能还），ReleaseAll 全部还掉（Codex 审查 v0.6.3 #2 的做法）。
    // 每次加载只还自己那一份：过期、没取到时不能连别的搭建借的一起还（Codex 审查 v0.7.0 #5）。
    private sealed class Borrow
    {
        internal AsyncOperationHandle<IAssetBundleResource> Handle;
    }

    private static readonly List<Borrow> handles = new();
    private static int generation;

    internal static AudioClip? LeverClip { get; private set; }

    internal static bool Holding => handles.Count > 0;

    /// <summary>这次加载的说明（日志、巡视报告用）。</summary>
    internal static string Note { get; private set; } = "";

    /// <summary>拉杆音效播出去的次数（巡视核对用）。</summary>
    internal static int LeverPlays { get; private set; }

    /// <summary>借音效包、取出拉杆音效。已经有了就跳过。alive 返回 false（离开大厅、任务过期）就把这次借的还掉。</summary>
    internal static IEnumerator Load(Func<bool> alive)
    {
        if (LeverClip != null)
        {
            yield break;
        }
        int gen = generation;
        float t0 = Time.realtimeSinceStartup;
        location ??= GameAssetBundle.Find(Group);
        if (location == null)
        {
            Note = $"音效 / sfx: 找不到音效包 / bundle not in the catalog: {Group}";
            GodhomePlugin.Log.LogWarning($"[Godhome] {Note}");
            yield break;
        }
        AsyncOperationHandle<IAssetBundleResource> h;
        Borrow mine;
        try
        {
            h = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(location);
            mine = new Borrow { Handle = h };
            handles.Add(mine);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 音效包加载出错 / sfx bundle load failed to start: {e.Message}");
            yield break;
        }
        float until = Time.realtimeSinceStartup + 30f;
        while (h.IsValid() && !h.IsDone && Time.realtimeSinceStartup < until && gen == generation && alive())
        {
            yield return null;
        }
        if (gen != generation)
        {
            // 途中 ReleaseAll 过：这份引用已经跟着还了。
            yield break;
        }
        if (!alive())
        {
            ReleaseOne(mine, "过期 / stale");
            yield break;
        }
        if (LeverClip != null)
        {
            // 另一次加载先取到了：这一份用不着。
            ReleaseOne(mine, "已经有了 / already loaded");
            yield break;
        }
        AudioClip? clip = null;
        try
        {
            if (h.IsValid() && h.IsDone && h.Status == AsyncOperationStatus.Succeeded && h.Result?.GetAssetBundle() is AssetBundle b)
            {
                clip = b.LoadAsset<AudioClip>(LeverClipPath);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 读音效出错 / sfx read failed: {e.Message}");
        }
        if (clip == null)
        {
            Note = $"音效 / sfx: 包里没取到 / not found in the bundle: {LeverClipPath}";
            GodhomePlugin.Log.LogWarning($"[Godhome] {Note}");
            ReleaseOne(mine, "没取到 / missing");
            yield break;
        }
        LeverClip = clip;
        Note = $"音效 / sfx: {clip.name} {clip.length:0.00}s（{Group}），{(Time.realtimeSinceStartup - t0):0.00}s";
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}");
    }

    /// <summary>在 pos 播一次拉杆的受击音效（和游戏 Lever.Hit 一样的音高、音量）。没借到就不响。</summary>
    internal static bool PlayLever(Vector3 pos)
    {
        if (LeverClip == null)
        {
            return false;
        }
        try
        {
            var ev = new AudioEvent { Clip = LeverClip, PitchMin = 0.95f, PitchMax = 1.05f, Volume = 1f };
            if (ev.SpawnAndPlayOneShot(pos) != null)
            {
                LeverPlays++;
                return true;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 拉杆音效播放出错 / lever sound failed: {e.Message}");
        }
        return false;
    }

    /// <summary>还掉借的音效包（离开大厅、回标题；用到音效的物体已经跟着大厅销毁）。</summary>
    internal static void ReleaseAll(string why)
    {
        generation++;
        LeverClip = null;
        if (handles.Count == 0)
        {
            return;
        }
        int n = handles.Count;
        foreach (Borrow b in handles)
        {
            SafeRelease(b.Handle);
        }
        handles.Clear();
        GodhomePlugin.Log.LogInfo($"[Godhome] 音效包已还 / sfx bundle released ({n}, {why})");
    }

    /// <summary>只还这一次加载借的那一份（别的搭建借的不动）。</summary>
    private static void ReleaseOne(Borrow b, string why)
    {
        if (!handles.Remove(b))
        {
            return;
        }
        SafeRelease(b.Handle);
        GodhomePlugin.Log.LogInfo($"[Godhome] 音效包这一份已还 / sfx bundle reference released ({why})");
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
                // 还在加载：等它加载完再还这一份引用。
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
                        GodhomePlugin.Log.LogWarning($"[Godhome] 还音效包出错 / sfx bundle release failed: {e.Message}");
                    }
                };
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 还音效包出错 / sfx bundle release failed: {e.Message}");
        }
    }

}

internal static class GameAssetBundle
{
    // Catalogs may use either the bundle filename or a filename with its content hash.
    internal static IResourceLocation? Find(string group)
    {
        var bundleKey = new Regex("^" + Regex.Escape(group) + @"(_[0-9a-f]{32})?\.bundle$", RegexOptions.IgnoreCase);
        try
        {
            foreach (IResourceLocator locator in Addressables.ResourceLocators)
            {
                foreach (object key in locator.Keys)
                {
                    if (key is not string k || k.IndexOf(group, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    string leaf = k.Substring(Math.Max(k.LastIndexOf('/'), k.LastIndexOf('\\')) + 1);
                    if (!bundleKey.IsMatch(leaf))
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
                        return bl;
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] Bundle lookup failed for {group}: {e.Message}");
        }
        return null;
    }
}
