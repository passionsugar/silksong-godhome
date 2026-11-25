using GlobalEnums;
using HarmonyLib;
using TeamCherry.Localization;

namespace Godhome;

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

/// <summary>游戏开神居存档：记一行日志（哪个槽）。</summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.StartNewGame))]
internal static class StartNewGameLogPatch
{
    private static void Prefix(GameManager __instance, bool permadeathMode, bool bossRushMode)
    {
        if (bossRushMode)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 开始新建神居存档 / starting a new godhome save in slot {__instance.profileID}");
        }
    }
}

/// <summary>神居分支新建完 PlayerData 后：打上神居标记、出生点、满配数值。</summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.AddGGPlayerDataOverrides))]
internal static class GodhomeNewSavePatch
{
    private static void Postfix(PlayerData __instance) => GodhomeSave.SetupNew(__instance);
}
