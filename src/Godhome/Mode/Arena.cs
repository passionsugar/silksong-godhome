using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 神居场地（第三版，见 docs/arena.md）：不新建地图，每次加载的原 Boss 场景就是一份新副本；
/// 在 SceneManager.sceneLoaded（物体 Awake 已跑、Start 没跑、人没放进来、画面全黑）时用前作神居的手段把它改成神居版：
/// 入场门 door_dreamEnter、场地控制器（游戏自己的 BossSceneController）、堵出口、关交互物体、按表给 FSM 打补丁、
/// 特殊结局插 EndGGBossScene。运行时不猜任何东西：门在哪、等哪个 FSM、怎么结束都写在 data/bosses.json 的 arena 段。
/// </summary>
internal static class Arena
{
    /// <summary>入场门的名字（前作 BossChallengeUI.LoadBoss 用的就是它）。名字带 door 就是门型入口（TransitionPoint.cs:452）。</summary>
    internal const string EnterGate = "door_dreamEnter";

    /// <summary>回程门：GameManager.EnterHero 把它换成 playerData.bossReturnEntryGate（GameManager.cs:3313）。</summary>
    internal const string ReturnGate = "door_dreamReturn";

    /// <summary>Boss 都倒下后等多久回程（BossSceneController.bossesDeadWaitTime）。要短：倒下后的剧情、对话来不及开始就离开。</summary>
    internal const float BossesDeadWait = 1.5f;

    /// <summary>附加场景 Boss：人进场后最多等多久 Boss 场景加载（建好场地控制器）。</summary>
    private const float ControllerWait = 12f;

    private const int TerrainLayer = 8;
    private const string ControllerName = "Godhome Boss Scene Controller";

    private static BossDef? boss;
    private static int tier;
    private static int serial;
    private static Action<string>? onBossesDead;
    private static Action? onComplete;

    /// <summary>这一场已经处理过的场景（按场景句柄，同名的新旧两份场景不会混）。</summary>
    private static readonly HashSet<int> prepared = new();

    /// <summary>准备过的场景（重打失败、放掉占用后在这些场景里再找，见 RetryElsewhere）。</summary>
    private static readonly List<Scene> preparedList = new();

    /// <summary>准备过的场景名（判胜检查据此知道这一场的场景是不是都来了）。</summary>
    private static readonly HashSet<string> preparedNames = new();

    /// <summary>
    /// 补丁、结束动作“已占用”：打上了，或者在等模板 FSM 醒来重打。只在这两种情况占用（Codex 审查 0.9.0 第二轮 #3）：
    /// 先加载的场景里有同路径、却不是目标的物体（没有这个 FSM），后加载的场景照样能找到真正的目标。
    /// </summary>
    private static readonly HashSet<int> patchDone = new();
    private static readonly HashSet<int> wavesDone = new();
    private static readonly HashSet<int> endDone = new();

    /// <summary>找到了物体却没打上 / 没插上的（最后一次的原因），所有场景都来了以后由 ReportMissing 报。</summary>
    private static readonly Dictionary<int, string> patchFail = new();
    private static readonly Dictionary<int, string> endFail = new();

    /// <summary>结束动作确实插上了的（最近一次插成功；模板 FSM 醒来重插失败会移掉）。</summary>
    private static readonly HashSet<int> endOk = new();

    /// <summary>
    /// 结束动作在还没醒的模板 FSM 上（这一场加载期间还没 Awake，醒来会按模板重建），等它醒来（OnFsmAwake）再插；
    /// 加载时已经插上的也记在这里，醒来重插成功才算数。记下 FSM：等的东西被销毁了就不算在等（Codex 审查 0.9.0 第二轮 #5、第三轮 #2）。
    /// </summary>
    private static readonly Dictionary<int, PlayMakerFSM> endWaiting = new();

    /// <summary>开战判定写了 via、离开等待状态的事件对不上的次数（日志只记前几次）。</summary>
    private static int wrongVia;

    /// <summary>表里 bossObjects 有、场景里没找到 HealthManager 的名字（双 Boss 只找到一个时就只登记了一个）。</summary>
    private static string bossesMissing = "";

    /// <summary>进场后没暂停的时间（Watch 每帧累加；暂停时 ModeController.Update 不调 Watch）。</summary>
    private static float sinceEntered;

    /// <summary>这一场赢不了（判胜装不上）：Watch 报一次，调用方回大厅。</summary>
    private static string? fatal;
    private static bool fatalReported;

    /// <summary>准备过的场景数：进场后附加场景晚到时，FsmEnters 据此重新收集 FSM。</summary>
    private static int preparedScenes;

    internal static TransitionPoint? Door { get; private set; }
    internal static BossSceneController? Controller { get; private set; }

    /// <summary>人从入场门进来后站在哪（日志、自检用）。</summary>
    internal static Vector2? Landed { get; private set; }

    /// <summary>人是从哪个入口进来的（应该是 door_dreamEnter）。</summary>
    internal static string EnteredVia { get; private set; } = "";
    internal static int ExitsClosed { get; private set; }
    internal static int WallsBuilt { get; private set; }
    internal static int InteractablesOff { get; private set; }
    internal static int BossesRegistered { get; private set; }

    /// <summary>加载时准备出错（门、控制器、补丁抛异常）。</summary>
    internal static string Error { get; private set; } = "";

    /// <summary>开战判定（arena.wake）：等的 FSM 在等待状态里待过、又离开了，或者 BattleScene 开战了。</summary>
    internal static bool Started { get; private set; }
    internal static string StartNote { get; private set; } = "";
    private static bool wakeSeen;
    private static bool wakeLogged;
    private static bool wakeLeft;
    private static string wakeLeftFrom = "";

    /// <summary>离开等待状态的那次跳转的事件名（Fsm.LastTransition，DoTransition 在切状态前设好）；不是经事件跳走的是空的。</summary>
    private static string wakeLeftVia = "";
    private static string wakeLast = "";
    private static PlayMakerFSM? wakeFsm;
    private static BattleScene? wakeBattle;
    private static float enteredAt = -1f;
    private static bool missingReported;
    private static bool controllerLate;

    internal static int PatchCount => boss?.Arena.Patch.Count ?? 0;
    internal static int PatchesApplied => patchDone.Count;
    internal static int SkipWaveCount => boss?.Arena.SkipWaves.Length ?? 0;
    internal static int WavesSkipped => wavesDone.Count;
    internal static int EndCount => boss?.Arena.End.Count ?? 0;
    internal static int EndsInserted => endOk.Count;

    /// <summary>门和控制器都建好了。</summary>
    internal static bool Ready => Door != null && Controller != null;

    /// <summary>日志、自检报告里的一行说明。</summary>
    internal static string Summary =>
        $"门 / door {(Door != null ? Fmt(Door.transform.position) : "-")} → 站 / landed {(Landed is Vector2 l ? Fmt(l) : "-")} via {(EnteredVia.Length > 0 ? EnteredVia : "-")}；"
        + $"出口 / exits {ExitsClosed}（墙 / walls {WallsBuilt}）、交互 / interactables {InteractablesOff}、补丁 / patches {PatchesApplied}/{PatchCount}、"
        + (SkipWaveCount > 0 ? $"跳过波次 / skipped waves {WavesSkipped}/{SkipWaveCount}、" : "")
        + $"结束动作 / end {EndsInserted}/{EndCount}、Boss 血量 / bosses {BossesRegistered}"
        + (Error.Length > 0 ? $"；出错 / error: {Error}" : "");

    /// <summary>开一场（BeginBoss）：这一场的状态清零。回调只认这一场（编号对得上才转给 ModeController）。</summary>
    internal static void Begin(BossDef b, int bossLevel, int fightSerial, Action<string> bossesDead, Action complete)
    {
        Clear();
        boss = b;
        tier = bossLevel;
        serial = fightSerial;
        onBossesDead = bossesDead;
        onComplete = complete;
    }

    internal static void Clear()
    {
        boss = null;
        onBossesDead = null;
        onComplete = null;
        prepared.Clear();
        preparedList.Clear();
        preparedNames.Clear();
        awoken.Clear();
        patchDone.Clear();
        wavesDone.Clear();
        endDone.Clear();
        patchFail.Clear();
        endFail.Clear();
        endOk.Clear();
        endWaiting.Clear();
        wrongVia = 0;
        bossesMissing = "";
        sinceEntered = 0f;
        fatal = null;
        fatalReported = false;
        preparedScenes = 0;
        rewake.Clear();
        Door = null;
        Controller = null;
        Landed = null;
        EnteredVia = "";
        ExitsClosed = 0;
        WallsBuilt = 0;
        InteractablesOff = 0;
        BossesRegistered = 0;
        Error = "";
        Started = false;
        StartNote = "";
        wakeSeen = false;
        wakeLogged = false;
        wakeLeft = false;
        wakeLeftFrom = "";
        wakeLeftVia = "";
        wakeLeftSeq = 0;
        wakeLast = "";
        wakeFsm = null;
        wakeBattle = null;
        enteredAt = -1f;
        missingReported = false;
        controllerLate = false;
    }

    // ───────────────────────────── 加载时 ─────────────────────────────

