using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 前作的神居当大厅（分支 hk1-godhome，规则 5、6 已取消）：前厅 GG_Atrium（四扇万神殿的门）、
/// 前厅屋顶 GG_Atrium_Roof（第五扇门）、手办屋 GG_Workshop（雕像）。三个场景之间走前作自己的门。
/// Boss 场地不搬，打的都是本作的 Boss（雕像、门接到本作的 Boss 和万神殿上，见 Hk1Hall、Hk1Doors）。
///
/// 存档里的复活点始终是本作的场景（织忆圣所的长椅，见 SaveSanitizePatch），没有前作或插件时读档照样能进；
/// 启用前作时，读档直接进入最近一次坐过长椅的大厅房间，首次进入落在前厅。
/// </summary>
internal static class Hk1Lobby
{
    internal const string Atrium = "GG_Atrium";
    internal const string Roof = "GG_Atrium_Roof";
    internal const string Workshop = "GG_Workshop";

    /// <summary>前作万神殿的休息点（温泉，有长椅）：用前作神居时万神殿的休息点用它。</summary>
    internal const string Spa = "GG_Spa";

    /// <summary>前作每个 Boss 场景（包括温泉）都有的入口门（前作 BossSequenceController 进下一场就走它）。</summary>
    internal const string DreamEnterGate = "door_dreamEnter";

    /// <summary>前厅里默认的落点。还没坐过大厅长椅的存档也从这里进。</summary>
    internal const string ArriveMarker = "Death Respawn Marker";

    /// <summary>前作雕像、门的回程门名的前缀（BossStatue.Awake / BossSequenceDoor.Start 把物体名接在后面）。</summary>
    internal const string ReturnGatePrefix = "door_dreamReturnGG_";

    private static readonly string[] Hub = { Atrium, Roof, Workshop };

    /// <summary>搬过来的所有前作场景（神居三个房间 + 温泉）。</summary>
    private static readonly string[] All = { Atrium, Roof, Workshop, Spa };

    private static bool decided;

    /// <summary>这次用不用前作的神居（第一次进神居存档时定下来，回标题重置）。</summary>
    internal static bool Active { get; private set; }

    internal static bool IsHub(string scene) => Array.IndexOf(Hub, scene) >= 0;

    internal static string BenchMarker(string scene) => scene == Atrium ? "RestBench" : "RestBench (1)";

    /// <summary>是不是搬过来的前作场景（神居三个房间或温泉）。存档里不能写这些场景名（SaveSanitizePatch）。</summary>
    internal static bool IsHk1Scene(string? scene) => scene != null && Array.IndexOf(All, scene) >= 0;

    /// <summary>当前场景是前作神居里的一个房间（而且这次在用）。</summary>
    internal static bool InHub
    {
        get
        {
            if (!Active)
            {
                return false;
            }
            GameManager gm = GameManager.UnsafeInstance;
            return gm != null && IsHub(gm.GetSceneNameString());
        }
    }

    /// <summary>
    /// 定下这次用不用前作的神居：开着、插件和前作都在、三个场景都打包好了才用（等打包完成，一般在标题画面就打好了）。
    /// 已经定过就直接返回。
    /// </summary>
    internal static bool Decide()
    {
        if (decided)
        {
            return Active;
        }
        decided = true;
        Active = false;
        if (!GodhomeConfig.Hk1Enabled.Value)
        {
            GodhomePlugin.Log.LogInfo("[Godhome] 前作神居已关（Experimental.Hk1Godhome = false），用织忆圣所 / hk1 godhome disabled, using the Loom sanctum");
            return false;
        }
        Hk1Assets.StartPrepare();
        if (Hk1Assets.Failed)
        {
            return false;
        }
        foreach (string s in Hub)
        {
            if (Hk1Assets.Locate(s) == null)
            {
                return false;
            }
        }
        Active = true;
        GodhomePlugin.Log.LogInfo($"[Godhome] 大厅用前作的神居 / the lobby is Hollow Knight's Godhome ({Hk1Assets.GameDir})");
        // 前作的连战转场特效：现在就开始在后台加载（进第一场之前早就好了）。
        Hk1Battle.BeginLoad();
        return true;
    }

    /// <summary>
    /// 前作神居的场景进不去（加载失败、一直卡在转场里）：这次游戏不再用前作神居，回标题重新读档也不用，退回织忆圣所（Codex 审查）。
    /// </summary>
    internal static void Abandon(string why)
    {
        if (!Active)
        {
            return;
        }
        Hk1Assets.MarkFailed(why);
        Active = false;
        Hk1Hall.Forget();
        Hk1Doors.Forget();
    }

    /// <summary>回标题：下次进神居存档时重新定（打包结果留着）。</summary>
    internal static void Reset()
    {
        if (!decided)
        {
            return;
        }
        decided = false;
        Active = false;
        Hk1Hall.Forget();
        Hk1Doors.Forget();
    }

    /// <summary>回程门：雕像（手办屋里前作雕像的物体名）或万神殿的门（door:p1）→ 场景和门名。不认识返回 null。</summary>
    internal static (string Scene, string Gate)? ReturnTo(string spot)
    {
        if (spot.StartsWith(Sanctum.DoorPrefix, StringComparison.Ordinal))
        {
            string id = spot.Substring(Sanctum.DoorPrefix.Length);
            (string Scene, string Door)? d = Hk1Doors.DoorOf(id);
            return d == null ? null : (d.Value.Scene, ReturnGatePrefix + d.Value.Door);
        }
        return Hk1Hall.IsStatue(spot) ? (Workshop, ReturnGatePrefix + spot) : null;
    }

