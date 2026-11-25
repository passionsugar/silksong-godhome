using System.Collections.Generic;
using HarmonyLib;

namespace Godhome;

/// <summary>
/// [Godhome] 模式里死亡走游戏自带的“非致命死亡”分支（忆境场景就是这么处理的）：
/// 不留茧、不掉念珠、丝轴不碎，在大厅醒来。见 HeroController.Die。
/// Die 是迭代器方法，前缀在状态机创建之前运行，改参数有效。
/// </summary>
[HarmonyPatch(typeof(HeroController), "Die", new[] { typeof(bool), typeof(bool) })]
internal static class HeroDiePatch
{
    private static void Prefix(HeroController __instance, ref bool nonLethal)
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null)
        {
            return;
        }
        // 已经在死亡里（Die 第一段就设 cState.dead，第二次调用自己会直接返回，反编译核实）：不再处理，
        // 免得第二次把回雕像、门前的落点清成 null（Codex 审查）。
        if (__instance != null && __instance.cState.dead)
        {
            if (mc.InMode)
            {
                nonLethal = true;
            }
            return;
        }
        if (mc.OnHeroDying())
        {
            nonLethal = true;
        }
    }
}

/// <summary>模式期间所有场景切换都过一遍：大厅出口送回大厅（圣所没有出口）；战斗中的其他切换 = 回大厅。</summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.BeginSceneTransition))]
internal static class BeginSceneTransitionPatch
{
    // 不在这里清 SuppressLevelState（Codex 复审）：回大厅时它从还原快照一直保持到进入大厅，
    // 由 ModeController 在到达时清掉；回标题等重置走 SaveGuard.Clear()。这样转场途中的存档、嵌套切换、
    // 抛异常都不会提前放开。
    private static void Prefix(GameManager __instance, GameManager.SceneLoadInfo info)
    {
        ModeController.Instance?.FilterTransition(__instance, info);
        // 去前作神居的房间：补上重新打包好的场景位置（改道后、或者前作自己的门发起的）。
        Hk1Lobby.FillLocation(info);
    }
}

/// <summary>
/// 会被改道的场景不预加载（ModeController.WouldRedirect）：没激活的预加载会挡住改道后那个场景的加载，卡在加载里。
/// ScenePreloader.SpawnPreloader（死亡时、走近出口时）和场景里的预加载组件都走 StartPreload。
/// </summary>
[HarmonyPatch(typeof(ScenePreloader), "StartPreload")]
internal static class ScenePreloaderPatch
{
    private static readonly AccessTools.FieldRef<ScenePreloader, string> SceneName =
        AccessTools.FieldRefAccess<ScenePreloader, string>("sceneNameToLoad");

    private static bool Prefix(ScenePreloader __instance)
    {
        ModeController? mc = ModeController.Instance;
        string scene = SceneName(__instance);
        if (mc == null || string.IsNullOrEmpty(scene) || !mc.WouldRedirect(scene))
        {
            return true;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 不预加载 / skipping the preload of {scene}（会被改道 / would be redirected, state {mc.State}）");
        return false;
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.FinishedEnteringScene))]
internal static class FinishedEnteringScenePatch
{
    private static void Postfix() => ModeController.Instance?.OnSceneEntered();
}

/// <summary>
/// 模板 FSM 第一次激活时 Awake 按模板新建一份 Fsm（PlayMakerFSM.InitTemplate），加载时打的补丁、插的结束动作会丢；
/// 醒来后让场地重打一次（Arena.OnFsmAwake 只认这一场加载时改过的模板 FSM，别的直接返回）。
/// </summary>
[HarmonyPatch(typeof(PlayMakerFSM), "Awake")]
internal static class FsmAwakePatch
{
    private static void Postfix(PlayMakerFSM __instance)
    {
        Arena.OnFsmAwake(__instance);
        Hk1Lobby.OnFsmAwake(__instance);
    }
}

