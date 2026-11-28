using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Godhome;

internal enum LoomState
{
    Off,
    ToLobby,
    Lobby,
    ToFight,
    Fight,
    Won,

    /// <summary>万神殿连战：正在去休息点（真实的长椅房间）。</summary>
    ToRest,

    /// <summary>万神殿连战：在休息点（坐长椅回血，从任意出口离开就进下一场）。</summary>
    Rest,
}

/// <summary>战斗结果，自检程序用。</summary>
internal enum FightResult
{
    None,
    Won,
    Died,
    Abandoned,
}

/// <summary>
/// [Godhome] 织忆圣所状态机（第二版：神居存档）。
/// 只在神居存档（PlayerData.bossRushMode = true）里工作：读档后接管 → 大厅 → 选 Boss → 战斗 → 回大厅。没有“离开”。
/// 存档模型：大厅里照常存档（写实时数据，换装能保存）；开战前给大厅状态拍快照，战斗中存档写快照，
/// 回大厅时把快照拷回实时数据再丢掉。战斗里发生的一切都不会留在神居存档里。
/// 场景切换都用游戏自己的 BeginSceneTransition：进场以场地的 door_dreamEnter 为入口，回程以 door_dreamReturn 为入口
/// （见 Arena 和 docs/arena.md）。
/// </summary>
internal sealed class ModeController : MonoBehaviour
{
    /// <summary>
    /// 赢了以后由场地控制器（BossSceneController）等 Arena.BossesDeadWait 秒再回程（OnBossSceneComplete）。
    /// 人正在危险复活、转场时它会一直等；超过这么久还没回程就自己走（兜底）。
    /// </summary>
    internal const float WonTimeout = Arena.BossesDeadWait + 10f;
    private const float TransitionTimeout = 60f;

    internal static ModeController? Instance { get; private set; }

    internal LoomState State { get; private set; } = LoomState.Off;

    internal bool InMode => State != LoomState.Off;

    internal BossDef? CurrentBoss { get; private set; }

    /// <summary>这一场的难度：0 素织（原版）、1 重织（受到的伤害翻倍）、2 断丝（挨一下就断）。</summary>
    internal int CurrentTier { get; private set; }

    /// <summary>从哪尊雕像（或哪扇万神殿的门，见 DoorSpot）开的战：回大厅时从它前面的回程门进来。名录、自检开的战没有。</summary>
    private string? returnStatue;

    /// <summary>前作神居：战斗里死了，复活后回到哪尊雕像、哪扇门前（OnHeroDying 记下，复活转场里用掉）。</summary>
    private string? deathReturn;

    internal FightResult LastResult { get; private set; }

    /// <summary>上次胜利是怎么判定的："bosses"（场地控制器登记的 Boss 都死了）或 "end …"（表里的结束状态里的 EndGGBossScene）。</summary>
    internal string LastWinHow { get; private set; } = "";

    /// <summary>上一次赢了以后是兜底回程的（场地控制器 WonTimeout 秒没回程）。自检记下来。</summary>
    internal bool LastWonFallback { get; private set; }

    /// <summary>当前 Boss 的血量组件（只给自检、调试用，判胜不看它；用之前调 ScanBoss）。</summary>
    internal IReadOnlyList<HealthManager> BossHealth => bossHms;

    internal float FightElapsed => State switch
    {
        LoomState.Fight => Time.unscaledTime - fightStart,
        LoomState.Won => fightEnd - fightStart,
        _ => 0f,
    };

    /// <summary>最近一次由我们发起的存档（大厅存档、SaveNow）的结果：null = 还没回来。</summary>
    internal bool? LastSaveResult { get; private set; }

    /// <summary>这一场 Boss 倒下以后，游戏自己要切去别的场景（演出、记忆、结局）被改道回圣所的次数。</summary>
    internal int WinRedirects { get; private set; }

    /// <summary>由我们发起的存档次数（自检用来等“这一次”的结果）。</summary>
    internal int SaveCount { get; private set; }

    private readonly List<HealthManager> bossHms = new();
    private readonly HeroRecovery heroRecovery = new();
    private bool ourTransition;

    /// <summary>暂停中要发起的场景切换，继续游戏后再切。记下当时的状态和场次，变了就作废。</summary>
    private (string Scene, string Gate, bool Respawning, LoomState State, int Serial)? pendingTransition;

    /// <summary>暂停中请求的回大厅（原因），继续游戏后再执行。</summary>
    private string? pendingReturn;
    private bool pendingReturnFault;
    private float stateSince;
    private float fightStart;
    private float fightEnd;

    /// <summary>
    /// 正在进入或进行一场战斗（整场战斗期间，场景物体的 Start 读持久记录时都要看它，见 PersistencePatches）。
    /// </summary>
    internal bool FightLoading => (State is LoomState.ToFight or LoomState.Fight) && CurrentBoss != null;
    private bool retriedTransition;

    /// <summary>转场重试后还是没到（或者一直卡在转场里）：已经放弃过一次（作废连战、解开束缚），换状态时清掉。</summary>
    private bool transitionGaveUp;

    /// <summary>这场用了调试辅助（自检、秒杀键、调试无敌）：不记尝试、通关、最快时间（Codex 审查 #12）。</summary>
    private bool fightAssisted;
    private bool attemptCounted;
    private readonly HashSet<string> ignoredHms = new();

    /// <summary>每场战斗的编号。延后执行的场景调整步骤每次等待后都核对它，过期的不再执行（Codex 复审）。</summary>
    private int fightSerial;

    // 回大厅：快照拷回后，到达大厅之前不保存场景状态；拍快照时的运行时复活点到达后再恢复一次。
    private RespawnState? lobbyRespawn;
    private bool restoreFailed;
    private int lobbyAttempts;

    // 每次读进神居存档只补一次满配（按数据对象认）。
    private PlayerData? kitAppliedTo;

    /// <summary>正在进行的万神殿连战（没有就是 null）。</summary>
    internal PantheonRun? Run { get; private set; }

    /// <summary>最近一次完成的万神殿结算（自检核对用）。</summary>
    internal PantheonResult? LastPantheonResult { get; private set; }

    /// <summary>最近一次失败（死亡、放弃）的万神殿和原因（自检核对用）。</summary>
    internal string LastPantheonFail { get; private set; } = "";

    /// <summary>回到门前以后要弹出的结算。</summary>
    private PantheonResult? pendingResult;

    /// <summary>这一场是万神殿连战里的一场：不记单个 Boss 的尝试和通关（前作万神殿也不算雕像的战绩）。</summary>
    private bool runFight;

    /// <summary>前作神居里在三个房间之间走门（前厅 ↔ 手办屋 ↔ 屋顶）：还在大厅里，到了不存档、不提示。</summary>
    private bool hubWalk;

    /// <summary>有前作时读档进来的本作场景（存档里的复活点，马上转去前厅）：黑幕挡着，不搭圣所。</summary>
    private Scene hk1EntryScene;

    private bool Hk1EntryShowing => hk1EntryScene.IsValid() && hk1EntryScene.isLoaded;

    private void Awake()
    {
        Instance = this;
        SceneManager.sceneLoaded += OnUnitySceneLoaded;
        SceneManager.sceneUnloaded += OnUnitySceneUnloaded;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
        {
            Bindings.Clear("mode controller destroyed");
        }
        heroRecovery.Clear();
        SceneManager.sceneLoaded -= OnUnitySceneLoaded;
        SceneManager.sceneUnloaded -= OnUnitySceneUnloaded;
    }

    /// <summary>这一场的编号（巡视用来确认还是同一场战斗）。</summary>
    internal int FightSerial => fightSerial;