    /// <summary>战斗里死了，回到雕像、门前用的复活点的名字前缀（后面接雕像名或 door:pX）。</summary>
    internal const string DeathReturnPrefix = "Godhome Death Return ";

    /// <summary>雕像或门（同 ReturnTo 的写法）→ 死后复活的场景和复活点名。不认识返回 null。</summary>
    internal static (string Scene, string Marker)? DeathReturn(string spot)
    {
        (string Scene, string Gate)? r = ReturnTo(spot);
        return r == null ? null : (r.Value.Scene, DeathReturnPrefix + spot);
    }

    /// <summary>
    /// 场景加载时，在雕像、门的回程门那里放一个复活点（丝之歌的 RespawnMarker；复活时 HeroController.LocateSpawnPoint 按入口名找它）。
    /// 位置用回程门下面的危险复活点（站人的高度），没有就用门本身；回程门也没有就放在雕像、门本身那里
    /// （DeathReturn 照样会按这个名字找复活点，找不到 HeroController.Respawn 会空引用，Codex 审查）。物体放进这个场景，跟着场景卸载。
    /// </summary>
    internal static void AddDeathMarker(Scene scene, string spot, GameObject? gate, Transform fallback)
    {
        Vector3 pos;
        if (gate != null)
        {
            HazardRespawnMarker? hz = gate.GetComponentInChildren<HazardRespawnMarker>(true);
            pos = hz != null ? hz.transform.position : gate.transform.position;
        }
        else
        {
            pos = fallback.position;
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作神居：{spot} 没有回程门，死后复活点放在它本身那里 / hk1 godhome: {spot} has no return gate; death marker at the object itself {pos}");
        }
        var go = new GameObject(DeathReturnPrefix + spot);
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.position = pos;
        RespawnMarker m = go.AddComponent<RespawnMarker>();
        // 运行时加的组件序列化字段是空的，复活时会读这两个（HeroController.cs:7237、GameManager.cs:3331）。
        m.customFadeDuration = new TeamCherry.SharedUtils.OverrideFloat();
        m.overrideMapZone = new OverrideMapZone();
        m.respawnFacingRight = true;
    }

    /// <summary>挑战或结算界面占用交互。</summary>
    internal static bool InteractionBlocked =>
        LoomUI.Instance != null && (LoomUI.Instance.ChallengeBusy || LoomUI.Instance.ResultBusy);

    internal static bool Ready => InHub;

    /// <summary>
    /// 前作的 FSM 走到一半被我们截走（开战、开万神殿）：它关掉的暂停、拿走的操作还回来，
    /// 不然快照里记下“不能暂停”，回大厅后暂停键没反应。
    /// </summary>
    internal static void ReleaseHero()
    {
        PlayerData pd = PlayerData.instance;
        if (pd != null)
        {
            pd.disablePause = false;
        }
        HeroController hero = HeroController.SilentInstance;
        if (hero != null)
        {
            // 前作挑战门的 Change Scene 在 BeginSceneTransition 之前给主角发 SendMessage，等前作 GG_Boss_Door_Entrance 的演出再交还。
            // 我们改道进本作的战场，没有那段演出：留着“进场不接输入”的话 FinishedEnteringScene 不调 AcceptInput，第一场落地后一直动不了
            // （玩家报“每个万神殿第一场开始时动不了一会儿”；0.10.6 p2 自检：幽影一场落地后 8 秒都没恢复操作）。
            string before = $"enterWithoutInput={hero.enterWithoutInput} relinquished={hero.controlReqlinquished} input={hero.acceptingInput} anim={hero.HasAnimationControl}";
            hero.EnterWithoutInput(false);
            hero.RegainControl();
            hero.StartAnimationControl();
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作 FSM 拿走的操作还回来 / hk1 FSM control handed back: {before} → enterWithoutInput={hero.enterWithoutInput} relinquished={hero.controlReqlinquished} input={hero.acceptingInput}");
        }
    }

    private static float stuckSince = -1f;

    /// <summary>
    /// 前作神居里人“没被收走控制、却不接输入”超过 2 秒：照暂停再继续的做法接上输入（HeroController.UnPauseInput 也是只要没被收走控制
    /// 就把 acceptingInput 设回 true，所以这不会比玩家按一次 Esc 多放开什么）。
    /// 0.10.6 实测清单第 7 条：掉出前厅边界以后，游戏把人从前厅的入口（door_dreamEnter_ReturnGGMode）重新送进前厅，
    /// 进来以后一直不接输入，按一次 Esc 才好；巡视 0.10.7 复现了：重进前厅 15 秒后 input=False relinquished=False。
    /// 界面、长椅、物品栏、交互、转场、死亡、危险复活中都不管。每次接上都记一条日志（场景、位置、入口），之后查是谁关掉的输入。
    /// </summary>
    internal static void InputWatch()
    {
        HeroController? hero = HeroController.SilentInstance;
        GameManager? gm = GameManager.UnsafeInstance;
        PlayerData? pd = PlayerData.instance;
        bool stuck = InHub && hero != null && gm != null && pd != null
            && gm.GameState == GlobalEnums.GameState.PLAYING && !gm.IsGamePaused() && !gm.IsInSceneTransition
            && !hero.acceptingInput && !hero.controlReqlinquished
            && !hero.cState.dead && !hero.cState.transitioning && !hero.cState.hazardDeath && !hero.cState.hazardRespawning
            && !InteractionBlocked && !Sanctum.Busy && InteractManager.BlockingInteractable == null
            && !pd.atBench && !pd.isInventoryOpen;
        if (!stuck)
        {
            stuckSince = -1f;
            return;
        }
        if (stuckSince < 0f)
        {
            stuckSince = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - stuckSince < 2f)
        {
            return;
        }
        stuckSince = -1f;
        hero!.AcceptInput();
        Vector3 p = hero.transform.position;
        GodhomePlugin.Log.LogWarning($"[Godhome] 前作神居：人没被收走控制却 2 秒不接输入，已接上 / hk1 godhome: input was off for 2 s without a control lock, turned it on"
            + $"（{gm!.GetSceneNameString()} ({p.x:0.0}, {p.y:0.0})，入口 / entry {gm.entryGateName}，enterWithoutInput={hero.enterWithoutInput}，状态 / state {hero.hero_state}）");
    }