/// <summary>
/// 附加场景：战斗时强制加载当前 Boss 的场景，不管原来的剧情条件；表里写了不加载的（arena.noAdditive）拦下。
/// TryTestLoad 返回 true 才加载（OnEnable / Start 里判，反编译 SceneAdditiveLoadConditional.cs）；
/// 别的加载器查 otherLoaderBlacklist 时也调它，拦下的就当没在加载（沙克拉的 Greymoor_08：只有她自己的加载器把
/// Boss Scene Loader 列进黑名单，而它是强制加载的，拦下不影响别的加载器）。
/// 真正附加加载了这一场以外的场景在 ModeController.OnUnitySceneLoaded 里记（这里返回 true 不等于加载了：别人探测时也调它）。
/// </summary>
[HarmonyPatch(typeof(SceneAdditiveLoadConditional), "TryTestLoad")]
internal static class AdditiveLoadPatch
{
    private static bool Prefix(SceneAdditiveLoadConditional __instance, ref bool __result, string ___sceneNameToLoad, string ___altSceneNameToLoad, ref bool ___loadAlt)
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null)
        {
            return true;
        }
        if (mc.ShouldForceAdditive(___sceneNameToLoad))
        {
            ___loadAlt = false;
            __result = true;
            if (AdditiveLoadLog.First(mc, "force", __instance))
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 强制加载附加场景 / force additive load: {___sceneNameToLoad}");
            }
            return false;
        }
        if (mc.ShouldBlockAdditive(___sceneNameToLoad) || (!string.IsNullOrEmpty(___altSceneNameToLoad) && mc.ShouldBlockAdditive(___altSceneNameToLoad)))
        {
            __result = false;
            if (AdditiveLoadLog.First(mc, "block", __instance))
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 这一场不加载附加场景 / additive load blocked for this arena: {___sceneNameToLoad}{(string.IsNullOrEmpty(___altSceneNameToLoad) ? "" : " / " + ___altSceneNameToLoad)}");
            }
            return false;
        }
        return true;
    }

}

/// <summary>AdditiveLoadPatch 的日志去重：同一场里每个加载器只记一次（别的加载器探测时会反复调到，Codex 审查 0.9.0 第二轮 #11）。</summary>
internal static class AdditiveLoadLog
{
    private static int loggedSerial = -1;
    private static readonly HashSet<string> logged = new();

    internal static bool First(ModeController mc, string kind, SceneAdditiveLoadConditional loader)
    {
        if (loggedSerial != mc.FightSerial)
        {
            loggedSerial = mc.FightSerial;
            logged.Clear();
        }
        return logged.Add(kind + ":" + (loader != null ? loader.GetInstanceID() : 0));
    }
}

// 神居存档里不发成就（成就在玩家的 Steam 账号上，满配打 Boss、解锁全部工具不该替玩家解锁）。
[HarmonyPatch(typeof(AchievementHandler), nameof(AchievementHandler.AwardAchievementToPlayer))]
internal static class AwardAchievementPatch
{
    private static bool Prefix(string key) => AchievementGate.Allow(key);
}

[HarmonyPatch(typeof(AchievementHandler), nameof(AchievementHandler.QueueAchievement))]
internal static class QueueAchievementPatch
{
    private static bool Prefix(string key) => AchievementGate.Allow(key);
}

[HarmonyPatch(typeof(AchievementHandler), nameof(AchievementHandler.UpdateAchievementProgress))]
internal static class UpdateAchievementProgressPatch
{
    private static bool Prefix(string key) => AchievementGate.Allow(key);
}

[HarmonyPatch(typeof(AchievementHandler), nameof(AchievementHandler.QueueAchievementProgress))]
internal static class QueueAchievementProgressPatch
{
    private static bool Prefix(string key) => AchievementGate.Allow(key);
}

