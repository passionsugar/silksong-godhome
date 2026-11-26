using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Godhome;

// [Godhome] 前作神居的类在丝之歌里都还在（反编译核实），读的是前作的 PlayerData 字段（丝之歌没有）。
// 这些补丁只在前作神居开着（Hk1Lobby.Active）、而且是我们接上的雕像和门时改；别的时候照原样。

/// <summary>前作按 playerData.unlockedBossScenes 判 Boss 解锁，丝之歌里是 null（加载前厅时的空引用就是它）。神居存档全部解锁。</summary>
[HarmonyPatch(typeof(BossScene), nameof(BossScene.IsUnlockedSelf))]
internal static class BossSceneUnlockedPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!Hk1Lobby.Active)
        {
            return true;
        }
        __result = true;
        return false;
    }
}

/// <summary>
/// 前作神居场景的场景管理器（前作 SceneManager，在丝之歌里是 CustomSceneManager；反编译核实两边的 Start）：
/// - 前作的 Start 直接用场景自己的环境光、色彩曲线、饱和度、角色光；丝之歌的 Start 只要 mapZone 不是 NONE、又没勾
///   overrideColorSettings（前作没有这个字段，读出来是 false），就换成丝之歌那个地图区的默认值。前作神居的 mapZone 数值对到
///   丝之歌是别的区，前作场景的精灵又大多吃环境光，于是整张图发暗，只剩角色光照到的一圈亮。这里打开 overrideColorSettings。
/// - 饱和度：前作在场景值上加 0.17，丝之歌加 0.4；按前作的算法算好，再让丝之歌不加。
/// - scenePools：前作没有这个字段（null），丝之歌 Awake 遍历它就是日志里的 CustomSceneManager.Awake 空引用。
/// - 音频快照属于前作的混音器，丝之歌自己的音乐、角色混音器没人恢复（神居里没声音），换成丝之歌的（Hk1Audio）。
/// </summary>
[HarmonyPatch(typeof(CustomSceneManager), "Awake")]
internal static class Hk1SceneManagerAwakePatch
{
    internal static readonly AccessTools.FieldRef<CustomSceneManager, bool> OverrideColors =
        AccessTools.FieldRefAccess<CustomSceneManager, bool>("overrideColorSettings");

    /// <summary>
    /// 前作的环境音（AtmosCue）和丝之歌的结构不一样：丝之歌 Start 里镜头就位后套用它，AtmosCue.GetChannelInfo 空引用，
    /// 那个回调后面的环境/角色音频快照、AudioManager.CustomSceneManagerReady 都跳过了，回调也没退订。前作神居里不套用前作的环境音。
    /// </summary>
    private static readonly AccessTools.FieldRef<CustomSceneManager, AtmosCue> Atmos =
        AccessTools.FieldRefAccess<CustomSceneManager, AtmosCue>("atmosCue");

    /// <summary>前作 SceneManager.AdjustSaturationForPlatform 加的量。</summary>
    private const float Hk1PlatformSaturation = 0.17f;

