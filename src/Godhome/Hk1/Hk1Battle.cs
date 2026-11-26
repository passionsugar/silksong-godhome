using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 前作的连战转场特效和音效（gg_battle_transitions：前作 BossSceneController、雕像回程门生成的预制体）。
/// 用前作神居时照前作放：
/// - 进战场、进温泉：场景加载时生成、马上盖住（GG TRANSITION OUT INSTANT），人放进来后揭开（GG TRANSITION IN，前作 BossSceneController.Start）；
/// - 一场打完、切场景之前：GG TRANSITION OUT（万神殿中间）、GG TRANSITION FINAL（万神殿最后一场）、GG TRANSITION OUT STATUE（雕像单场），
///   等它发 GG TRANSITION END（最多几秒）再切（前作 BossSceneController.EndSceneDelayed）。
/// 预制体从前作手办屋复仇蝇雕像回程门的 FSM 参数里取（Hk1Assets.TransitionsHolder：单独打包这个物体，不进手办屋也能用）。
/// 取不到就不放，照旧是丝之歌的淡入淡出。特效只是盖在画面上的东西：放完、到时间都销毁（看门狗），不会一直盖着画面。
/// </summary>
internal static class Hk1Battle
{
    internal const string PrefabName = "gg_battle_transitions";
    internal const string EvIn = "GG TRANSITION IN";
    internal const string EvOutInstant = "GG TRANSITION OUT INSTANT";
    internal const string EvOut = "GG TRANSITION OUT";
    internal const string EvFinal = "GG TRANSITION FINAL";
    internal const string EvOutStatue = "GG TRANSITION OUT STATUE";
    internal const string EvEnd = "GG TRANSITION END";

    /// <summary>揭开以后特效最多留多久（前作的揭开约一秒）。</summary>
    private const float InLifetime = 5f;

    /// <summary>盖住以后一直没等到揭开（人没进来、进错场景）：最多盖多久。</summary>
    private const float CoverLifetime = 15f;

    /// <summary>出去：读不到特效自己的时长时，最多等多久 GG TRANSITION END。</summary>
    private const float OutWait = 2.5f;

    /// <summary>特效的主 FSM（前作 gg_battle_transitions 上的 Transitions，导出核实）和它放完后停的状态。</summary>
    private const string MainFsm = "Transitions";
    private static readonly string[] FinishedStates = { "Send Event", "Send Event Basic", "Disable" };

    /// <summary>调试：自检时设成截图目录，每种转场放到一半截一张图（tr-*.jpg）。平时是 null。</summary>
    internal static string? ShotDir;
    private static readonly HashSet<string> shot = new(StringComparer.Ordinal);

    /// <summary>出去以后没切走（切场景失败）：特效最多留多久。</summary>
    private const float OutLifetime = 8f;

    private static AsyncOperationHandle<GameObject>? handle;
    private static GameObject? template;
    private static bool loadStarted, searched, dumped;
    private static string failure = "";

    /// <summary>用不了的原因（自检、日志用）。</summary>
    internal static string Failure => failure.Length > 0 ? failure : loadStarted ? "还没加载好 / still loading" : "没开始加载 / not started";

    /// <summary>场景加载时盖上的那个（等人进来揭开）。</summary>
    private static GameObject? cover;
    private static Scene coverScene;

    private static Runner? runner;

    /// <summary>能放：在用前作神居，预制体已经取到。</summary>
    internal static bool Usable
    {
        get
        {
            if (!Hk1Lobby.Active)
            {
                return false;
            }
            Resolve();
            return template != null;
        }
    }

    /// <summary>开始在后台加载载体（Hk1Lobby.Decide 定下用前作神居时调；只做一次）。</summary>
    internal static void BeginLoad()
    {
        if (loadStarted)
        {
            return;
        }
        loadStarted = true;
        handle = Hk1Assets.LoadTransitionsHolder();
        if (handle == null)
        {
            failure = "载体加载不了 / the holder could not be loaded";
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场用不了，照旧用丝之歌的淡入淡出 / hk1 battle transitions unavailable, using the plain fade: {failure}");
        }
    }

