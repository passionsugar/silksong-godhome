using System;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Godhome;

/// <summary>[Godhome] BepInEx 配置（BepInEx\config\local.silksong.godhome.cfg）。说明一律中英双语。</summary>
internal static class GodhomeConfig
{
    internal static ConfigEntry<KeyboardShortcut> LoomKey = null!;
    internal static ConfigEntry<string> EnabledSlots = null!;
    internal static ConfigEntry<string> LobbyScene = null!;
    internal static ConfigEntry<string> LobbyMarker = null!;
    internal static ConfigEntry<bool> DebugHallTour = null!;

    internal static ConfigEntry<bool> DebugMode = null!;
    internal static ConfigEntry<bool> DebugInvincible = null!;
    internal static ConfigEntry<KeyboardShortcut> DebugKillKey = null!;
    internal static ConfigEntry<KeyboardShortcut> DebugSelfTestKey = null!;
    internal static ConfigEntry<string> SelfTestBosses = null!;
    internal static ConfigEntry<float> SelfTestStepTimeout = null!;
    internal static ConfigEntry<float> SelfTestWatch = null!;
    internal static ConfigEntry<bool> SelfTestTrace = null!;
    internal static ConfigEntry<string> SelfTestPantheon = null!;
    internal static ConfigEntry<string> SelfTestBindings = null!;
    internal static ConfigEntry<int> SelfTestPantheonAbortAt = null!;
    internal static ConfigEntry<int> SelfTestPantheonRestAfter = null!;
    internal static ConfigEntry<int> DebugAutoLoadSlot = null!;
    internal static ConfigEntry<bool> DebugAutoNewGodhome = null!;
    internal static ConfigEntry<bool> DebugAutoSelfTest = null!;
    internal static ConfigEntry<bool> DebugQuitAfterSelfTest = null!;
    internal static ConfigEntry<string> DebugHk1Probe = null!;
    internal static ConfigEntry<string> Hk1GameDir = null!;
    internal static ConfigEntry<string> DebugHk1ProbeMode = null!;
    internal static ConfigEntry<string> DebugHk1ProbeFsm = null!;
    internal static ConfigEntry<bool> Hk1Enabled = null!;
    internal static ConfigEntry<bool> DebugHk1Tour = null!;
    internal static ConfigEntry<string> DebugPromo = null!;
    internal static ConfigEntry<string> DebugPromoOut = null!;
    internal static ConfigEntry<string> DebugPromoFfmpeg = null!;
    internal static ConfigEntry<string> DebugPromoBosses = null!;
    internal static ConfigEntry<float> DebugPromoBotSeconds = null!;
    internal static ConfigEntry<bool> DebugPromoMuteMusic = null!;