    /// <summary>
    /// 前作神居里的门（TransitionPoint）转场时不带场景的位置（前作的场景不在丝之歌的资源里），
    /// 去前作的三个房间时补上重新打包好的位置，不然丝之歌找不到场景。
    /// </summary>
    internal static void FillLocation(GameManager.SceneLoadInfo info)
    {
        if (!Active || info == null || info.SceneResourceLocation != null || !Hk1Assets.Has(info.SceneName))
        {
            return;
        }
        info.SceneResourceLocation = Hk1Assets.Locate(info.SceneName);
    }

    // ───────────────────────────── 加载时 ─────────────────────────────

    /// <summary>
    /// 场景刚加载完（Awake 已跑、Start 没跑）：前作的复活触发器关掉（它会把复活点写成前作场景）、
    /// 调试画线组件去掉、交互 FSM 里丝之歌没有的方法换掉、离开神居的出口关掉；然后布置雕像或门。
    /// </summary>
    internal static void Prepare(Scene scene)
    {
        if (!Active || !IsHub(scene.name))
        {
            return;
        }
        GameObject[] roots = scene.GetRootGameObjects();
        (int triggers, int debug, int fsms) = Sanitize(roots);
        foreach (RespawnMarker marker in roots.SelectMany(r => r.GetComponentsInChildren<RespawnMarker>(true))
            .Where(m => m.name == BenchMarker(scene.name) || m.name == ArriveMarker))
        {
            marker.customFadeDuration ??= new TeamCherry.SharedUtils.OverrideFloat();
            marker.overrideMapZone ??= new OverrideMapZone();
        }
        if (scene.name == Roof)
        {
            Hk1RoofPlatform.Prepare(roots);
        }
        // 前作的“离开神居”（前厅左边的梦境出口）：织忆圣所没有出口。
        foreach (GameObject r in roots.Where(r => r.name == "Dream Exit Warp"))
        {
            r.SetActive(false);
        }
        FixDoors(scene.name, roots);
        LowerBlurPlanes(roots);
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居 / hk1 godhome: {scene.name} 准备好 / prepared（复活触发器 / respawn triggers {triggers}，调试画线 / debug draw {debug}，改过的交互 FSM / fixed fsms {fsms}）");
        if (scene.name == Workshop)
        {
            Hk1Hall.Prepare(scene);
        }
        else
        {
            Hk1Doors.Prepare(scene);
        }
    }

    /// <summary>
    /// 前作场景（神居的房间、温泉）共用的加载时处理：前作的复活触发器关掉（它会把复活点写成前作场景）、
    /// 调试画线组件去掉、FSM 里丝之歌接不上的地方换掉（FixFsm）。返回关掉的触发器、去掉的画线、改过的 FSM 个数。
    /// </summary>
    internal static (int Triggers, int Debug, int Fsms) Sanitize(GameObject[] roots)
    {
        int triggers = 0, debug = 0, fsms = 0;
        foreach (GameObject r in roots)
        {
            foreach (RespawnTrigger t in r.GetComponentsInChildren<RespawnTrigger>(true))
            {
                t.enabled = false;
                foreach (Collider2D c in t.GetComponents<Collider2D>())
                {
                    c.enabled = false;
                }
                triggers++;
            }
            foreach (DebugDrawColliderRuntime d in r.GetComponentsInChildren<DebugDrawColliderRuntime>(true))
            {
                Object.Destroy(d);
                debug++;
            }
            foreach (PlayMakerFSM pm in r.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (FixFsm(pm))
                {
                    fsms++;
                }
            }
        }
        return (triggers, debug, fsms);
    }

    private static readonly AccessTools.FieldRef<InteractableBase, string> OldPromptLabel = AccessTools.FieldRefAccess<InteractableBase, string>("promptLabel");
    private static readonly AccessTools.FieldRef<InteractableBase, InteractableBase.PromptLabels> LabelField =
        AccessTools.FieldRefAccess<InteractableBase, InteractableBase.PromptLabels>("interactLabel");