    /// <summary>载体加载好了就从它的 FSM 参数里找出特效的预制体（只找一次）。</summary>
    private static void Resolve()
    {
        if (template != null || searched || handle == null)
        {
            return;
        }
        AsyncOperationHandle<GameObject> h = handle.Value;
        if (!h.IsValid() || !h.IsDone)
        {
            return;
        }
        searched = true;
        try
        {
            if (h.Status != AsyncOperationStatus.Succeeded || h.Result == null)
            {
                failure = $"载体加载失败 / holder load failed: {h.Status} {h.OperationException?.GetBaseException().Message}";
            }
            else
            {
                template = FindPrefab(h.Result, out string where);
                failure = template == null ? $"载体 {h.Result.name} 的 FSM 参数里没有 {PrefabName} / no {PrefabName} in the holder's fsm params" : "";
                if (template != null)
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场特效取到了 / hk1 battle transitions ready: {template.name}（{where}，active {template.activeSelf}，"
                        + $"FSM {template.GetComponentsInChildren<PlayMakerFSM>(true).Length}，EventRegister {template.GetComponentsInChildren<EventRegister>(true).Length}，"
                        + $"声音 / audio {template.GetComponentsInChildren<AudioSource>(true).Length}）");
                }
            }
        }
        catch (Exception e)
        {
            template = null;
            failure = "找预制体出错 / finding the prefab failed: " + e.GetBaseException().Message;
        }
        if (template == null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场用不了，照旧用丝之歌的淡入淡出 / hk1 battle transitions unavailable, using the plain fade: {failure}");
        }
    }

    private static readonly AccessTools.FieldRef<ActionData, List<Object>> UnityObjects = AccessTools.FieldRefAccess<ActionData, List<Object>>("unityObjectParams");
    private static readonly AccessTools.FieldRef<ActionData, List<FsmGameObject>> FsmGameObjects = AccessTools.FieldRefAccess<ActionData, List<FsmGameObject>>("fsmGameObjectParams");

    /// <summary>在物体（没实例化的预制体）的 FSM 序列化参数里找 gg_battle_transitions（CreateObject 的 gameObject 参数）。</summary>
    private static GameObject? FindPrefab(GameObject holder, out string where)
    {
        where = "";
        foreach (PlayMakerFSM pm in holder.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            Fsm? f = pm.FsmTemplate != null ? pm.FsmTemplate.fsm : pm.Fsm;
            if (f?.States == null)
            {
                continue;
            }
            foreach (FsmState st in f.States)
            {
                ActionData? ad = st?.ActionData;
                if (ad == null)
                {
                    continue;
                }
                GameObject? go = UnityObjects(ad)?.OfType<GameObject>().FirstOrDefault(o => o != null && o.name == PrefabName)
                    ?? FsmGameObjects(ad)?.Select(v => v?.Value).FirstOrDefault(o => o != null && o.name == PrefabName);
                if (go != null)
                {
                    where = $"{pm.name} / {f.Name} / {st!.Name}";
                    return go;
                }
            }
        }
        return null;
    }

    // ───────────────────────────── 进来 ─────────────────────────────

    /// <summary>
    /// 战场、温泉刚加载完（画面还是黑的，人还没进来）：生成特效放进这个场景，下一帧盖住（OUT INSTANT）。
    /// 等人进来调 Reveal 揭开。用不了就什么都不做。
    /// </summary>
    internal static void Cover(Scene scene)
    {
        if (!Usable)
        {
            return;
        }
        try
        {
            Drop(ref cover);
            GameObject go = Spawn(scene, "cover");
            cover = go;
            coverScene = scene;
            Run().Begin(CoverRoutine(go));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：盖住出错 / hk1 battle transitions: cover failed: {e.GetBaseException().Message}");
        }
    }

    private static IEnumerator CoverRoutine(GameObject go)
    {
        // 等它的 FSM 跑过 Start 再发（前作 BossSceneController 也是在 Start 里生成后发的）。
        yield return null;
        if (go == null)
        {
            yield break;
        }
        Send(go, EvOutInstant);
        float t = 0f;
        while (go != null && go == cover && t < CoverLifetime)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (go != null && go == cover)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：盖了 {CoverLifetime:0} 秒没等到揭开，撤掉 / hk1 battle transitions: no reveal in {CoverLifetime:0} s, removing the cover");
            Drop(ref cover);
        }
    }

    /// <summary>自检开始时调：截图目录（null = 不截），每种转场重新截一次。</summary>
    internal static void SetShotDir(string? dir)
    {
        ShotDir = dir;
        shot.Clear();
    }

    /// <summary>
    /// 人进来了（战场、温泉）：揭开（GG TRANSITION IN）。加载时没盖（或者盖的不是这个场景的）就现生成一个，照前作先盖住 hold 秒再揭开。
    /// </summary>
    internal static void Reveal(float hold = 0f)
    {
        if (!Usable)
        {
            return;
        }
        try
        {
            Scene active = SceneManager.GetActiveScene();
            GameObject? go = cover != null && coverScene == active ? cover : null;
            if (go == null)
            {
                Drop(ref cover);
                go = Spawn(active, "reveal");
                hold = Mathf.Max(hold, 0.1f);
            }
            cover = null;
            Run().Begin(RevealRoutine(go, hold));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：揭开出错 / hk1 battle transitions: reveal failed: {e.GetBaseException().Message}");
        }
    }

    private static IEnumerator RevealRoutine(GameObject go, float hold)
    {
        if (hold > 0f)
        {
            // 现生成的：等一帧（FSM 跑过 Start），盖住，停一会儿再揭开（前作 transitionInHoldTime）。
            yield return null;
            if (go == null)
            {
                yield break;
            }
            Send(go, EvOutInstant);
            yield return new WaitForSeconds(hold);
        }
        if (go == null)
        {
            yield break;
        }
        Send(go, EvIn);
        Dump(go);
        Run().Begin(RevealWatch(go));
        yield return DebugShot(go, "in");
        float t = 0f;
        while (go != null && t < InLifetime && !Finished(go))
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        // 放完了就照前作留在场景里（跟着场景卸载，动画的尾巴放完）；到时间还没放完才强行撤掉（看门狗）。
        if (go != null && !Finished(go))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：揭开 {InLifetime:0} 秒还没放完（{MainOf(go)?.ActiveStateName ?? "-"}），撤掉 / hk1 battle transition: reveal not finished in {InLifetime:0} s, removing it");
            Retire(go, "in");
        }
    }

    // ───────────────────────────── 出去 ─────────────────────────────

    /// <summary>
    /// 一场打完、切场景之前：放“出去”（ev 是 EvOut / EvFinal / EvOutStatue），等到 GG TRANSITION END 或者最多 OutWait 秒（暂停不算），再调 then。
    /// 用不了就直接调 then。then 一定会被调一次。
    /// </summary>
    internal static void PlayOut(string ev, Action then)
    {
        // then 只调一次：协程里、出错兜底、外面的截止时间（ModeController.WonOutCap）哪条先到都只算一次。
        bool called = false;
        void Once()
        {
            if (called)
            {
                return;
            }
            called = true;
            then();
        }
        if (!Usable)
        {
            Once();
            return;
        }
        try
        {
            Drop(ref cover);
            GameObject go = Spawn(SceneManager.GetActiveScene(), "out");
            Run().Begin(OutRoutine(go, ev, Once));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：出去出错 / hk1 battle transitions: out failed: {e.GetBaseException().Message}");
            Once();
        }
    }

    /// <summary>
    /// 正在放的“出去”的 END 监听。协程半路出错停了，摘不掉的订阅留在这里，下一次 PlayOut、AbandonOut 时摘
    /// （Codex 审计 0.10.3 F06：以前第一次 yield 之后外部调用一抛异常，then 和解除订阅都不会执行）。
    /// </summary>
    private static EventRegister? outListen;
    private static Action? outHandler;

    private static void DropOutListen()
    {
        if (outListen != null && outHandler != null)
        {
            try
            {
                outListen.ReceivedEvent -= outHandler;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：摘 {EvEnd} 监听出错 / removing the {EvEnd} listener failed: {e.Message}");
            }
        }
        outListen = null;
        outHandler = null;
    }

    /// <summary>外面等“出去”等到截止时间，自己走了（ModeController）：摘掉这次的监听。特效跟着旧场景卸载。</summary>
    internal static void AbandonOut(string why)
    {
        if (outListen != null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：不等“出去”了 / giving up on the hk1 out transition（{why}）");
        }
        DropOutListen();
    }

    private static IEnumerator OutRoutine(GameObject go, string ev, Action then)
    {
        bool ended = false;
        DropOutListen();
        try
        {
            EventRegister listen = EventRegister.GetRegisterGuaranteed(Run().gameObject, EvEnd);
            Action h = OnEnd;
            listen.ReceivedEvent += h;
            outListen = listen;
            outHandler = h;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：收不到 {EvEnd} / cannot listen for {EvEnd}: {e.GetBaseException().Message}");
        }
        yield return null;
        float t = 0f;
        float limit = OutWait;
        if (go != null)
        {
            // yield 之后的外部调用（发事件、读 FSM 变量、调试截图）各自兜住：一个抛异常，后面照样解除订阅、调 then。
            try
            {
                Send(go, ev);
                Dump(go);
                limit = Duration(go, ev);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：放 {ev} 出错，不等了 / playing {ev} failed, not waiting: {e.GetBaseException().Message}");
                limit = 0f;
            }
            yield return Guarded(DebugShot(go, ev == EvFinal ? "final" : ev == EvOutStatue ? "statue" : "out"), "debug shot");
            while (!ended && go != null && t < limit)
            {
                GameManager gm = GameManager.UnsafeInstance;
                if (gm == null || !gm.IsGamePaused())
                {
                    t += Time.unscaledDeltaTime;
                }
                yield return null;
            }
        }
        DropOutListen();
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场：{ev} {(ended ? $"放完 / ended（{t:0.0}/{limit:0.0} 秒）" : go == null ? "特效没了 / effect gone" : $"等了 {t:0.0} 秒没收到 END / no END after {t:0.0} s")}，切场景 / leaving");
        if (!ended)
        {
            // 没放完就走（特效会跟着旧场景卸载）：它关掉的暂停还回来（放完时它自己在 Send Event 里还）。
            try
            {
                ReleasePause(go, ev);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：还暂停出错 / releasing the pause failed: {e.Message}");
            }
        }
        // 暂停中不切（ModeController.Transition 会挂起，这里等一下更稳）。
        while (GameManager.UnsafeInstance != null && GameManager.UnsafeInstance.IsGamePaused())
        {
            yield return null;
        }
        try
        {
            then();
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 前作连战转场之后切场景出错 / leaving after the hk1 battle transition failed: {e}");
        }
        // 切场景时特效跟着旧场景卸载；没切走（切场景失败）也不能一直盖着。
        yield return new WaitForSecondsRealtime(OutLifetime);
        try
        {
            Retire(go, ev);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：撤掉特效出错 / retiring the effect failed: {e.Message}");
        }

        void OnEnd() => ended = true;
    }

    /// <summary>
    /// 跑一个子协程，它抛异常就记警告、当它结束（不让异常打断外面的协程）。含 yield 的方法里不能用带 catch 的 try 包住 yield，
    /// 所以把 MoveNext 单独包起来。子协程 yield 出来的嵌套协程不在保护范围内（这里只用于调试截图）。
    /// </summary>
    private static IEnumerator Guarded(IEnumerator inner, string what)
    {
        while (true)
        {
            object? current;
            try
            {
                if (!inner.MoveNext())
                {
                    yield break;
                }
                current = inner.Current;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：{what} 出错，跳过 / {what} failed, skipped: {e.GetBaseException().Message}");
                yield break;
            }
            yield return current;
        }
    }

    /// <summary>特效自己的时长（Transitions FSM 的变量，前作 FSM 导出核实）加一点余量；读不到用 OutWait。</summary>
    private static float Duration(GameObject go, string ev)
    {
        string var = ev == EvFinal ? "TransitionEndTime" : "Transition Out Time";
        PlayMakerFSM? pm = MainOf(go);
        FsmFloat? f = pm != null ? pm.FsmVariables.FindFsmFloat(var) : null;
        float d = f != null ? Mathf.Clamp(f.Value + 1f, 1f, 8f) : OutWait;
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场：{ev} 时长 / duration {var} = {(f != null ? f.Value.ToString("0.##") : "-")}，最多等 / waiting up to {d:0.##} 秒");
        return d;
    }

    private static PlayMakerFSM? MainOf(GameObject? go)
        => go == null ? null : go.GetComponents<PlayMakerFSM>().FirstOrDefault(p => p.FsmName == MainFsm);

    /// <summary>特效的主 FSM 放完了（停在发 END 的状态上）。</summary>
    private static bool Finished(GameObject go)
    {
        PlayMakerFSM? pm = MainOf(go);
        return pm != null && Array.IndexOf(FinishedStates, pm.ActiveStateName) >= 0;
    }

    /// <summary>
    /// 前作特效放的时候关暂停（SetPlayerDataBool disablePause = true），放完在 Send Event 里打开。
    /// 没放完就销毁、离开的：替它打开，不然回到大厅、进下一场后暂停键没反应。
    /// </summary>
    private static void ReleasePause(GameObject? go, string why)
    {
        if (go != null && Finished(go))
        {
            return;
        }
        PlayerData pd = PlayerData.instance;
        if (pd != null && pd.disablePause)
        {
            pd.disablePause = false;
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场没放完（{why}，{MainOf(go)?.ActiveStateName ?? "-"}），暂停还回来 / pause re-enabled");
        }
    }

    /// <summary>销毁一个特效（没放完的话先把暂停还回来）。</summary>
    private static void Retire(GameObject? go, string why)
    {
        if (go == null)
        {
            return;
        }
        ReleasePause(go, why);
        Object.Destroy(go);
    }

    /// <summary>调试：自检时每种转场放到一半截一张连界面的图（看前作特效确实画出来了）。</summary>
    private static IEnumerator DebugShot(GameObject go, string kind)
    {
        if (ShotDir == null || !shot.Add(kind))
        {
            yield break;
        }
        yield return new WaitForSecondsRealtime(0.35f);
        if (go != null)
        {
            yield return Shots.Capture(Path.Combine(ShotDir, $"tr-{kind}.jpg"), null);
        }
    }

    // ───────────────────────────── 共用 ─────────────────────────────

    private static GameObject Spawn(Scene scene, string why)
    {
        GameObject go = Object.Instantiate(template!);
        go.name = PrefabName + " (Godhome " + why + ")";
        if (scene.IsValid() && scene.isLoaded && go.scene != scene)
        {
            SceneManager.MoveGameObjectToScene(go, scene);
        }
        // 特效的动画不许被剔除：温泉里实测揭开的动画间歇地几乎不动（进度 1.2 秒才 0.01），白底一直盖在界面上。
        foreach (Animator a in go.GetComponentsInChildren<Animator>(true))
        {
            if (!loggedCulling)
            {
                loggedCulling = true;
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场的动画 / hk1 transition animators: "
                    + string.Join("，", go.GetComponentsInChildren<Animator>(true).Select(x => $"{x.name} culling {x.cullingMode} update {x.updateMode}")));
            }
            a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        return go;
    }

    private static bool loggedCulling;

    /// <summary>
    /// 揭开放完以后白底还盖着（揭开的动画没走完）：直接关掉揭开的那组图，不让白屏留在画面上。
    /// 时长按特效自己的 Transition In Time 加一点余量，暂停的时间不算。
    /// </summary>
    private static IEnumerator RevealWatch(GameObject go)
    {
        PlayMakerFSM? pm = MainOf(go);
        FsmFloat? f = pm != null ? pm.FsmVariables.FindFsmFloat("Transition In Time") : null;
        float limit = Mathf.Clamp((f != null ? f.Value : 1f) + 0.75f, 1f, 4f);
        float t = 0f;
        while (go != null && t < limit)
        {
            GameManager gm = GameManager.UnsafeInstance;
            if (gm == null || !gm.IsGamePaused())
            {
                t += Time.unscaledDeltaTime;
            }
            yield return null;
        }
        if (go == null)
        {
            yield break;
        }
        GameObject? enter = pm != null ? pm.FsmVariables.FindFsmGameObject("TransitionIn")?.Value : null;
        if (enter == null || !enter.activeInHierarchy)
        {
            yield break;
        }
        SpriteRenderer[] white = enter.GetComponentsInChildren<SpriteRenderer>(false)
            .Where(r => r.enabled && r.color.a > 0.5f && r.sprite != null && r.sprite.name.IndexOf("white", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (white.Length == 0)
        {
            yield break;
        }
        Animator? anim = enter.GetComponent<Animator>();
        string state = anim != null ? $"t={anim.GetCurrentAnimatorStateInfo(0).normalizedTime:0.##} enabled {anim.enabled} culling {anim.cullingMode}" : "-";
        GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场：揭开 {t:0.0} 秒后白底还盖着（{string.Join("，", white.Select(r => r.name + " a=" + r.color.a.ToString("0.##") + " visible " + r.isVisible))}；动画 / animator {state}），收掉 / hk1 transition: still white {t:0.0} s after the reveal, hiding it");
        enter.SetActive(false);
    }

    /// <summary>照前作 EventRegister.SendEvent 发事件，只发给这个特效：它自己订了这个事件的 EventRegister 收；一个都没有就直接发给它的 FSM。</summary>
    private static void Send(GameObject go, string ev)
    {
        int byRegister = 0;
        foreach (EventRegister er in go.GetComponentsInChildren<EventRegister>(true))
        {
            if (er.SubscribedEvent == ev)
            {
                er.ReceiveEvent();
                byRegister++;
            }
        }
        int byFsm = 0;
        if (byRegister == 0)
        {
            foreach (PlayMakerFSM pm in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                pm.SendEvent(ev);
                byFsm++;
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作连战转场 / hk1 battle transition: {ev}（EventRegister {byRegister}，FSM {byFsm}）"
            + $" 状态 / states: {string.Join("，", go.GetComponentsInChildren<PlayMakerFSM>(true).Select(p => p.FsmName + "=" + p.ActiveStateName))}");
    }

    private static void Drop(ref GameObject? go)
    {
        Retire(go, "drop");
        go = null;
    }

    /// <summary>调试开着：第一次放的时候把特效的层级、FSM 写到 BepInEx\Godhome\hk1-battle-transitions.txt（调时间、核对事件用）。</summary>
    private static void Dump(GameObject go)
    {
        if (dumped || !GodhomeConfig.DebugMode.Value)
        {
            return;
        }
        dumped = true;
        try
        {
            var sb = new StringBuilder();
            Camera cam = Camera.main;
            sb.AppendLine($"# {go.name} pos {go.transform.position} camera {(cam != null ? cam.transform.position.ToString() : "-")} scene {go.scene.name}");
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                sb.Append($"- {FsmDump.PathOf(t)} active {t.gameObject.activeSelf} pos {t.position} layer {t.gameObject.layer}:");
                foreach (Component c in t.GetComponents<Component>())
                {
                    sb.Append(' ').Append(c != null ? c.GetType().Name : "(missing)");
                    if (c is Renderer r)
                    {
                        sb.Append($"[{r.sortingLayerName}/{r.sortingOrder} {(r.sharedMaterial != null ? r.sharedMaterial.name : "-")}]");
                    }
                    else if (c is AudioSource a)
                    {
                        sb.Append($"[{(a.clip != null ? a.clip.name : "-")} mixer {(a.outputAudioMixerGroup != null ? a.outputAudioMixerGroup.name : "-")}]");
                    }
                    else if (c is EventRegister er)
                    {
                        sb.Append($"[{er.SubscribedEvent}]");
                    }
                }
                sb.AppendLine();
            }
            foreach (PlayMakerFSM pm in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                sb.AppendLine();
                FsmDump.Write(pm, sb);
            }
            string dir = Path.Combine(Paths.BepInExRootPath, "Godhome");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "hk1-battle-transitions.txt"), sb.ToString());
            GodhomePlugin.Log.LogInfo("[Godhome] 前作连战转场特效的结构写到 / hk1 battle transitions dumped to BepInEx\\Godhome\\hk1-battle-transitions.txt");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作连战转场特效导出失败 / dumping the hk1 battle transitions failed: {e.Message}");
        }
    }

    /// <summary>调试：场上所有前作转场特效（主 FSM 状态、开着的动画和进度），还有前作 FSM 用的白幕（全局变量 HUD Blanker White）。</summary>
    internal static string Describe()
    {
        var sb = new StringBuilder("前作转场 / hk1 transitions:");
        try
        {
            foreach (PlayMakerFSM pm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(p => p != null && p.FsmName == MainFsm && p.name.StartsWith(PrefabName, StringComparison.Ordinal)))
            {
                sb.Append($" [{pm.name} @{pm.gameObject.scene.name} {pm.ActiveStateName}");
                foreach (Animator a in pm.GetComponentsInChildren<Animator>(false))
                {
                    AnimatorClipInfo[] ci = a.GetCurrentAnimatorClipInfo(0);
                    AnimatorStateInfo si = a.GetCurrentAnimatorStateInfo(0);
                    sb.Append($"；{a.name} {(ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name + " " + ci[0].clip.length.ToString("0.##") + "s" : "-")} t={si.normalizedTime:0.##} "
                        + $"速度 / speed {a.speed:0.##}×{si.speed:0.##} {a.updateMode} culling {a.cullingMode} enabled {a.enabled} "
                        + $"visible {a.GetComponentsInChildren<Renderer>(false).Count(r => r.isVisible)}/{a.GetComponentsInChildren<Renderer>(false).Length}");
                }
                sb.Append(']');
            }
            GameObject? b = FsmVariables.GlobalVariables.FindFsmGameObject("HUD Blanker White")?.Value;
            sb.Append(" 白幕 / HUD Blanker White: ");
            if (b == null)
            {
                sb.Append('-');
            }
            else
            {
                sb.Append($"{FsmDump.PathOf(b.transform)} active {b.activeInHierarchy} {string.Join(",", b.GetComponents<PlayMakerFSM>().Select(p => p.FsmName + "=" + p.ActiveStateName))}");
                foreach (Renderer r in b.GetComponentsInChildren<Renderer>(false).Take(3))
                {
                    Color c = r is SpriteRenderer s ? s.color : r.sharedMaterial != null && r.sharedMaterial.HasProperty("_Color") ? r.sharedMaterial.color : Color.clear;
                    sb.Append($"；{r.name} {r.GetType().Name} enabled {r.enabled} color {c}");
                }
                foreach (PlayMakerFSM p in b.GetComponents<PlayMakerFSM>())
                {
                    FsmFloat? ft = p.FsmVariables.FindFsmFloat("Fade Time");
                    sb.Append($"；{p.FsmName} Fade Time {(ft != null ? ft.Value.ToString("0.##") : "没有 / none")}");
                }
            }
        }
        catch (Exception e)
        {
            sb.Append(" 出错 / error " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>模式重置：盖着的撤掉（协程跟着各自的特效收尾）。</summary>
    internal static void Clear()
    {
        Drop(ref cover);
    }

    private static Runner Run()
    {
        if (runner == null)
        {
            var go = new GameObject("Godhome Hk1 Battle Transitions");
            Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<Runner>();
        }
        return runner;
    }

    /// <summary>跑协程用的常驻物体（不跟着场景卸载）。</summary>
    private sealed class Runner : MonoBehaviour
    {
        internal void Begin(IEnumerator r) => StartCoroutine(r);
    }
}