    /// <summary>
    /// 大厅这个场景实例真的卸载了（按场景句柄认）：用到借来精灵的物体已经跟着销毁，这时才放图集（Codex 审查 v0.6.0 #10）。
    /// </summary>
    private void OnUnitySceneUnloaded(Scene scene)
    {
        try
        {
            if (Sanctum.LobbySceneRef.handle != 0 && scene.handle == Sanctum.LobbySceneRef.handle)
            {
                ReleaseSanctum("lobby unloaded / 大厅卸载");
            }
            // 前作神居：离开去战场（或休息点）时放掉门、雕像借的图集。在三个房间之间走门时留着（状态是 ToLobby / Lobby）。
            else if (Hk1Lobby.Active && Hk1Lobby.IsHub(scene.name) && State is not (LoomState.Lobby or LoomState.ToLobby))
            {
                ReleaseSanctum("hk1 hub unloaded / 前作神居卸载");
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 大厅卸载时清理出错 / cleanup on lobby unload failed: {e}");
        }
    }

    /// <summary>大厅实例已经不在了（卸载了或者从没准备过）：可以放掉图集。</summary>
    private static bool LobbyGone => !Sanctum.LobbySceneRef.IsValid() || !Sanctum.LobbySceneRef.isLoaded;

    /// <summary>前作神居的房间还有加载着的（读档时新场景可能已经加载、活动场景还是标题）。</summary>
    private static bool AnyHubLoaded()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (s.isLoaded && Hk1Lobby.IsHub(s.name))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 场景刚加载完（物体 Awake 已跑、Start 没跑，大黄蜂还没放进来，画面是黑的）。只在神居存档里：
    /// - 战斗的场景（入口、宿主、附加的 Boss 场景）：改成神居场地（Arena.Prepare：入场门、场地控制器、堵出口、清场、补丁）。
    /// - 大厅：马上藏起原房间、搭好圣所的地形、镜头和回程门，黑幕盖住画面，等到达后借图集搭画面（Sanctum.BuildRoutine）再揭开。
    /// 普通存档进这些房间什么都不改。
    /// </summary>
    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            GameManager gm = GameManager.UnsafeInstance;
            if (gm == null || !GodhomeSave.IsGodhome(gm))
            {
                return;
            }
            if (FightLoading && ScenesOf(CurrentBoss!).Contains(scene.name))
            {
                Arena.Prepare(scene);
                // 前作神居：照前作，进战场时前作的连战转场先盖住画面，人进来后揭开（Hk1Battle）。
                if (State == LoomState.ToFight && scene.name == CurrentBoss!.EntryScene)
                {
                    Hk1Battle.Cover(scene);
                }
                return;
            }
            // 前作的温泉（万神殿的休息点，Hk1Spa）：只在去休息点、在休息点时处理。
            if (scene.name == Hk1Lobby.Spa)
            {
                if (Hk1Lobby.Active && State is LoomState.ToRest or LoomState.Rest)
                {
                    Hk1Spa.Prepare(scene, Run?.Current);
                }
                return;
            }
            if (IsForeignAdditive(scene.name))
            {
                // 战斗中按原条件附加加载了这一场以外的场景（宿主场景还带着别的 Boss 场景时看得出来，例如沙克拉的 Greymoor_08_boss）。
                // 这里记真正加载了的（Codex 审查 0.9.0 第二轮 #11）；要不加载就在表里写 arena.noAdditive。
                // 措辞不说“附加”：进错主场景时也会到这里（第三轮 #12），括号里是加载方式。
                GodhomePlugin.Log.LogWarning($"[Godhome] 战斗中加载了这一场以外的场景 / a scene outside this fight was loaded during it: {scene.name} ({mode})");
            }
            // 前作神居的三个房间（不在休息、战斗里）：关掉前作的复活触发器、修交互，布置雕像或门。
            if (Hk1Lobby.IsHub(scene.name))
            {
                if (Hk1Lobby.Decide() && State is not (LoomState.ToRest or LoomState.Rest or LoomState.ToFight or LoomState.Fight))
                {
                    Hk1Lobby.Prepare(scene);
                }
                return;
            }
            if (Run != null && State is (LoomState.ToRest or LoomState.Rest) && scene.name == PantheonCatalog.RestScene)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var pm in root.GetComponentsInChildren<PlayMakerFSM>(true))
                    {
                        PantheonRestBench.Configure(pm);
                    }
                }
                return;
            }
            if (scene.name != GodhomeConfig.LobbyScene.Value)
            {
                return;
            }
            // 万神殿的休息点借的就是这个长椅房间：保持原来的房间，不搭圣所。
            if (State is LoomState.ToRest or LoomState.Rest)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 休息点：保持原来的长椅房间 / rest stop: keeping the original bench room {scene.name}");
                return;
            }
            // 有前作：大厅是前作的神居，这里（存档里的复活点）只是落脚，接管后马上转去前厅。黑幕挡住原来的房间，不搭圣所。
            if (Hk1Lobby.Decide())
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：{scene.name} 只是落脚，接管后转去前厅 / hk1 godhome: {scene.name} is only the save's spot, going to {Hk1Lobby.Atrium} after attaching");
                hk1EntryScene = scene;
                LoomUI.Instance?.Cover(1f, () => Hk1EntryShowing && State is LoomState.Off or LoomState.ToLobby);
                return;
            }
            Sanctum.Prepare(scene);
            // 黑幕挡到画面搭好（搭建任务接手后由它管）；准备失败就不挡。
            if (Sanctum.Prepared)
            {
                LoomUI.Instance?.Cover(1f, () => Sanctum.Prepared && !Sanctum.Built && Sanctum.LobbySceneRef == scene);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 场景加载时准备出错 / prepare on scene load failed ({scene.name}): {e}");
        }
    }

    /// <summary>圣所的画面还要不要继续搭（离开大厅、模式重置了就停）。</summary>
    private bool LobbyAlive() => State == LoomState.Lobby && Sanctum.Prepared;

    /// <summary>离开大厅后（大厅场景已卸载）放掉圣所借的图集和静态引用。</summary>
    private static void ReleaseSanctum(string why)
    {
        Sanctum.Forget();
        // 挑战面板的图标用的是大厅的图集精灵：先解开（面板开着就撤掉），再还图集（Codex 审查 v0.7.0 #4）。
        // 万神殿的面板、结算也一样。
        LoomUI.Instance?.AbortResult(why);
        ChallengeMenu.ReleaseArt(why);
        PantheonMenu.ReleaseArt(why);
        PantheonResultScreen.ReleaseArt(why);
        SanctumArt.ReleaseAll(why);
        Tk2dArt.ReleaseAll(why);
        SanctumAudio.ReleaseAll(why);
    }

    /// <summary>标题画面每帧的清理（从 Update 挪出来，出错时整块兜底，不影响前面已经解除的存档拦截和模式重置）。</summary>
    private void TitleCleanup()
    {
        // 读档时新大厅可能已经加载、活动场景还是标题：大厅实例还在就不动它（它的黑幕、圣所归它自己）。
        if (LobbyGone)
        {
            if (SanctumArt.Holding || Tk2dArt.Holding || SanctumAudio.Holding || Sanctum.Prepared)
            {
                ReleaseSanctum("title / 标题画面");
            }
            // 首次接管前就回了标题（InMode 还是 false）也要撤掉大厅的黑幕（Codex 审查 v0.6.0 #7）。
            // 读档时落脚的本作场景已经加载（活动场景还是标题）：它的黑幕留着。
            if (LoomUI.Instance != null && LoomUI.Instance.CoverAlpha > 0f && !Hk1EntryShowing)
            {
                LoomUI.Instance.ClearCover();
            }
            // 雕好的石像在两次进大厅之间留着缓存，回标题才销毁（这时没有雕像在用它们）。
            StatueCarver.ReleaseAll();
            // 标题画面不留复制的挑战面板（没有就什么都不做）。
            ChallengeMenu.Destroy("title / 标题画面");
            PantheonMenu.Destroy("title / 标题画面");
            PantheonResultScreen.Destroy("title / 标题画面");
        }
        kitAppliedTo = null;
        hubWalk = false;
        // 前作神居：标题画面就开始在后台打包（第一次约 4 秒），下次进神居存档时重新决定用不用。
        if (GodhomeConfig.Hk1Enabled.Value)
        {
            Hk1Assets.StartPrepare();
        }
        if (!GameManager.instance.IsInSceneTransition && !Hk1EntryShowing && !AnyHubLoaded())
        {
            Hk1Lobby.Reset();
        }
    }

    private void Update()
    {
        GameManager gm = GameManager.UnsafeInstance;
        if (gm == null)
        {
            return;
        }
        if (gm.IsMenuScene())
        {
            // 先解除存档拦截、再清理：下面的清理万一抛异常（每帧都会再抛），拦截也不能带进接着读的普通存档（审查）。
            if (SaveGuard.BlockAll)
            {
                SaveGuard.BlockAll = false;
                GodhomePlugin.Log.LogInfo("[Godhome] 回到标题画面，解除存档拦截 / back at the title screen, saves unblocked");
            }
            if (InMode)
            {
                // 回到标题画面：战斗中存的档已经是快照，这里只需丢掉内存状态。ForceReset 每一步单独兜底，最后一定回到 Off。
                ForceReset("returned to title / 回到标题画面");
            }
            try
            {
                TitleCleanup();
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogError($"[Godhome] 标题画面清理出错 / title-screen cleanup failed: {e}");
            }
            return;
        }
        // 有快照，内存里却已经是别的存档数据（读了别的档）：快照作废，不能再按模式的规则处理别人的数据（Codex 复审）。
        if (SaveGuard.Active && SaveGuard.Stale(gm))
        {
            if (SaveGuard.Mixed(gm))
            {
                // 只换了一个：另一个可能还是战斗里改过的实时数据，回标题之前一律不存（Codex 审查 0.8.0）。
                GodhomePlugin.Log.LogError("[Godhome] 内存里的存档数据只换了一半，拦下所有存档直到回标题画面 / only one of playerData/sceneData was replaced; blocking saves until the title screen");
                SaveGuard.BlockAll = true;
                ShowNotice(L.Get("ERR_NEED_RELOAD"), 10f);
            }
            ForceReset("save data replaced / 内存里换成了别的存档数据");
            return;
        }
        if (InMode && !GodhomeSave.IsGodhome(gm))
        {
            ForceReset("not a godhome save any more / 已经不是神居存档");
            return;
        }
        // 战斗中快照没了（不该发生）：实时数据是战斗数据，不能写进神居存档。回标题重新读档之前拦下所有存档。
        if ((State is LoomState.ToFight or LoomState.Fight or LoomState.Won or LoomState.ToRest or LoomState.Rest) && !SaveGuard.Active)
        {
            GodhomePlugin.Log.LogError("[Godhome] 战斗中快照丢失，拦下所有存档直到回标题画面 / the fight snapshot is gone; blocking saves until the title screen");
            SaveGuard.BlockAll = true;
            ForceReset("fight snapshot lost / 战斗快照丢失");
            ShowNotice(L.Get("ERR_NEED_RELOAD"), 10f);
            return;
        }

        if (State == LoomState.Off)
        {
            TryAttach(gm);
            return;
        }
        GodhomeResources.Tick();

        // 暂停菜单开着：时间停了（timeScale 0），游戏的转场协程在暂停中开始会一直卡在淡出计时上
        // （GameManager.BeginSceneTransitionRoutine 用 Time.deltaTime 数 0.34 秒）。暂停期间什么都不做，
        // 战斗计时、转场超时也不算这段时间；要切的场景等继续游戏后再切。
        if (gm.IsGamePaused())
        {
            // 用真实时间数的计时都往后推（Codex 审查 v0.5.0 #4）：战斗用时、胜利后的回程兜底、转场超时。
            float dt = Time.unscaledDeltaTime;
            stateSince += dt;
            if (State is LoomState.Fight or LoomState.Won)
            {
                fightStart += dt;
            }
            if (State == LoomState.Won)
            {
                fightEnd += dt;
                wonLeavingAt += dt;
            }
            return;
        }
        if (pendingReturn is string why)
        {
            // 暂停时请求的回大厅：还原快照、清难度、切场景放在继续以后一起做（Codex 审查 v0.5.0 #1）。
            bool fault = pendingReturnFault;
            pendingReturn = null;
            pendingReturnFault = false;
            GodhomePlugin.Log.LogInfo($"[Godhome] 继续游戏，回大厅 / resumed, going back to the lobby ({why}{(fault ? "，场地出错 / arena fault" : "")})");
            ReturnToLobby(why, fault);
        }
        if (pendingTransition is (string pScene, string pGate, bool pRespawning, LoomState pState, int pSerial))
        {
            pendingTransition = null;
            if (pState != State || pSerial != fightSerial)
            {
                // 等待期间状态已经变了（死亡复活、游戏自己的转场接管）：这个请求作废（Codex 审查 v0.5.0 #6）。
                GodhomePlugin.Log.LogInfo($"[Godhome] 挂起的切场景已过期，丢弃 / dropping a stale pending transition to {pScene} (state {pState} → {State})");
            }
            else
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 继续游戏，开始切场景 / resumed, changing scene now → {pScene}@{pGate}");
                Transition(pScene, pGate, pRespawning);
            }
        }

        if (State == LoomState.Lobby)
        {
            heroRecovery.CheckLobby(gm, fightSerial);
            Hk1Lobby.InputWatch();
        }

        if (GodhomeConfig.LoomKey.Value.IsDown())
        {
            OnLoomKey();
        }

        // 万神殿用时：只数战斗这几段没暂停的时间（暂停时上面已经返回了），休息点不算。
        if (Run != null && State is LoomState.ToFight or LoomState.Fight or LoomState.Won)
        {
            Run.Elapsed += Time.unscaledDeltaTime;
        }

        // 场地：看开战判定；附加场景的控制器迟迟没建好就回大厅（不在没改过的原场景里打）。
        if (FightLoading && Arena.Watch() is string arenaError)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 场地出错，回大厅 / arena error, back to the lobby: {arenaError}");
            ShowNotice(L.Get("ERR_ARENA"), 8f);
            ReturnToLobby("arena error / 场地出错", arenaFault: true);
            return;
        }
        CheckFightHud();

        switch (State)
        {
            case LoomState.Won:
                // 前作的“出去”放了太久还没回调（协程半路出错停了：Codex 审计 0.10.3 F06）：不再等，自己走。
                // 下面的兜底计时进 FinishWon 时看到 wonLeaving 会直接返回，所以这里单独数，不靠那个协程活着。
                if (wonLeaving && Time.unscaledTime - wonLeavingAt > WonOutCap)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 前作的“出去” {WonOutCap:0} 秒没回调，自己走 / the hk1 out transition did not call back in {WonOutCap:0} s; leaving anyway");
                    wonLeaving = false;
                    Hk1Battle.AbandonOut("won-out cap / 截止时间到");
                    LastWonFallback = true;
                    LeaveWon();
                    break;
                }
                // 正常由场地控制器的 OnBossSceneComplete 回程（OnArenaComplete）；它一直没来（人在危险复活、转场）就兜底。
                if (Time.unscaledTime - fightEnd > WonTimeout)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 场地控制器 {WonTimeout:0} 秒没回程，自己走 / the arena controller did not finish in {WonTimeout:0} s; leaving anyway");
                    LastWonFallback = true;
                    FinishWon();
                }
                break;
            case LoomState.ToLobby:
            case LoomState.ToFight:
            case LoomState.ToRest:
                CheckTransitionTimeout();
                break;
        }
    }

    /// <summary>赢了以后离开战场：万神殿去下一场（或结算），单场回大厅。</summary>
    private void FinishWon()
    {
        if (State != LoomState.Won || wonLeaving)
        {
            return;
        }
        if (Hk1Battle.Usable)
        {
            // 前作神居：照前作 BossSceneController.EndSceneDelayed，先放前作的“出去”（万神殿中间 OUT、最后一场 FINAL、雕像 OUT STATUE），
            // 放完再走。等的时候状态还是 Won（兜底计时再到也不会走第二次，换了状态、换了场次就不走）。
            string ev = Run == null ? Hk1Battle.EvOutStatue : Run.IsLast ? Hk1Battle.EvFinal : Hk1Battle.EvOut;
            int serial = fightSerial;
            wonLeaving = true;
            wonLeavingAt = Time.unscaledTime;
            Hk1Battle.PlayOut(ev, () =>
            {
                if (wonLeaving && State == LoomState.Won && serial == fightSerial)
                {
                    wonLeaving = false;
                    LeaveWon();
                }
            });
            return;
        }
        LeaveWon();
    }

    /// <summary>正在放前作的“出去”，放完再离开战场（FinishWon）。换状态时清掉。</summary>
    private bool wonLeaving;

    /// <summary>开始等“出去”的时刻（暂停的时间往后推）。</summary>
    private float wonLeavingAt;

    /// <summary>
    /// 等前作“出去”回调最多多少秒（不算暂停）。协程自己最多等特效时长（8 秒以内）再加暂停，正常远远用不完；
    /// 到了就当协程已经停了，自己离开战场（Codex 审计 0.10.3 F06）。
    /// </summary>
    private const float WonOutCap = 20f;

    private void LeaveWon()
    {
        if (Run != null)
        {
            ContinueRun(null);
        }
        else
        {
            ReturnToLobby("win");
        }
    }

    /// <summary>场地控制器：Boss 都倒下了（BossSceneController.OnBossesDead，Arena 已核对是这一场）。</summary>
    private void OnArenaBossesDead(string how)
    {
        if (State == LoomState.Fight && CurrentBoss != null)
        {
            OnWin(how);
            return;
        }
        if (State == LoomState.ToFight && CurrentBoss != null)
        {
            // 人还没进场就判了胜（结束状态在加载时就走到了之类）：控制器的结束机会已经用掉（EndBossScene 防重入），
            // 这一场再也赢不了。进场后按场地出错回大厅、不记尝试（Codex 审查 0.9.0 第二轮 #2）。
            Arena.MarkFatal(fightSerial, $"进场前就判了胜 / bosses-dead before the hero entered: {how}");
            return;
        }
        // 已经在回程（Won、ToLobby……）收到的判胜不算（Codex 审查 0.9.0 #6）：记下来，出了问题查得到。
        GodhomePlugin.Log.LogWarning($"[Godhome] 不在战斗中收到判胜，不算 / bosses-dead outside a fight ignored: {how} (state {State})");
    }

    /// <summary>场地控制器：倒下后等完了（BossSceneController.OnBossSceneComplete）。人死了、正在转场时游戏不会调它。</summary>
    private void OnArenaComplete()
    {
        if (State == LoomState.Won)
        {
            FinishWon();
        }
    }

    // ───────────────────────────── 接管神居存档 ─────────────────────────────

    /// <summary>读进了神居存档、能操作了：补满配，在大厅就直接进入大厅状态，不在就送回大厅。普通存档什么都不做。</summary>
    private void TryAttach(GameManager gm)
    {
        if (!GodhomeSave.IsGodhome(gm))
        {
            return;
        }
        HeroController hero = HeroController.SilentInstance;
        if (hero == null || !gm.IsGameplayScene() || gm.IsInSceneTransition || gm.GameState != GlobalEnums.GameState.PLAYING)
        {
            return;
        }
        if (SaveGuard.Active)
        {
            // 不该有快照（上一个存档的快照在回标题时就清了）。
            GodhomePlugin.Log.LogWarning("[Godhome] 接管时还有旧快照，丢弃 / a stale snapshot was still around when attaching, discarding");
            SaveGuard.Clear();
        }
        int kitChanges = 0;
        if (!ReferenceEquals(kitAppliedTo, gm.playerData))
        {
            kitAppliedTo = gm.playerData;
            kitChanges = GodhomeSave.ApplyKit(gm.playerData);
        }
        bool justCreated = GodhomeSave.TakeJustCreated(gm.playerData);
        LastResult = FightResult.None;
        string scene = gm.GetSceneNameString();
        Hk1Lobby.Decide();
        bool repairedCutsceneData = HeroRecovery.NormalizeLoadedData(gm);
        GodhomePlugin.Log.LogInfo($"[Godhome] 接管神居存档 / attached to godhome save (slot {gm.profileID}, scene {scene}{(justCreated ? ", new" : "")}{(Hk1Lobby.Active ? ", hk1 godhome" : "")})");
        if (Lobby.Is(scene))
        {
            SetState(LoomState.Lobby);
            SetDeathRespawnToLobby();
            Heal();
            heroRecovery.ArmLobby(gm, fightSerial);
            if (Hk1Lobby.Active)
            {
                OnHk1Arrive(scene);
            }
            else
            {
                StartCoroutine(Sanctum.BuildRoutine(LobbyAlive));
            }
            ShowBanner(L.F("BANNER_LOBBY", L.Get("LOOM_NAME"), GodhomeConfig.LoomKey.Value), 5f);
            // 新建的神居存档马上存一次（游戏的神居分支不播开场、也不存档，不存的话关游戏后槽还是空的）；
            // 补了满配也存一次。这时没有快照，写的是大厅的实时数据。
            if (justCreated || kitChanges > 0 || repairedCutsceneData)
            {
                SaveNow(justCreated ? "new godhome save / 新建神居存档" : repairedCutsceneData ? "repaired cutscene state / 修复演出标记" : "kit / 补满配");
            }
        }
        else
        {
            SetState(LoomState.ToLobby);
            lobbyAttempts = 0;
            // 新建的存档、补了满配：到了大厅（OnLobbyArrive）会存（复活点照样写本作的场景，见 SaveSanitizePatch）。
            GoLobby();
        }
    }

    /// <summary>到了前作神居的一个房间：布置门或雕像（借图集、换石像），没有要等的就掀开黑幕。</summary>
    private void OnHk1Arrive(string scene, bool returningFromFight = false)
    {
        Hk1Lobby.OnArrive(scene);
        if (LoomUI.Instance != null && LoomUI.Instance.CoverAlpha > 0f)
        {
            LoomUI.Instance.FadeCover(0f, 0.3f);
        }
        if (scene == Hk1Lobby.Workshop || returningFromFight)
        {
            StartCoroutine(Hk1FadeGuard(fightSerial, scene));
        }
    }

    private void OnLoomKey()
    {
        switch (State)
        {
            case LoomState.Fight:
                // 调试放弃也受存档槽白名单限制（Codex 复审）。
                if (DebugAllowed)
                {
                    ReturnToLobby("debug key / 调试键放弃");
                }
                break;
        }
    }

    /// <summary>调试开着，而且当前存档槽在白名单里。</summary>
    private static bool DebugAllowed
    {
        get
        {
            GameManager gm = GameManager.UnsafeInstance;
            return GodhomeConfig.DebugMode.Value && gm != null && GodhomeConfig.IsSlotEnabled(gm.profileID);
        }
    }

    internal void ForceReset(string reason)
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 模式重置 / mode reset: {reason}");
        // 每一步单独兜底（审查）：前面哪一步抛异常，后面的清理（尤其是束缚、快照）照样做，最后一定回到 Off，
        // 否则模式状态会带进接着读的普通存档（死亡不致命、出口改道、成就被拦）。
        static void Step(string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogError($"[Godhome] 模式重置时这一步出错 / mode reset step failed ({what}): {e}");
            }
        }
        Step("hero recovery", heroRecovery.Clear);
        Step("bindings", () => Bindings.Clear("mode reset / 模式重置"));
        Step("snapshot", SaveGuard.Clear);
        // 万神殿连战作废（回标题、换存档：这次不记录），束缚解开，结算不再弹。
        Step("damage hook", UnhookDamage);
        if (Run != null)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿作废 / pantheon dropped: {Run}（{reason}）");
        }
        Run = null;
        runFight = false;
        pendingResult = null;
        Step("result", () => LoomUI.Instance?.AbortResult("mode reset"));
        CurrentBoss = null;
        bossHms.Clear();
        ourTransition = false;
        pendingTransition = null;
        pendingReturn = null;
        pendingReturnFault = false;
        returnStatue = null;
        deathReturn = null;
        hubWalk = false;
        Step("hk1 doors", Hk1Doors.ClearPending);
        Step("hk1 spa", Hk1Spa.ClearSetup);
        Step("hk1 battle", Hk1Battle.Clear);
        Step("arena", Arena.Clear);
        Step("challenge", () => LoomUI.Instance?.AbortChallenge("mode reset"));
        // 复制的挑战面板挂在常驻的 UICanvas 下：模式重置（回标题、换存档）时销毁，下次打开再复制。
        Step("challenge menu", () => ChallengeMenu.Destroy("mode reset / 模式重置"));
        Step("pantheon menu", () => PantheonMenu.Destroy("mode reset / 模式重置"));
        Step("result screen", () => PantheonResultScreen.Destroy("mode reset / 模式重置"));
        Step("cover", () => LoomUI.Instance?.ClearCover());
        lobbyRespawn = null;
        restoreFailed = false;
        fightSerial++;
        SetState(LoomState.Off);
        // 大厅实例还在（人还在里面，或者正在转场还没卸载）就留着圣所，卸载时（OnUnitySceneUnloaded）再放；
        // 大厅已经不在了才在这里放（Codex 审查 v0.6.0 #10：不按“是不是活动场景”猜）。
        if (LobbyGone)
        {
            Step("sanctum", () => ReleaseSanctum("mode reset / 模式重置"));
        }
        // 前作神居同理：房间都不在了才放掉前作的场景包，下次进大厅重新决定（Codex 审查）。
        if (!Hk1EntryShowing && !AnyHubLoaded())
        {
            Step("hk1 lobby", Hk1Lobby.Reset);
        }
    }

    /// <summary>由我们发起一次存档，结果记在 LastSaveResult（自检结束时用）。</summary>
    internal void SaveNow(string why)
    {
        LastSaveResult = null;
        SaveCount++;
        GameManager.instance.SaveGame(ok =>
        {
            LastSaveResult = ok;
            GodhomePlugin.Log.LogInfo($"[Godhome] 存档 / save ({why}): {ok}");
        });
    }

    // ───────────────────────────── 大厅 ─────────────────────────────

    private void GoLobby()
    {
        SetDeathRespawnToLobby();
        (string scene, string gate, bool respawning) = LobbyEntry();
        Transition(scene, gate, respawning);
    }

    /// <summary>
    /// 回大厅的入口。从雕像（或万神殿的门）开的战：照前作，以 door_dreamReturn 为入口，把那尊雕像前的回程门名写进
    /// playerData.bossReturnEntryGate，GameManager.EnterHero 换过去（GameManager.cs:3313），游戏自己把人放在雕像前。
    /// 名录、自检开的战回到长椅。要在快照拷回（RestoreForLobby）之后调，不然会被快照里的值盖掉。
    /// </summary>
    private (string Scene, string Gate, bool Respawning) LobbyEntry()
    {
        PlayerData pd = GameManager.instance.playerData;
        if (Hk1Lobby.Active)
        {
            // 前作神居：回到那尊雕像（手办屋）或那扇门（前厅、屋顶）前，用前作自己的回程门。
            (string Scene, string Gate)? back = returnStatue != null ? Hk1Lobby.ReturnTo(returnStatue) : null;
            pd.currentBossStatueCompletionKey = "";
            if (back != null)
            {
                pd.bossReturnEntryGate = back.Value.Gate;
                // 雕像打赢了：照前作，回来时铭牌上放新得的那一档的动画（BossStatue.Start 读这两个）。
                Hk1Hall.Slot? slot = returnStatue != null ? Hk1Hall.SlotNamed(returnStatue) : null;
                if (slot != null && LastResult == FightResult.Won && !string.IsNullOrEmpty(slot.Pd))
                {
                    pd.currentBossStatueCompletionKey = slot.Pd;
                    pd.bossStatueTargetLevel = CurrentTier;
                }
                return (back.Value.Scene, Arena.ReturnGate, false);
            }
            pd.bossReturnEntryGate = "";
            return (Hk1Lobby.Atrium, Hk1Lobby.ArriveMarker, true);
        }
        string? gate = returnStatue != null ? Sanctum.ReturnGateName(returnStatue) : null;
        if (gate != null)
        {
            pd.bossReturnEntryGate = gate;
            return (GodhomeConfig.LobbyScene.Value, Arena.ReturnGate, false);
        }
        pd.bossReturnEntryGate = "";
        return (GodhomeConfig.LobbyScene.Value, GodhomeConfig.LobbyMarker.Value, true);
    }

    /// <summary>
    /// 回大厅前把快照拷回实时数据（战斗里的改动全部撤掉），从这一刻到进入大厅都不保存场景状态，
    /// 免得把战场的场景状态写进还原后的数据（第一版离开圣所时的做法，Codex 复审）。
    /// 还原失败（含核对不一致）时保留快照：存档照样只写快照，回标题重新读档即可；之后不能再开战。
    /// </summary>
    private void RestoreForLobby(string why)
    {
        // 回大厅的每条路都经过这里：万神殿的束缚这时解开。
        Bindings.Clear(why);
        if (!SaveGuard.Active)
        {
            return;
        }
        try
        {
            // 连战里在休息点换过配装（RunGear）：拷回快照后配装回到开殿前，工具缓存、纹章招式要跟着刷新。
            // 上面解开束缚时发的刷新在拷回之前，按的还是换过的配装（Codex 审计 0.10.3 风险“装备字段恢复后缓存是否刷新”）。
            RunGear gear = RunGear.Take(PlayerData.instance);
            // 先拷回、快照留着（Codex 审查 v0.6.0 #5）：到大厅之前人还在战场，转场途中的存档照样只写快照，
            // 到了以后 FinishRestore 再拷一次、丢掉快照。
            lobbyRespawn = SaveGuard.RestoreKeep(GameManager.instance, why);
            SaveGuard.SuppressLevelState = true;
            restoreFailed = false;
            if (gear.Differs(PlayerData.instance))
            {
                RunGear.Refresh("back to the lobby / 回大厅，配装回到开殿前");
            }
            heroRecovery.LeaveFight(GameManager.instance, fightSerial, why);
        }
        catch (Exception e)
        {
            restoreFailed = true;
            GodhomePlugin.Log.LogError($"[Godhome] 回大厅时还原失败（{why}），快照保留，存档只写快照 / restore failed on the way back ({why}); keeping the snapshot: {e}");
        }
    }

    /// <summary>
    /// 已经离开战场（到了大厅，或者回不去大厅只能停下）：最后再从快照拷一次（转场途中战场脚本改过的实时数据也撤掉），
    /// 核对一致后丢掉快照。失败就保留快照：存档照样只写快照，回标题重新读档即可，之后不能再开战。
    /// </summary>
    private void FinishRestore(string why)
    {
        if (!SaveGuard.Active)
        {
            return;
        }
        try
        {
            lobbyRespawn = SaveGuard.RestoreAndClear(GameManager.instance);
            restoreFailed = false;
        }
        catch (Exception e)
        {
            restoreFailed = true;
            GodhomePlugin.Log.LogError($"[Godhome] 到达后还原失败（{why}），快照保留，存档只写快照 / final restore failed ({why}); keeping the snapshot: {e}");
        }
    }

    /// <param name="arenaFault">场地出错（门没建成、判胜没装上、Boss 场景没加载）：不是玩家放弃，不记这场和万神殿的尝试（Codex 审查 0.9.0 #3）。</param>
    internal void ReturnToLobby(string reason, bool arenaFault = false)
    {
        if (State is not (LoomState.Fight or LoomState.Won or LoomState.ToFight))
        {
            return;
        }
        // 场地已经判定这一场赢不了（Watch 还没来得及报，比如进场后第一帧先处理了别的回大厅请求）：照样按场地出错算。
        arenaFault |= Arena.FatalFor(fightSerial);
        if (GameManager.instance.IsGamePaused())
        {
            // 暂停中不还原快照：转场要等继续游戏，这段时间人还在战场，快照得一直护着存档（Codex 审查 v0.5.0 #1）。
            if (pendingReturn == null)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 游戏暂停中，继续后再回大厅 / game paused; going back to the lobby after it resumes ({reason})");
            }
            pendingReturn = reason;
            // 暂停中又来了一次回大厅：场地出错的标记留着（Codex 审查 0.9.0 第二轮 #7），不被普通请求盖掉。
            pendingReturnFault |= arenaFault;
            return;
        }
        if (State != LoomState.Won)
        {
            LastResult = FightResult.Abandoned;
            // 进了战场才算一次尝试（进场失败、场地出错退回来的不算）。
            if (State == LoomState.Fight && !arenaFault)
            {
                CountAttempt();
            }
        }
        // 连战中途回大厅 = 这次万神殿作废（打完最后一个时 FinishRun 先清掉了 Run 再回来）。
        if (Run != null)
        {
            FailRun(reason, countAttempt: !arenaFault);
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 回大厅 / back to the lobby: {reason}");
        SetState(LoomState.ToLobby);
        lobbyAttempts = 0;
        RestoreForLobby(reason);
        GoLobby();
    }

    private void OnLobbyArrive(string scene)
    {
        // 走门只在前作神居里算数：前作神居中途放弃了（转场超时），迟到的到达按正常回大厅处理（审查）。
        bool walk = hubWalk && Hk1Lobby.Active;
        hubWalk = false;
        if (walk)
        {
            // 前作神居里在房间之间走门：还在大厅里，只布置新房间。
            SetState(LoomState.Lobby);
            SetDeathRespawnToLobby();
            OnHk1Arrive(scene);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：走到 / hk1 godhome: walked into {scene}");
            return;
        }
        FinishRestore("arrived / 到达大厅");
        SaveGuard.SuppressLevelState = false;
        Bindings.Clear("arrived / 到达大厅");
        runFight = false;
        lobbyRespawn?.Apply(PlayerData.instance);
        lobbyRespawn = null;
        // 临时纹章标记、Boss 演出关掉的暂停等：在离开战场时（SaveGuard.RestoreKeep）就处理了，不在到达时清（Codex 复查）。
        SetState(LoomState.Lobby);
        CurrentBoss = null;
        bossHms.Clear();
        Arena.Clear();
        SetDeathRespawnToLobby();
        Heal();
        heroRecovery.ArmLobby(GameManager.instance, fightSerial);
        // 兜底：战斗中放弃、Boss 演出收着 HUD 时被改道回来、判胜后死了，到了大厅 HUD 还收着就叫回来（大厅里没有该藏 HUD 的演出）。
        // 放在回满血之后：RestoreHud 见没血就当人在死，不叫。
        RestoreHud("arrived / 到达大厅");
        // 从雕像开的战：游戏已经按回程门把人放在雕像前了（door_dreamReturn → bossReturnEntryGate），这里只核对。
        if (returnStatue != null)
        {
            string want = (Hk1Lobby.Active ? Hk1Lobby.ReturnTo(returnStatue)?.Gate : Sanctum.ReturnGateName(returnStatue)) ?? "-";
            string via = HeroController.SilentInstance != null ? HeroController.SilentInstance.GetEntryGateName() : "";
            if (via == want)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 回到 / back at {returnStatue}（回程门 / return gate {via}）");
            }
            else
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 没有从回程门进来 / did not come back through the return gate: want {want}, came via '{via}'");
            }
        }
        returnStatue = null;
        if (Hk1Lobby.Active)
        {
            OnHk1Arrive(scene, returningFromFight: true);
        }
        else
        {
            StartCoroutine(Sanctum.BuildRoutine(LobbyAlive));
        }
        if (restoreFailed)
        {
            ShowNotice(L.Get("ERR_RETURN"), 12f);
        }
        else
        {
            ShowBanner(L.F("BANNER_LOBBY", L.Get("LOOM_NAME"), GodhomeConfig.LoomKey.Value), 5f);
        }
        // 把战绩写盘：DataManager 跟着游戏存档保存模式进度。这时快照已丢弃，写的是大厅的实时数据
        // （还原失败时快照还在，写的是快照）。
        SaveNow("lobby / 大厅");
        // 万神殿打完：站到门前、黑幕揭开以后弹出结算（照前作 BossDoorChallengeCompleteUI）。
        if (pendingResult != null)
        {
            PantheonResult r = pendingResult;
            pendingResult = null;
            StartCoroutine(ShowResultWhenReady(r));
        }
    }

    /// <summary>
    /// 前作神居的回程门是自己淡入的门（TransitionPoint.customFade，HeroController.EnterHeroSubFadeUp 就不调 FadeSceneIn），
    /// 淡入由门上的 FSM（门前是 Boss Sequence Finish 的 Fade In）做，它只认“从这扇门里出去的”（前作门的 FSM 开门时记下）。
    /// 没从门里出去就回到门前（开门动画没发起转场的 8 秒兜底，Hk1Doors.PendingWatch）就没人淡入，整屏一直黑（自检实测，
    /// 淡入淡出的 FSM 一直停在 Scene Fade Out）。到场 5 秒后还停在那里，就照游戏自己的做法淡入（GameManager.FadeSceneIn）。
    /// </summary>
    private IEnumerator Hk1FadeGuard(int serial, string room)
    {
        float t0 = Time.unscaledTime;
        while (Time.unscaledTime - t0 < 5f)
        {
            if (State != LoomState.Lobby || fightSerial != serial)
            {
                yield break;
            }
            // Once control is back in the workshop, a stale native black screen can be lifted.
            if (room == Hk1Lobby.Workshop && Time.unscaledTime - t0 >= 0.3f
                && !GameManager.instance.IsInSceneTransition && HeroController.SilentInstance is { acceptingInput: true, controlReqlinquished: false })
            {
                break;
            }
            yield return null;
        }
        GameManager gm = GameManager.instance;
        PlayMakerFSM? fader = gm != null ? gm.screenFader_fsm : null;
        if (State == LoomState.Lobby && fightSerial == serial && Hk1Lobby.InHub && fader != null && fader.ActiveStateName == "Scene Fade Out"
            && !gm!.IsInSceneTransition && gm.GetSceneNameString() == room)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] Still faded out after entering {room} ({Time.unscaledTime - t0:0.00}s), fading in.");
            gm.FadeSceneIn();
        }
    }

    /// <summary>到了温泉：马上淡入，1 秒、3 秒后还黑着再补（离开休息点、换了场次就停）。</summary>
    private IEnumerator SpaFadeIn(int serial)
    {
        float[] at = { 0f, 1f, 3f };
        float t0 = Time.unscaledTime;
        foreach (float t in at)
        {
            while (Time.unscaledTime - t0 < t)
            {
                yield return null;
            }
            GameManager gm = GameManager.instance;
            if (State != LoomState.Rest || fightSerial != serial || gm == null || gm.IsInSceneTransition || gm.GetSceneNameString() != Hk1Lobby.Spa)
            {
                yield break;
            }
            Hk1FadeSceneIn.FadeInIfDark($"温泉 / GG_Spa +{t:0}s");
        }
    }

    /// <summary>等圣所搭好、黑幕揭开、人站好（最多 25 秒），再弹出万神殿的结算。期间离开大厅就不弹。</summary>
    private IEnumerator ShowResultWhenReady(PantheonResult r)
    {
        float waited = 0f;
        while (true)
        {
            if (State != LoomState.Lobby)
            {
                yield break;
            }
            HeroController hero = HeroController.SilentInstance;
            // 别的交互（门前、雕像的挑战面板）开着时一直等，超时也不弹（Codex 审查 0.8.0）：
            // 结算会接管操控和暂停，和面板抢着还原就会把暂停一直关着。
            bool otherUi = LoomUI.Instance == null || LoomUI.Instance.ChallengeBusy || LoomUI.Instance.ResultBusy;
            // 前作神居：结算中间的纹章、闪光借的图集（Hk1Doors.OnArrive 异步加载）到了再弹，最多等 5 秒；
            // 结算面板只在打开时取一次图（Codex 审查）。
            bool art = !Hk1Lobby.Active || waited >= 5f || (SanctumArt.Has("crest") && SanctumArt.Has("core_glows"));
            bool ready = !otherUi && art && Lobby.Ready && LoomUI.Instance!.CoverAlpha < 0.02f
                && hero != null && hero.isHeroInPosition && hero.acceptingInput && !hero.controlReqlinquished
                && !GameManager.instance.IsGamePaused();
            if (ready || (!otherUi && waited >= 25f && !GameManager.instance.IsGamePaused()))
            {
                break;
            }
            if (!GameManager.instance.IsGamePaused())
            {
                waited += Time.unscaledDeltaTime;
            }
            yield return null;
        }
        if (State == LoomState.Lobby && LoomUI.Instance != null)
        {
            LoomUI.Instance.ShowResult(r);
        }
    }

    // ───────────────────────────── 战斗 ─────────────────────────────

    /// <summary>神居存档里每个 Boss 都能挑战。</summary>
    internal bool IsAvailable(BossDef b) => true;

    internal void StartFight(BossDef b) => StartFight(b, 0, null);

    internal void StartFight(BossDef b, int tier, string? statue)
    {
        if (State != LoomState.Lobby)
        {
            return;
        }
        // 表里没有场地数据：运行时不猜，不开战（拍快照之前就拒绝）。
        if (!b.ArenaReady)
        {
            GodhomePlugin.Log.LogError($"[Godhome] {b.Id} 不能开战 / cannot fight: {(b.Arena.Broken.Length > 0 ? b.Arena.Broken : "没有场地数据（arena.door）/ no arena data")}");
            ShowNotice(L.Get("ERR_FIGHT"), 5f);
            return;
        }
        // 难度和雕像等快照拍成功以后再记（Codex 审查 v0.5.0 #7：被拒绝的开战不能留下它们）。
        if (SaveGuard.BlockAll || restoreFailed || SaveGuard.Active)
        {
            // 上一场没能还原（快照还在）或者出过异常：不能再拍新快照，只能回标题重新读档。
            ShowNotice(L.Get("ERR_NEED_RELOAD"), 6f);
            return;
        }
        // 游戏的存档在后台线程序列化，捕获的对象引用的是存档开始时的数据；还有没写完的存档时先不改数据（Codex 复审）。
        if (SaveGuard.SaveQueueBusy())
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 正在存档，稍后再开战 / a save is still being written, not starting {b.Id} yet");
            ShowNotice(L.Get("WAIT_SAVING"), 3f, error: false);
            return;
        }
        // 挑战面板开着（自检、调试键直接开战）：先关掉并交还暂停和交互，快照里不能带着面板的 disablePause（Codex 审查 v0.5.0 #2）。
        LoomUI.Instance?.AbortChallenge("fight starting");
        HeroRecovery.NormalizeLoadedData(GameManager.instance);
        try
        {
            // 给大厅的状态拍快照：战斗中存档写它，回大厅时拷回来。失败时还什么都没改，留在大厅（Codex 审查 #5）。
            SaveGuard.Take(GameManager.instance);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 开战前拍快照失败，取消 / snapshot before the fight failed, cancelled: {e}");
            SaveGuard.Clear();
            ShowNotice(L.Get("ERR_FIGHT"), 5f);
            return;
        }
        CurrentTier = Mathf.Clamp(tier, 0, 2);
        returnStatue = statue;
        GodhomePlugin.Log.LogInfo($"[Godhome] 开始重温 / reliving {b.Id}（{L.Get("TIER_" + CurrentTier)}{(statue != null ? "，雕像 / statue " + statue : "")}）");
        BeginBoss(b, heal: true, null);
    }

    /// <summary>
    /// 开一场（单场和万神殿的每一场都走这里）：这一场的状态清零、按表改 PlayerData、清持久记录，
    /// 然后以场地的 door_dreamEnter 为入口切到战场（场景加载时 Arena.Prepare 建门、建控制器）。
    /// redirect 不为空时是在游戏自己的转场里（FilterTransition）：改它的目的地，不另起转场。
    /// </summary>
    private void BeginBoss(BossDef b, bool heal, GameManager.SceneLoadInfo? redirect)
    {
        CurrentBoss = b;
        fightSerial++;
        heroRecovery.BeginFight(GameManager.instance, fightSerial);
        LastResult = FightResult.None;
        bossHms.Clear();
        ignoredHms.Clear();
        WinRedirects = 0;
        Arena.Begin(b, CurrentTier, fightSerial, OnArenaBossesDead, OnArenaComplete);
        ApplyBossState(b);
        ClearPersistent(b);
        if (heal)
        {
            Heal();
        }
        SetDeathRespawnToLobby();
        runFight = Run != null;
        if (Run != null)
        {
            // 开战时的配装（上一场带过来的，或者休息点长椅上刚换的）。换下一场时带它（ContinueRun）。
            try
            {
                Run.FightGear = RunGear.Copy(PlayerData.instance);
            }
            catch (Exception e)
            {
                Run.FightGear = null;
                GodhomePlugin.Log.LogWarning($"[Godhome] 记开战时的配装出错，下一场按战斗结束时的带 / copying the loadout at fight start failed: {e.Message}");
            }
        }
        // 万神殿里用过一次调试辅助，整串都算（前作没有这个，是我们不记录辅助战绩的规则）。
        fightAssisted = Run != null && Run.Assisted;
        attemptCounted = false;
        SetState(LoomState.ToFight);
        if (DebugTools.Instance != null && DebugTools.Instance.SelfTestRunning)
        {
            MarkAssisted("self-test / 自检");
        }
        else if (DebugTools.Instance != null && DebugTools.Instance.InvincibleActive)
        {
            // 无敌在大厅里就开着：开战时就算辅助，不等下一帧（Codex 复审）。
            MarkAssisted("invincible / 调试无敌");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 进场 / to the arena: {b.Id} → {b.EntryScene} @ {Arena.EnterGate}{(Run != null ? "（万神殿 / pantheon " + Run + "）" : "")}");
        // 上一场的回程门名不能留着（万神殿连战中途不回大厅）。
        GameManager.instance.playerData.bossReturnEntryGate = "";
        if (redirect != null)
        {
            retriedTransition = false;
            Redirect(GameManager.instance, redirect, b.EntryScene, Arena.EnterGate, respawning: false, "pantheon next / 万神殿下一场");
        }
        else
        {
            Transition(b.EntryScene, Arena.EnterGate, respawning: false);
        }
    }

    // ───────────────────────────── 万神殿 ─────────────────────────────

    /// <summary>这个万神殿开了没有（要先完成的都完成了；前作第五扇门要前四扇都完成）。</summary>
    internal static bool PantheonUnlocked(PantheonDef p)
    {
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        return p.Requires.All(rec.PantheonDone);
    }

    /// <summary>
    /// 开始一次万神殿连战（门前的挑战面板选好束缚后）：拍一份快照（整串共用），套上束缚，进第一场。
    /// 打完或作废都回到这扇门前（死亡照游戏自己的复活在长椅上醒来）。
    /// </summary>
    /// <param name="redirect">前作神居的门：开门动画放完后前作的 FSM 自己发起的转场，改它的目的地（不另起转场）。</param>
    /// <param name="ignoreLock">只给万神殿自检用：没解锁的殿也开（自检的连战算调试辅助，不记录，不会因此解锁）。</param>
    internal void StartPantheon(PantheonDef p, Binding bindings, GameManager.SceneLoadInfo? redirect = null, bool ignoreLock = false)
    {
        if (State != LoomState.Lobby || p.Bosses.Count == 0)
        {
            return;
        }
        if (!PantheonUnlocked(p) && !(ignoreLock && DebugTools.Instance != null && DebugTools.Instance.SelfTestPrivileged))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 万神殿还没开 / pantheon locked: {p.Id}（要先完成 / requires {string.Join(", ", p.Requires)}）");
            return;
        }
        // 每一场都要有场地数据（运行时不猜），有一个没有就不开始。
        List<string> noArena = p.Bosses.Where(x => !x.ArenaReady).Select(x => x.Id).Distinct().ToList();
        if (noArena.Count > 0)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 万神殿 {p.Id} 里有 Boss 没有场地数据，不能开始 / pantheon has bosses without arena data: {string.Join(", ", noArena)}");
            ShowNotice(L.Get("ERR_FIGHT"), 5f);
            return;
        }
        if (SaveGuard.BlockAll || restoreFailed || SaveGuard.Active)
        {
            ShowNotice(L.Get("ERR_NEED_RELOAD"), 6f);
            return;
        }
        if (SaveGuard.SaveQueueBusy())
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 正在存档，稍后再开始 / a save is still being written, not starting pantheon {p.Id} yet");
            ShowNotice(L.Get("WAIT_SAVING"), 3f, error: false);
            return;
        }
        LoomUI.Instance?.AbortChallenge("pantheon starting");
        HeroRecovery.NormalizeLoadedData(GameManager.instance);
        try
        {
            SaveGuard.Take(GameManager.instance);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 万神殿开始前拍快照失败，取消 / snapshot before the pantheon failed, cancelled: {e}");
            SaveGuard.Clear();
            ShowNotice(L.Get("ERR_FIGHT"), 5f);
            return;
        }
        CurrentTier = 0;
        returnStatue = DoorSpot(p.Id);
        Run = new PantheonRun(p, bindings & Binding.All);
        LastPantheonResult = null;
        LastPantheonFail = "";
        pendingResult = null;
        Bindings.Apply(Run.Bindings, GameManager.instance.playerData, "pantheon " + p.Id);
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿开始 / pantheon started: {Run}");
        if (redirect != null)
        {
            // 前作门的转场是梦境式的淡出（前作的视觉效果），改回普通的。
            redirect.Visualization = GameManager.SceneLoadVisualizations.Default;
        }
        BeginBoss(p.Bosses[0], heal: true, redirect);
        HookDamage();
    }

    /// <summary>自检用：在休息点直接走去下一场（玩家是从出口走出去，FilterTransition 改道；这里自己发起转场）。</summary>
    internal void DebugLeaveRest()
    {
        if (State != LoomState.Rest || Run == null)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 自检：离开休息点 / self-test: leaving the rest stop → {Run.Current.Id}");
        BeginBoss(Run.Current, heal: false, null);
    }

    /// <summary>万神殿的门在 returnStatue 里的写法（和雕像共用回程门那条路，见 Sanctum.ReturnGateName）。</summary>
    internal static string DoorSpot(string pantheonId) => Sanctum.DoorPrefix + pantheonId;

    /// <summary>
    /// 这一场赢了：后面还有就带着血和丝进下一场（或者先去休息点），最后一个打完就结算。
    /// 每场之间把快照拷回实时数据（快照留着），上一场改过的剧情标记、场景状态都撤掉，再给下一场改。
    /// redirect：在游戏自己的转场里（Boss 倒下后的演出直接切场景）就改它的目的地。
    /// </summary>
    private void ContinueRun(GameManager.SceneLoadInfo? redirect)
    {
        PantheonRun? run = Run;
        if (run == null)
        {
            return;
        }
        run.Assisted |= fightAssisted;
        if (run.IsLast)
        {
            FinishRun(redirect);
            return;
        }
        PlayerData pd = PlayerData.instance;
        int hp = pd.health;
        int silk = pd.silk;
        int blue = pd.healthBlue;
        // 身上的配装（休息点长椅上可能换过）：拷回快照以后放回去（Codex 审计 0.10.3 F03）。
        RunGear gear = RunGear.Take(pd);
        if (run.FightGear != null && run.FightGear.Differs(pd))
        {
            // 战斗里玩家换不了配装（只能在长椅上换），变了的是游戏自己改的：幽影倒下的 UI Msg 给 hasParry 并 AutoEquipTool 装上十字缝，
            // 0.10.4 起照样带到了之后各场（玩家报“二门打完幽影自动装上弹反”）。按这一场开战时的带。
            GodhomePlugin.Log.LogInfo($"[Godhome] 这一场里游戏改了配装，不带到下一场 / the game changed the loadout during this fight; carrying the fight-start one instead（{CurrentBoss?.Id}）");
            gear = run.FightGear;
        }
        bool rest = run.Def.RestAfter(run.Index) || (DebugTools.Instance != null && DebugTools.Instance.ExtraRest(run));
        try
        {
            var supply = new RunToolSupply(pd);
            SaveGuard.RestoreKeep(GameManager.instance, "pantheon next / 万神殿下一场");
            Bindings.ReapplyNeedleLevel(pd);
            supply.Restore(pd);
        }
        catch (Exception e)
        {
            // 拷不回去：这次连战到此为止，按回大厅的规矩走（快照留着，存档只写快照）。
            GodhomePlugin.Log.LogError($"[Godhome] 万神殿换场时还原失败，作废 / restore between pantheon fights failed, dropping the run: {e}");
            // 先作废连战（Codex 审查 0.8.0）：redirect 不为空时 LeaveForLobby 直接改道，不经过 ReturnToLobby 里的 FailRun，
            // 连战和受伤事件会留到大厅里。
            FailRun("pantheon restore failed / 万神殿换场还原失败", countAttempt: false);
            LeaveForLobby(redirect, "pantheon restore failed / 万神殿换场还原失败");
            return;
        }
        // 从这一刻到进下一个战场，不把旧战场（或休息点）的场景状态写进拷回的数据（下一场可能还是同一个场景）。
        SaveGuard.SuppressLevelState = true;
        run.Index++;
        // 配装先放回去：血、丝的上限可能跟着装备变。
        bool gearChanged = gear.PutBack(pd, "pantheon next / 万神殿下一场");
        pd.health = Mathf.Clamp(hp, 1, Mathf.Max(1, pd.CurrentMaxHealth));
        pd.silk = Mathf.Clamp(silk, 0, Mathf.Max(0, pd.CurrentSilkMax));
        // 蓝血一直带（Codex 审计 0.10.3 F04：以前拷回快照时变回开殿时的值，用掉的补回来、途中得到的丢掉）。去休息点时长椅照游戏的规矩清。
        pd.healthBlue = Mathf.Max(0, blue);
        if (gearChanged)
        {
            RunGear.Refresh("pantheon next / 万神殿下一场");
        }
        // 仍在旧场景、仍是旧场次：下一场 BeginBoss 会换编号并登记新的角色所有权。
        // 不能挪到新场景 FinishedEnteringScene 后，那时新 Boss 的开场可能已经拿走控制。
        heroRecovery.LeaveFight(GameManager.instance, fightSerial, "pantheon next / 万神殿下一场");
        // 血、丝、蓝血和换场前一样（HUD 显示的就是这些），不用刷新 HUD。
        run.Carry = (pd.health, pd.silk, pd.healthBlue);
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿下一场 / pantheon next: {run}，带过去 / carried hp {pd.health}、silk {pd.silk}、blue {pd.healthBlue}{(gearChanged ? "、换过的配装 / changed loadout" : "")}{(rest ? "，先去休息点 / resting first" : "")}");
        if (rest)
        {
            GoRest(redirect);
        }
        else
        {
            BeginBoss(run.Current, heal: false, redirect);
        }
    }

    /// <summary>
    /// 万神殿的休息点。用前作神居时照前作，是前作的温泉 GG_Spa（从前作 Boss 场景都有的 door_dreamEnter 走进来，
    /// 前作的场景控制器放前作的连战转场，Hk1Spa）；不然是本作的长椅房间，醒来坐在长椅上。
    /// </summary>
    private static (string Scene, string Gate, bool Respawning) RestStop()
        => Hk1Spa.Usable ? (Hk1Lobby.Spa, Hk1Lobby.DreamEnterGate, false) : (PantheonCatalog.RestScene, PantheonCatalog.RestMarker, true);

    /// <summary>
    /// 上一场的演出把 HUD 收起来了，叫回来。HUD 本来就在就什么都不做。
    /// 例：寡妇倒下的过场给 HUD 发 OUT（正常游戏里过场放完才 IN），万神殿不放完过场就去下一场，
    /// 下一场（蕾丝）整场都没有血量显示（探针实测，2026-11-26）。
    /// 调用的地方：SetState 从 Won 离开时（在旧场景里发，下一场的开场演出要藏 HUD 的照样能在加载后再藏）；到达大厅时兜底。
    /// </summary>
    private static void RestoreHud(string why)
    {
        GameCameras gc = GameCameras.SilentInstance;
        if (gc == null || gc.hudCanvasSlideOut == null || gc.IsHudVisible)
        {
            return;
        }
        // 人正在死（判胜以后、离场之前死了，OnHeroDying 从 Won 切到 ToLobby）：不在死亡演出前叫，复活到大厅时兜底（Codex 复查 #3）。
        HeroController hero = HeroController.SilentInstance;
        if (hero != null && (hero.cState.dead || PlayerData.instance.health <= 0))
        {
            return;
        }
        // 已经在滑入（In Wait → Come In，“Is Visible”要到 Come In 才变真）：不用再叫。
        string was = gc.hudCanvasSlideOut.ActiveStateName;
        if (was is "In Wait" or "Come In")
        {
            return;
        }
        gc.HUDIn();
        GodhomePlugin.Log.LogInfo($"[Godhome] HUD 是收起的，叫回来 / the HUD was hidden, bringing it back（{why}，HUD {was} → {gc.hudCanvasSlideOut.ActiveStateName}）");
    }

    private float fightHudHidden;

    /// <summary>
    /// 每帧（没暂停时）：开战以后 HUD 一直收着就叫回来。
    /// 例：原初罪者进场走神龛召唤，神龛的 Look Back 给 HUD 发 OUT，神龛和原初罪者的 FSM 里都没有再发 IN 的地方，
    /// 整场没有血量显示（0.10.1 玩家报）。Boss 开场演出、打斗中的对话会收 HUD，这时人一般不能操作（对话、过场都收回了控制），
    /// 所以只在人能操作（acceptingInput、没交出控制、没死）时才数；收着的时间（不算暂停）连续满 arena.hudGrace 秒（默认 2 秒）才叫，
    /// 给演出自己叫回来的机会。崇高圣母灵丝：自检替玩家直接给挑战 FSM 发 INTERACT，开场演出里人能动
    /// （玩家自己按挑战走 InteractEvents，收不收控制没核实）；原版要到 Silk Boss 的 Begin 才发 IN（挑战以后约 20 秒），
    /// 表里写了 hudGrace 45，免得演出中途提前叫回来。
    /// </summary>
    private void CheckFightHud()
    {
        if (State != LoomState.Fight || !Arena.Started)
        {
            fightHudHidden = 0f;
            return;
        }
        GameCameras gc = GameCameras.SilentInstance;
        HeroController hero = HeroController.SilentInstance;
        bool hidden = gc != null && gc.hudCanvasSlideOut != null && !gc.IsHudVisible;
        bool canAct = hero != null && hero.acceptingInput && !hero.controlReqlinquished && !hero.cState.dead;
        if (!hidden || !canAct)
        {
            fightHudHidden = 0f;
            return;
        }
        float grace = CurrentBoss?.Arena.HudGrace ?? 2f;
        fightHudHidden += Time.unscaledDeltaTime;
        if (fightHudHidden >= grace)
        {
            fightHudHidden = 0f;
            RestoreHud($"开战后一直收着 / hidden for {grace:0.#} s after the fight started（{CurrentBoss?.Id}）");
        }
    }

    /// <summary>去休息点（RestStop）。</summary>
    private void GoRest(GameManager.SceneLoadInfo? redirect)
    {
        CurrentBoss = null;
        bossHms.Clear();
        runFight = false;
        SetState(LoomState.ToRest);
        lobbyAttempts = 0;
        SetDeathRespawnToLobby();
        (string scene, string gate, bool respawning) = RestStop();
        Hk1Spa.BeforeLoad(scene);
        if (redirect != null)
        {
            retriedTransition = false;
            Redirect(GameManager.instance, redirect, scene, gate, respawning, "pantheon rest / 万神殿休息点");
        }
        else
        {
            Transition(scene, gate, respawning);
        }
    }

    /// <summary>最后一个打完：记完成情况（照前作 FinishLastBossScene），回到这扇门前，弹出结算。</summary>
    private void FinishRun(GameManager.SceneLoadInfo? redirect)
    {
        PantheonRun run = Run!;
        run.Assisted |= fightAssisted;
        UnhookDamage();
        PantheonRecord rec = GodhomePlugin.Instance.Records.Pantheon(run.Def.Id);
        var result = new PantheonResult
        {
            Def = run.Def,
            Bindings = run.Bindings,
            Damaged = run.Damaged,
            Seconds = run.Elapsed,
            Assisted = run.Assisted,
            WasCompleted = rec.Completed,
            WasAllBindings = rec.AllBindings,
            WasNoHits = rec.NoHits,
            WasBound = (rec.BoundNeedle ? Binding.Needle : 0) | (rec.BoundMask ? Binding.Mask : 0) | (rec.BoundTools ? Binding.Tools : 0) | (rec.BoundSilk ? Binding.Silk : 0),
        };
        if (!run.Assisted)
        {
            rec.Attempts++;
            rec.RecordClear(run.Bindings, run.Damaged, run.Elapsed);
        }
        LastPantheonResult = result;
        pendingResult = result;
        returnStatue = DoorSpot(run.Def.Id);
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿完成 / pantheon complete: {run}，用时 / time {L.Time(run.Elapsed)}，{(run.Damaged ? "受过伤 / damaged" : "无伤 / no hits")}{(run.Assisted ? "，调试辅助不记录 / assisted, not recorded" : "")}");
        Run = null;
        LeaveForLobby(redirect, "pantheon complete / 万神殿完成");
    }

    /// <summary>这次连战作废（死亡、放弃、离开战场）：记一次尝试（场地出错、进错场景、切场景卡住这类技术故障不记），不记完成。</summary>
    private void FailRun(string why, bool countAttempt = true)
    {
        PantheonRun? run = Run;
        if (run == null)
        {
            return;
        }
        run.Assisted |= fightAssisted;
        UnhookDamage();
        if (!run.Assisted && countAttempt)
        {
            GodhomePlugin.Instance.Records.Pantheon(run.Def.Id).Attempts++;
        }
        LastPantheonFail = $"{run.Def.Id}@{run.Index + 1}: {why}";
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿失败 / pantheon failed: {run}（{why}）");
        Run = null;
    }

    /// <summary>回大厅：自己发起（ReturnToLobby），或者在游戏自己的转场里改道（和离开战场的那条路一样）。</summary>
    private void LeaveForLobby(GameManager.SceneLoadInfo? redirect, string why)
    {
        if (redirect == null)
        {
            if (State is LoomState.Rest or LoomState.ToRest)
            {
                // 在休息点（不在战场）：按回大厅的规矩直接走。
                SetState(LoomState.ToLobby);
                lobbyAttempts = 0;
                RestoreForLobby(why);
                GoLobby();
                return;
            }
            ReturnToLobby(why);
            return;
        }
        SetState(LoomState.ToLobby);
        lobbyAttempts = 0;
        RestoreForLobby(why);
        SetDeathRespawnToLobby();
        (string scene, string gate, bool respawning) = LobbyEntry();
        Redirect(GameManager.instance, redirect, scene, gate, respawning, why);
    }

    /// <summary>前作 BossSceneController 判“无伤”的做法：挂在 HeroController.OnTakenDamage 上。</summary>
    private void HookDamage()
    {
        PantheonRun? run = Run;
        HeroController hero = HeroController.SilentInstance;
        if (run == null || hero == null || ReferenceEquals(run.DamageHook, hero))
        {
            return;
        }
        UnhookDamage();
        hero.OnTakenDamage += OnRunDamaged;
        run.DamageHook = hero;
    }

    private void UnhookDamage()
    {
        HeroController? h = Run?.DamageHook;
        if (h == null)
        {
            return;
        }
        try
        {
            h.OnTakenDamage -= OnRunDamaged;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 摘受伤事件出错 / unhooking damage failed: {e.Message}");
        }
        Run!.DamageHook = null;
    }

    private void OnRunDamaged()
    {
        PantheonRun? run = Run;
        if (run != null && !run.Damaged && State is LoomState.ToFight or LoomState.Fight or LoomState.Won)
        {
            run.Damaged = true;
            GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿：受伤了，这次不算无伤 / pantheon: took damage, no no-hit this run ({run})");
        }
    }

    /// <summary>这场用了调试辅助：不记战绩。同一场只记一次日志。</summary>
    internal void MarkAssisted(string why)
    {
        if (fightAssisted || CurrentBoss == null || State is not (LoomState.ToFight or LoomState.Fight or LoomState.Lobby))
        {
            return;
        }
        fightAssisted = true;
        GodhomePlugin.Log.LogInfo($"[Godhome] 调试辅助，这场不记战绩 / debug-assisted, not recorded: {CurrentBoss.Id} ({why})");
    }

    /// <summary>
    /// 一场打完（赢、死、放弃）时记一次尝试。开战时不记（Codex 复审）：中途崩溃、或者后来用了调试辅助，
    /// 都不会留下这次尝试；调试辅助的场次也不会建出空记录。
    /// </summary>
    private void CountAttempt()
    {
        if (fightAssisted || attemptCounted || CurrentBoss == null || runFight)
        {
            return;
        }
        GodhomePlugin.Instance.Records.Get(CurrentBoss.Id).Attempts++;
        attemptCounted = true;
    }

    /// <summary>按数据表改 PlayerData，让 Boss 重新出现（回大厅时快照会还原）。</summary>
    private static void ApplyBossState(BossDef b)
    {
        PlayerData pd = PlayerData.instance;
        foreach (string flag in b.DefeatedFlags)
        {
            SetField(pd, flag, new JValue(false));
        }
        // 当作见过这个 Boss，走“重打”分支（没有初见对话和开场演出）。表里的前置条件在后面，可以再改回来。
        if (b.Arena.Encountered)
        {
            foreach (string flag in b.EncounteredFlags)
            {
                SetField(pd, flag, new JValue(true));
            }
        }
        foreach (KeyValuePair<string, JToken> kv in b.PlayerDataSet)
        {
            SetField(pd, kv.Key, kv.Value);
        }
    }

    private static void SetField(PlayerData pd, string name, JToken value)
    {
        FieldInfo? f = BossCatalog.PlayerDataField(name);
        if (f == null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] PlayerData 没有字段 / no field {name}");
            return;
        }
        try
        {
            object? v = value.ToObject(f.FieldType);
            f.SetValue(pd, v);
            GodhomePlugin.Log.LogInfo($"[Godhome]   {name} = {v}");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 设置字段失败 / failed to set {name}: {e.Message}");
        }
    }

    /// <summary>
    /// 这场战斗的场景：主场景、宿主场景、入口场景，以及 allowScenes（多场景 Boss 战的后续场景，Codex 复审）。
    /// 场景调整、持久记录、血量判定都只在这些场景里找。
    /// </summary>
    internal static List<string> ScenesOf(BossDef b)
    {
        var l = new List<string> { b.Scene };
        void Add(string? s)
        {
            if (!string.IsNullOrEmpty(s) && !l.Contains(s!))
            {
                l.Add(s!);
            }
        }
        Add(b.HostScene);
        Add(b.EntryScene);
        foreach (string s in b.AllowScenes)
        {
            Add(s);
        }
        return l;
    }

    /// <summary>删掉“这个敌人已被击杀”的持久记录（PersistentBoolItem 默认 ID 是物体名），否则 Boss 进场就被隐藏。回大厅时快照还原。</summary>
    private static void ClearPersistent(BossDef b)
    {
        SceneData sd = SceneData.instance;
        if (sd == null)
        {
            return;
        }
        // The right-hand trapdoor can also restore its own saved open state in Start.
        if (b.Id == "the_unravelled")
        {
            sd.PersistentBools.Remove(b.EntryScene, "Pipe_Vent_Hatch");
        }
        foreach (string scene in ScenesOf(b))
        {
            foreach (string id in b.BossObjects)
            {
                if (sd.PersistentBools.Remove(scene, id))
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome]   清除持久记录 / cleared persistent bool {scene}/{id}");
                }
            }
        }
    }

    internal bool ShouldForceAdditive(string sceneName) =>
        State is LoomState.ToFight or LoomState.Fight
        && CurrentBoss != null
        && CurrentBoss.Additive
        && sceneName == CurrentBoss.Scene;

    /// <summary>这一场的表里写了不加载这个附加场景（arena.noAdditive）。</summary>
    internal bool ShouldBlockAdditive(string sceneName) =>
        State is LoomState.ToFight or LoomState.Fight
        && CurrentBoss != null
        && CurrentBoss.Arena.NoAdditive.Contains(sceneName);

    /// <summary>战斗中按原条件附加加载了这一场以外的场景（只记日志：宿主场景还带着别的 Boss 场景时看得出来）。</summary>
    internal bool IsForeignAdditive(string sceneName) =>
        State is LoomState.ToFight or LoomState.Fight
        && CurrentBoss != null
        && !ScenesOf(CurrentBoss).Contains(sceneName);

    /// <summary>
    /// 找当前 Boss 的血量组件（只给自检、调试用：秒杀键、自检的“打 Boss”；判胜由场地控制器管）。
    /// 只看这个 Boss 的场景（Codex 审查 #21：别的已加载场景里同名的敌人不算）。
    /// </summary>
    internal void ScanBoss()
    {
        bossHms.Clear();
        BossDef? b = CurrentBoss;
        if (b == null || b.BossObjects.Length == 0)
        {
            return;
        }
        List<string> scenes = ScenesOf(b);
        foreach (HealthManager hm in FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (hm == null || !hm.gameObject.scene.IsValid())
            {
                continue;
            }
            string name = hm.gameObject.name.Replace("(Clone)", "").Trim();
            if (!b.BossObjects.Contains(name))
            {
                continue;
            }
            string scene = hm.gameObject.scene.name;
            if (!scenes.Contains(scene))
            {
                if (ignoredHms.Add($"{scene}/{name}"))
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome] 忽略别的场景里的同名血量组件 / ignoring {name} in scene {scene} (boss scenes: {string.Join(", ", scenes)})");
                }
                continue;
            }
            bossHms.Add(hm);
        }
    }

    private void OnWin(string how)
    {
        BossDef b = CurrentBoss!;
        fightEnd = Time.unscaledTime;
        float t = fightEnd - fightStart;
        LastResult = FightResult.Won;
        LastWinHow = how;
        LastWonFallback = false;
        CountAttempt();
        if (!fightAssisted && !runFight)
        {
            // Get 先把分档以前的记录迁移到素织，RecordWin 再一起改总数和分档（Codex 审查 v0.5.0 #3）。
            BossRecord r = GodhomePlugin.Instance.Records.Get(b.Id);
            r.RecordWin(CurrentTier, t);
            r.FirstClearUtc ??= DateTime.UtcNow.ToString("o");
        }
        SetState(LoomState.Won);
        ShowBanner(L.F(fightAssisted ? "BANNER_WIN_ASSISTED" : "BANNER_WIN", b.DisplayName + "·" + L.Get("TIER_" + CurrentTier), L.Time(t)), Arena.BossesDeadWait + 1f);
        GodhomePlugin.Log.LogInfo($"[Godhome] 胜利 / victory: {b.Id} tier {CurrentTier} in {t:0.0}s (by {how}){(fightAssisted ? "，调试辅助不记战绩 / assisted, not recorded" : "")}");
    }
    /// <summary>HeroController.Die 前缀调用。返回 true 表示按非致命死亡处理。</summary>
    internal bool OnHeroDying()
    {
        if (!InMode)
        {
            return false;
        }
        SetDeathRespawnToLobby();
        // 前作神居：照前作，战斗里死了回到这尊雕像、这扇门前。前作在神居的 Boss 场景（地图区 GODS_GLORY）里死亡是梦境死亡
        // （HK1 HeroController.Die），走 door_dreamReturn，EnterHero 换成 bossReturnEntryGate，人回到雕像、门前（反编译核实）。
        // 我们照样走丝之歌的非致死复活，只把落点换成那里（复活转场里改道，FilterTransition）。
        deathReturn = Hk1Lobby.Active && State is LoomState.Fight or LoomState.ToFight or LoomState.Won or LoomState.Rest or LoomState.ToRest
            ? returnStatue
            : null;
        // 死亡走游戏自己的复活：在长椅上醒来。挂起的回程、雕像目的地都作废（Codex 审查 v0.5.0 #6），
        // 否则复活转场迟迟不来、超时重试时 GoLobby 还会按雕像走长廊入口。
        pendingTransition = null;
        pendingReturn = null;
        pendingReturnFault = false;
        returnStatue = null;
        // 前作神居：挂着的开门请求作废，走门的标记清掉（复活转场按死亡处理）。
        hubWalk = false;
        Hk1Doors.ClearPending();
        if (State is LoomState.Fight or LoomState.ToFight)
        {
            LastResult = FightResult.Died;
            if (State == LoomState.Fight)
            {
                CountAttempt();
            }
            ShowBanner(L.Get("BANNER_DEATH"), 4f);
            GodhomePlugin.Log.LogInfo($"[Godhome] 战斗中死亡 / died fighting {CurrentBoss?.Id}");
        }
        // 万神殿里死了：这次连战作废（照前作，死亡就要从第一个重新打）。
        if (Run != null)
        {
            FailRun("death / 死亡");
        }
        // 快照在游戏发起的复活转场开始时（FilterTransition）拷回，那之前死亡演出还在战场里进行。
        SetState(LoomState.ToLobby);
        lobbyAttempts = 0;
        return true;
    }

    /// <summary>
    /// 游戏要预加载一个场景（ScenePreloader：死亡时按复活点预加载，场景里的预加载组件、走近出口时按目的地预加载）。
    /// 这个场景的转场会不会被 FilterTransition 改道去别处。会的话就不该预加载：预加载的场景加载完不激活，
    /// 会挡住后面的场景加载，而 SceneLoad 要等目标场景加载完才去激活别的预加载 —— 互相等，卡在加载里。
    /// 实机：前作神居里战斗中死亡，游戏预加载圣所 Weave_07，复活转场改道去 GG_Atrium，卡了 90 秒没进去（Codex 审查 #1）。
    /// </summary>
    internal bool WouldRedirect(string scene)
    {
        switch (State)
        {
            case LoomState.ToLobby:
                return !hubWalk && !Lobby.Is(scene);
            case LoomState.Lobby:
                return Hk1Lobby.Active ? !Hk1Lobby.IsHub(scene) : scene != Lobby.Scene;
            case LoomState.Fight:
            case LoomState.Won:
                return CurrentBoss == null || !CurrentBoss.AllowScenes.Contains(scene);
            case LoomState.ToFight:
                // 刚进战场、还没切到 Fight 时（场景里的预加载组件 Start 就在这时）也一样：战场以外的都会被改道（审查）。
                return CurrentBoss == null || !(scene == CurrentBoss.EntryScene || scene == CurrentBoss.Scene || CurrentBoss.AllowScenes.Contains(scene));
            case LoomState.Rest:
            case LoomState.ToRest:
                // 休息点：离开就改道去下一场（没有连战就回大厅），FilterTransition 一律改道（Codex 复审）。
                // 预加载多跳过一个只是慢一点，少跳过一个会卡死。
                return true;
            default:
                return false;
        }
    }

    private static void SetDeathRespawnToLobby()
    {
        PlayerData pd = PlayerData.instance;
        pd.nonLethalRespawnScene = GodhomeConfig.LobbyScene.Value;
        pd.nonLethalRespawnMarker = GodhomeConfig.LobbyMarker.Value;
    }

    /// <summary>
    /// 万神殿进场后把带过来的血、丝和蓝血再设一次（进入游戏自己的记忆区域时 EnteredNewMapZone 会回满血，见 PantheonRun.Carry）。
    /// 只用一次：之后这一场里的回血照常。
    /// </summary>
    private void ReapplyCarry()
    {
        if (Run?.Carry is not (int hp, int silk, int blue))
        {
            return;
        }
        Run.Carry = null;
        PlayerData pd = PlayerData.instance;
        HeroController hero = HeroController.SilentInstance;
        if (pd == null || hero == null)
        {
            return;
        }
        int wantHp = Mathf.Clamp(hp, 1, Mathf.Max(1, pd.CurrentMaxHealth));
        int wantSilk = Mathf.Clamp(silk, 0, Mathf.Max(0, pd.CurrentSilkMax));
        int wantBlue = Mathf.Max(0, blue);
        if (pd.health == wantHp && pd.silk == wantSilk && pd.healthBlue == wantBlue)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿：进场后改回带过来的血和丝 / pantheon: restoring carried hp {pd.health}→{wantHp}, silk {pd.silk}→{wantSilk}, blue {pd.healthBlue}→{wantBlue} after entering");
        bool blueChanged = pd.healthBlue != wantBlue;
        pd.health = wantHp;
        pd.silk = wantSilk;
        hero.RefreshSilk();
        EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
        if (blueChanged)
        {
            SetBlueHealth(pd, hero, wantBlue);
        }
    }

    /// <summary>
    /// 把蓝血改成 want，HUD 的蓝面具跟着对上（例如离开记忆场景时游戏把蓝血清了：GameManager 里 PreMemoryState 还原后 UpdateBlueHealth）。
    /// 丝之歌的 UPDATE BLUE HEALTH 事件是“清零”：HUD 的 Blue Health Control 收到后移掉蓝面具、调 GameManager.UpdateBlueHealth 把 healthBlue 设成 0，
    /// 不能拿来“刷新”（0.10.4 实测：发了它蓝血就没了）。加蓝血照游戏自己的做法：GameManager.AddBlueHealthQueued，
    /// HUD 一格一格加（healthBlue +1、画一个蓝面具）；要变少时先清零再加。
    /// </summary>
    private static void SetBlueHealth(PlayerData pd, HeroController hero, int want)
    {
        GameManager gm = GameManager.instance;
        if (gm == null)
        {
            pd.healthBlue = want;
            return;
        }
        if (pd.healthBlue > want)
        {
            EventRegister.SendEvent(EventRegisterEvents.UpdateBlueHealth);
            // HUD 不在收这个事件的状态时也要清零。
            hero.UpdateBlueHealth();
        }
        int add = want - pd.healthBlue;
        for (int i = 0; i < add; i++)
        {
            gm.AddBlueHealthQueued();
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿：蓝血排队加回 / pantheon: queued {Mathf.Max(0, add)} blue masks to reach {want}");
    }

    private static void Heal()
    {
        HeroController hero = HeroController.SilentInstance;
        if (hero == null)
        {
            return;
        }
        hero.MaxHealth();
        hero.RefillSilkToMaxSilent();
    }

    // ───────────────────────────── 场景切换 ─────────────────────────────

    private void Transition(string scene, string gate, bool respawning)
    {
        GameManager gm = GameManager.instance;
        if (gm.IsGamePaused())
        {
            // 暂停中开始转场会卡死（见 Update）：记下来，继续游戏后再切。
            if (pendingTransition == null)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 游戏暂停中，继续后再切场景 / game paused; the scene change to {scene} waits until it resumes");
            }
            pendingTransition = (scene, gate, respawning, State, fightSerial);
            return;
        }
        pendingTransition = null;
        ourTransition = true;
        retriedTransition = false;
        gm.RespawningHero = respawning;
        gm.BeginSceneTransition(new GameManager.SceneLoadInfo
        {
            SceneName = scene,
            EntryGateName = gate,
            PreventCameraFadeOut = false,
            WaitForSceneTransitionCameraFade = true,
            Visualization = GameManager.SceneLoadVisualizations.Default,
            AlwaysUnloadUnusedAssets = true,
        });
    }

    /// <summary>GameManager.sceneLoad（私有）：不为空 = 正在加载场景，这时再来的转场会被游戏丢掉。</summary>
    private static readonly HarmonyLib.AccessTools.FieldRef<GameManager, SceneLoad> SceneLoadField =
        HarmonyLib.AccessTools.FieldRefAccess<GameManager, SceneLoad>("sceneLoad");

    /// <summary>BeginSceneTransition 前缀：不是自己发起的切换，按当前状态改目的地。</summary>
    internal void FilterTransition(GameManager gm, GameManager.SceneLoadInfo info)
    {
        if (State == LoomState.Off)
        {
            if (info.IsFirstLevelForPlayer && gm.RespawningHero && GodhomeSave.IsGodhome(gm))
            {
                // GetRespawnInfo may already have substituted Tut_01 for an imported bench.
                // Supply the real first scene before loading and keep a vanilla checkpoint on disk.
                gm.playerData.ResetTempRespawn();
                GodhomeSave.SetLobbyCheckpoint(gm.playerData);
                string scene = GodhomeConfig.LobbyScene.Value;
                string marker = GodhomeConfig.LobbyMarker.Value;
                if (Hk1Lobby.Decide())
                {
                    string? saved = GodhomePlugin.Instance.SaveData?.HubBenchScene;
                    bool hasBench = saved != null && Hk1Lobby.IsHub(saved);
                    scene = hasBench ? saved! : Hk1Lobby.Atrium;
                    marker = hasBench ? Hk1Lobby.BenchMarker(scene) : Hk1Lobby.ArriveMarker;
                }
                Redirect(gm, info, scene, marker, respawning: true, "godhome save entry");
            }
            return;
        }
        // 试验：正在试原生转场进前作场景，放行这一个（Debug.Hk1ProbeMode = native）。
        if (Hk1Gate.NativeScene != null && info.SceneName == Hk1Gate.NativeScene)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作试验：放行转场 / hk1 probe: letting the transition to {info.SceneName}@{info.EntryGateName} through");
            return;
        }
        if (ourTransition)
        {
            ourTransition = false;
            return;
        }
        // 已经有场景在加载：游戏会直接丢掉这次转场（BeginSceneTransitionRoutine 开头 sceneLoad != null 就 yield break），
        // 不能按它改状态（Codex 审查 0.8.0：去休息点的淡出途中 Boss 的演出又发起转场，会被当成“离开”作废连战）。
        if (SceneLoadField(gm) != null)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 已经在切场景，游戏会丢掉这次 / a scene load is already running; the game will drop this transition to {info.SceneName}");
            return;
        }
        // 游戏自己发起了转场：暂停时挂起的请求不再执行（Codex 审查 v0.5.0 #6）。
        if (pendingTransition != null || pendingReturn != null)
        {
            GodhomePlugin.Log.LogInfo("[Godhome] 游戏自己开始切场景，丢弃挂起的请求 / the game started its own transition; dropping the pending request");
            pendingTransition = null;
            pendingReturn = null;
            pendingReturnFault = false;
        }
        string target = info.SceneName;
        string lobby = Lobby.Scene;
        switch (State)
        {
            case LoomState.ToLobby:
                if (hubWalk)
                {
                    // 前作神居里走门的路上（不该再有转场）：照游戏的走。
                    return;
                }
                // 战斗中死亡后游戏自己发起的复活转场：这时才把快照拷回（死亡演出已经结束）。
                // 死亡走游戏自己的复活流程，在长椅上醒来，不回雕像前（前作神居：在前厅醒来）。
                returnStatue = null;
                RestoreForLobby("death / 死亡");
                if (!Lobby.Is(target))
                {
                    if (Hk1Lobby.Active && gm.RespawningHero)
                    {
                        // 复活落点先看临时复活点（HeroController.LocateSpawnPoint）。非致死死亡把它设成了圣所的长椅，
                        // 在大厅里死的没有快照把它还原：改道去前作前厅时清掉，落在我们给的死亡复活点（Codex 审查）。
                        gm.playerData.ResetTempRespawn();
                        // 战斗里死的：照前作回到雕像、门前（场景加载时在回程门那里放了复活点，Hk1Lobby.AddDeathMarker）。
                        string? spot = deathReturn;
                        deathReturn = null;
                        (string Scene, string Marker)? back = spot != null ? Hk1Lobby.DeathReturn(spot) : null;
                        if (back != null)
                        {
                            Redirect(gm, info, back.Value.Scene, back.Value.Marker, respawning: true, $"death: back to {spot} / 死后回到雕像、门前");
                            return;
                        }
                    }
                    Redirect(gm, info, lobby, Lobby.Marker, respawning: true, "to lobby");
                }
                return;
            case LoomState.Lobby when Hk1Lobby.Active && Hk1Doors.Pending is (PantheonDef pDef, Binding pBind, _) && !Hk1Lobby.IsHub(target):
                // 前作的门：开门动画放完，前作的 FSM 发起转场（去前作的第一个 Boss 场景）：改成本作万神殿的第一场。
                Hk1Doors.ClearPending();
                Hk1Lobby.ReleaseHero();
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作的门：开门动画放完，开始万神殿 / hk1 door: animation done, starting pantheon {pDef.Id}（前作的目的地 / hk1 target {target}@{info.EntryGateName}）");
                StartPantheon(pDef, pBind, info);
                if (State != LoomState.ToFight)
                {
                    // 没开成（存档中、快照还在……）：回到这扇门前（前作的回程门），不去前作的 Boss 场景。
                    (string Scene, string Gate)? back = Hk1Lobby.ReturnTo(DoorSpot(pDef.Id));
                    hubWalk = true;
                    SetState(LoomState.ToLobby);
                    lobbyAttempts = 0;
                    if (back != null)
                    {
                        gm.playerData.bossReturnEntryGate = back.Value.Gate;
                        Redirect(gm, info, back.Value.Scene, Arena.ReturnGate, respawning: false, "hk1 door: pantheon did not start");
                    }
                    else
                    {
                        Redirect(gm, info, lobby, Lobby.Marker, respawning: true, "hk1 door: pantheon did not start");
                    }
                    info.Visualization = GameManager.SceneLoadVisualizations.Default;
                }
                return;
            case LoomState.Lobby when Hk1Lobby.Active:
                // 前作神居：前厅、屋顶、手办屋之间走前作自己的门（场景的位置在 FillLocation 里补上）；
                // 去别的地方（前作神居外的房间，没有搬过来）一律送回前厅。
                // 选了门还没开门就走到别的前作房间：那次开门请求作废（Codex 审查）。
                Hk1Doors.ClearPending();
                hubWalk = true;
                SetDeathRespawnToLobby();
                SetState(LoomState.ToLobby);
                lobbyAttempts = 0;
                if (!Hk1Lobby.IsHub(target))
                {
                    Redirect(gm, info, lobby, Lobby.Marker, respawning: true, "hk1 godhome: room not ported");
                }
                else
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome] 前作神居：走门 / hk1 godhome: walking to {target}@{info.EntryGateName}");
                }
                return;
            case LoomState.Lobby:
                // 圣所没有出口：走出大厅的门都送回大厅的长椅。
                SetDeathRespawnToLobby();
                SetState(LoomState.ToLobby);
                lobbyAttempts = 0;
                Redirect(gm, info, lobby, GodhomeConfig.LobbyMarker.Value, respawning: true, "the Loom has no exit");
                return;
            case LoomState.Fight:
            case LoomState.Won:
                if (CurrentBoss != null && CurrentBoss.AllowScenes.Contains(target))
                {
                    return;
                }
                if (State == LoomState.Won)
                {
                    WinRedirects++;
                }
                if (State == LoomState.Fight)
                {
                    // 出口都堵上了，还没赢（场地控制器没报 Boss 倒下）就切场景 = 离开战场，算放弃。
                    // Boss 倒下的演出直接切场景的，要在表里把 arena.end 写在演出之前的状态。
                    GodhomePlugin.Log.LogWarning($"[Godhome] 还没赢就切场景，算放弃 / scene change before the win, counted as abandoned: → {target}@{info.EntryGateName}");
                    LastResult = FightResult.Abandoned;
                    CountAttempt();
                }
                if (Run != null)
                {
                    if (State == LoomState.Won)
                    {
                        // 万神殿：Boss 倒下后游戏自己要切场景（演出、记忆），改道去下一场（或者休息点、结算）。
                        if (!Run.IsLast)
                        {
                            Hk1Battle.PlayRoomChime(SceneManager.GetActiveScene());
                        }
                        ContinueRun(info);
                        return;
                    }
                    FailRun("left the arena / 离开战场");
                }
                SetState(LoomState.ToLobby);
                lobbyAttempts = 0;
                RestoreForLobby("left the arena / 离开战场");
                SetDeathRespawnToLobby();
                {
                    (string backScene, string gate, bool respawning) = LobbyEntry();
                    Redirect(gm, info, backScene, gate, respawning, "left the arena / 离开战场");
                }
                return;
            case LoomState.Rest when Run != null:
                // 休息点：从任意出口离开就进下一场（休息点场景的出口都改道到下一个战场）。
                GodhomePlugin.Log.LogInfo($"[Godhome] 离开休息点 / leaving the rest stop → {Run.Current.Id}");
                Run.Carry = (gm.playerData.health, gm.playerData.silk, gm.playerData.healthBlue);
                BeginBoss(Run.Current, heal: false, info);
                return;
            case LoomState.Rest:
            case LoomState.ToRest:
                // 去休息点的路上游戏自己切了场景，或者没有连战却在休息点（都不该发生）：这次连战作废，回大厅。
                if (Run != null)
                {
                    // 技术故障，不记尝试（和 OnSceneEntered 里“去休息点到了别的场景”一样，Codex 审查 0.9.0 第三轮 #9）。
                    FailRun("transition on the way to the rest stop / 去休息点途中切场景", countAttempt: false);
                }
                SetState(LoomState.ToLobby);
                lobbyAttempts = 0;
                RestoreForLobby("rest without a run / 休息点没有连战");
                SetDeathRespawnToLobby();
                Redirect(gm, info, lobby, Lobby.Marker, respawning: true, "rest without a run");
                return;
            default:
                return;
        }
    }

    private static void Redirect(GameManager gm, GameManager.SceneLoadInfo info, string scene, string gate, bool respawning, string why)
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 改道 / redirect {info.SceneName}@{info.EntryGateName} → {scene}@{gate} ({why})");
        info.SceneName = scene;
        info.EntryGateName = gate;
        info.SceneResourceLocation = null;
        gm.RespawningHero = respawning;
    }

    /// <summary>GameManager.FinishedEnteringScene 后缀。</summary>
    internal void OnSceneEntered()
    {
        if (State == LoomState.Off)
        {
            return;
        }
        GameManager gm = GameManager.instance;
        // 到场时内存里已经不是神居存档（Codex 审查 v0.6.0 #1）：不按模式处理（不设复活点、不回血、不搭圣所、不存档）。
        if (!GodhomeSave.IsGodhome(gm))
        {
            ForceReset("entered a scene without a godhome save / 到场时不是神居存档");
            return;
        }
        string scene = gm.GetSceneNameString();
        switch (State)
        {
            case LoomState.ToLobby:
                if (Lobby.Is(scene))
                {
                    OnLobbyArrive(scene);
                }
                else if (++lobbyAttempts <= 3)
                {
                    // 到了别的场景（不该发生）：下一帧再回大厅。场景状态照样不保存。
                    GodhomePlugin.Log.LogWarning($"[Godhome] 回大厅时到了别的场景，再走一次 / reached {scene} on the way to the lobby, retrying ({lobbyAttempts}/3)");
                    StartCoroutine(NextFrame(() =>
                    {
                        if (State == LoomState.ToLobby)
                        {
                            GoLobby();
                        }
                    }));
                }
                else
                {
                    GodhomePlugin.Log.LogError($"[Godhome] 回不了大厅，停在 {scene} / could not reach the lobby, staying in {scene}");
                    // 不丢快照、不放开场景状态（Codex 审查 0.8.0）：人还在别的场景，之后的存档只写快照，
                    // 这个场景的状态也不写进数据；快照在，就不能再开战，回标题重新读档时才清掉。
                    restoreFailed = true;
                    SetState(LoomState.Lobby);
                    ShowNotice(L.Get("ERR_NEED_RELOAD"), 10f);
                }
                break;
            case LoomState.ToFight:
                if (CurrentBoss != null)
                {
                    if (transitionGaveUp)
                    {
                        // 已经放弃过这次进场（连战作废、束缚解开）：迟到的加载不再开战，下一帧回大厅（Codex 审查 0.9.0 #4）。
                        // 状态还是 ToFight，回大厅不记尝试。
                        GodhomePlugin.Log.LogWarning($"[Godhome] 进场已经放弃过，迟到的场景不开战，回大厅 / the entry was given up; late scene {scene} is not fought, back to the lobby");
                        StartCoroutine(NextFrame(() => ReturnToLobby("late entry after giving up / 放弃后迟到的进场", arenaFault: true)));
                        break;
                    }
                    if (scene != CurrentBoss.EntryScene && scene != CurrentBoss.Scene)
                    {
                        // 进错场景不开战（Codex 审查 #13）：下一帧回大厅，不在别的场景里按战斗处理。
                        // 是技术故障，不是玩家放弃：万神殿也不记尝试（Codex 审查 0.9.0 第二轮 #7）。
                        GodhomePlugin.Log.LogWarning($"[Godhome] 进入的场景不对，回大厅 / unexpected scene {scene}, expected {CurrentBoss.EntryScene}; back to the lobby");
                        StartCoroutine(NextFrame(() => ReturnToLobby("unexpected scene / 进错场景", arenaFault: true)));
                        break;
                    }
                    fightStart = Time.unscaledTime;
                    SetState(LoomState.Fight);
                    if (Run != null)
                    {
                        // 万神殿换场：上一个场景（战场或休息点）已经离开，这个战场的场景状态照常保存（多场景战斗要用）。
                        SaveGuard.SuppressLevelState = false;
                        HookDamage();
                        ReapplyCarry();
                    }
                    // 大厅场景卸载时（OnUnitySceneUnloaded）已经放过；万一没收到卸载回调，这里兜底（大厅确实不在了才放）。
                    if (LobbyGone && (SanctumArt.Holding || Tk2dArt.Holding || SanctumAudio.Holding || Sanctum.Prepared))
                    {
                        ReleaseSanctum("arena entered / 进入战场");
                    }
                    ShowBanner(L.F("BANNER_FIGHT", CurrentBoss.DisplayName), 3f);
                    GodhomePlugin.Log.LogInfo($"[Godhome] 进入战场 / arena entered: {scene}");
                    // 前作神居：揭开加载时盖上的前作连战转场（前作 BossSceneController.Start 的 GG TRANSITION IN）。
                    Hk1Battle.Reveal();
                    // 场地在加载时已经改好（Arena.Prepare）；人应该是从 door_dreamEnter 进来的。不是（门没建成、游戏退回了别的入口）
                    // 就不在没改过的原场景里打，下一帧回大厅。
                    if (!Arena.OnHeroEntered(this))
                    {
                        StartCoroutine(NextFrame(() => ReturnToLobby("arena not prepared / 场地没准备好", arenaFault: true)));
                    }
                }
                break;
            case LoomState.ToRest:
                if (transitionGaveUp)
                {
                    // 去休息点放弃过（连战已经作废）：迟到的到达直接回大厅，不停在休息点（Codex 审查 0.9.0 #4）。
                    GodhomePlugin.Log.LogWarning($"[Godhome] 去休息点已经放弃过，回大厅 / the rest-stop transition was given up; reached {scene}, back to the lobby");
                    StartCoroutine(NextFrame(() =>
                    {
                        if (State == LoomState.ToRest)
                        {
                            LeaveForLobby(null, "late rest stop after giving up / 放弃后迟到的休息点");
                        }
                    }));
                    break;
                }
                if (Run == null)
                {
                    break;
                }
                Hk1Spa.Arrived();
                if (scene != RestStop().Scene)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 去休息点时到了别的场景，这次万神殿作废 / reached {scene} instead of the rest stop {RestStop().Scene}; dropping the run");
                    StartCoroutine(NextFrame(() =>
                    {
                        if (State == LoomState.ToRest)
                        {
                            FailRun("wrong rest scene / 休息点场景不对", countAttempt: false);
                            LeaveForLobby(null, "wrong rest scene / 休息点场景不对");
                        }
                    }));
                    break;
                }
                // 回血和补工具仍在到场时做。保留上一场剩余的丝，由长椅的风灵谣恢复。
                SaveGuard.SuppressLevelState = false;
                SetState(LoomState.Rest);
                SetDeathRespawnToLobby();
                if (Run.Carry is { } carry)
                {
                    gm.playerData.silk = Mathf.Clamp(carry.Silk, 0, gm.playerData.CurrentSilkMax);
                    HeroController.SilentInstance?.RefreshSilk();
                    Run.Carry = null;
                }
                HeroController.SilentInstance?.MaxHealth();
                RunToolSupply.RefillAtRest(gm.playerData);
                GodhomePlugin.Log.LogInfo($"[Godhome] 到了休息点 / at the rest stop: {scene}（下一场 / next {Run.Current.Id}，{Run}）");
                if (scene == Hk1Lobby.Spa)
                {
                    // 温泉的入口门是“自己淡入”的门（前作由场景控制器的转场揭开），丝之歌不替它淡入，整屏一直黑（自检截图实测）。
                    StartCoroutine(SpaFadeIn(fightSerial));
                    Hk1Battle.Reveal();
                }
                break;
        }
    }

    private static IEnumerator NextFrame(Action a)
    {
        yield return null;
        a();
    }

    private void CheckTransitionTimeout()
    {
        // 放弃是终止：不再重试（Codex 审查 0.9.0 #4：放弃前没重试过的，加载标志解除后还会再发一次）。
        // 之后只等迟到的到达（OnSceneEntered 按放弃处理，回大厅）或回标题重新读档。
        if (transitionGaveUp)
        {
            return;
        }
        float waited = Time.unscaledTime - stateSince;
        if (waited < TransitionTimeout)
        {
            return;
        }
        GameManager gm = GameManager.UnsafeInstance;
        // 重试过还没到、或者一直卡在转场里，等到两倍超时还没好：放弃（Codex 审查 0.8.0）。
        // 连战作废、束缚解开；快照留着（存档照样只写快照），不能再开战，提示回标题重新读档。
        if (waited >= TransitionTimeout * 2f && (retriedTransition || gm == null || gm.IsInSceneTransition))
        {
            transitionGaveUp = true;
            // 游戏的切场景流程用 Time.deltaTime 倒数淡出时间（GameManager.BeginSceneTransitionRoutine），时间停住就永远走不完：把时间倍率一起记下。
            GodhomePlugin.Log.LogError($"[Godhome] 场景切换一直没完成，放弃 / the transition never finished in state {State}; giving up (in transition: {gm != null && gm.IsInSceneTransition}, "
                + $"timeScale {Time.timeScale:0.###}, TimeManager {TimeManager.TimeScale:0.###}, game state {(gm != null ? gm.GameState.ToString() : "-")}, scene {(gm != null ? gm.GetSceneNameString() : "-")})");
            if (Run != null)
            {
                // 切场景卡住是技术故障，不记万神殿的尝试（Codex 审查 0.9.0 第二轮 #7）。
                FailRun("transition timed out / 切场景超时", countAttempt: false);
            }
            if (State == LoomState.ToLobby && Hk1Lobby.Active)
            {
                // 回前作神居卡住了：回标题重新读档以后改用织忆圣所，不再卡在同一个地方（Codex 审查）。
                Hk1Lobby.Abandon("the transition to the hk1 hub never finished / 进前作神居一直没完成");
                // 和重试那条路一样清掉走门标记：迟到的到达不能按“走门”处理（那样不搭圣所、不回血、不存档，审查）。
                hubWalk = false;
            }
            Bindings.Clear("transition timed out / 切场景超时");
            if (SaveGuard.Active)
            {
                restoreFailed = true;
            }
            ShowNotice(L.Get("ERR_NEED_RELOAD"), 10f);
            return;
        }
        if (gm == null || gm.IsInSceneTransition)
        {
            return;
        }
        if (retriedTransition)
        {
            return;
        }
        retriedTransition = true;
        stateSince = Time.unscaledTime;
        GodhomePlugin.Log.LogWarning($"[Godhome] 场景切换超时，重试 / transition timed out in state {State}, retrying");
        switch (State)
        {
            case LoomState.ToLobby:
                if (Hk1Lobby.Active)
                {
                    // 前作神居的房间没进去（加载失败）：这次改用织忆圣所，重试去圣所（Codex 审查）。
                    Hk1Lobby.Abandon("the transition to the hk1 hub timed out / 进前作神居超时");
                    hubWalk = false;
                }
                RestoreForLobby("timeout / 超时");
                GoLobby();
                break;
            case LoomState.ToFight when CurrentBoss != null:
                // 重新进一次：这一场的场地状态清零（新加载的场景重新准备）。换一个场次号（Codex 审查 0.9.0 #5）：
                // 上一次加载建的控制器、开战标记、延后步骤拿的是旧号，都不再认。
                fightSerial++;
                heroRecovery.BeginFight(gm, fightSerial);
                Arena.Begin(CurrentBoss, CurrentTier, fightSerial, OnArenaBossesDead, OnArenaComplete);
                Transition(CurrentBoss.EntryScene, Arena.EnterGate, respawning: false);
                break;
            case LoomState.ToRest:
                {
                    (string scene, string gate, bool respawning) = RestStop();
                    Hk1Spa.BeforeLoad(scene);
                    Transition(scene, gate, respawning);
                }
                break;
        }
        retriedTransition = true;
    }

    // ───────────────────────────── 其他 ─────────────────────────────

    private void SetState(LoomState s)
    {
        LoomState was = State;
        if (s != LoomState.Lobby)
        {
            heroRecovery.CancelLobby();
        }
        if (State != s)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 状态 / state {State} → {s}");
            transitionGaveUp = false;
            wonLeaving = false;
        }
        State = s;
        stateSince = Time.unscaledTime;
        // 赢了离开战场（下一场、休息点、回大厅，包括改道游戏自己的转场）：Boss 倒下的演出收起的 HUD 叫回来。
        // 只在从 Won 走的时候：从大厅开战（面板、雕像的选难度界面自己会收、会放）、战斗中放弃、死亡都不在这里管（Codex 审查第七轮 #1、#4）。
        if (was == LoomState.Won && s is LoomState.ToFight or LoomState.ToRest or LoomState.ToLobby)
        {
            RestoreHud($"leaving the won arena / 赢了离开战场 → {s}");
        }
    }

    /// <summary>装饰性的提示（进场、回大厅、胜利、死亡）只写日志、不上屏（0.7.0 起照你的要求，界面上不画这些文字）。</summary>
    internal void ShowBanner(string text, float seconds)
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 提示（不上屏）/ notice (not shown): {text}");
    }

    /// <summary>
    /// 出错、存档被拦、要等一下：玩家得知道为什么没反应、下一步怎么做，上屏显示 seconds 秒（LoomUI 底部一行），也写日志
    /// （Codex 审计 0.10.3 F05：以前只写日志，开始按钮没反应、拒绝存档、还原失败都看不到原因）。
    /// error = false 是“请稍等”这类，和出错用不同的颜色。
    /// </summary>
    internal void ShowNotice(string text, float seconds, bool error = true)
    {
        if (error)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 提示（上屏）/ notice shown: {text}");
        }
        else
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 提示（上屏）/ notice shown: {text}");
        }
        LoomUI.Instance?.ShowNotice(text, seconds, error);
    }
}