    /// <summary>
    /// 前作场景里的转场点（TransitionPoint）。丝之歌的转场点本身是可交互物体（继承 InteractableBase），门型的（isADoor）站进去就出提示、按上就进门；
    /// 前作的门型转场点自己不交互（进门、提示都由门上的 FSM 管）。读成丝之歌的转场点后：
    /// - 提示标签：前作数据里没有丝之歌的标签字段，InteractableBase.Awake 把代码里旧字段 promptLabel 的默认值 "Speak" 升级成标签，
    ///   所以前作的门全都显示“交谈”（用户实机看到；反编译 InteractableBase.UpgradeOldSerializedFields 核实）。
    /// - 入场、回程门（door_dream…，雕像、万神殿门、温泉入口前都有一个）：照前作不能交互（SetIsInactive，只管交互，当入口照常用）。
    ///   不关的话雕像前、门前多一个“交谈”，按上会从回程门“进门”，没有目标场景，被改道送回前厅。
    /// - 去没搬过来的房间的门（屋顶的风暴之地、前厅的蓝色房间）：丝之歌查不到目标就把人送回这扇门，等于原地重进一次房间，也关掉（Codex 审查）。
    /// - 剩下的真门（前厅去手办屋的 Door_Workshop）：照前作是“进入”。
    /// - 层：前作前厅去屋顶的 top1、top2 在第 10 层（前作的 TRANSITION_GATES），丝之歌没有这一层，主角（第 9 层）和它不碰，
    ///   跳进去收不到触发、不切场景（玩家报“五门那里好像是上去了但没有切场景”；0.10.6 巡视：碰不碰 False、出口收到的调用 0）。
    ///   丝之歌的转场点和前作别的门都在第 13 层（HERO_DETECTOR，离线读两边的场景核实），改过去。
    /// </summary>
    internal static void FixDoors(string sceneName, GameObject[] roots)
    {
        List<string> exits = new(), quiet = new(), enter = new(), relayered = new();
        foreach (TransitionPoint tp in roots.SelectMany(r => r.GetComponentsInChildren<TransitionPoint>(true)))
        {
            string target = tp.targetScene;
            exits.Add($"{tp.name}→{(string.IsNullOrEmpty(target) ? "-" : target)}{(tp.isADoor ? "(door)" : "")}{(tp.isInactive ? "(inactive)" : "")}");
            if (tp.gameObject.layer == Hk1TransitionGates)
            {
                tp.gameObject.layer = (int)GlobalEnums.PhysLayers.HERO_DETECTOR;
                relayered.Add(tp.name);
            }
            // 标签一律先改好（旧字段清掉，之后醒来的也不会再升级成“交谈”）。
            OldPromptLabel(tp) = null!;
            LabelField(tp) = InteractableBase.PromptLabels.Enter;
            if (!tp.isADoor || tp.isInactive)
            {
                continue;
            }
            bool realDoor = !string.IsNullOrEmpty(target) && IsHub(target) && !tp.name.StartsWith("door_dream", StringComparison.Ordinal);
            if (realDoor)
            {
                enter.Add(tp.name);
                continue;
            }
            if (tp.gameObject.activeInHierarchy)
            {
                tp.SetIsInactive(true);
            }
            else
            {
                // 还没 Awake（物体关着）：只改字段，Awake 时它自己按字段关掉交互。
                tp.isInactive = true;
            }
            quiet.Add(tp.name);
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作场景的门 / hk1 doors: {sceneName}：{string.Join("，", exits)}；"
            + $"关掉交互 / made non-interactive: {(quiet.Count == 0 ? "-" : string.Join("，", quiet))}；进入 / enter: {(enter.Count == 0 ? "-" : string.Join("，", enter))}"
            + $"；第 10 层改到第 13 层 / moved from layer 10 to 13: {(relayered.Count == 0 ? "-" : string.Join("，", relayered))}");
    }

    /// <summary>前作的转场层（GlobalEnums.PhysLayers.TRANSITION_GATES，丝之歌的枚举里没有）。</summary>
    private const int Hk1TransitionGates = 10;

