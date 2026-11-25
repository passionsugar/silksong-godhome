using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnitySceneRepacker;

namespace Godhome;

/// <summary>
/// [Godhome] 前作（空洞骑士，装在本机）的神居场景：用 UnitySceneRepacker 把前作的场景文件重新打包成丝之歌能读的包
/// （缓存在 BepInEx\cache\unity-scene-repacker，第一次约 4 秒，之后直接用缓存），交给游戏自己的转场去加载
/// （SceneLoadInfo.SceneResourceLocation）。只读前作的游戏文件，不改。
/// 这个类的公开成员不带 UnitySceneRepacker 的类型：没装那个插件时插件照样能加载，用到它的只在 NoInlining 的方法里。
/// </summary>
internal static class Hk1Assets
{
    /// <summary>重新打包的名字（缓存里按它分开；试验用的另起名字，免得两份请求互相覆盖）。</summary>
    internal const string PackName = "godhome-hk1";

    /// <summary>要打包的前作场景：前厅、前厅屋顶（第五扇门）、手办屋、万神殿的休息点（温泉 GG_Spa）。</summary>
    internal static readonly string[] Scenes = { Hk1Lobby.Atrium, Hk1Lobby.Roof, Hk1Lobby.Workshop, Hk1Lobby.Spa };

    /// <summary>
    /// 前作连战转场特效（gg_battle_transitions）的“载体”：手办屋里复仇蝇雕像的回程门，它的“Return from boss” FSM 在 Transition Wait 里
    /// 生成这个特效（读前作场景的 FSM 导出核实）。单独打包这个物体（不实例化），从 FSM 的参数里取出特效的预制体。
    /// 不用等进手办屋：从前厅直接进万神殿也有前作的转场。
    /// 场景文件里回程门叫 door_dreamReturnGG（37 个，扫前作 level470 核实），雕像 Awake 时才接上雕像名（运行时的导出里是 door_dreamReturnGG_GG_Statue_Vengefly）。
    /// </summary>
    internal static readonly (string Scene, string Path) TransitionsHolder = (Hk1Lobby.Workshop, "GG_Statue_Vengefly/door_dreamReturnGG");

    private static object? assets;
    private static object? transitionsAsset;

    /// <summary>
    /// 带着载体打包失败过（插件找不到这个物体时整份包都失败）：已经不带它重新打包了。
    /// 转场特效只是锦上添花，不能连累前作神居本身（实测：路径写错时前厅也进不去，退回了织忆圣所）。
    /// </summary>
    private static bool holderDropped;
    private static readonly Dictionary<string, object> requested = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IResourceLocation> located = new(StringComparer.Ordinal);

    internal static string? GameDir { get; private set; }

    /// <summary>为什么用不了（日志用）。</summary>
    internal static string Failure { get; private set; } = "";

    internal static bool Failed { get; private set; }

    internal static bool Started => assets != null;