    private static void Prefix(CustomSceneManager __instance)
    {
        string scene = __instance.gameObject.scene.name;
        // 神居的三间屋子和温泉都是（温泉漏过一次：scenePools 空引用，实机日志）。丝之歌自己没有这些场景名。
        if (!Hk1Lobby.IsHk1Scene(scene))
        {
            // 只读：记下丝之歌自己的环境效果混音器，前作场景换快照时用（Hk1Audio）。
            Hk1Audio.NoteOurs(__instance);
            return;
        }
        // 音频是全局的：只在正式切进这个场景时换。游戏切场景的几条路（BeginSceneTransition、旧的 ChangeToScene → LoadScene、
        // LoadSceneAdditive）都在加载前把 nextSceneName 设成目的地（反编译 GameManager.cs:696、3062、3075）。
        // 调试实验 Hk1Probe 附加加载的前作房间（它直接用 UnitySceneRepacker 读）不是目的地，不动，免得停掉当前场景还在用的环境音（Codex 审查第七轮 #2、复查 #1）。
        // 已知：那个附加房间自己的 Start 照样会把前作的音乐配置交给 AudioManager（复查 #2），只在调试实验里，玩家碰不到。
        GameManager gm = GameManager.UnsafeInstance;
        if (gm != null && gm.nextSceneName == scene)
        {
            try
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居 {scene}：音频换成丝之歌的混音器 / audio moved to our mixers：{Hk1Audio.Adopt(__instance)}");
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作神居 {scene}：换音频快照出错 / audio fix failed: {e.Message}");
            }
        }
        else
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居 {scene}：不是这次切场景的目的地（附加加载），不动音频 / not the transition's destination (additive load), audio left alone");
        }
        try
        {
            __instance.scenePools ??= Array.Empty<SceneObjectPool>();
            Atmos(__instance) = null!;
            OverrideColors(__instance) = true;
            if (!__instance.ignorePlatformSaturationModifiers)
            {
                __instance.saturation += Hk1PlatformSaturation;
                __instance.ignorePlatformSaturationModifiers = true;
            }
            __instance.redChannel ??= AnimationCurve.Linear(0f, 0f, 1f, 1f);
            __instance.greenChannel ??= AnimationCurve.Linear(0f, 0f, 1f, 1f);
            __instance.blueChannel ??= AnimationCurve.Linear(0f, 0f, 1f, 1f);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居 {scene}：用场景自己的光 / using the scene's own lighting"
                + $"（环境光 / ambient {__instance.defaultColor} x{__instance.defaultIntensity:0.00}，饱和度 / saturation {__instance.saturation:0.00}，"
                + $"角色光 / hero light {__instance.heroLightColor}，暗度 / darkness {__instance.darknessLevel}，地图区 / map zone {(int)__instance.mapZone}）");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作神居 {scene}：改场景光出错 / scene lighting fix failed: {e.Message}");
        }
    }
}

/// <summary>
/// 场景粒子按地图区编号取（SceneParticlesController.EnableParticles 里 mapZoneParticles[(int)区]）。前作温泉的地图区是前作的编号，
/// 超出丝之歌这张表，数组越界（实机日志），异常从镜头就位的协程（CameraController.DoPositionToHero）里抛出来，后面的步骤都跳过。
/// 区号超出表的范围时当成“这个场景不要场景粒子”。丝之歌自己的场景区号都在表里，不受影响。
/// </summary>
[HarmonyPatch(typeof(SceneParticlesController), nameof(SceneParticlesController.EnableParticles))]
internal static class Hk1SceneParticlesPatch
{
    private static readonly AccessTools.FieldRef<SceneParticlesController, CustomSceneManager> Sm =
        AccessTools.FieldRefAccess<SceneParticlesController, CustomSceneManager>("sm");
    private static readonly AccessTools.FieldRef<SceneParticlesController, GameObject[]> ZoneParticles =
        AccessTools.FieldRefAccess<SceneParticlesController, GameObject[]>("mapZoneParticles");

    private static bool logged;

    private static void Prefix(SceneParticlesController __instance, ref bool noSceneParticles)
    {
        if (noSceneParticles)
        {
            return;
        }
        try
        {
            CustomSceneManager sm = Sm(__instance);
            GameObject[] table = ZoneParticles(__instance);
            if (sm == null || table == null)
            {
                return;
            }
            int zone = (int)(sm.overrideParticlesWith == GlobalEnums.MapZone.NONE ? sm.mapZone : sm.overrideParticlesWith);
            if (zone >= 0 && zone < table.Length)
            {
                return;
            }
            noSceneParticles = true;
            if (!logged)
            {
                logged = true;
                GodhomePlugin.Log.LogInfo($"[Godhome] 场景 {sm.gameObject.scene.name} 的地图区 {zone} 超出场景粒子表（{table.Length}），不开场景粒子 / map zone {zone} is outside the scene particle table; no scene particles");
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 场景粒子检查出错 / scene particle check failed: {e.Message}");
        }
    }
}

/// <summary>
/// 前作场景里的物体读前作 PlayerData 的开关（丝之歌的 PlayerData 没有这些字段，GetBool 一律是 false）：
/// 前作神居开着、人在前作神居里时，按我们的记录回答；写的时候不写（丝之歌没有这些字段）。
/// - finalBossDoorUnlocked：屋顶的门（第五个万神殿）开了没有。前厅顶上去屋顶的出口（top2）、前厅右上的门板（BossDoorTargetLock）都读它。
/// - bossDoorCageUnlocked：第四扇门的骨笼开了没有。
/// 别的前作字段第一次被读时记日志（以后要接再加）。
/// </summary>
internal static class Hk1PlayerData
{
    private static readonly Dictionary<string, bool> known = new(StringComparer.Ordinal);
    private static readonly HashSet<string> logged = new(StringComparer.Ordinal);