    /// <summary>
    /// 模糊背景板：丝之歌给它换上自己的屏幕空间模糊材质（LightBlurredBackground），前作的板排序值是 0，
    /// 盖在同一排序层的前景上，整个画面都发糊（实机截图核实）。排到最底下，背景照前作那样糊、前景清楚。
    /// </summary>
    internal static void LowerBlurPlanes(GameObject[] roots)
    {
        foreach (BlurPlane bp in roots.SelectMany(r => r.GetComponentsInChildren<BlurPlane>(true)))
        {
            MeshRenderer mr = bp.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sortingOrder = short.MinValue;
            }
        }
    }

    /// <summary>到了前作神居的一个房间（大黄蜂已经放进来）。</summary>
    internal static void OnArrive(string scene)
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：到了 {scene}（到现在醒来时改过的 FSM / fsms fixed on awake so far: {awakeFixed}，按模板重建的 / template inits {Hk1TemplatePromptPatch.calls}，提示 / prompt prefab {(PromptPrefab() != null ? "ok" : "-")}）");
        if (GodhomeConfig.DebugMode.Value)
        {
            LogCameras(SceneManager.GetActiveScene());
        }
        if (scene == Workshop)
        {
            Hk1Hall.OnArrive();
        }
        else
        {
            Hk1Doors.OnArrive(scene);
        }
    }

    /// <summary>醒来时改过的 FSM 个数（到达时记进日志）。</summary>
    private static int awakeFixed;

    /// <summary>关着的物体第一次激活时 FSM 才 Awake（模板 FSM 这时按模板重建，加载时的改动会丢）：醒来时再改一次。</summary>
    internal static void OnFsmAwake(PlayMakerFSM pm)
    {
        if (!Active || pm == null)
        {
            return;
        }
        try
        {
            if (IsHk1Scene(pm.gameObject.scene.name) && FixFsm(pm))
            {
                awakeFixed++;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作 FSM 醒来时改动出错 / hk1 fsm fix on awake failed ({pm.name}): {e.Message}");
        }
    }

    /// <summary>
    /// 前作 FSM 里丝之歌接不上的几处（3、4 见函数里的注释：回程门的淡入、长椅的护符提示）：
    /// 1. 交互 FSM（npc_control、inspect_region）在“Can Talk?”里调 HeroController.CanTalk，丝之歌没有这个方法
    ///    （反编译 HeroController.cs 核实）。换成丝之歌的 CanInteract（我们的界面开着时也不行）。
    /// 2. ShowPromptMarker 生成前作的上键提示（Arrow Prompt New），丝之歌的 PromptMarker 要的 NestedFadeGroup 它没有，
    ///    OnEnable / Show / Hide 都空引用（Player.log 核实），异常一路抛到发事件的人。换成丝之歌自己的交互提示
    ///    （InteractManager.interactPromptPrefab，标签读丝之歌的 Prompts 表：Inspect、Challenge……）。
    /// </summary>
    private static bool FixFsm(PlayMakerFSM pm)
    {
        Fsm f = pm.Fsm;
        // 还没醒的（关着的物体上）等它 Awake 时再改（OnFsmAwake）。
        if (f == null || f.States == null || !f.Initialized)
        {
            return false;
        }
        bool changed = false;
        // 3. 回程门（TransitionPoint.customFade，游戏自己不淡入）上前作的 FSM 负责淡入：门前是“Boss Sequence Finish”的“Fade In”，
        //    雕像前是“Return from boss”的“Transition Wait”（放前作的梦境转场特效）。它们发的是前作的事件，丝之歌 HUD 上的整屏淡入淡出
        //    （Screen Fader）收不到，回来一直黑（实测：这两个状态走过了，淡入淡出的 FSM 还停在 Scene Fade Out）。
        //    在这两个状态最前面加一步，照丝之歌自己的做法淡入（GameManager.FadeSceneIn：镜头淡入 + Screen Fader 的 SCENE FADE IN）。
        string? fadeState = pm.FsmName switch { "Boss Sequence Finish" => "Fade In", "Return from boss" => "Transition Wait", _ => null };
        if (fadeState != null && f.GetState(fadeState) is FsmState fadeIn && !fadeIn.Actions.Any(a => a is Hk1FadeSceneIn))
        {
            fadeIn.Actions = new FsmStateAction[] { new Hk1FadeSceneIn() }.Concat(fadeIn.Actions).ToArray();
            changed = true;
        }
        // 4. 前作长椅（Bench Control）坐下后的“Charm Prompt?”：前作是没护符（hasCharm 假）或提示过（charmBenchMsg 真）就 SKIP，
        //    否则生成前作的护符装备提示（Charm Equip Msg：小骑士打开物品栏换护符，文字是前作的键）。丝之歌的 PlayerData 没有这两个字段，
        //    丝之歌的 PlayerDataBoolTest 查不到字段时一个事件都不发（反编译 PlayerDataBoolTest.OnEnter），于是每次坐下都弹（玩家反馈）。
        //    在这个状态最前面直接发 SKIP，和前作没护符时一样（发了事件，后面的动作就不跑：FsmState.ActivateActions）。
        if (pm.FsmName == "Bench Control" && f.GetState("Charm Prompt?") is FsmState charm && !charm.Actions.Any(a => a is Hk1SendEvent))
        {
            charm.Actions = new FsmStateAction[] { new Hk1SendEvent { EventName = "SKIP" } }.Concat(charm.Actions).ToArray();
            changed = true;
        }
        foreach (FsmState st in f.States)
        {
            FsmStateAction[] acts = st.Actions;
            if (acts == null)
            {
                continue;
            }
            // Only the gate used for this return reaches these states. Its own
            // "Gotten Up" action re-enables the correct statue's Inspect object.
            if (pm.gameObject.scene.name == Workshop && pm.FsmName == "Return from boss"
                && st.Name is "Transition In" or "Get Up Wait" && !acts.Any(a => a is Hk1StatueReturnReady))
            {
                string next = st.Name == "Transition In" ? "DREAM WAKE" : "FINISHED";
                acts = acts.Concat(new[] { new Hk1StatueReturnReady { EventName = next } }).ToArray();
                changed = true;
            }
            for (int i = 0; i < acts.Length; i++)
            {
                FsmStateAction a = acts[i];
                if (pm.FsmName == "Bench Control" && st.Name == "Save Game"
                    && a is HutongGames.PlayMaker.Actions.SendMessage save && save.functionCall?.FunctionName == "SaveGame")
                {
                    acts[i] = new Hk1SaveBench();
                    changed = true;
                }
                else if (a is ShowPromptMarker spm && (spm.prefab?.Value == null || NeedsOurPrompt(spm.prefab.Value)))
                {
                    acts[i] = new Hk1ShowPrompt { Label = spm.labelName, SpawnPoint = spm.spawnPoint, Store = spm.storeObject };
                    changed = true;
                }
                else if (a != null && a.GetType().Name == "CallMethodProper" && MethodNameOf(a) == "CanTalk")
                {
                    // 结果存到原动作自己的变量里（不是写死的“Talk Allowed”），每帧检查的照样每帧检查（审查）。
                    string? store = Traverse.Create(a).Field("storeResult").GetValue<FsmVar>()?.variableName;
                    acts[i] = new Hk1CanTalk
                    {
                        Store = pm.FsmVariables.GetFsmBool(string.IsNullOrEmpty(store) ? "Talk Allowed" : store),
                        EveryFrame = Traverse.Create(a).Field("EveryFrame").GetValue<bool>(),
                    };
                    changed = true;
                }
            }
            if (changed)
            {
                st.Actions = acts;
            }
        }
        return changed;
    }

    private static string? MethodNameOf(FsmStateAction a)
    {
        object? v = AccessTools.Field(a.GetType(), "methodName")?.GetValue(a);
        return v is FsmString s ? s.Value : v as string;
    }

    private static readonly AccessTools.FieldRef<InteractManager, PromptMarker> PromptField = AccessTools.FieldRefAccess<InteractManager, PromptMarker>("interactPromptPrefab");
    private static readonly AccessTools.FieldRef<PromptMarker, TeamCherry.NestedFadeGroup.NestedFadeGroup> GroupField =
        AccessTools.FieldRefAccess<PromptMarker, TeamCherry.NestedFadeGroup.NestedFadeGroup>("group");

    /// <summary>丝之歌自己的交互提示（常驻的 InteractManager 上）。</summary>
    internal static GameObject? PromptPrefab()
    {
        InteractManager im = ManagerSingleton<InteractManager>.UnsafeInstance;
        PromptMarker? p = im != null ? PromptField(im) : null;
        return p != null ? p.gameObject : null;
    }

    /// <summary>前作的提示：没有 PromptMarker，或者 PromptMarker 缺丝之歌要的淡入淡出组。</summary>
    private static bool NeedsOurPrompt(GameObject prefab)
    {
        PromptMarker pm = prefab.GetComponent<PromptMarker>();
        return pm == null || GroupField(pm) == null;
    }

    // ───────────────────────────── 前作的模糊背景 ─────────────────────────────

    /// <summary>诊断：模糊背景板和镜头（全屏发糊时看这个）。</summary>
    internal static void LogCameras(Scene scene)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (BlurPlane bp in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BlurPlane>(true)))
            {
                MeshRenderer mr = bp.GetComponent<MeshRenderer>();
                sb.Append($" plane {bp.name} z={bp.transform.position.z:0.0} active={bp.isActiveAndEnabled} layer={bp.gameObject.layer}");
                if (mr != null)
                {
                    sb.Append($" sorting={mr.sortingLayerName}({mr.sortingLayerID})/{mr.sortingOrder} enabled={mr.enabled} mat={(mr.sharedMaterial != null ? mr.sharedMaterial.name + "/" + (mr.sharedMaterial.shader != null ? mr.sharedMaterial.shader.name : "-") + "/q" + mr.sharedMaterial.renderQueue : "-")} bounds={mr.bounds.size.x:0}x{mr.bounds.size.y:0};");
                }
            }
            sb.Append($" closest={(BlurPlane.ClosestBlurPlane != null ? BlurPlane.ClosestBlurPlane.name + "@" + BlurPlane.ClosestBlurPlane.PlaneZ.ToString("0.0") + " in " + BlurPlane.ClosestBlurPlane.gameObject.scene.name : "-")} count={BlurPlane.BlurPlaneCount};");
            foreach (Camera c in Camera.allCameras)
            {
                sb.Append($" cam {c.name} depth={c.depth} z={c.transform.position.z:0.0} near={c.nearClipPlane:0.0} far={c.farClipPlane:0.0} mask=0x{c.cullingMask:X} rt={(c.targetTexture != null ? c.targetTexture.name : "-")};");
            }
            HeroController hero = HeroController.SilentInstance;
            Renderer? hr = hero != null ? hero.GetComponent<Renderer>() : null;
            if (hr != null && hero != null)
            {
                sb.Append($" hero sorting={hr.sortingLayerName}({hr.sortingLayerID})/{hr.sortingOrder} z={hero.transform.position.z:0.000} mat={(hr.sharedMaterial != null ? (hr.sharedMaterial.shader != null ? hr.sharedMaterial.shader.name : "-") + "/q" + hr.sharedMaterial.renderQueue : "-")};");
            }
            // 场景的光：前作场景管理器带来的值、现在实际的环境光和饱和度、角色暗角的等级（整张图发暗时看这个）。
            CustomSceneManager? csm = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<CustomSceneManager>(true)).FirstOrDefault();
            if (csm != null)
            {
                sb.Append($" scene manager: zone={(int)csm.mapZone}/{csm.mapZone} type={csm.sceneType} dark={csm.darknessLevel} noLantern={csm.noLantern}"
                    + $" sat={csm.saturation:0.00} ignorePlat={csm.ignorePlatformSaturationModifiers} ambient={csm.defaultColor} x{csm.defaultIntensity:0.00}"
                    + $" heroLight={csm.heroLightColor} override={Hk1SceneManagerAwakePatch.OverrideColors(csm)}"
                    + $" curves={(csm.redChannel != null ? csm.redChannel.length : -1)}/{(csm.greenChannel != null ? csm.greenChannel.length : -1)}/{(csm.blueChannel != null ? csm.blueChannel.length : -1)};");
            }
            GameCameras? gc = GameCameras.instance;
            sb.Append($" now: ambient={RenderSettings.ambientLight} sat={(gc != null && gc.colorCorrectionCurves != null ? gc.colorCorrectionCurves.saturation.ToString("0.00") : "-")}");
            GameObject? vignette = GameObject.FindGameObjectWithTag("Vignette");
            PlayMakerFSM? dark = vignette != null ? FSMUtility.LocateFSM(vignette, "Darkness Control") : null;
            if (dark != null)
            {
                sb.Append($" vignette darkness={dark.FsmVariables.GetFsmInt("Darkness Level")?.Value} state={dark.ActiveStateName};");
            }
            sb.Append(" sorting layers: " + string.Join(",", SortingLayer.layers.Select(l => $"{l.name}={l.id}/{l.value}")));
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居的镜头和模糊板 / hk1 cameras and blur planes ({scene.name}):{sb}");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 记镜头出错 / camera log failed: {e.Message}");
        }
    }
}