    /// <summary>
    /// 这场战斗的一个场景刚加载完（sceneLoaded）。入口场景建门；每个场景堵出口、关交互、打补丁；Boss 所在场景建控制器。
    /// 同一个场景实例只做一次。
    /// </summary>
    internal static void Prepare(Scene scene)
    {
        BossDef? b = boss;
        if (b == null || !prepared.Add(scene.handle))
        {
            return;
        }
        preparedScenes++;
        preparedList.Add(scene);
        preparedNames.Add(scene.name);
        GameObject[] roots = scene.GetRootGameObjects();
        var did = new List<string>();
        void Step(string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Error += (Error.Length > 0 ? "; " : "") + what + ": " + e.Message;
                GodhomePlugin.Log.LogError($"[Godhome] 场地准备出错 / arena prepare error ({b.Id}, {scene.name}, {what}): {e}");
            }
        }
        if (scene.name == b.EntryScene && Door == null)
        {
            Step("door", () => BuildDoor(b, scene));
        }
        Step("exits", () => CloseExits(scene, roots));
        Step("interactables", () => ClearInteractables(b, roots));
        Step("waves", () => SkipWaves(b, scene, roots));
        Step("patches", () => ApplyPatches(b, scene, roots));
        Step("end", () => InsertEnds(b, scene, roots));
        Step("wake", () => ResolveWake(b, roots));
        if (scene.name == b.Scene && Controller == null)
        {
            Step("controller", () => BuildController(b, scene, roots));
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 场地 / arena {b.Id} @ {scene.name}（加载时 / at load）：{Summary}");
    }

    /// <summary>
    /// 入场门：门型 TransitionPoint（isADoor、不能交互、没有碰撞体），子物体是危险复活点。
    /// 先建不激活的物体、填好字段再激活：TransitionPoint.Awake 按字段决定能不能交互，并把自己登记进 TransitionPoints。
    /// </summary>
    private static void BuildDoor(BossDef b, Scene scene)
    {
        if (b.Arena.Door is not Vector2 at)
        {
            throw new InvalidOperationException("arena.door missing");
        }
        var go = new GameObject(EnterGate);
        go.SetActive(false);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        var marker = new GameObject("Hazard Respawn Marker");
        marker.transform.SetParent(go.transform, false);
        HazardRespawnMarker hrm = marker.AddComponent<HazardRespawnMarker>();
        Traverse.Create(hrm).Field("respawnFacingDirection").SetValue(
            b.Arena.Face > 0 ? HazardRespawnMarker.FacingDirection.Right
            : b.Arena.Face < 0 ? HazardRespawnMarker.FacingDirection.Left
            : HazardRespawnMarker.FacingDirection.None);
        TransitionPoint tp = go.AddComponent<TransitionPoint>();
        tp.isADoor = true;
        tp.isInactive = true;
        tp.dontWalkOutOfDoor = true;
        tp.alwaysEnterRight = b.Arena.Face > 0;
        tp.alwaysEnterLeft = b.Arena.Face < 0;
        tp.nonHazardGate = false;
        tp.respawnMarker = hrm;
        tp.targetScene = "";
        tp.entryPoint = "";
        go.SetActive(true);
        Door = tp;
    }

    /// <summary>
    /// 原有的出口：门型的设成不能交互；边上的关掉触发碰撞体（走进去不再切场景），在原位放一块实心空气墙。
    /// 没激活的出口只关碰撞体（量不到位置，也走不进去）。
    /// </summary>
    private static void CloseExits(Scene scene, GameObject[] roots)
    {
        foreach (GameObject r in roots)
        {
            foreach (TransitionPoint tp in r.GetComponentsInChildren<TransitionPoint>(true))
            {
                if (tp == null || tp == Door)
                {
                    continue;
                }
                ExitsClosed++;
                if (tp.isADoor)
                {
                    tp.SetIsInactive(true);
                    continue;
                }
                bool measured = false;
                Bounds bounds = default;
                foreach (Collider2D c in tp.GetComponents<Collider2D>())
                {
                    if (c == null || !c.enabled)
                    {
                        continue;
                    }
                    if (tp.gameObject.activeInHierarchy)
                    {
                        if (!measured)
                        {
                            bounds = c.bounds;
                            measured = true;
                        }
                        else
                        {
                            bounds.Encapsulate(c.bounds);
                        }
                    }
                    c.enabled = false;
                }
                if (measured)
                {
                    var wall = new GameObject("Godhome Wall " + tp.name) { layer = TerrainLayer };
                    SceneManager.MoveGameObjectToScene(wall, scene);
                    wall.transform.position = new Vector3(bounds.center.x, bounds.center.y, 0f);
                    BoxCollider2D box = wall.AddComponent<BoxCollider2D>();
                    box.size = new Vector2(Mathf.Max(1f, bounds.size.x), Mathf.Max(1f, bounds.size.y));
                    WallsBuilt++;
                }
            }
        }
    }

    /// <summary>交互物体（长椅、NPC、记忆入口、拉杆……）不能交互。Boss 自己和它下面的、表里 keep 的不动；出口在 CloseExits 里处理。</summary>
    private static void ClearInteractables(BossDef b, GameObject[] roots)
    {
        var bosses = new List<Transform>();
        foreach (GameObject r in roots)
        {
            foreach (Transform t in r.GetComponentsInChildren<Transform>(true))
            {
                if (b.BossObjects.Contains(CleanName(t.name)))
                {
                    bosses.Add(t);
                }
            }
        }
        foreach (GameObject r in roots)
        {
            foreach (InteractableBase ib in r.GetComponentsInChildren<InteractableBase>(true))
            {
                if (ib == null || ib is TransitionPoint || ib.IsDisabled)
                {
                    continue;
                }
                if (bosses.Any(t => ib.transform == t || ib.transform.IsChildOf(t)))
                {
                    continue;
                }
                string path = SceneTools.PathOf(ib.transform);
                if (b.Arena.Keep.Any(k => path == k || path.StartsWith(k + "/", StringComparison.Ordinal)))
                {
                    continue;
                }
                ib.Deactivate(false);
                InteractablesOff++;
            }
        }
    }

    /// <summary>
    /// 跳过 Boss 前的小怪波次（arena.skipWaves）：从所属 BattleScene 的 waves 表里拿掉，波里的敌人不激活。
    /// BattleScene 开战从 waves[0] 开始、清完一波按表开下一波，数剩下的敌人也只数表里的波（BattleScene.cs：
    /// DoStartBattle → StartWave(0)、WaveEnd、DoCheckEnemiesNew），所以拿掉以后开战第一波就是 Boss 那一波。
    /// 波次物体本身不删、也不靠关它：场景里别的组件还引用它（例如卢戈利的 ActivateIfPlayerdataFalse 会在 Start 里重新激活它），
    /// 只把它下面的敌人关掉（原版里它们也只有开这一波时才会被激活）。路径不在这个场景里的留到它所在的场景加载时。
    /// </summary>
    private static void SkipWaves(BossDef b, Scene scene, GameObject[] roots)
    {
        for (int i = 0; i < b.Arena.SkipWaves.Length; i++)
        {
            if (wavesDone.Contains(i))
            {
                continue;
            }
            string path = b.Arena.SkipWaves[i];
            GameObject? go = FindIn(roots, path);
            if (go == null)
            {
                continue;
            }
            wavesDone.Add(i);
            string result = SkipWave(b, go, roots);
            if (Failed(result))
            {
                NoteError($"skip wave {path}: {result}");
            }
            GodhomePlugin.Log.LogInfo($"[Godhome]   跳过波次 / skip wave {path} @ {scene.name}: {result}");
        }
    }

