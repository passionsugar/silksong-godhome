using HarmonyLib;

namespace Godhome;

/// <summary>调试：自检跑着时记下每次 GameManager.FadeSceneIn（等场景好了才淡入的也记），查万神殿打完回门前一直黑。</summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.FadeSceneIn))]
internal static class FadeSceneInTrace
{
    private static void Prefix(GameManager __instance)
    {
        if (DebugTools.Instance == null || !DebugTools.Instance.SelfTestRunning)
        {
            return;
        }
        string from = new System.Diagnostics.StackTrace(2, false).GetFrame(0)?.GetMethod() is System.Reflection.MethodBase m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?";
        GodhomePlugin.Log.LogInfo($"[Godhome]   淡入 / FadeSceneIn（{UnityEngine.Time.unscaledTime:0.00}s，{__instance.GetSceneNameString()}，waitingForScene={GameManager.IsWaitingForSceneReady}，fader={__instance.screenFader_fsm?.ActiveStateName}，from {from}）");
    }
}