/// <summary>Bridge the HK1 wake event once Silksong has returned control to the player.</summary>
internal sealed class Hk1StatueReturnReady : FsmStateAction
{
    public string EventName = "";

    public override void OnUpdate()
    {
        GameManager gm = GameManager.UnsafeInstance;
        HeroController hero = HeroController.SilentInstance;
        if (ModeController.Instance?.State != LoomState.Lobby || gm == null || !GodhomeSave.IsGodhome(gm) || gm.IsInSceneTransition
            || gm.GameState != GlobalEnums.GameState.PLAYING || Hk1Lobby.InteractionBlocked
            || hero == null || !hero.acceptingInput || hero.controlReqlinquished || !hero.CanInteract())
        {
            return;
        }
        Finish();
        Fsm.Event(FsmEvent.GetFsmEvent(EventName));
    }
}

/// <summary>[Godhome] 进状态就发一个事件（这个状态后面的动作不再跑）。前作 FSM 里丝之歌判不了的分支用它直接走。</summary>
internal sealed class Hk1SendEvent : FsmStateAction
{
    public string EventName = "";

    public override void OnEnter()
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作 FSM 直接走 / hk1 fsm shortcut: {Owner?.name}|{Fsm?.Name} [{State?.Name}] → {EventName}");
        Finish();
        Fsm?.Event(FsmEvent.GetFsmEvent(EventName));
    }
}

