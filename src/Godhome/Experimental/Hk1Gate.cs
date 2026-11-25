using System;
using System.Runtime.CompilerServices;
using UnitySceneRepacker;

namespace Godhome;

/// <summary>
/// [Godhome] 试验：前作场景的开关。这个类本身不用 UnitySceneRepacker 的类型（没装插件也能加载），
/// 用到它的只在 <see cref="Remap"/> 里，而且只在插件装了、开了“原生转场”试验时才调用。
/// </summary>
internal static class Hk1Gate
{
    /// <summary>正在试验原生转场的前作场景：神居模式的改道放行这一个（其余照常改回大厅）。</summary>
    internal static string? NativeScene;

    /// <summary>原生转场要把前作的场景管理器（类名 SceneManager）改绑到丝之歌的 CustomSceneManager，游戏进场时要找它。</summary>
    internal static bool WantsNative
    {
        get
        {
            string mode = GodhomeConfig.DebugHk1ProbeMode.Value?.Trim().ToLowerInvariant() ?? "";
            return !string.IsNullOrWhiteSpace(GodhomeConfig.DebugHk1Probe.Value) && (mode == "native" || mode == "both");
        }
    }

    /// <summary>
    /// 插件加载时（第一次打包之前）：前作神居开着、或者在试原生转场，就把前作的场景管理器改绑过去
    /// （改绑要在第一次打包前做，打好的包里记的是改绑后的类）。
    /// </summary>
    internal static void EarlyInit()
    {
        try
        {
            if ((GodhomeConfig.Hk1Enabled.Value || WantsNative) && Hk1Probe.Available)
            {
                Remap();
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作脚本改绑失败 / hk1 script remap failed: {e.Message}");
        }
    }

    /// <summary>前作的场景管理器改绑成功了（前作神居要它，见 Hk1Assets.StartPrepare）。</summary>
    internal static bool Remapped { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Remap()
    {
        UnitySceneRepackerPlugin.RemapScript("Assembly-CSharp", "SceneManager", "Assembly-CSharp", "CustomSceneManager");
        Remapped = true;
        GodhomePlugin.Log.LogInfo("[Godhome] 前作场景：SceneManager 改绑到 CustomSceneManager / hk1 scenes: remapped SceneManager → CustomSceneManager");
    }
}