    internal static bool TryGet(string name, out bool value)
    {
        value = false;
        if (!Hk1Lobby.Active || string.IsNullOrEmpty(name))
        {
            return false;
        }
        switch (name)
        {
            case "finalBossDoorUnlocked":
                value = Hk1Doors.Unlocked(Hk1Doors.FinalDoor);
                return true;
            case "bossDoorCageUnlocked":
                value = Hk1Doors.Unlocked("GG_Challenge_Door (4)");
                return true;
        }
        if (!known.TryGetValue(name, out bool exists))
        {
            exists = AccessTools.Field(typeof(PlayerData), name) != null;
            known[name] = exists;
        }
        if (!exists && Hk1Lobby.InHub && logged.Add(name))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居读了丝之歌没有的存档字段 / hk1 scene read a PlayerData bool Silksong lacks: {name}（当 false / treated as false）");
        }
        return false;
    }

    internal static bool Handles(string name) => Hk1Lobby.Active && name is "finalBossDoorUnlocked" or "bossDoorCageUnlocked";
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.GetBool))]
internal static class Hk1GetBoolPatch
{
    private static bool Prefix(string boolName, ref bool __result)
    {
        if (Hk1PlayerData.TryGet(boolName, out bool v))
        {
            __result = v;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBool))]
internal static class Hk1SetBoolPatch
{
    private static bool Prefix(string boolName) => !Hk1PlayerData.Handles(boolName);
}

/// <summary>
/// 前作的长椅（前厅里有一张，Bench Control）坐下时用 SetPlayerDataString（GameManager.SetPlayerDataString → PlayerData.SetString）
/// 把复活点写成前作的场景。存档前虽然会换回圣所（SaveSanitizePatch），但存档是把同一个 PlayerData 排进队列异步序列化的，
/// 中途再写就漏进存档（Codex 审查）：在源头就不写前作场景名。复活点名照写（前作长椅叫 RestBench，和圣所的长椅同名）。
/// </summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetString))]
internal static class Hk1SetStringPatch
{
    private static bool Prefix(string stringName, string value)
    {
        // 不看 Hk1Lobby.Active：前作神居刚放弃（超时）时迟到的前作房间还会加载进来，这时也不能写（Codex 复审）。
        // 丝之歌自己没有这些场景名，正常游戏碰不到。温泉里也有前作的长椅，一起拦。
        if (string.IsNullOrEmpty(value) || !Hk1Lobby.IsHk1Scene(value))
        {
            return true;
        }
        if (stringName is nameof(PlayerData.respawnScene) or nameof(PlayerData.tempRespawnScene) or nameof(PlayerData.nonLethalRespawnScene))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：不把复活点写成前作场景 / hk1 godhome: not writing {stringName} = {value}");
            return false;
        }
        return true;
    }
}

/// <summary>
/// 前作神居里的门（TransitionPoint）出发前查丝之歌的场景表（SceneTeleportMap），前作的场景不在表里，
/// 门就改成“回到当前场景”（实机日志：Transition will fail! Returning to current scene）。去前作神居的三个房间时当它在表里。
/// </summary>
[HarmonyPatch(typeof(TransitionPoint), "SceneGateExists")]
internal static class Hk1SceneGatePatch
{
    private static bool Prefix(string sceneName, ref bool __result)
    {
        if (Hk1Lobby.Active && Hk1Lobby.IsHub(sceneName))
        {
            __result = true;
            return false;
        }
        return true;
    }
}

// ───────────────────────────── 雕像 ─────────────────────────────

/// <summary>雕像的完成情况读我们的记录（主展示物体上的版本）。</summary>
[HarmonyPatch(typeof(BossStatue), nameof(BossStatue.StatueState), MethodType.Getter)]
internal static class StatueStateGetPatch
{
    private static bool Prefix(BossStatue __instance, ref BossStatue.Completion __result)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(__instance);
        if (s == null)
        {
            return true;
        }
        __result = Hk1Hall.CompletionOf(s.Def.Variants[s.Main], s.UsingAlt);
        return false;
    }
}