/// <summary>前作回程门 FSM 的“Fade In”里加的一步：照丝之歌进场景的做法淡入（镜头 + HUD 上的 Screen Fader），马上结束。</summary>
internal sealed class Hk1FadeSceneIn : FsmStateAction
{
    public override void OnEnter()
    {
        try
        {
            FadeInIfDark("前作回程门 / hk1 return gate " + Owner?.name);
        }
        catch (System.Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作回程门淡入出错 / hk1 return gate fade-in failed: {e.Message}");
        }
        Finish();
    }

    /// <summary>
    /// 整屏还黑着（HUD 上的 Screen Fader 停在 Scene Fade Out）就照丝之歌进场景的做法淡入（GameManager.FadeSceneIn：镜头 + Screen Fader）。
    /// 已经亮了（游戏自己淡入过、兜底补过）就不再来一次（Codex 审查）。返回这次有没有淡入。
    /// 前作的回程门、温泉的入口门都是“自己淡入”的门（TransitionPoint.customFade），丝之歌进场时不替它们淡入。
    /// </summary>
    internal static bool FadeInIfDark(string who)
    {
        GameManager gm = GameManager.instance;
        if (gm == null)
        {
            return false;
        }
        string? fader = gm.screenFader_fsm != null ? gm.screenFader_fsm.ActiveStateName : null;
        bool dark = fader == "Scene Fade Out";
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作场景淡入 / hk1 fade-in（{who}，淡入淡出 / fader {fader ?? "-"}{(dark ? "" : "，已经亮了，跳过 / already visible, skipped")}）");
        if (dark)
        {
            gm.FadeSceneIn();
        }
        return dark;
    }
}