/// <summary>
/// 发放排队的成就：神居存档里整个跳过（Codex 复审）。原函数逐个调 AwardAchievementToPlayer 后清空队列；
/// 神居存档里那一步会被拦下，队列却照样清空，之前排队的正当成就（普通存档里排的）就丢了。
/// 神居存档里新的排队本来就被拦，所以回到普通存档后队列里只有那边的，下次发放时照常发。
/// </summary>
[HarmonyPatch(typeof(AchievementHandler), nameof(AchievementHandler.AwardQueuedAchievements))]
internal static class AwardQueuedAchievementsPatch
{
    private static bool Prefix(AchievementHandler __instance)
    {
        if (AchievementGate.Blocking)
        {
            if (__instance.QueuedAchievements.Count > 0)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 神居存档里不发排队的成就 / queued achievements not awarded in a godhome save: {string.Join(", ", __instance.QueuedAchievements)}");
            }
            return false;
        }
        return true;
    }
}

internal static class AchievementGate
{
    private static readonly System.Collections.Generic.HashSet<string> logged = new();

    /// <summary>当前是神居存档（或者模式还开着）。</summary>
    internal static bool Blocking =>
        GodhomeSave.IsGodhome(GameManager.UnsafeInstance) || (ModeController.Instance != null && ModeController.Instance.InMode);

    internal static bool Allow(string key)
    {
        if (Blocking)
        {
            // 同一个成就只记一次日志（补满配时会一次排进很多个）。
            if (logged.Add(key ?? ""))
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 神居存档里不发成就 / achievement suppressed in a godhome save: {key}");
            }
            return false;
        }
        return true;
    }
}

/// <summary>
/// 前作留下的神居结局：神居存档里播结局动画后，游戏会去加载 GG_End_Sequence（CinematicPlayer.cs:739），
/// 丝之歌里没有这个场景。改成普通的字幕场景，免得卡死。
/// </summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.LoadScene))]
internal static class LoadSceneGuardPatch
{
    private static void Prefix(ref string destScene)
    {
        if (destScene == "GG_End_Sequence")
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 丝之歌里没有前作的神居结局场景，改去普通字幕 / GG_End_Sequence does not exist in Silksong, loading End_Credits instead");
            destScene = "End_Credits";
        }
    }
}

/// <summary>
/// 进战场、休息点不弹区域名（“灰沼”“骸底镇”之类）：前作神居里打 Boss 不弹。
/// AreaTitleController.Play 只在 currentArea 和这里的区域不同时才弹（Finish：没来过弹大字、来过弹小字），
/// 神居存档的 currentArea 一直是别处，所以每进一场都弹。在 Play 里直接收掉：记成放过了、关掉自己，
/// 不写存档（currentArea、VisitedBool 都不动）。ForcePlay（剧情 FSM 手动弹的）同样不弹。
/// </summary>
[HarmonyPatch(typeof(AreaTitleController))]
internal static class AreaTitlePatch
{
    private static readonly AccessTools.FieldRef<AreaTitleController, bool> Played = AccessTools.FieldRefAccess<AreaTitleController, bool>("played");

    internal static bool Suppress => ModeController.Instance != null
        && ModeController.Instance.State is LoomState.ToFight or LoomState.Fight or LoomState.Won or LoomState.ToRest or LoomState.Rest;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(AreaTitleController.Play))]
    private static bool BeforePlay(AreaTitleController __instance)
    {
        if (!Suppress || Played(__instance))
        {
            return true;
        }
        Played(__instance) = true;
        GodhomePlugin.Log.LogInfo($"[Godhome] 战斗里不弹区域名 / area title suppressed in a fight: {__instance.gameObject.scene.name}/{__instance.name}");
        __instance.gameObject.SetActive(false);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(AreaTitleController.ForcePlay))]
    private static bool BeforeForcePlay() => !Suppress;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(AreaTitleController.ForcePlayLarge))]
    private static bool BeforeForcePlayLarge() => !Suppress;
}