[HarmonyPatch(typeof(BossStatue), nameof(BossStatue.StatueState), MethodType.Setter)]
internal static class StatueStateSetPatch
{
    private static bool Prefix(BossStatue __instance, BossStatue.Completion value)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(__instance);
        if (s == null)
        {
            return true;
        }
        // 改的是我们自己的 Slot，value 只读：分析器 Harmony003 误报“改了非 ref 参数”。
#pragma warning disable Harmony003
        s.UsingAlt = value.usingAltVersion;
#pragma warning restore Harmony003
        return false;
    }
}

/// <summary>另一个展示物体上的版本（前作的梦境版 / 拉杆版）。</summary>
[HarmonyPatch(typeof(BossStatue), nameof(BossStatue.DreamStatueState), MethodType.Getter)]
internal static class DreamStatueStateGetPatch
{
    private static bool Prefix(BossStatue __instance, ref BossStatue.Completion __result)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(__instance);
        if (s == null)
        {
            return true;
        }
        if (s.Def.Variants.Count < 2)
        {
            __result = BossStatue.Completion.None;
            return false;
        }
        __result = Hk1Hall.CompletionOf(s.Def.Variants[s.Alt], s.UsingAlt);
        return false;
    }
}

[HarmonyPatch(typeof(BossStatue), nameof(BossStatue.DreamStatueState), MethodType.Setter)]
internal static class DreamStatueStateSetPatch
{
    private static bool Prefix(BossStatue __instance, BossStatue.Completion value)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(__instance);
        if (s == null)
        {
            return true;
        }
        // 同上：改的是 Slot，不是参数（Harmony003 误报）。
#pragma warning disable Harmony003
        s.UsingAlt = value.usingAltVersion;
#pragma warning restore Harmony003
        return false;
    }
}

/// <summary>拉杆敲中（doAnim）：要露出来的展示物体先换成下一个版本的石像，前作的沉下升起动画照放。</summary>
[HarmonyPatch(typeof(BossStatue), nameof(BossStatue.SetDreamVersion))]
internal static class StatueTogglePatch
{
    private static void Prefix(BossStatue __instance, bool value, bool useAltStatue, bool doAnim)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(__instance);
        if (s != null && useAltStatue && doAnim && s.Def.Variants.Count > 1)
        {
            Hk1Hall.OnToggle(s, value);
        }
    }
}

/// <summary>雕像的挑战面板（前作的 GG_Challenge_Statue_Canvas）：名字、说明、三档的名字换成本作的。</summary>
[HarmonyPatch(typeof(BossChallengeUI), nameof(BossChallengeUI.Setup))]
internal static class StatueChallengeSetupPatch
{
    private static bool logged;

    private static void Postfix(BossChallengeUI __instance, BossStatue bossStatue)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(bossStatue);
        if (s == null)
        {
            return;
        }
        try
        {
            BossDef b = s.Shown;
            if (!logged)
            {
                logged = true;
                GodhomePlugin.Log.LogInfo("[Godhome] 前作雕像的挑战面板上的字（换之前）/ hk1 statue challenge panel texts (before): "
                    + string.Join(" | ", __instance.GetComponentsInChildren<Text>(true).Select(t => $"{t.name}='{t.text}'")));
            }
            Hk1Text.Set(__instance.bossNameText, b.DisplayName);
            Hk1Text.Set(__instance.descriptionText, b.Region);
            BossChallengeUI.ButtonDisplay[] tiers = { __instance.tier1Button, __instance.tier2Button, __instance.tier3Button };
            for (int i = 0; i < tiers.Length; i++)
            {
                if (tiers[i]?.button == null)
                {
                    continue;
                }
                foreach (Text t in tiers[i].button.GetComponentsInChildren<Text>(true))
                {
                    Hk1Text.Set(t, L.Get("TIER_" + i));
                }
            }
            Hk1Text.FixMissing(__instance);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像的挑战面板 / hk1 statue challenge: {s.Name} → {b.Id}");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作雕像的挑战面板换字出错 / hk1 statue panel text failed: {e.Message}");
        }
    }
}