    /// <summary>开始在后台打包（已经开始、或者已经失败就什么都不做）。要在第一次用到之前（标题画面）调。</summary>
    internal static void StartPrepare()
    {
        if (assets != null || Failed)
        {
            return;
        }
        if (!Hk1Probe.Available)
        {
            Fail("没装 UnitySceneRepacker 插件 / the UnitySceneRepacker plugin is not installed");
            return;
        }
        // 前作的场景管理器没改绑到丝之歌的 CustomSceneManager，场景进去了游戏也找不到它（镜头、场景设置都坏）：当成用不了。
        if (!Hk1Gate.Remapped)
        {
            Fail("前作的场景管理器没改绑 / the hk1 scene manager was not remapped (see the earlier log)");
            return;
        }
        GameDir = Hk1Probe.FindGame();
        if (GameDir == null)
        {
            Fail("找不到前作（空洞骑士）的安装目录 / Hollow Knight install not found");
            return;
        }
        try
        {
            Begin(GameDir, PackName, withHolder: true);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：开始打包 / hk1 godhome: repacking {string.Join(", ", Scenes)} from {GameDir}");
        }
        catch (Exception e)
        {
            // 同步就出错：先试一次不带载体（转场特效）的。
            if (!DropHolder("打包出错 / repack failed to start: " + e.GetBaseException().Message))
            {
                Fail("打包出错 / repack failed to start: " + e.Message);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Begin(string dir, string name, bool withHolder)
    {
        RepackedAssets a = UnitySceneRepackerPlugin.RepackedAssets(dir, name);
        requested.Clear();
        located.Clear();
        foreach (string s in Scenes)
        {
            requested[s] = a.RequestScene(s);
        }
        transitionsAsset = withHolder ? a.Request(TransitionsHolder.Scene, TransitionsHolder.Path) : null;
        a.Prepare();
        assets = a;
    }

    /// <summary>
    /// 带着转场特效的载体打包失败：不带它重新打包（另起包名，失败的那份没加载过任何包，不会重复加载）。
    /// 已经丢过、没有游戏目录就返回 false（调用方按失败处理）。
    /// </summary>
    private static bool DropHolder(string why)
    {
        if (holderDropped || GameDir == null)
        {
            return false;
        }
        holderDropped = true;
        GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场特效的载体打包失败，不带它重新打包（前作神居照常用，转场用丝之歌的淡入淡出） / repacking without the hk1 battle transitions holder: {why}");
        try
        {
            Begin(GameDir, PackName + "-scenes", withHolder: false);
            return true;
        }
        catch (Exception e)
        {
            Fail("重新打包出错 / repack without the holder failed to start: " + e.GetBaseException().Message);
            return true;
        }
    }

    /// <summary>开始加载连战转场特效的载体（打包好以后）。用不了返回 null。句柄一直留着（放掉的话包会卸载，特效的图和声音跟着没了）。</summary>
    internal static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<UnityEngine.GameObject>? LoadTransitionsHolder()
    {
        if (Failed || transitionsAsset == null)
        {
            return null;
        }
        try
        {
            return LoadAsset(transitionsAsset);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场特效加载不了 / hk1 battle transitions failed to load: {e.GetBaseException().Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<UnityEngine.GameObject> LoadAsset(object ra) => ((RepackedAsset)ra).Load();

    /// <summary>这个场景是不是前作的、在这份包里。</summary>
    internal static bool Has(string scene) => requested.ContainsKey(scene);

    /// <summary>
    /// 场景的位置（第一次要等打包完成：后台已经在打，这里会阻塞到打完）。用不了返回 null（之后都返回 null）。
    /// </summary>
    internal static IResourceLocation? Locate(string scene)
    {
        if (located.TryGetValue(scene, out IResourceLocation? loc))
        {
            return loc;
        }
        if (Failed || assets == null || !requested.TryGetValue(scene, out object? rs))
        {
            return null;
        }
        try
        {
            loc = LocateScene(rs);
            located[scene] = loc;
            return loc;
        }
        catch (Exception e)
        {
            string why = $"找不到 {scene} 的位置 / locating {scene} failed: {e.GetBaseException().Message}";
            // 带着载体的那份包失败了（找不到那个物体时整份都失败）：不带它重新打包，再找一次。
            if (transitionsAsset != null && DropHolder(why) && !Failed && requested.TryGetValue(scene, out object? again))
            {
                try
                {
                    loc = LocateScene(again);
                    located[scene] = loc;
                    return loc;
                }
                catch (Exception e2)
                {
                    why = $"不带载体也找不到 {scene} 的位置 / locating {scene} failed without the holder too: {e2.GetBaseException().Message}";
                }
            }
            Fail(why);
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IResourceLocation LocateScene(object rs) => ((RepackedScene)rs).Locate();

    /// <summary>试验（Hk1Probe）共用这份包里的场景（同一批依赖包不能加载两次）。不在这份包里返回 null。</summary>
    internal static object? SharedScene(string scene) => requested.TryGetValue(scene, out object? rs) ? rs : null;

    /// <summary>运行中发现用不了（场景进不去）：之后都当成失败。</summary>
    internal static void MarkFailed(string why) => Fail(why);

    private static void Fail(string why)
    {
        Failed = true;
        Failure = why;
        GodhomePlugin.Log.LogWarning($"[Godhome] 前作神居用不了，用织忆圣所 / hk1 godhome unavailable, using the Loom sanctum: {why}");
    }
}