    internal static void Bind(ConfigFile cfg)
    {
        DebugPromo = cfg.Bind("Debug", "Promo", "",
            "调试用：录宣传片。按顺序跑这些段落（逗号分隔：menu,tools,atrium,hall,fights,pantheon,roof），边跑边把画面（1920×1080，60 帧）和声音录下来。留空 = 关。只在 Debug.Enabled、Safety.EnabledSlots 允许的槽里生效。\n" +
            "Debug: record promo footage. Runs these segments in order (comma separated: menu,tools,atrium,hall,fights,pantheon,roof) while recording the picture (1920x1080, 60 fps) and the sound. Empty = off. Only with Debug.Enabled on a slot allowed by Safety.EnabledSlots.");
        DebugPromoOut = cfg.Bind("Debug", "PromoOut", "",
            "调试用：宣传片录像放在哪个目录。留空 = BepInEx\\Godhome\\promo。\nDebug: folder for the promo recordings. Empty = BepInEx\\Godhome\\promo.");
        DebugPromoFfmpeg = cfg.Bind("Debug", "PromoFfmpeg", "",
            "调试用：录像要用的 ffmpeg.exe 的完整路径。找不到就不录。\nDebug: full path of the ffmpeg.exe used for the promo recording. Nothing is recorded without it.");
        DebugPromoBosses = cfg.Bind("Debug", "PromoBosses", "",
            "调试用：宣传片 fights 段落依次打的 Boss id（逗号分隔，从各自的雕像开打）。\nDebug: boss ids fought in order by the promo 'fights' segment (comma separated; each started from its statue).");
        DebugPromoBotSeconds = cfg.Bind("Debug", "PromoBotSeconds", 8f,
            "调试用：宣传片里每场先让自动操作的大黄蜂真打这么多秒，之后才用调试伤害收尾。\nDebug: in the promo, let the auto-piloted Hornet really fight for this many seconds before the debug damage finishes the boss.");
        DebugPromoMuteMusic = cfg.Bind("Debug", "PromoMuteMusic", true,
            "调试用：录宣传片时把游戏音乐调成静音（只在运行时改混音器，不改设置），只录音效。\nDebug: mute the game's music while recording the promo (runtime mixer only, settings untouched), so only sound effects are recorded.");
        DebugHk1Tour = cfg.Bind("Debug", "Hk1Tour", false,
            "调试用：自动读档进前作的神居后巡视一遍（门、挑战界面、开一次万神殿、手办屋的雕像、拉杆、从雕像开一场、走回前厅和屋顶）并截图。\n" +
            "Debug: after the auto-load into Hollow Knight's Godhome, tour it (doors, challenge UIs, one pantheon start, the Hall of Gods statues, a lever, a statue fight, walking back to the atrium and roof) taking screenshots.");
        Hk1Enabled = cfg.Bind("Experimental", "Hk1Godhome", true,
            "本机装了前作（空洞骑士）和 UnitySceneRepacker 插件时，大厅用前作的神居（前厅、手办屋、前厅屋顶），Boss 和万神殿都是本作的。" +
            "没装、找不到或打包失败时自动退回织忆圣所。改了要重启游戏。\n" +
            "When Hollow Knight and the UnitySceneRepacker plugin are installed, the lobby is Hollow Knight's Godhome (Atrium, Hall of Gods, Atrium roof) " +
            "with Silksong's bosses and pantheons. Falls back to the Loom sanctum when they are missing or repacking fails. Restart the game after changing.");
        DebugHk1ProbeFsm = cfg.Bind("Debug", "Hk1ProbeFsm", "",
            "试验用：前作场景里路径匹配这个正则的物体，把它们的 FSM（状态、动作、跳转、变量）整个写出来。留空 = 不写。\n" +
            "Experiment: dump the FSMs (states, actions, transitions, variables) of objects in the Hollow Knight scenes whose path matches this regex. Empty = off.");
        DebugHk1ProbeMode = cfg.Bind("Debug", "Hk1ProbeMode", "additive",
            "试验用：前作场景怎么进。additive = 叠加在大厅上看；native = 走游戏自己的转场真的进去（场景名后可写 @入口门，如 GG_Workshop@left1）；both = 两个都试。\n" +
            "Experiment: how to enter the Hollow Knight scenes. additive = overlay on the lobby; native = a real scene transition through the game (an entry gate may follow the scene name, e.g. GG_Workshop@left1); both = try both.");
        DebugHk1Probe = cfg.Bind("Debug", "Hk1Probe", "",
            "试验用：自动读档进大厅后，把前作（空洞骑士）的这些场景（逗号分隔，如 GG_Workshop,GG_Atrium）用 UnitySceneRepacker 重新打包、叠加加载进来，记下里面有什么、走几个点截图。留空 = 关。需要装 UnitySceneRepacker 插件和前作。\n" +
            "Experiment: after the auto-load, repack these Hollow Knight scenes (comma separated, e.g. GG_Workshop,GG_Atrium) with UnitySceneRepacker, load them additively, log their contents and take screenshots. Empty = off. Needs the UnitySceneRepacker plugin and Hollow Knight installed.");
        Hk1GameDir = cfg.Bind("Experimental", "Hk1GameDir", "",
            "前作（空洞骑士）的安装目录（里面有 hollow_knight_Data）。留空 = 自动找（和丝之歌同一个 steamapps\\common，或各个盘的 Steam 库）。\n" +
            "Hollow Knight install folder (the one containing hollow_knight_Data). Empty = look for it next to Silksong or in the Steam libraries on each drive.");
        LoomKey = cfg.Bind("General", "LoomKey", new KeyboardShortcut(KeyCode.F6),
            "织忆键：在神居存档的大厅里打开/关闭名录。\n" +
            "Loom key: opens/closes the list in the lobby of a godhome save.");

        EnabledSlots = cfg.Bind("Safety", "EnabledSlots", "3",
            "调试功能只在这些存档槽生效（逗号分隔，例如 \"3,4\"；写 all 表示所有槽；留空表示一个槽都不开）。开发期间默认只开槽 3（神居存档的测试槽）。神居存档本身不受这项限制。\n" +
            "Debug features only work on these save slots (comma separated, e.g. \"3,4\"; \"all\" = every slot; empty = no slot). During development only slot 3 (the godhome test slot) is enabled. Godhome saves themselves are not limited by this.");

        LobbyScene = cfg.Bind("Lobby", "Scene", "Weave_07",
            "大厅使用的场景（第二版·1 临时借用织巢里的长椅房间，第二版·2 换成自建的圣所）。\nScene used as the lobby (v2 stage 1 borrows a weavenest bench room; stage 2 replaces it with our own sanctum).");
        LobbyMarker = cfg.Bind("Lobby", "Marker", "RestBench",
            "大厅里醒来的位置（RespawnMarker 名）。\nRespawn marker the hero wakes at in the lobby.");
        DebugHallTour = cfg.Bind("Debug", "HallTour", false,
            "调试用：自动读档进大厅后走一遍长廊截图，再从一尊雕像开一场重织、放弃、看是否回到雕像前。\n" +
            "Debug: after the auto-load, tour the hall taking screenshots, then start a tier-2 fight from a statue, abandon it and check the return spot.");

        DebugMode = cfg.Bind("Debug", "Enabled", false,
            "调试模式（默认关）：可用秒杀键、战斗中按织忆键放弃、自检。\n" +
            "Debug mode (off by default): enables the instakill key, abandoning a fight with the Loom key, and the self-test.");
        DebugInvincible = cfg.Bind("Debug", "Invincible", false,
            "调试模式下在神居存档里无敌。\nInvincible in a godhome save while debug mode is on.");
        DebugKillKey = cfg.Bind("Debug", "KillKey", new KeyboardShortcut(KeyCode.F8),
            "秒杀当前 Boss。\nInstantly kill the current boss.");
        DebugSelfTestKey = cfg.Bind("Debug", "SelfTestKey", new KeyboardShortcut(KeyCode.F10),
            "开始自检：对每个 Boss 依次跑 从入场门进场 → 秒杀 → 回大厅，结果写到 BepInEx\\Godhome\\selftest-latest.md。\n" +
            "Run the self-test: enter through door_dreamEnter → kill → return for every boss; results go to BepInEx\\Godhome\\selftest-latest.md.");
        SelfTestBosses = cfg.Bind("Debug", "SelfTestBosses", "",
            "自检只跑这些 Boss id（逗号分隔，留空=全部）。\nOnly self-test these boss ids (comma separated; empty = all).");
        SelfTestStepTimeout = cfg.Bind("Debug", "SelfTestStepTimeout", 45f,
            "自检每一步的超时秒数。\nTimeout in seconds for each self-test step.");
        SelfTestWatch = cfg.Bind("Debug", "SelfTestWatch", 0f,
            "调试用：自检开战后先不打，观察这么多秒（每半秒记下场景里所有激活的大体型敌人的位置、在不在画面里，每 3 秒截图）。0 = 不观察。\n" +
            "Debug: after a self-test fight starts, watch it for this many seconds without attacking (log every active large enemy's position and whether it is on screen every 0.5 s, screenshot every 3 s). 0 = off.");
        SelfTestTrace = cfg.Bind("Debug", "SelfTestTrace", false,
            "调试用：自检打 Boss 时，Boss 物体的激活状态和它身上每个 FSM 的状态一变就记一行日志（带血量、位置），用来查 Boss 卡在哪一步。\n" +
            "Debug: while the self-test attacks a boss, log every change of the boss objects' active state and of each of their FSMs' states (with hp and position), to find where a boss gets stuck.");
        SelfTestPantheon = cfg.Bind("Debug", "SelfTestPantheon", "",
            "调试用：自检改跑这个万神殿（id，如 p1）：巡视前厅的门 → 在门前的面板里套上 SelfTestBindings → 开始 → 逐个打完（经过休息点）→ 看结算 → 回到门前。留空 = 照常测单个 Boss。\n" +
            "Debug: make the self-test run this pantheon (id such as p1): tour the atrium doors, set SelfTestBindings in the door panel, begin, fight through (via rest stops), check the result screen and the return to the door. Empty = the usual per-boss self-test.");
        SelfTestBindings = cfg.Bind("Debug", "SelfTestBindings", "",
            "调试用：万神殿自检要套上的束缚（needle,mask,tools,silk，逗号分隔，all = 全部，留空 = 不套）。\n" +
            "Debug: bindings for the pantheon self-test (needle,mask,tools,silk; comma separated; all = every one; empty = none).");
        SelfTestPantheonAbortAt = cfg.Bind("Debug", "SelfTestPantheonAbortAt", 0,
            "调试用：万神殿自检打到第几场时放弃（回大厅，测“作废”那条路；0 = 不放弃）。\n" +
            "Debug: abandon the pantheon self-test at this fight (back to the lobby, to test the failed-run path; 0 = never).");
        SelfTestPantheonRestAfter = cfg.Bind("Debug", "SelfTestPantheonRestAfter", 0,
            "调试用：万神殿自检时临时在第几场之后加一个休息点（测休息点那条路；0 = 照表）。\n" +
            "Debug: during the pantheon self-test, add a rest stop after this fight (to test the rest stop; 0 = as in the table).");
        DebugAutoLoadSlot = cfg.Bind("Debug", "AutoLoadSlot", 0,
            "调试用：到标题画面后自动读取这个存档槽（0=关）。受 Safety.EnabledSlots 限制。\n" +
            "Debug: automatically load this save slot at the title screen (0 = off). Limited by Safety.EnabledSlots.");
        DebugAutoNewGodhome = cfg.Bind("Debug", "AutoNewGodhome", false,
            "调试用：自动读档的槽是空的时，在那里自动新建神居存档（只用空槽，绝不覆盖已有存档）。\n" +
            "Debug: if the auto-load slot is empty, create a new godhome save there (empty slots only; never overwrites a save).");
        DebugAutoSelfTest = cfg.Bind("Debug", "AutoSelfTest", false,
            "调试用：自动读档后自动开始自检。\nDebug: start the self-test automatically after the auto-load.");
        DebugQuitAfterSelfTest = cfg.Bind("Debug", "QuitAfterSelfTest", false,
            "调试用：自检结束后退出游戏。\nDebug: quit the game when the self-test finishes.");
    }

    /// <summary>
    /// 这个存档槽能不能用织忆圣所。留空 = 一个都不开（安全设置写错或被清空时宁可不开，Codex 审查 #9）；
    /// 要所有槽必须明确写 all（或 *）。
    /// 只认游戏真有的存档槽（Codex 复审）：Platform.IsSaveSlotIndexValid 认 0..4，0 不是玩家的槽，所以是 1..4；
    /// 写 all 也只放开这几个。
    /// </summary>
    internal static bool IsSlotEnabled(int slot)
    {
        if (slot < 1 || !Platform.IsSaveSlotIndexValid(slot))
        {
            return false;
        }
        string raw = EnabledSlots.Value?.Trim() ?? "";
        string[] parts = raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(s => s == "*" || s.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return parts.Any(s => int.TryParse(s, out int n) && n == slot);
    }
}