/// <summary>
/// 手办屋门口的总览板（前作的 BossSummaryUI.SetupUI，BossSummaryBoard.Start 时建一次）：前作一尊雕像列一行，
/// 有梦境版的再列一行，名字查前作的键（丝之歌里没有）。我们一尊雕像上放一到几个本作 Boss：
/// 每个 Boss 列一行，名字、完成情况都用我们的（Codex 审查）。行的样子照前作（同一个模板、同一套状态图）。
/// </summary>
[HarmonyPatch(typeof(BossSummaryUI), nameof(BossSummaryUI.SetupUI))]
internal static class SummaryBoardPatch
{
    internal static int Rows { get; private set; }

    private static bool Prefix(BossSummaryUI __instance, List<BossStatue> bossStatues)
    {
        GameObject template = __instance.listItemTemplate;
        if (!Hk1Lobby.Active || template == null || bossStatues == null || !bossStatues.Any(st => Hk1Hall.SlotOf(st) != null))
        {
            return true;
        }
        try
        {
            template.SetActive(true);
            Rows = 0;
            // 先照前作板上的顺序；前作板上没列的雕像（实机少一尊）接在后面，按下层到上层、从左到右。
            List<Hk1Hall.Slot> order = bossStatues.Select(Hk1Hall.SlotOf).Where(s => s != null).Select(s => s!).Distinct().ToList();
            List<Hk1Hall.Slot> extra = Hk1Hall.Slots.Where(s => !order.Contains(s))
                .OrderBy(s => s.Statue.transform.position.y).ThenBy(s => s.Statue.transform.position.x).ToList();
            if (extra.Count > 0)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作总览板上没列的雕像，接在后面 / statues missing from the hk1 board, appended: {string.Join(", ", extra.Select(s => s.Name))}");
            }
            foreach (Hk1Hall.Slot s in order.Concat(extra))
            {
                BossStatue st = s.Statue;
                foreach (BossDef b in s.Def.Variants)
                {
                    BossStatue.Completion c = Hk1Hall.CompletionOf(b, false);
                    int state = c.completedTier3 ? 4 : c.completedTier2 ? 3 : c.completedTier1 ? 2 : c.isUnlocked ? 1 : 0;
                    GameObject row = Object.Instantiate(template, template.transform.parent);
                    row.name = $"{template.name} ({st.name} {b.Id})";
                    Image img = row.GetComponentInChildren<Image>();
                    if (img != null && __instance.stateSprites != null && state < __instance.stateSprites.Length)
                    {
                        img.sprite = __instance.stateSprites[state];
                        img.SetNativeSize();
                    }
                    Text t = row.GetComponentInChildren<Text>();
                    if (t != null)
                    {
                        Hk1Text.Set(t, state > 0 ? b.DisplayName : __instance.defaultName);
                    }
                    Rows++;
                }
            }
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作手办屋的总览板 / hk1 hall summary board: {Rows} 行 / rows");
            Fit(template.transform.parent, Rows);
            // 标题（前作文本表里的 GG_SUMMARY_TITLE）换成我们的。
            Hk1Text.FixMissing(__instance);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作手办屋的总览板出错 / hk1 hall summary board failed: {e.Message}");
        }
        finally
        {
            template.SetActive(false);
        }
        return false;
    }

    /// <summary>前作的板按 44 行排（4 列，每列 11 行，实机截图）。我们 50 行，第 5 列排到板外：每列多排几行、行高按比例缩，总高度不变。</summary>
    private const int Columns = 4;

    // 普通的排版辅助函数，不是补丁：Harmonize 按类扫描时误报 HARMONIZE004，HarmonyX 不会把它当补丁（没有标记、名字也不是约定名）。
#pragma warning disable HARMONIZE004
    private static void Fit(Transform list, int rows)