/// <summary>
/// 代替前作的 ShowPromptMarker：生成丝之歌自己的交互提示，标签照前作写的（Inspect、Challenge……丝之歌的 Prompts 表里都有），
/// 跟着前作放提示的那个点（丝之歌的提示画在 HUD 镜头上，要 SetFollowing 才在对的位置，InteractManager 也是这么做的）。
/// 收起照旧用前作 FSM 里的 HidePromptMarker（丝之歌的 PromptMarker.Hide）。
/// </summary>
internal sealed class Hk1ShowPrompt : FsmStateAction
{
    /// <summary>已经记过日志的标签（每种只记一次）。</summary>
    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal);

    public FsmString? Label;
    public FsmGameObject? SpawnPoint;
    public FsmGameObject? Store;

    public override void OnEnter()
    {
        try
        {
            GameObject? prefab = Hk1Lobby.PromptPrefab();
            GameObject? at = SpawnPoint?.Value;
            if (prefab != null && at != null)
            {
                GameObject go = Store?.Value != null ? Store.Value : prefab.Spawn();
                if (Store != null)
                {
                    Store.Value = go;
                }
                PromptMarker pm = go.GetComponent<PromptMarker>();
                if (pm != null)
                {
                    string key = PromptKey(Label?.Value);
                    pm.SetLabel(key);
                    pm.SetOwner(Owner);
                    pm.SetFollowing(at.transform, Vector3.zero);
                    pm.Show();
                    // 每种标签第一次显示时记下：FSM 里写的、换成的键、提示上实际的字（核对“都变成交谈”用）。
                    if (Shown.Add($"{Label?.Value}|{key}"))
                    {
                        string text = go.GetComponentInChildren<TMProOld.TMP_Text>(true)?.text ?? "-";
                        GodhomePlugin.Log.LogInfo($"[Godhome] 前作交互提示 / hk1 prompt: {FsmDump.PathOf(Owner.transform)}|{Fsm?.Name} 写的 / label \"{Label?.Value}\" → 键 / key {key} → 显示 / shows \"{text}\"");
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作的交互提示出错 / hk1 prompt failed: {e.Message}");
        }
        Finish();
    }

    /// <summary>
    /// 前作写的提示（Challenge、Inspect、Listen、Enter、Exit……）丝之歌的 Prompts 表里都有，照用；
    /// 表里没有的换成最接近的（前作屋顶的 Ascend_GG → Ascend），空的、认不出的用 Inspect。
    /// </summary>
    internal static string PromptKey(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return "Inspect";
        }
        string key = label!.ToUpperInvariant();
        if (TeamCherry.Localization.Language.Has(key, "Prompts"))
        {
            return label;
        }
        if (key.StartsWith("ASCEND", StringComparison.Ordinal))
        {
            return "Ascend";
        }
        if (key.StartsWith("DESCEND", StringComparison.Ordinal))
        {
            return "Descend";
        }
        return "Inspect";
    }
}

/// <summary>
/// 前作场景里用模板的 FSM（门、雕像的 npc_control，牌子的 inspect_region……）：PlayMaker 按模板建 FSM 时，
/// 只有模板里标了“在检视面板显示”的变量才换成物体自己写的值（FsmVariables.OverrideVariableValues，反编译核实）。
/// 前作在每个物体上写的提示（Prompt Name：门和雕像是 Challenge，牌子是 Inspect，侍者是 Listen，读前作场景文件核实）
/// 到了丝之歌没换上，用的是模板的默认值（实机：门和雕像前都显示“交谈”）。
/// 只管前作神居的场景（神居存档、用前作神居时）：模板建好后把物体自己写的 Prompt Name 补回去。普通存档 Hk1Lobby.Active 是 false，直接返回。
/// </summary>
[HarmonyPatch(typeof(PlayMakerFSM), "InitTemplate")]
internal static class Hk1TemplatePromptPatch
{
    internal const string PromptVar = "Prompt Name";

    private static readonly AccessTools.FieldRef<PlayMakerFSM, Fsm> FsmRef = AccessTools.FieldRefAccess<PlayMakerFSM, Fsm>("fsm");

    /// <summary>每种改法只记一次日志（几十个雕像是同一种）。</summary>
    private static readonly HashSet<string> logged = new(StringComparer.Ordinal);

    /// <summary>前作场景里按模板重建的 FSM 个数（到达前作房间时记进日志）。</summary>
    internal static int calls;

    private static void Prefix(PlayMakerFSM __instance, out string? __state)
    {
        __state = null;
        if (!Hk1Lobby.Active || __instance == null)
        {
            return;
        }
        try
        {
            if (Hk1Assets.Has(__instance.gameObject.scene.name))
            {
                __state = FsmRef(__instance)?.Variables?.FindFsmString(PromptVar)?.Value;
                calls++;
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作模板 FSM 读提示出错 / hk1 template prompt read failed ({__instance.name}): {e.Message}");
        }
    }

    private static void Postfix(PlayMakerFSM __instance, string? __state)
    {
        if (string.IsNullOrEmpty(__state) || __instance == null)
        {
            return;
        }
        try
        {
            FsmString? v = __instance.FsmVariables?.FindFsmString(PromptVar);
            if (v == null || v.Value == __state)
            {
                // 模板重建后本来就是物体自己的值（或者没有这个变量）：每个模板记一次，核对这个补丁确实跑过。
                string t = __instance.FsmTemplate != null ? __instance.FsmTemplate.name : "-";
                if (logged.Add($"{t}|same|{__state}"))
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome] 前作模板 FSM 的提示不用补 / hk1 template prompt unchanged: {t}（{__instance.gameObject.scene.name}/{__instance.name}）{(v == null ? "没有这个变量 / no such variable" : "\"" + v.Value + "\"")}");
                }
                return;
            }
            string was = v.Value;
            v.Value = __state;
            string template = __instance.FsmTemplate != null ? __instance.FsmTemplate.name : "-";
            if (logged.Add($"{template}|{was}|{__state}"))
            {
                bool? shown = __instance.FsmTemplate?.fsm?.Variables?.FindFsmString(PromptVar)?.ShowInInspector;
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作模板 FSM 的提示补回物体自己的 / hk1 template prompt restored: {template}（{__instance.gameObject.scene.name}/{__instance.name}）{was} → {__state}（模板里显示 / exposed in template: {shown?.ToString() ?? "-"}）");
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作模板 FSM 补提示出错 / hk1 template prompt restore failed ({__instance.name}): {e.Message}");
        }
    }
}

/// <summary>代替前作的 HeroController.CanTalk：丝之歌的 CanInteract，我们的界面开着时也不行。</summary>
internal sealed class Hk1CanTalk : FsmStateAction
{
    public FsmBool? Store;

    /// <summary>原动作勾了 EveryFrame：每帧都重新检查，不结束。</summary>
    public bool EveryFrame;

    public override void OnEnter()
    {
        Check();
        if (!EveryFrame)
        {
            Finish();
        }
    }

    public override void OnUpdate() => Check();

    private void Check()
    {
        HeroController hero = HeroController.SilentInstance;
        bool ok = hero != null && hero.CanInteract() && !Hk1Lobby.InteractionBlocked;
        if (Store != null)
        {
            Store.Value = ok;
        }
    }
}
