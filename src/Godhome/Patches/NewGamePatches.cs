using GlobalEnums;
using HarmonyLib;
using TeamCherry.Localization;

namespace Godhome;

[HarmonyPatch(typeof(UnityEngine.UI.SaveSlotButton), "PresentSaveSlot")]
internal static class GodhomeSaveCardPatch
{
    private static void Postfix(SaveStats __0, UnityEngine.UI.Text ___locationText, UnityEngine.UI.Image ___background,
        SaveSlotBackgrounds ___saveSlots, UnityEngine.GameObject ___blackThreadOverlay)
    {
        if (__0 == null || __0.IsBlank || !__0.BossRushMode)
        {
            return;
        }
        ___locationText.text = L.Get("MODE_NAME");
        var art = ___saveSlots.GetBackground(MapZone.WEAVER_SHRINE)?.BackgroundImage;
        if (art != null)
        {
            ___background.sprite = art;
            ___blackThreadOverlay.SetActive(false);
        }
    }
}

// [Godhome] 新建存档时可以选“织忆圣所”（神居存档）。
//
// 丝之歌主菜单的“选择模式”界面（Menu_Title 场景 _UIManager/UICanvas/PlaymodeMenuScreen）里，
// 除了“普通”“钢魂”，还留着前作的神居按钮 Content/GGButton（StartGameEventTrigger.bossRush = 1），
// 文字用的是前作的 CP3 表 MODE_GODSGLORY / MODE_GODSGLORY_DESC（丝之歌没有这张表）。
// 游戏的规则（反编译代码）：
//   - 选空槽时，只有状态记录 RecPermadeathMode 或 RecBossRushMode 为 1 才进“选择模式”，否则直接开新游戏（SaveSlotButton.cs:701-711）；
//   - 神居按钮只在 RecBossRushMode == 1 时显示（StartGameEventTrigger.cs:53-65，MenuButtonList.cs:122-131）。
// 我们不改这两个状态记录（它们存在全局的 shared.dat 里），只在运行时：
//   1. 选空槽开新游戏时先进“选择模式”；
//   2. 让神居按钮显示；
//   3. 给按钮配上我们的中英文名字；
//   4. 新建的 PlayerData 打上神居标记、设出生点、写满配（GodhomeSave.SetupNew）。

/// <summary>在存档列表里选空槽开新游戏：先进“选择模式”界面（和解锁过钢魂时游戏自己的流程一样）。</summary>
[HarmonyPatch(typeof(UIManager), nameof(UIManager.StartNewGame))]
internal static class NewGameModeMenuPatch
{
    private static bool Prefix(UIManager __instance, bool permaDeath, bool bossRush)
    {
        if (permaDeath || bossRush || __instance.menuState != MainMenuState.SAVE_PROFILES || __instance.playModeMenuScreen == null)
        {
            return true;
        }
        GodhomePlugin.Log.LogInfo("[Godhome] 新建存档：先进“选择模式”界面 / new game: showing the play mode menu first");
        __instance.UIGoToPlayModeMenu();
        return false;
    }
}

/// <summary>“选择模式”里的神居按钮总是显示（不看 RecBossRushMode）。</summary>
[HarmonyPatch(typeof(StartGameEventTrigger), nameof(StartGameEventTrigger.IsFulfilled))]
internal static class GodhomeButtonPatch
{
    private static void Postfix(bool ___bossRush, bool ___permaDeath, ref bool __result)
    {
        if (___bossRush && !___permaDeath)
        {
            __result = true;
        }
    }
}

/// <summary>神居按钮的文字：前作 CP3 表的两个键换成我们的中英文。</summary>
internal static class ModeText
{
    private const string Sheet = "CP3";

    internal static bool TryGet(string key, string sheet, out string text)
    {
        text = "";
        if (sheet != Sheet)
        {
            return false;
        }
        switch (key)
        {
            case "MODE_GODSGLORY":
                text = L.Get("MODE_NAME");
                return true;
            case "MODE_GODSGLORY_DESC":
                text = L.Get("MODE_DESC");
                return true;
            default:
                return false;
        }
    }
}

[HarmonyPatch(typeof(Language), nameof(Language.Get), new[] { typeof(string), typeof(string) })]
internal static class ModeTextGetPatch
{
    private static bool Prefix(string key, string sheetTitle, ref string __result)
    {
        if (ModeText.TryGet(key, sheetTitle, out string text))
        {
            __result = text;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Language), nameof(Language.Has), new[] { typeof(string), typeof(string) })]