#pragma warning restore HARMONIZE004
    {
        GridLayoutGroup grid = list.GetComponent<GridLayoutGroup>();
        if (grid == null)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 总览板的列表不是网格排版，不调 / summary list is not a grid: "
                + string.Join(", ", list.GetComponents<Component>().Select(c => c.GetType().Name)));
            return;
        }
        RectTransform rt = (RectTransform)list;
        string before = $"cell {grid.cellSize} spacing {grid.spacing} constraint {grid.constraint}/{grid.constraintCount} axis {grid.startAxis} size {rt.rect.size}";
        float step = grid.cellSize.y + grid.spacing.y;
        int perColumn = grid.constraint == GridLayoutGroup.Constraint.FixedRowCount
            ? grid.constraintCount
            : step > 0f ? Mathf.FloorToInt((rt.rect.height + grid.spacing.y) / step) : 0;
        int need = Mathf.CeilToInt(rows / (float)Columns);
        if (grid.startAxis == GridLayoutGroup.Axis.Vertical && grid.constraint != GridLayoutGroup.Constraint.FixedColumnCount && perColumn > 0 && need > perColumn)
        {
            float k = perColumn / (float)need;
            grid.cellSize = new Vector2(grid.cellSize.x, grid.cellSize.y * k);
            grid.spacing = new Vector2(grid.spacing.x, grid.spacing.y * k);
            if (grid.constraint == GridLayoutGroup.Constraint.FixedRowCount)
            {
                grid.constraintCount = need;
            }
            GodhomePlugin.Log.LogInfo($"[Godhome] 总览板改成每列 {need} 行 / summary board: {perColumn} → {need} rows per column（{before} → cell {grid.cellSize} spacing {grid.spacing}）");
        }
        else
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 总览板排版没改 / summary board layout unchanged: {before}，每列 / per column {perColumn}，要 / need {need}");
        }
    }
}

/// <summary>
/// 选好难度（前作 BossChallengeUI.LoadBoss）：不走前作的“载入 Boss 场景”（丝之歌里没有前作的 Boss 场景），
/// 走我们的开战流程（快照、场地、回到这尊雕像前）。开战没成（存档中、快照还在……）就照前作关掉面板。
/// </summary>
[HarmonyPatch(typeof(BossChallengeUI), nameof(BossChallengeUI.LoadBoss), new[] { typeof(int), typeof(bool) })]
internal static class StatueLoadBossPatch
{
    private static readonly AccessTools.FieldRef<BossChallengeUI, BossStatue> StatueField = AccessTools.FieldRefAccess<BossChallengeUI, BossStatue>("bossStatue");
    private static readonly AccessTools.FieldRef<BossChallengeUI, BossChallengeUI.HideEvent> CancelField = AccessTools.FieldRefAccess<BossChallengeUI, BossChallengeUI.HideEvent>("OnCancel");

    private static bool Prefix(BossChallengeUI __instance, int level, bool doHideAnim)
    {
        Hk1Hall.Slot? s = Hk1Hall.SlotOf(StatueField(__instance));
        ModeController? mc = ModeController.Instance;
        if (s == null || mc == null)
        {
            return true;
        }
        BossDef b = s.Shown;
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像：开战 / hk1 statue: fight {b.Id}（{L.Get("TIER_" + Mathf.Clamp(level, 0, 2))}，雕像 / statue {s.Name}）");
        Hk1Lobby.ReleaseHero();
        mc.StartFight(b, level, s.Name);
        if (mc.State == LoomState.ToFight)
        {
            // 开战了：面板收起，前作的 FSM 不再往下走（场景马上切走）。
            CancelField(__instance) = null!;
            __instance.Hide(doHideAnim);
        }
        else
        {
            // 没开成：照前作关面板（OnCancel 让 FSM 收尾、交还操作）。
            __instance.Hide(doHideAnim);
        }
        return false;
    }
}

// ───────────────────────────── 万神殿的门 ─────────────────────────────

/// <summary>
/// 门的 Start（前作读 PlayerData 里这扇门的 Completion 字段，丝之歌没有，读到一半就出错）：按我们的记录和开门条件摆门
/// （锁着的显示锁，开着的显示门和完成的装饰），照前作把回程门改名（门名接在后面）。没接上的门照原样。
/// </summary>
[HarmonyPatch(typeof(BossSequenceDoor), "Start")]
internal static class DoorStartPatch
{
    private static readonly AccessTools.FieldRef<BossSequenceDoor, BossSequenceDoor.Completion> CompletionField =
        AccessTools.FieldRefAccess<BossSequenceDoor, BossSequenceDoor.Completion>("completion");
    private static readonly AccessTools.FieldRef<BossDoorLockUI> LockedUi = AccessTools.StaticFieldRefAccess<BossDoorLockUI>(AccessTools.Field(typeof(BossSequenceDoor), "lockedUI"));
    private static readonly System.Reflection.MethodInfo SetDisplay = AccessTools.Method(typeof(BossSequenceDoor), "SetDisplayState");