    private static string SkipWave(BossDef b, GameObject go, GameObject[] roots)
    {
        BattleWave w = go.GetComponent<BattleWave>();
        if (w == null)
        {
            return Bad($"{go.name} 不是 BattleWave / not a BattleWave");
        }
        BattleScene? bs = roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<BattleScene>(true))
            .FirstOrDefault(s => s.waves != null && s.waves.Contains(w));
        if (bs == null)
        {
            return Bad($"{go.name} 不在任何 BattleScene 的波次表里 / not in any BattleScene's waves");
        }
        if (go.GetComponentsInChildren<Transform>(true).Any(t => b.BossObjects.Contains(CleanName(t.name))))
        {
            return Bad($"{go.name} 里有 Boss，不能跳过 / holds the boss; not skipped");
        }
        if (bs.waves.Count(x => x != w) == 0)
        {
            return Bad($"拿掉 {go.name} 就没有波次了 / it is the last wave; not skipped");
        }
        bs.waves.Remove(w);
        int off = 0;
        foreach (Transform c in go.transform)
        {
            if (c.gameObject.activeSelf)
            {
                c.gameObject.SetActive(false);
                off++;
            }
        }
        go.SetActive(false);
        return $"已拿掉 / removed（{SceneTools.PathOf(bs.transform)} 剩 / left {bs.waves.Count} 波 / waves，关掉 / disabled {off} 个物体 / objects）";
    }

    /// <summary>
    /// 按表打补丁（FSM 还没开始跑）。路径不在这个场景里的留到它所在的场景加载时；找到了却没打上的也留着（见 patchDone），
    /// 原因记在 patchFail。每一项单独兜住，一项抛异常不影响后面的。
    /// </summary>
    private static void ApplyPatches(BossDef b, Scene scene, GameObject[] roots)
    {
        for (int i = 0; i < b.Arena.Patch.Count; i++)
        {
            if (!patchDone.Contains(i))
            {
                TryPatch(b, i, scene, roots, null);
            }
        }
    }

    /// <summary>在这个场景里找第 i 条补丁的物体并打上；skip 是已经试过、不算的那个物体（重打失败后再找时用）。</summary>
    private static void TryPatch(BossDef b, int i, Scene scene, GameObject[] roots, GameObject? skip)
    {
        ArenaPatch p = b.Arena.Patch[i];
        try
        {
            GameObject? go = FindIn(roots, p.Path);
            if (go == null || go == skip)
            {
                return;
            }
            string result = ApplyPatch(p, go);
            PlayMakerFSM? pm = p.Type is "activate" or "deactivate" ? null : FsmOn(go, p.Fsm);
            bool rewakes = pm != null && WillRewake(pm);
            bool failed = Failed(result);
            GodhomePlugin.Log.LogInfo($"[Godhome]   补丁 / patch {p} @ {scene.name}: {result}{(failed && rewakes ? "（模板 FSM 醒来后重打 / re-applied when the template FSM wakes）" : "")}");
            if (failed && !rewakes)
            {
                patchFail[i] = $"{scene.name}: {result}";
                return;
            }
            patchDone.Add(i);
            patchFail.Remove(i);
            if (pm != null && pm.UsesTemplate)
            {
                Rewake(pm).Patches.Add(i);
            }
        }
        catch (Exception e)
        {
            patchFail[i] = $"{scene.name}: {Bad(e.Message)}";
            GodhomePlugin.Log.LogError($"[Godhome]   补丁出错 / patch error {p} @ {scene.name}: {e}");
        }
    }

    /// <summary>
    /// 这一场加载期间已经 Awake 过的 PlayMakerFSM（实例号，PlayMakerFSM.Awake 后置记下）。
    /// 不能看物体激活没有：Awake 过的物体可能在 sceneLoaded 前又被别的脚本关掉（Codex 审查 0.9.0 第三轮 #2）。
    /// </summary>
    private static readonly HashSet<int> awoken = new();

    /// <summary>
    /// 模板 FSM 之后还会醒来按模板重建一份（这一场加载期间还没 Awake 过），加载时打的会丢，醒来后由 OnFsmAwake 重打。
    /// 已经 Awake 过的不会再重建。
    /// </summary>
    private static bool WillRewake(PlayMakerFSM pm) => pm.UsesTemplate && !awoken.Contains(pm.GetInstanceID());

    /// <summary>
    /// 加载时改过的模板 FSM：醒来（Awake）后要重打的补丁、结束动作序号。
    /// PlayMakerFSM 反序列化时就 InitData（OnAfterDeserialize），所以加载时 Initialized 已经是真的、补丁也打得上；
    /// 但物体第一次激活时 Awake → Init → InitTemplate 按模板新建一份 Fsm（反编译 PlayMakerFSM.cs），加载时改的全丢。
    /// 没激活的模板 FSM 醒来后由 OnFsmAwake 补打一次；加载时已经醒了的不会再 Awake，原来打的就留着。
    /// </summary>
    private static readonly Dictionary<PlayMakerFSM, RewakeList> rewake = new();

    private sealed class RewakeList
    {
        internal readonly List<int> Patches = new();
        internal readonly List<int> Ends = new();
        internal bool Wake;
    }

    private static RewakeList Rewake(PlayMakerFSM pm)
    {
        if (!rewake.TryGetValue(pm, out RewakeList? r))
        {
            rewake[pm] = r = new RewakeList();
        }
        return r;
    }

    /// <summary>
    /// PlayMakerFSM.Awake 后置（ModePatches.FsmAwakePatch）：记下这一场加载期间醒过的 FSM；加载时改过的模板 FSM 醒了，重打一次。
    /// 重打失败的放掉占用，在这一场已经准备过的别的场景里再找同路径的目标（Codex 审查 0.9.0 第三轮 #1）。
    /// </summary>
    internal static void OnFsmAwake(PlayMakerFSM pm)
    {
        BossDef? b = boss;
        if (b == null)
        {
            return;
        }
        awoken.Add(pm.GetInstanceID());
        if (rewake.Count == 0 || !rewake.TryGetValue(pm, out RewakeList? r))
        {
            return;
        }
        rewake.Remove(pm);
        GameObject self = pm.gameObject;
        string where = $"{self.scene.name}（模板 FSM 醒来后 / after the template FSM woke）";
        // 每一项单独兜住（Codex 审查 0.9.0 #11）：一个补丁抛异常，后面的结束动作、开战判定标记照样重插。
        string Each(string what, Func<string> a)
        {
            string result;
            try
            {
                result = a();
            }
            catch (Exception e)
            {
                result = Bad(e.Message);
                GodhomePlugin.Log.LogError($"[Godhome]   模板 FSM 醒了，重打出错 / template FSM re-apply error ({b.Id}, {what}): {e}");
            }
            GodhomePlugin.Log.LogInfo($"[Godhome]   模板 FSM 醒了，重打 / template FSM woke, re-applied {what}: {result}");
            return result;
        }
        foreach (int i in r.Patches)
        {
            ArenaPatch p = b.Arena.Patch[i];
            string res = Each($"补丁 / patch {p}", () => ApplyPatch(p, self));
            if (Failed(res))
            {
                patchDone.Remove(i);
                patchFail[i] = $"{where}: {res}";
                RetryElsewhere(b, i, end: false, self);
                if (!patchDone.Contains(i))
                {
                    NoteError($"patch {p}: {patchFail[i]}");
                }
            }
        }
        foreach (int i in r.Ends)
        {
            FsmRef e = b.Arena.End[i];
            endWaiting.Remove(i);
            endOk.Remove(i);
            string res = Each($"结束动作 / end action {e}", () =>
            {
                InsertEnd(e, self, out string result);
                return result;
            });
            if (Failed(res))
            {
                endDone.Remove(i);
                endFail[i] = $"{where}: {res}";
                RetryElsewhere(b, i, end: true, self);
                if (!endDone.Contains(i))
                {
                    NoteError($"end {e}: {endFail[i]}");
                }
            }
            else
            {
                endOk.Add(i);
                endFail.Remove(i);
            }
        }
        if (r.Wake && b.Arena.Wake != null)
        {
            string res = Each($"开战判定标记 / wake mark {b.Arena.Wake}", () => InsertWakeMarks(pm, b.Arena.Wake.State));
            if (Failed(res))
            {
                NoteError($"wake mark {b.Arena.Wake}: {res}");
            }
        }
    }

    /// <summary>放掉占用的第 i 项（补丁或结束动作）在这一场已经准备过、还在的场景里再找一次（跳过刚失败的那个物体）。</summary>
    private static void RetryElsewhere(BossDef b, int i, bool end, GameObject skip)
    {
        foreach (Scene sc in preparedList)
        {
            if ((end ? endDone : patchDone).Contains(i))
            {
                return;
            }
            if (!sc.IsValid() || !sc.isLoaded)
            {
                continue;
            }
            GameObject[] roots = sc.GetRootGameObjects();
            if (end)
            {
                TryEnd(b, i, sc, roots, skip);
            }
            else
            {
                TryPatch(b, i, sc, roots, skip);
            }
        }
    }

    private static string ApplyPatch(ArenaPatch p, GameObject go)
    {
        switch (p.Type)
        {
            case "activate":
            case "deactivate":
                // 成功的标准是物体自己的开关（activeSelf，和游戏自己的 ActivateGameObject 一样）：上层没开时它跟着上层一起出现，
                // 这一项算打上了，只在结果里注明（Codex 审查 0.9.0 第三轮 #11）。
                bool want = p.Type == "activate";
                go.SetActive(want);
                if (go.activeSelf != want)
                {
                    return Bad($"开关没改成 / activeSelf is still {go.activeSelf}");
                }
                return go.activeInHierarchy == want ? "ok" : $"ok（自己开了，上层没开，跟着上层出现 / activeSelf set; a parent is off: {SceneTools.Describe(go)}）";
        }
        PlayMakerFSM? pm = FsmOn(go, p.Fsm);
        if (pm == null)
        {
            return Bad($"物体上没有 FSM / no FSM '{p.Fsm}' (has: {string.Join(", ", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName))})");
        }
        Fsm f = pm.Fsm;
        if (pm.FsmTemplate != null && !f.Initialized)
        {
            // 平常反序列化时已经 InitData 过（PlayMakerFSM.OnAfterDeserialize）；没有就是 PlayMaker 还没准备好，不碰。
            // 还没醒的模板 FSM 醒来后会按模板重建，由 OnFsmAwake 重打。
            return Bad("模板 FSM 还没初始化，补丁会被丢掉 / template FSM not initialised yet; the patch would be lost");
        }
        if (f.Started)
        {
            return Bad($"FSM 已经开始运行 / FSM already started (state '{pm.ActiveStateName}')");
        }
        switch (p.Type)
        {
            case "start":
                if (f.GetState(p.State) == null)
                {
                    return Bad($"没有状态 / no state '{p.State}'");
                }
                // 起始状态：OnEnable 已经按旧的起始状态设了 ActiveState（Fsm.OnEnable），Start 时直接进 ActiveState，两个都改。
                f.StartState = p.State;
                if (f.ActiveState != null)
                {
                    AccessTools.Property(typeof(Fsm), nameof(Fsm.ActiveState)).SetValue(f, f.GetState(p.State));
                }
                return "ok";
            case "goto":
            {
                if (!f.Initialized)
                {
                    f.InitData();
                }
                FsmState? to = f.GetState(p.To);
                if (to == null)
                {
                    return Bad($"没有状态 / no state '{p.To}'");
                }
                FsmTransition? t;
                if (p.From == "*")
                {
                    t = f.GlobalTransitions.FirstOrDefault(x => x.EventName == p.Event);
                }
                else
                {
                    FsmState? from = f.GetState(p.From);
                    if (from == null)
                    {
                        return Bad($"没有状态 / no state '{p.From}'");
                    }
                    t = from.Transitions.FirstOrDefault(x => x.EventName == p.Event);
                    if (t == null)
                    {
                        // 原来没有这个事件的跳转：加一条（前作的神居分支也是在原状态上多一个出口）。
                        t = new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent(p.Event) };
                        from.Transitions = from.Transitions.Concat(new[] { t }).ToArray();
                    }
                }
                if (t == null)
                {
                    return Bad($"没有全局跳转 / no global transition '{p.Event}'");
                }
                t.ToState = p.To;
                t.ToFsmState = to;
                return "ok";
            }
            case "var":
            {
                FsmVariables v = f.Variables;
                switch (p.ValueType)
                {
                    case "bool" when v.FindFsmBool(p.Name) is FsmBool fb:
                        fb.Value = p.Value!.ToObject<bool>();
                        return "ok";
                    case "int" when v.FindFsmInt(p.Name) is FsmInt fi:
                        fi.Value = p.Value!.ToObject<int>();
                        return "ok";
                    case "float" when v.FindFsmFloat(p.Name) is FsmFloat ff:
                        ff.Value = p.Value!.ToObject<float>();
                        return "ok";
                    case "string" when v.FindFsmString(p.Name) is FsmString fs:
                        fs.Value = p.Value!.ToObject<string>();
                        return "ok";
                }
                return Bad($"没有 {p.ValueType} 变量 / no {p.ValueType} variable '{p.Name}'");
            }
        }
        return Bad($"不认识的补丁 / unknown patch type '{p.Type}'");
    }

    /// <summary>特殊结局（arena.end）：在这个状态最前面插游戏自己的 EndGGBossScene（前作特殊 Boss 就是这么宣布结束的）。</summary>
    /// <remarks>
    /// 插上了、或者在等模板 FSM 醒来才占用这一项（同 ApplyPatches）；找到物体却插不上的原因记在 endFail，后加载的场景还会再找。
    /// 每一项单独兜住（Codex 审查 0.9.0 第二轮 #3）。
    /// </remarks>
    private static void InsertEnds(BossDef b, Scene scene, GameObject[] roots)
    {
        for (int i = 0; i < b.Arena.End.Count; i++)
        {
            if (!endDone.Contains(i))
            {
                TryEnd(b, i, scene, roots, null);
            }
        }
    }

    /// <summary>
    /// 在这个场景里找第 i 个结束点并插上 EndGGBossScene；skip 同 TryPatch。
    /// 还会醒来重建的模板 FSM（插上了也一样）记进 endWaiting：醒来前被销毁，判胜检查就知道这一项没了（Codex 审查 0.9.0 第三轮 #2）。
    /// </summary>
    private static void TryEnd(BossDef b, int i, Scene scene, GameObject[] roots, GameObject? skip)
    {
        FsmRef e = b.Arena.End[i];
        try
        {
            GameObject? go = FindIn(roots, e.Path);
            if (go == null || go == skip)
            {
                return;
            }
            bool ok = InsertEnd(e, go, out string result);
            PlayMakerFSM? pm = FsmOn(go, e.Fsm);
            bool rewakes = pm != null && WillRewake(pm);
            GodhomePlugin.Log.LogInfo($"[Godhome]   结束动作 / end action {e} @ {scene.name}: {result}{(rewakes ? "（模板 FSM 醒来后重插 / re-inserted when the template FSM wakes）" : "")}");
            if (!ok && !rewakes)
            {
                endFail[i] = $"{scene.name}: {result}";
                return;
            }
            endDone.Add(i);
            endFail.Remove(i);
            if (ok)
            {
                endOk.Add(i);
            }
            if (rewakes)
            {
                endWaiting[i] = pm!;
            }
            if (pm != null && pm.UsesTemplate)
            {
                Rewake(pm).Ends.Add(i);
            }
        }
        catch (Exception ex)
        {
            endFail[i] = $"{scene.name}: {Bad(ex.Message)}";
            GodhomePlugin.Log.LogError($"[Godhome]   结束动作出错 / end action error {e} @ {scene.name}: {ex}");
        }
    }

    /// <summary>插好了（或者最前面已经是启用的 EndGGBossScene）返回 true。</summary>
    private static bool InsertEnd(FsmRef e, GameObject go, out string result)
    {
        PlayMakerFSM? pm = FsmOn(go, e.Fsm);
        if (pm == null)
        {
            result = Bad($"物体上没有 FSM / no FSM '{e.Fsm}'");
            return false;
        }
        Fsm f = pm.Fsm;
        if (pm.FsmTemplate != null && !f.Initialized)
        {
            // 同 ApplyPatch：还没醒的模板 FSM 由 OnFsmAwake 重插。
            result = Bad("模板 FSM 还没初始化，插的动作会被丢掉 / template FSM not initialised yet");
            return false;
        }
        // 动作是 Fsm.InitData 时从数据里读出来的（FsmState.LoadActions）；先读好再插，之后不会再被重读覆盖。
        if (!f.Initialized)
        {
            f.InitData();
        }
        FsmState? st = f.GetState(e.State);
        if (st == null)
        {
            result = Bad($"没有状态 / no state '{e.State}'");
            return false;
        }
        // 只认排在最前面、启用的（Codex 审查 0.9.0 #11）：排在后面的可能在它之前就被别的动作跳走，停用的不执行。
        // 多一个也不会重复判胜：BossSceneController.EndBossScene 自己防重入（endedScene，反编译 BossSceneController.cs）。
        if (st.Actions.Length > 0 && st.Actions[0] is EndGGBossScene first && first.Enabled)
        {
            result = "已经有了 / already there";
            return true;
        }
        // 新动作每次进状态都由 FsmState.ActivateActions 调 Init(this) 再 OnEnter（反编译 FsmState.cs），不用另外初始化。
        st.Actions = new FsmStateAction[] { new EndGGBossScene() }.Concat(st.Actions).ToArray();
        result = "ok";
        return true;
    }

    private static void ResolveWake(BossDef b, GameObject[] roots)
    {
        WakeDef? w = b.Arena.Wake;
        if (w == null || wakeFsm != null || wakeBattle != null)
        {
            return;
        }
        if (w.Battle.Length > 0)
        {
            GameObject? bg = FindIn(roots, w.Battle);
            if (bg != null)
            {
                wakeBattle = bg.GetComponent<BattleScene>();
                if (wakeBattle == null)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome]   开战判定 / wake: '{w.Battle}' 上没有 BattleScene / has no BattleScene");
                }
            }
            return;
        }
        GameObject? go = FindIn(roots, w.Path);
        if (go != null)
        {
            wakeFsm = FsmOn(go, w.Fsm);
            if (wakeFsm == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   开战判定 / wake: '{w.Path}' 上没有 FSM / has no FSM '{w.Fsm}'");
                return;
            }
            string marks = InsertWakeMarks(wakeFsm, w.State);
            if (Failed(marks))
            {
                NoteError($"wake mark {w}: {marks}");
            }
            GodhomePlugin.Log.LogInfo($"[Godhome]   开战判定标记 / wake mark {w}: {marks}");
            if (wakeFsm.UsesTemplate)
            {
                Rewake(wakeFsm).Wake = true;
            }
        }
    }

    /// <summary>
    /// 场地控制器（前作 Boss Scene Controller 预制体）：游戏自己的 BossSceneController，照前作 BossChallengeUI.LoadBoss 用 SetupEvent 设好。
    /// 先建不激活的物体、填公开字段，再激活：Awake 调 SetupEvent，Setup() 给 bosses 的 OnDeath 计数。
    /// </summary>
    private static void BuildController(BossDef b, Scene scene, GameObject[] roots)
    {
        var hms = new List<HealthManager>();
        if (b.Arena.End.Count == 0)
        {
            foreach (GameObject r in roots)
            {
                foreach (HealthManager hm in r.GetComponentsInChildren<HealthManager>(true))
                {
                    if (hm != null && b.BossObjects.Contains(CleanName(hm.gameObject.name)) && !hms.Contains(hm))
                    {
                        hms.Add(hm);
                    }
                }
            }
            if (hms.Count == 0)
            {
                Error += (Error.Length > 0 ? "; " : "") + "no boss HealthManager and no arena.end";
                GodhomePlugin.Log.LogError($"[Godhome] {b.Id}: Boss 物体上没有 HealthManager，表里也没写 arena.end，这一场赢不了 / no boss HealthManager in {scene.name} and no arena.end; the fight cannot end");
            }
            else
            {
                // 表里每个名字都要找到（Codex 审查 0.9.0 #3）：双苔藓之母只找到 A 的话，控制器只登记 A，打死 A 就判赢。
                string[] missing = b.BossObjects.Where(n => !hms.Any(h => CleanName(h.gameObject.name) == n)).ToArray();
                if (missing.Length > 0)
                {
                    bossesMissing = string.Join(", ", missing);
                    Error += (Error.Length > 0 ? "; " : "") + "boss objects missing: " + bossesMissing;
                    GodhomePlugin.Log.LogError($"[Godhome] {b.Id}: 表里的 Boss 物体在 {scene.name} 里没找到 HealthManager，判胜不全 / no HealthManager for boss objects: {bossesMissing}");
                }
            }
            foreach (HealthManager hm in hms.Where(h => h.hasSpecialDeath))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: {SceneTools.PathOf(hm.transform)} 是特殊死亡（不发 OnDeath），要写 arena.end / has a special death (no OnDeath); needs arena.end");
            }
        }
        var go = new GameObject(ControllerName);
        go.SetActive(false);
        SceneManager.MoveGameObjectToScene(go, scene);
        BossSceneController bsc = go.AddComponent<BossSceneController>();
        bsc.doTransitionIn = false;
        bsc.doTransitionOut = false;
        bsc.bossesDeadWaitTime = BossesDeadWait;
        bsc.bosses = hms.ToArray();
        int mine = serial;
        int level = tier;
        BossSceneController.SetupEvent = self =>
        {
            self.BossLevel = level;
            // 本作没有前作的转场预制体：不关的话 Start 报错；OnDestroy 里的 RestoreBindings 会回满血、清丝（我们的束缚自己管）。
            Traverse.Create(self).Field("doTransition").SetValue(false);
            Traverse.Create(self).Field("restoreBindingsOnDestroy").SetValue(false);
            self.OnBossesDead += () =>
            {
                if (mine != serial)
                {
                    return;
                }
                if (bossesMissing.Length > 0)
                {
                    // 表里的 Boss 没找全时控制器只登记了找到的那几个，它们倒下不是赢（Codex 审查 0.9.0 第二轮 #4）：
                    // 切到战斗后、Watch 报错前的那几帧里也不能记胜利。Watch 会按场地出错回大厅。
                    fatal ??= MissingBosses();
                    GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: 登记的 Boss 都倒下了，但表里的没找全，不算胜利 / registered bosses are down but some boss objects were never found; not a win ({bossesMissing})");
                    return;
                }
                onBossesDead?.Invoke(WinHow());
            };
            self.OnBossSceneComplete += () =>
            {
                if (mine == serial)
                {
                    onComplete?.Invoke();
                }
            };
        };
        try
        {
            go.SetActive(true);
        }
        finally
        {
            // Awake 正常时自己清；万一抛异常也不能留给别的场景。
            BossSceneController.SetupEvent = null;
        }
        Controller = bsc;
        BossesRegistered = hms.Count;
    }

    /// <summary>判胜方式：表里写了 end 的，报当时停在哪个结束状态；否则是 bosses（OnDeath 都到了）。</summary>
    private static string WinHow()
    {
        BossDef? b = boss;
        if (b == null || b.Arena.End.Count == 0)
        {
            return "bosses";
        }
        foreach (FsmRef e in b.Arena.End)
        {
            PlayMakerFSM? pm = SceneTools.FindFsm(e.Path, e.Fsm, ModeController.ScenesOf(b));
            if (pm != null && pm.ActiveStateName == e.State)
            {
                return $"end {e}";
            }
        }
        return "end";
    }

    // ───────────────────────────── 进场后 ─────────────────────────────

    /// <summary>人进场了（FinishedEnteringScene）：记下从哪个门、站在哪，执行进场步骤。门没建好返回 false（调用方回大厅）。</summary>
    internal static bool OnHeroEntered(ModeController host)
    {
        BossDef? b = boss;
        HeroController hero = HeroController.SilentInstance;
        if (b == null || hero == null)
        {
            return false;
        }
        EnteredVia = hero.GetEntryGateName();
        Landed = hero.transform.position;
        enteredAt = Time.unscaledTime;
        bool ok = Door != null && EnteredVia == EnterGate;
        string line = $"[Godhome] 进场 / entered {b.Id}：{Summary}";
        if (ok)
        {
            GodhomePlugin.Log.LogInfo(line);
            // 调试：进场几秒内有没有碰到伤害（撞 Boss、掉岩浆），只记日志。
            ArrivalWatch.Begin(host, b, Landed.Value);
        }
        else
        {
            GodhomePlugin.Log.LogError(line + "（没有从入场门进来 / did not come in through door_dreamEnter）");
            return false;
        }
        if (Controller != null)
        {
            ReportMissing(b);
        }
        int mine = serial;
        host.StartCoroutine(Arrive(host, b, () => mine == serial && host.State == LoomState.Fight && host.CurrentBoss == b));
        return true;
    }

    private static readonly MethodInfo? BattleSceneEnter = AccessTools.Method(typeof(BattleScene), "OnTriggerEnter2D");
    private static readonly MethodInfo? TriggerEventEnter = AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Trigger2dEvent), "TriggerEnter2D");

    /// <summary>人就位后看多久 FSM 的“进入”监听（秒）。</summary>
    private const float FsmEnterWatch = 3f;

    /// <summary>
    /// 人就位后：给人所在的开战触发区补发“进入”，再执行进场步骤（arena.enter）。入场门在开战触发区里时，
    /// 加载那一刻的“进入”有两种收不到的情况，人不出去就不会再有下一次：
    /// 1. BattleScene.OnTriggerEnter2D 只认 isHeroInPosition 为真的人（反编译 BattleScene.cs）。等人就位、过一个物理帧，
    ///    对和人重叠、属于本场地的 BattleScene 触发区（启用的）补发一次。
    /// 2. PlayMaker 的 Trigger2dEvent 只在“等进入”的那个状态里才挂上监听（OnEnter 里 AddCallback，反编译 Trigger2dEvent.cs），
    ///    FSM 常常要等人就位（WaitForHeroInPosition）才走到这个状态，比如第四圣咏团的 Remeet Ready。见 FsmEnters。
    /// 不挑位置、不猜触发区，开不开战仍由 BattleScene / FSM 自己判断，等于玩家刚走进来。
    /// </summary>
    private static IEnumerator Arrive(ModeController host, BossDef b, Func<bool> same)
    {
        // 最多等 10 秒没暂停的时间（Codex 审查 0.9.0 第三轮 #3：暂停久了不能把等待耗完）。
        float waitedPos = 0f;
        while (same() && waitedPos < 10f)
        {
            HeroController h = HeroController.SilentInstance;
            if (h != null && h.isHeroInPosition)
            {
                break;
            }
            yield return null;
            if (!Paused())
            {
                waitedPos += Time.unscaledDeltaTime;
            }
        }
        yield return new WaitForFixedUpdate();
        if (!same())
        {
            yield break;
        }
        HeroController hero = HeroController.SilentInstance;
        Collider2D? heroCol = hero != null ? hero.GetComponent<Collider2D>() : null;
        if (hero == null || !hero.isHeroInPosition || heroCol == null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome]   人没就位，不补发进入 / hero not in position; no enter re-sent ({b.Id})");
        }
        else
        {
            List<string> scenes = ModeController.ScenesOf(b);
            var hits = new List<Collider2D>();
            var filter = new ContactFilter2D { useTriggers = true };
            heroCol.Overlap(filter, hits);
            int sent = 0;
            foreach (Collider2D c in hits)
            {
                if (c == null || !c.enabled || !c.isTrigger || !scenes.Contains(c.gameObject.scene.name))
                {
                    continue;
                }
                BattleScene bs = c.GetComponent<BattleScene>();
                if (bs == null || !bs.isActiveAndEnabled || BattleSceneEnter == null)
                {
                    continue;
                }
                BattleSceneEnter.Invoke(bs, new object[] { heroCol });
                sent++;
                GodhomePlugin.Log.LogInfo($"[Godhome]   补发进入 / re-sent enter: {SceneTools.PathOf(bs.transform)} (BattleScene)");
            }
            if (sent == 0)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome]   就位 / in position: 人所在处没有 BattleScene 触发区 / no BattleScene trigger here");
            }
            // 进场步骤等 FSM 的第一轮补发做完再开始（Codex 审查 0.9.0 #8）：不然步骤先发事件、强切状态，补发对着的是已经变了的状态。
            // 只数没暂停的时间（第二轮 #6）：暂停时物理帧不走，第一轮补发也不会做。做完、出错、等不到都分开记。
            int scan = 0;
            host.StartCoroutine(FsmEnters(b, heroCol, same, ok => scan = ok ? 1 : -1));
            float waited = 0f;
            while (scan == 0 && same() && waited < 1f)
            {
                yield return null;
                if (!Paused())
                {
                    waited += Time.unscaledDeltaTime;
                }
            }
            if (scan != 1 && same())
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   补发进入的第一轮{(scan == 0 ? "没做完 / first pass did not finish in time" : "出错 / first pass failed")}，照样执行进场步骤 / running the enter steps anyway ({b.Id})");
            }
        }
        if (b.Arena.Enter.Count > 0 && same())
        {
            yield return EnterSteps(b, same);
        }
    }

    private static bool Paused() => GameManager.UnsafeInstance is GameManager gm && gm.IsGamePaused();

    /// <summary>
    /// 人就位后 FsmEnterWatch 秒内，每个物理帧看一次：本场地哪个 FSM 的当前状态里有启用的“进入”监听
    /// （Trigger2dEvent / Trigger2dEventLayer，类型 OnTriggerEnter2D），而人已经在它看的那个物体的触发区里，
    /// 就把“进入”交给这个动作（每个动作一次）。它在这个状态里还没收到过“进入”，收到过早就跳走了；
    /// 标签、层的过滤和发什么事件都由动作自己判断（反编译 Trigger2dEvent.cs / Trigger2dEventLayer.cs）。
    /// Unity 把触发消息发给触发区所在物体和它挂的刚体所在物体，这里两个都算。
    /// 进场后附加场景晚到（Boss 场景还没准备好）：一直看到它准备好，新场景来了重新收集 FSM，再看 FsmEnterWatch 秒（Codex 审查 0.9.0 #7）。
    /// 每个物理帧醒来先看有没有新场景、收集完再扫、扫完才判断要不要停（第二轮 #1：场景在等物理帧时准备好，不能先停掉）。
    /// 时间按物理帧累加：暂停时物理帧不走，窗口也不走。第一轮扫完调 firstScan(true)；出错、中途不是这一场了调 firstScan(false)。
    /// </summary>
    private static IEnumerator FsmEnters(BossDef b, Collider2D heroCol, Func<bool> same, Action<bool> firstScan)
    {
        List<string> scenes = ModeController.ScenesOf(b);
        var fsms = new List<PlayMakerFSM>();
        int collectedAt = -1;
        void Collect()
        {
            collectedAt = preparedScenes;
            foreach (PlayMakerFSM pm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (pm == null || pm.Fsm == null || !pm.gameObject.scene.IsValid() || !scenes.Contains(pm.gameObject.scene.name) || fsms.Contains(pm))
                {
                    continue;
                }
                if (pm.FsmStates.Any(st => st.Actions.Any(IsEnterListener)))
                {
                    fsms.Add(pm);
                }
            }
        }
        var done = new HashSet<FsmStateAction>();
        var hits = new List<Collider2D>();
        var filter = new ContactFilter2D { useTriggers = true };
        var touching = new HashSet<GameObject>();
        float left = FsmEnterWatch;
        bool reported = false;
        try
        {
            Collect();
            while (true)
            {
                yield return new WaitForFixedUpdate();
                left -= Time.fixedDeltaTime;
                if (!same() || heroCol == null)
                {
                    yield break;
                }
                if (collectedAt != preparedScenes)
                {
                    int had = fsms.Count;
                    Collect();
                    left = FsmEnterWatch;
                    GodhomePlugin.Log.LogInfo($"[Godhome]   新场景准备好了，重新收集“进入”监听 / a new scene was prepared; enter listeners {had} → {fsms.Count}");
                }
                if (fsms.Count > 0)
                {
                    Scan();
                }
                if (!reported)
                {
                    reported = true;
                    firstScan(true);
                }
                // Boss 场景还没准备好（控制器没建、也还没报超时）就一直看下去。
                if (left <= 0f && (Controller != null || controllerLate))
                {
                    break;
                }
            }
        }
        finally
        {
            if (!reported)
            {
                firstScan(false);
            }
        }

        void Scan()
        {
            hits.Clear();
            touching.Clear();
            heroCol.Overlap(filter, hits);
            foreach (Collider2D c in hits)
            {
                if (c == null || !c.enabled || !c.isTrigger || !scenes.Contains(c.gameObject.scene.name))
                {
                    continue;
                }
                touching.Add(c.gameObject);
                if (c.attachedRigidbody != null)
                {
                    touching.Add(c.attachedRigidbody.gameObject);
                }
            }
            if (touching.Count == 0)
            {
                return;
            }
            foreach (PlayMakerFSM pm in fsms)
            {
                if (pm == null || !pm.isActiveAndEnabled || pm.Fsm == null || pm.Fsm.Finished || pm.Fsm.ActiveState == null)
                {
                    continue;
                }
                FsmState st = pm.Fsm.ActiveState;
                foreach (FsmStateAction a in st.Actions)
                {
                    if (!IsEnterListener(a) || done.Contains(a))
                    {
                        continue;
                    }
                    GameObject? target = a is HutongGames.PlayMaker.Actions.Trigger2dEvent t ? pm.Fsm.GetOwnerDefaultTarget(t.gameObject) : pm.gameObject;
                    if (target == null || !touching.Contains(target))
                    {
                        continue;
                    }
                    done.Add(a);
                    if (a is HutongGames.PlayMaker.Actions.Trigger2dEvent te)
                    {
                        if (TriggerEventEnter == null)
                        {
                            continue;
                        }
                        TriggerEventEnter.Invoke(te, new object[] { heroCol });
                    }
                    else
                    {
                        a.DoTriggerEnter2D(heroCol);
                    }
                    GodhomePlugin.Log.LogInfo($"[Godhome]   补发进入 / re-sent enter: {SceneTools.PathOf(pm.transform)}::{pm.FsmName} [{st.Name}] ← {SceneTools.PathOf(target.transform)} (now '{pm.ActiveStateName}')");
                    // 收到“进入”后 FSM 当场跳走了（Codex 审查 0.9.0 #8）：这个状态里剩下的监听已经退出，不再发。
                    if (pm.Fsm == null || pm.Fsm.ActiveState != st)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>启用的“进入”监听动作：Trigger2dEvent / Trigger2dEventLayer，类型 OnTriggerEnter2D。</summary>
    private static bool IsEnterListener(FsmStateAction a)
    {
        if (a == null || !a.Enabled)
        {
            return false;
        }
        if (a is HutongGames.PlayMaker.Actions.Trigger2dEvent t)
        {
            return t.trigger == Trigger2DType.OnTriggerEnter2D;
        }
        return a is HutongGames.PlayMaker.Actions.Trigger2dEventLayer l && l.trigger == PlayMakerUnity2d.Trigger2DType.OnTriggerEnter2D;
    }

    /// <summary>进场步骤（arena.enter，前作 Dream Entry 的最后几步）：按顺序执行，写了 when 的先等那个 FSM 进到这个状态（最多 15 秒）。</summary>
    private static IEnumerator EnterSteps(BossDef b, Func<bool> same)
    {
        List<string> scenes = ModeController.ScenesOf(b);
        foreach (FsmStep s in b.Arena.Enter)
        {
            if (s.Delay > 0)
            {
                yield return new WaitForSeconds(s.Delay);
            }
            if (s.When.Length > 0)
            {
                // 最多等 15 秒游戏时间（WaitForSeconds 按游戏时间走，暂停时不走；第三轮 #3：不按真实时间截止）。
                float waited = 0f;
                PlayMakerFSM? fsm = null;
                while (same() && waited < 15f)
                {
                    fsm = SceneTools.FindFsm(s.Path, s.Fsm, scenes);
                    if (fsm != null && fsm.gameObject.activeInHierarchy && SceneTools.InState(fsm, s.When))
                    {
                        break;
                    }
                    yield return new WaitForSeconds(0.2f);
                    waited += 0.2f;
                }
                if (fsm == null || !SceneTools.InState(fsm, s.When))
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome]   进场步骤没等到状态 / enter step: {s.Path}::{s.Fsm} never reached '{s.When}' (now '{fsm?.ActiveStateName ?? "-"}')");
                    if (s.Required && same())
                    {
                        // 开战离不开这一步：这一场开不了，按场地出错回大厅（Codex 审计 0.10.3：以前等不到就接着往下，Watch 不会因为“永远没开战”回大厅）。
                        MarkFatal(serial, $"必需的进场步骤没等到状态 / a required enter step never reached its state: {s.Path}::{s.Fsm} '{s.When}' (now '{fsm?.ActiveStateName ?? "-"}')");
                        yield break;
                    }
                }
            }
            // 暂停中不执行（发事件、开关物体），等继续游戏。
            while (Paused() && same())
            {
                yield return null;
            }
            if (!same())
            {
                yield break;
            }
            if (!SceneTools.RunStep(s, scenes) && s.Required)
            {
                MarkFatal(serial, $"必需的进场步骤没执行成 / a required enter step failed: {s}");
                yield break;
            }
        }
    }

    /// <summary>
    /// 每帧（战斗中、进场途中）：看开战判定；附加场景的控制器迟迟没建好就报错。
    /// 返回错误说明（调用方回大厅），没事返回 null。
    /// </summary>
    internal static string? Watch()
    {
        BossDef? b = boss;
        if (b == null)
        {
            return null;
        }
        if (enteredAt > 0f)
        {
            // 只数没暂停的时间（Codex 审查 0.9.0 #7）：暂停超过 ControllerWait 秒再继续，不算附加场景没加载。
            sinceEntered += Time.unscaledDeltaTime;
        }
        if (Controller == null && enteredAt > 0f && !controllerLate && sinceEntered > ControllerWait)
        {
            controllerLate = true;
            return $"Boss 场景 {b.Scene} 没加载、场地控制器没建 / boss scene {b.Scene} never loaded; no arena controller";
        }
        if (Controller != null && enteredAt > 0f && !missingReported)
        {
            ReportMissing(b);
        }
        if (Controller != null && enteredAt > 0f && fatal == null)
        {
            fatal = Unwinnable(b);
        }
        // 进场以后才报（进场途中回大厅会和游戏自己的切场景撞上）：进场前记下的（MarkFatal、控制器回调）也在这里报。
        if (fatal != null && !fatalReported && enteredAt > 0f)
        {
            fatalReported = true;
            return fatal;
        }
        if (Started)
        {
            return null;
        }
        if (wakeBattle != null)
        {
            if (Traverse.Create(wakeBattle).Field("started").GetValue<bool>())
            {
                MarkStarted(b, $"BattleScene {b.Arena.Wake!.Battle}");
            }
            return null;
        }
        if (wakeFsm == null || b.Arena.Wake == null)
        {
            return null;
        }
        // 主要靠插在等待状态里的标记（WakeMark）：只待一帧的状态也记得到。每帧看状态留作后备（标记没插上时）。
        string st = wakeFsm.ActiveStateName;
        if (wakeSeen && !wakeLogged)
        {
            wakeLogged = true;
            GodhomePlugin.Log.LogInfo($"[Godhome]   等开战 / waiting: {b.Arena.Wake} (now '{st}'{(wakeLeft ? "，已经离开 / already left" : "")})");
        }
        WakeDef w = b.Arena.Wake;
        // 写了 via 的：离开等待状态的事件要对得上才算开战（Codex 审查 0.9.0 第二轮 #9，沙克拉 ENEMY 是去打别的敌人）。
        // 对不上就作废这次等待，回到等待状态以后再等。
        void Left(string via, string note, int at)
        {
            if (w.Via.Length > 0 && !w.Via.Split('|').Any(v => v.Trim() == via))
            {
                wakeLeft = false;
                wakeSeen = false;
                if (wrongVia++ < 5)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome]   离开等待状态的事件不对，不算开战 / left the wait state via '{via}', expected '{w.Via}'; not a start — {note}");
                }
                return;
            }
            MarkStarted(b, note, at);
        }
        if (wakeLeft)
        {
            Left(wakeLeftVia, $"{w.Path}::{w.Fsm} '{wakeLeftFrom}' {(wakeLeftVia.Length > 0 ? "-" + wakeLeftVia + "→" : "→")} '{st}'", wakeLeftSeq);
        }
        else if (!wakeFsm.isActiveAndEnabled || st.Length == 0)
        {
            // FSM 关掉了（物体失活、组件停用）：之前在等待状态里待过也作废（Codex 审查 0.9.0 #10），
            // 重新打开后从起始状态走到别的状态不算开战，要再进一次等待状态。
            wakeSeen = false;
        }
        else if (SceneTools.InState(wakeFsm, w.State))
        {
            wakeSeen = true;
        }
        else if (wakeSeen)
        {
            // 标记没插上时的后备：只能看最近一次跳转（Fsm.LastTransition）。写了 via 的，这次跳转要是等待状态自己的跳转才确认得了
            // （中间又跳过一次就不是离开等待状态的那次了）；确认不了不算开战，自检会报没开战（Codex 审查 0.9.0 第三轮 #10）。
            FsmTransition? t = wakeFsm.Fsm?.LastTransition;
            string via = t?.EventName ?? "";
            string note = $"{w.Path}::{w.Fsm} '{wakeLast}' → '{st}'（每帧看状态 / polled, last event '{via}'）";
            if (w.Via.Length > 0 && !FromWaitState(wakeFsm, w.State, t))
            {
                wakeSeen = false;
                if (wrongVia++ < 5)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome]   确认不了离开等待状态的事件，不算开战 / cannot confirm the event that left the wait state; not a start — {note}");
                }
            }
            else
            {
                Left(via, note, 0);
            }
        }
        wakeLast = st;
        return null;
    }

    /// <summary>
    /// 哪几场开过战（场次号 → 说明）。Clear 不清：万神殿自检在打完以后才看挑战位开没开战，
    /// 那时游戏的胜利转场可能已经开了下一场（Arena 的状态已经换成下一场的）。只留最近几场。
    /// </summary>
    private static readonly Dictionary<int, (string Note, int Seq)> startedFights = new();

    /// <summary>
    /// 开战相关事件的先后号（只增不减）：离开等待状态时（WakeMark.OnExit，和跳转同步）取一个号，记开战时存下。
    /// 自检比较“挑战发出”和“离开等待状态”谁先（Codex 审查 0.9.0 第三轮 #6：Watch 下一帧才记开战，同一帧里的先后看不出来）。
    /// </summary>
    private static int seq;
    private static int wakeLeftSeq;

    /// <summary>当前的先后号（自检在发挑战之前读一次）。</summary>
    internal static int Seq => seq;

    /// <summary>这一场（场次号）开过战没有，开过的话 note 是开战说明。</summary>
    internal static bool StartedIn(int fightSerial, out string note)
    {
        bool ok = startedFights.TryGetValue(fightSerial, out var v);
        note = ok ? v.Note : "";
        return ok;
    }

    /// <summary>这一场开战事件的先后号（见 seq；每帧看状态、BattleScene 开战是发现时取的号，比实际晚）。</summary>
    internal static bool StartedAt(int fightSerial, out int at)
    {
        bool ok = startedFights.TryGetValue(fightSerial, out var v);
        at = ok ? v.Seq : 0;
        return ok;
    }

    private static void MarkStarted(BossDef b, string note, int at = 0)
    {
        Started = true;
        StartNote = note;
        startedFights[serial] = (note, at > 0 ? at : ++seq);
        foreach (int old in startedFights.Keys.Where(k => k < serial - 16).ToList())
        {
            startedFights.Remove(old);
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 开战 / battle started: {b.Id} — {note}");
        // 诊断：开战那一刻等待 FSM 里设了值、不是它自己身上的物体变量（触发区存下来的“谁进来了”就在这里，
        // 例如 Trigger2dEvent 的 storeCollider）。
        if (wakeFsm != null && wakeFsm.FsmVariables?.GameObjectVariables is FsmGameObject[] gos)
        {
            Transform self = wakeFsm.transform;
            string vars = string.Join(", ", gos.Where(v => v != null && v.Value != null && !v.Value.transform.IsChildOf(self)).Take(12)
                .Select(v => $"{v.Name}={SceneTools.PathOf(v.Value.transform)}@{v.Value.scene.name}"));
            if (vars.Length > 0)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome]   开战时的物体变量 / object variables at start: {vars}");
            }
        }
    }

    /// <summary>
    /// 判胜装不全就是赢不了或判不对（Codex 审查 0.9.0 #3、第二轮 #4 #5）：不让人在这种场地里白打，调用方回大厅（不记尝试）。没问题返回 null。
    /// 写了 arena.end：每一项都要插上，或者在等还活着的模板 FSM 醒来。表里的几个结束点可能是不同路线各自的结局（互斥），
    /// 缺一个，玩家走那条路线就卡住，所以一项都不能少。这一场的场景都准备过了就马上判；还有场景没来（附加场景晚到）时，
    /// 没找到的项最多等 ControllerWait 秒。
    /// 没写 arena.end：控制器一个 Boss 血量都没登记，或者表里的 Boss 物体没找全。
    /// </summary>
    private static string? Unwinnable(BossDef b)
    {
        // 表里标了 required 的补丁（开战离不开它）：这一场的场景都准备过了还没打上，就开不了战（Codex 审计 0.10.3）。
        // 还有场景没来（附加场景晚到）时最多等 ControllerWait 秒。模板 FSM 醒来后重打失败的，OnFsmAwake 会把它从 patchDone 里拿掉。
        if (b.Arena.Patch.Any(p => p.Required))
        {
            bool pending = !ModeController.ScenesOf(b).All(preparedNames.Contains) && sinceEntered <= ControllerWait;
            var missing = new List<string>();
            for (int i = 0; i < b.Arena.Patch.Count; i++)
            {
                if (b.Arena.Patch[i].Required && !patchDone.Contains(i))
                {
                    missing.Add($"{b.Arena.Patch[i]}: {(patchFail.TryGetValue(i, out string? why) ? why : "没找到物体 / target not found")}");
                }
            }
            if (missing.Count > 0)
            {
                if (pending)
                {
                    return null;
                }
                return $"必需的补丁没打上，这一场开不了 / a required arena.patch is not in place: {string.Join("; ", missing)}";
            }
        }
        if (b.Arena.End.Count > 0)
        {
            bool scenesPending = !ModeController.ScenesOf(b).All(preparedNames.Contains) && sinceEntered <= ControllerWait;
            var bad = new List<string>();
            for (int i = 0; i < b.Arena.End.Count; i++)
            {
                // 先看在等的：加载时插上了、但醒来会重建的模板 FSM 也在这里，醒来前被销毁就是没了。
                if (endWaiting.TryGetValue(i, out PlayMakerFSM? w))
                {
                    if (w != null)
                    {
                        continue;
                    }
                    bad.Add($"{b.Arena.End[i]}: 等的模板 FSM 醒来前被销毁了 / the template FSM was destroyed before it woke");
                    continue;
                }
                if (endOk.Contains(i))
                {
                    continue;
                }
                if (!endDone.Contains(i) && scenesPending)
                {
                    return null;
                }
                bad.Add($"{b.Arena.End[i]}: {(endFail.TryGetValue(i, out string? why) ? why : "没找到物体 / target not found")}");
            }
            return bad.Count == 0 ? null
                : $"结束动作没插全，这一场可能赢不了 / not every arena.end action is in place ({EndsInserted}/{EndCount}): {string.Join("; ", bad)}";
        }
        if (BossesRegistered == 0)
        {
            return "场地控制器没有登记 Boss 血量，这一场赢不了 / the arena controller has no boss HealthManager";
        }
        if (bossesMissing.Length > 0)
        {
            return MissingBosses();
        }
        return null;
    }

    private static string MissingBosses() => $"表里的 Boss 物体没找全，判胜不对 / boss objects missing: {bossesMissing}";

    /// <summary>
    /// 这一场（场次号对得上）已经确定赢不了：例如人还没进场，场地控制器就判了胜（结束机会已经用掉，EndBossScene 防重入）。
    /// 进场后 Watch 报一次，调用方回大厅（不记尝试，Codex 审查 0.9.0 第二轮 #2）。
    /// </summary>
    /// <summary>这一场（场次号对得上）已经确定赢不了（还没报也算）：这时不管怎么回大厅都按场地出错算（Codex 审查 0.9.0 第三轮 #8）。</summary>
    internal static bool FatalFor(int fightSerial) => boss != null && fightSerial == serial && fatal != null;

    internal static void MarkFatal(int fightSerial, string why)
    {
        if (fightSerial != serial || boss == null)
        {
            return;
        }
        if (fatal == null)
        {
            fatal = why;
            GodhomePlugin.Log.LogError($"[Godhome] {boss.Id}: 这一场赢不了，进场后回大厅 / this fight cannot be won; back to the lobby once entered: {why}");
        }
    }

    /// <summary>
    /// WakeMark.OnEnter：进了开战判定的等待状态。等待状态写了几个（A|B）时，A 走到 B 是先退 A 再进 B，
    /// 同一次切换里接着调到这里（Fsm.SwitchState 先 ExitState 再 EnterState，反编译 Fsm.cs），把刚记下的“离开”撤掉。
    /// </summary>
    internal static void WakeEntered(int mark)
    {
        if (mark == serial && boss != null)
        {
            wakeSeen = true;
            wakeLeft = false;
        }
    }

    /// <summary>WakeMark.OnExit：离开了开战判定的等待状态（FsmState.OnExit 对进过的动作都调，做完了的也调）。Watch 下一帧看到就算开战。</summary>
    internal static void WakeExited(int mark, string from, string via)
    {
        if (mark == serial && boss != null)
        {
            wakeLeft = true;
            wakeLeftFrom = from;
            wakeLeftVia = via;
            wakeLeftSeq = ++seq;
        }
    }

    /// <summary>
    /// WakeMark.OnExit 时 FSM 正被关掉：不算开战，之前在等待状态里待过也作废（Codex 审查 0.9.0 #10）。
    /// 同一帧里关了又开（Watch 看不到关着的那一帧）也不会把重启后的状态当成开战。已经记下的“离开”不撤。
    /// </summary>
    internal static void WakeReset(int mark)
    {
        if (mark == serial && boss != null)
        {
            wakeSeen = false;
        }
    }

    /// <summary>在开战判定的等待状态（可以写 A|B）最前面插 WakeMark。模板 FSM 醒来后由 OnFsmAwake 重插。</summary>
    private static string InsertWakeMarks(PlayMakerFSM pm, string when)
    {
        Fsm f = pm.Fsm;
        if (pm.FsmTemplate != null && !f.Initialized)
        {
            return Bad("模板 FSM 还没初始化 / template FSM not initialised yet");
        }
        if (!f.Initialized)
        {
            f.InitData();
        }
        var done = new List<string>();
        foreach (string name in when.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            FsmState? st = f.GetState(name);
            if (st == null)
            {
                return Bad($"没有状态 / no state '{name}'");
            }
            // 这一场的标记已经在最前面就不再插；别的场次留下的（编号不对）拿掉换新的。
            if (!(st.Actions.Length > 0 && st.Actions[0] is WakeMark m && m.Mark == serial))
            {
                st.Actions = new FsmStateAction[] { new WakeMark(serial, name) }.Concat(st.Actions.Where(a => a is not WakeMark)).ToArray();
            }
            done.Add(name);
        }
        return $"ok（{string.Join(", ", done)}）";
    }

    /// <summary>所有场景都加载了以后，还没找到的补丁、结束动作路径记警告（写错了路径，或者在没加载的场景里）。</summary>
    private static void ReportMissing(BossDef b)
    {
        missingReported = true;
        for (int i = 0; i < b.Arena.Patch.Count; i++)
        {
            if (patchDone.Contains(i))
            {
                continue;
            }
            if (patchFail.TryGetValue(i, out string? why))
            {
                NoteError($"patch {b.Arena.Patch[i]}: {why}");
                GodhomePlugin.Log.LogWarning($"[Godhome]   补丁没打上 / patch not applied: {b.Arena.Patch[i]} — {why}");
            }
            else
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   补丁没找到物体 / patch target not found: {b.Arena.Patch[i]} — {SceneTools.ExplainMissing(b.Arena.Patch[i].Path, ModeController.ScenesOf(b))}");
            }
        }
        for (int i = 0; i < b.Arena.SkipWaves.Length; i++)
        {
            if (!wavesDone.Contains(i))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   要跳过的波次没找到 / skip wave not found: {b.Arena.SkipWaves[i]} — {SceneTools.ExplainMissing(b.Arena.SkipWaves[i], ModeController.ScenesOf(b))}");
            }
        }
        for (int i = 0; i < b.Arena.End.Count; i++)
        {
            if (endDone.Contains(i))
            {
                continue;
            }
            if (endFail.TryGetValue(i, out string? why))
            {
                NoteError($"end {b.Arena.End[i]}: {why}");
                GodhomePlugin.Log.LogWarning($"[Godhome]   结束动作没插上 / end action not inserted: {b.Arena.End[i]} — {why}");
            }
            else
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   结束动作没找到物体 / end target not found: {b.Arena.End[i]} — {SceneTools.ExplainMissing(b.Arena.End[i].Path, ModeController.ScenesOf(b))}");
            }
        }
        if (b.Arena.Wake != null && wakeFsm == null && wakeBattle == null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome]   开战判定没找到 / wake target not found: {b.Arena.Wake}");
        }
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    /// <summary>这次跳转是等待状态（可以写 A|B）自己的跳转（不是全局跳转，也不是之后别的状态的）。</summary>
    private static bool FromWaitState(PlayMakerFSM pm, string when, FsmTransition? t)
    {
        if (t == null || pm.Fsm == null)
        {
            return false;
        }
        foreach (string name in when.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            FsmState? st = pm.Fsm.GetState(name);
            if (st != null && st.Transitions.Contains(t))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>在这个场景的根物体里按层级路径找（未激活的也找得到）。</summary>
    private static GameObject? FindIn(GameObject[] roots, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        int slash = path.IndexOf('/');
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        string rest = slash < 0 ? "" : path.Substring(slash + 1);
        foreach (GameObject r in roots)
        {
            if (r == null || r.name != rootName)
            {
                continue;
            }
            if (rest.Length == 0)
            {
                return r;
            }
            Transform? t = r.transform.Find(rest);
            if (t != null)
            {
                return t.gameObject;
            }
        }
        return null;
    }

    private static PlayMakerFSM? FsmOn(GameObject go, string fsmName) =>
        go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => string.IsNullOrEmpty(fsmName) || f.FsmName == fsmName);

    private static string CleanName(string n) => n.Replace("(Clone)", "").Trim();

    private const string FailMark = "失败 / FAILED: ";

    /// <summary>失败的结果说明（不记进 Error：同路径的候选物体失败了，后加载的场景里可能还有真正的目标；最后由调用方决定记不记）。</summary>
    private static string Bad(string why) => FailMark + why;

    private static bool Failed(string result) => result.StartsWith(FailMark, StringComparison.Ordinal);

    private static void NoteError(string why) => Error += (Error.Length > 0 ? "; " : "") + why;

    private static string Fmt(Vector3 v) => $"({v.x:0.0}, {v.y:0.0})";

    private static string Fmt(Vector2 v) => $"({v.x:0.0}, {v.y:0.0})";
}