internal static class ModeTextHasPatch
{
    private static void Postfix(string key, string sheetTitle, ref bool __result)
    {
        if (!__result && ModeText.TryGet(key, sheetTitle, out _))
        {
            __result = true;
        }
    }
}

/// <summary>
/// 游戏开神居存档：新建完 PlayerData 后打上神居标记、出生点、满配数值。
///
/// 以前挂在 PlayerData.AddGGPlayerDataOverrides 的后缀上。2026-11-25 录宣传片时这个后缀没被调用：
/// 日志里有“开始新建神居存档”，却没有 SetupNew 的那一行，结果在默认出生点 Tut_01 开了普通新游戏
/// （2026-11-20 第二版·1 时还是好的）。最可能的原因：那是个空函数，Mono 编译 GameManager.StartNewGame
/// （DataManager 和我们都给它打了补丁，每打一次重新编译一次）时把它内联了，内联掉的调用不会经过补丁；
/// 会不会内联取决于补丁打上的先后，所以时好时坏。不再依赖它。
///
/// 现在改成 StartNewGame 自己的后缀：StartNewGame 不是小函数，补丁一定生效（前缀的日志一直都在）。
/// 后缀来得及：神居分支最后一步 StartCoroutine(RunContinueGame())，协程会同步跑到第一个 yield
/// （fromMenu 默认是 true，第一个 yield 是 WaitForSeconds(1f)），这之前只做淡出和静音，
/// 读 PlayerData（UpgradeSaveData、respawnScene 等）都在 yield 之后（GameManager.cs:5184-5298）。
/// </summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.StartNewGame))]
internal static class GodhomeNewSavePatch
{
    /// <summary>记下调用前的 PlayerData：后缀只改 StartNewGame 新建出来的那一份。</summary>
    private static void Prefix(GameManager __instance, bool bossRushMode, out PlayerData? __state)
    {
        __state = __instance.playerData;
        if (bossRushMode)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 开始新建神居存档 / starting a new godhome save in slot {__instance.profileID}");
        }
    }

    /// <summary>
    /// 排在别的插件的后缀前面（Codex 审查）：DataManager 也有 StartNewGame 的后缀，万一它抛异常，后面的后缀就不跑了，
    /// 而 RunContinueGame 已经启动，会拿没设置过的数据开游戏。
    /// </summary>
    [HarmonyPriority(Priority.First)]
    private static void Postfix(GameManager __instance, bool bossRushMode, PlayerData? __state)
    {
        PlayerData? pd = __instance.playerData;
        if (!bossRushMode || pd == null)
        {
            // 新建普通存档：丢掉上一份没走到接管的神居存档读档记录（Codex 复查 #6）。
            GodhomeSave.ForgetLoad();
            return;
        }
        // 原函数被别的补丁跳过时后缀照样会跑（HarmonyX），这时 playerData 还是旧的那份，不能改（Codex 审查）。
        if (ReferenceEquals(pd, __state))
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 开始新游戏没有新建 PlayerData（原函数被跳过了？），不设置神居存档 / StartNewGame created no new PlayerData, not setting up a godhome save");
            return;
        }
        GodhomeSave.SetupNew(pd);
    }
}

/// <summary>
/// 读进神居存档：还在标题画面、HUD 还没开始初始化，先把满配的数值字段写进去（GodhomeSave.ApplyFieldsOnLoad）。
/// 为的是 bindCutscenePlayed（玩家反馈“左上角的血量不显示”）：HUD 的面具、丝轴、工具图标都要它为真才出现，
/// 为假时一直等开场的缚丝演出（神居存档不播开场），左上角什么都不画。进游戏后接管时（ApplyKit）再补就晚了：
/// HUD 的 FSM 进游戏时已经停在“等开场”，这一趟游戏里都不会出来。
/// 所有读档的路都经过 SetLoadedGameData(SaveGameData, int)（反编译 GameManager.cs:4545-4636）。普通存档什么都不做。
/// </summary>
[HarmonyPatch(typeof(GameManager), "SetLoadedGameData", new[] { typeof(SaveGameData), typeof(int) })]
internal static class GodhomeLoadPatch
{
    private static void Postfix(GameManager __instance)
    {
        PlayerData? pd = __instance.playerData;
        if (pd == null || !GodhomeSave.IsGodhome(pd))
        {
            // 上一份神居存档没走到接管就回标题了：不再拿着它（Codex 审查第七轮 #6）。
            GodhomeSave.ForgetLoad();
            return;
        }
        try
        {
            GodhomeSave.ApplyFieldsOnLoad(pd);
        }
        catch (System.Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 读档时写满配出错 / applying the kit on load failed: {e.Message}");
        }
    }
}