    private static bool Prefix(BossSequenceDoor __instance)
    {
        PantheonDef? p = Hk1Doors.DefOf(__instance);
        if (p == null)
        {
            return true;
        }
        try
        {
            BossSequenceDoor.Completion c = Hk1Doors.CompletionOf(p);
            CompletionField(__instance) = c;
            if (c.unlocked)
            {
                SetDisplay.Invoke(__instance, new object[] { c });
                if (__instance.lockSet != null)
                {
                    __instance.lockSet.SetActive(false);
                }
                if (__instance.unlockedSet != null)
                {
                    __instance.unlockedSet.SetActive(true);
                }
            }
            else
            {
                SetDisplay.Invoke(__instance, new object[] { BossSequenceDoor.Completion.None });
                if (__instance.lockSet != null)
                {
                    __instance.lockSet.SetActive(true);
                }
                if (__instance.unlockedSet != null)
                {
                    __instance.unlockedSet.SetActive(false);
                }
                if (__instance.lockedUIPrefab != null && LockedUi() == null)
                {
                    GameObject go = Object.Instantiate(__instance.lockedUIPrefab);
                    LockedUi() = go.GetComponent<BossDoorLockUI>();
                    go.SetActive(false);
                }
            }
            if (__instance.dreamReturnGate != null && !__instance.dreamReturnGate.name.EndsWith("_" + __instance.name, StringComparison.Ordinal))
            {
                __instance.dreamReturnGate.name = __instance.dreamReturnGate.name + "_" + __instance.name;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 前作的门 {__instance.name}（{p.Id}）摆放出错 / hk1 door setup failed: {e}");
        }
        return false;
    }
}

/// <summary>门的完成情况不写 PlayerData（丝之歌没有那些字段；我们的记录在结算时写）。</summary>
[HarmonyPatch(typeof(BossSequenceDoor), "SaveState")]
internal static class DoorSaveStatePatch
{
    private static bool Prefix(BossSequenceDoor __instance) => Hk1Doors.DefOf(__instance) == null;
}

/// <summary>门前的挑战界面：名字、说明、束缚图标换成本作的。</summary>
[HarmonyPatch(typeof(BossDoorChallengeUI), nameof(BossDoorChallengeUI.Setup))]
internal static class DoorChallengeSetupPatch
{
    private static bool logged;

    private static void Postfix(BossDoorChallengeUI __instance, BossSequenceDoor door)
    {
        PantheonDef? p = Hk1Doors.DefOf(door);
        if (p == null)
        {
            return;
        }
        try
        {
            Hk1Doors.DressChallengeUI(__instance, p);
            if (!logged)
            {
                logged = true;
                GodhomePlugin.Log.LogInfo("[Godhome] 前作门前的挑战界面上的字 / hk1 door challenge texts: "
                    + string.Join(" | ", __instance.GetComponentsInChildren<Text>(true).Select(t => $"{t.name}='{t.text}'")));
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作门前的挑战界面换字出错 / hk1 door panel dressing failed: {e.Message}");
        }
    }
}

/// <summary>
/// 门前“开始”（前作 BossDoorChallengeUI.Begin）：不调前作的 BossSequenceController（它的束缚会改丝之歌的血量上限），
/// 记下选的束缚，收起界面，照前作发 OnBegin（FSM 放开门动画，最后发起转场，ModeController.FilterTransition 接过去开始万神殿）。
/// </summary>
[HarmonyPatch(typeof(BossDoorChallengeUI), nameof(BossDoorChallengeUI.Begin))]
internal static class DoorBeginPatch
{
    private static readonly AccessTools.FieldRef<BossDoorChallengeUI, BossSequenceDoor> DoorField = AccessTools.FieldRefAccess<BossDoorChallengeUI, BossSequenceDoor>("door");
    private static readonly AccessTools.FieldRef<BossDoorChallengeUI, BossDoorChallengeUI.BeginEvent> BeginField = AccessTools.FieldRefAccess<BossDoorChallengeUI, BossDoorChallengeUI.BeginEvent>("OnBegin");
    private static readonly System.Reflection.MethodInfo HideSequence = AccessTools.Method(typeof(BossDoorChallengeUI), "HideSequence");