/// <summary>
/// 开战判定标记：Arena 在加载时插在 arena.wake 等待状态的最前面。进状态告诉 Arena “在等开战”，离开告诉它“开战了”。
/// 每帧看 ActiveStateName 会漏掉只待一帧的等待状态（人一落地就在触发区里，FSM 进来当帧就走了：末代裁决者、炽焰之父）。
/// 进状态时马上 Finish，不挡这个状态的 FINISHED；FsmState.OnExit 对进过的动作都调 OnExit，做完了的也调（反编译 FsmState.cs）。
/// FSM 被关掉时退出状态不算开战（和每帧看状态一样：关掉后状态名是空的，不算）。
/// 只认插它的那一场（编号对得上）。
/// </summary>
internal sealed class WakeMark : FsmStateAction
{
    private readonly int mark;
    private readonly string stateName;

    internal WakeMark(int fightSerial, string state)
    {
        mark = fightSerial;
        stateName = state;
    }

    internal int Mark => mark;

    public override void OnEnter()
    {
        Arena.WakeEntered(mark);
        Finish();
    }

    public override void OnExit()
    {
        // FSM 被关掉（物体失活、组件停用）时 Fsm.OnDisable → Stop → StopAndReset 也会退出当前状态（反编译 Fsm.cs），那不是开战。
        MonoBehaviour? owner = Fsm?.Owner;
        if (owner != null && !owner.isActiveAndEnabled)
        {
            Arena.WakeReset(mark);
            return;
        }
        // 经事件跳走时 Fsm.DoTransition 在切状态前设了 LastTransition（反编译 Fsm.cs）；目标不是别的状态的（旧值）不算。
        FsmTransition? t = Fsm?.LastTransition;
        string via = t != null && t.ToState != stateName ? t.EventName : "";
        Arena.WakeExited(mark, stateName, via);
    }
}