    private static bool Prefix(BossDoorChallengeUI __instance)
    {
        PantheonDef? p = Hk1Doors.DefOf(DoorField(__instance));
        if (p == null)
        {
            return true;
        }
        Binding b = Binding.None;
        if (__instance.boundNailButton != null && __instance.boundNailButton.Selected)
        {
            b |= Binding.Needle;
        }
        if (__instance.boundHeartButton != null && __instance.boundHeartButton.Selected)
        {
            b |= Binding.Mask;
        }
        if (__instance.boundCharmsButton != null && __instance.boundCharmsButton.Selected)
        {
            b |= Binding.Tools;
        }
        if (__instance.boundSoulButton != null && __instance.boundSoulButton.Selected)
        {
            b |= Binding.Silk;
        }
        __instance.StartCoroutine((IEnumerator)HideSequence.Invoke(__instance, new object[] { false }));
        Hk1Doors.Begin(p, b);
        BeginField(__instance)?.Invoke();
        return false;
    }
}

/// <summary>
/// 第四扇门前作关在笼子里（读前作的 PlayerData）：开不开由我们在场景加载时定（Hk1Doors.Prepare，按 pantheons.json 的 requires），
/// 笼子自己的 Start 不跑（在 Start 里关自己会报“正在激活”）。
/// </summary>
[HarmonyPatch(typeof(BossDoorCage), "Start")]
internal static class DoorCageStartPatch
{
    private static bool Prefix(BossDoorCage __instance) => !(Hk1Lobby.Active && Hk1Lobby.IsHub(__instance.gameObject.scene.name));
}

[HarmonyPatch(typeof(BossDoorCage), "Unlock")]
internal static class DoorCageUnlockPatch
{
    private static bool Prefix(BossDoorCage __instance) => !(Hk1Lobby.Active && Hk1Lobby.IsHub(__instance.gameObject.scene.name));
}

/// <summary>束缚按钮选中、取消、全选后前作会把图标设回原始尺寸（SetNativeSize）：再放回前作图标的框里。</summary>
[HarmonyPatch(typeof(BossDoorChallengeUIBindingButton))]
internal static class BindingIconSizePatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(BossDoorChallengeUIBindingButton.Reset))]
    private static void AfterReset(BossDoorChallengeUIBindingButton __instance) => Hk1Doors.FitIcon(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(BossDoorChallengeUIBindingButton.OnSubmit))]
    private static void AfterSubmit(BossDoorChallengeUIBindingButton __instance) => Hk1Doors.FitIcon(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(BossDoorChallengeUIBindingButton.SetAllSelected))]
    private static void AfterAll(BossDoorChallengeUIBindingButton __instance) => Hk1Doors.FitIcon(__instance);
}

/// <summary>锁着的门前的提示（前作显示这扇门里每个 Boss 的头像）：换成要先完成的万神殿的门徽，完成的点亮。</summary>
[HarmonyPatch(typeof(BossDoorLockUI), nameof(BossDoorLockUI.Show))]
internal static class DoorLockUiPatch
{
    private static readonly AccessTools.FieldRef<BossDoorLockUI, BossDoorLockUIIcon[]> IconsField = AccessTools.FieldRefAccess<BossDoorLockUI, BossDoorLockUIIcon[]>("bossIcons");

    private static void Postfix(BossDoorLockUI __instance, BossSequenceDoor door)
    {
        PantheonDef? p = Hk1Doors.DefOf(door);
        if (p == null)
        {
            return;
        }
        try
        {
            BossDoorLockUIIcon[] icons = IconsField(__instance) ?? Array.Empty<BossDoorLockUIIcon>();
            GodhomeSaveData rec = GodhomePlugin.Instance.Records;
            for (int i = 0; i < icons.Length; i++)
            {
                BossDoorLockUIIcon ic = icons[i];
                if (ic == null)
                {
                    continue;
                }
                ic.StopAllCoroutines();
                PantheonDef? need = i < p.Requires.Length ? PantheonCatalog.Find(p.Requires[i]) : null;
                ic.gameObject.SetActive(need != null);
                if (need == null)
                {
                    continue;
                }
                Sprite? sp = string.IsNullOrEmpty(need.Emblem) ? null : SanctumArt.Get("achievement_icons_hornet", need.Emblem);
                if (ic.bossIcon != null)
                {
                    ic.bossIcon.enabled = sp != null;
                    ic.bossIcon.sprite = sp;
                    ic.bossIcon.preserveAspect = true;
                }
                ic.SetUnlocked(rec.PantheonDone(need.Id));
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作锁门提示换图出错 / hk1 door lock ui failed: {e.Message}");
        }
    }
}
