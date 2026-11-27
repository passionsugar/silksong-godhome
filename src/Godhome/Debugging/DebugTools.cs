using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 调试功能（Debug.Enabled=true 才生效，且只在 Safety.EnabledSlots 允许的存档槽）：
/// 秒杀、无敌、自动读档、自检（每个 Boss 跑 从入场门进场 → 秒杀 → 回大厅，输出通过/失败表）。
/// </summary>
internal sealed partial class DebugTools : MonoBehaviour
{
    internal static DebugTools? Instance { get; private set; }

    internal bool SelfTestRunning { get; private set; }

    internal string SelfTestStatus { get; private set; } = "";

    private bool weSetInvincible;

    /// <summary>万神殿自检临时加的休息点（殿的 id、第几场之后）。</summary>
    private (string Id, int After)? extraRest;

    /// <summary>万神殿自检跑着、打的是那个殿时：这一场打完要不要临时去休息点。自检停了就不算（不改共用的表）。</summary>
    internal bool ExtraRest(PantheonRun run) =>
        SelfTestRunning && extraRest is (string id, int after) && id == run.Def.Id && after == run.Index + 1 && after < run.Def.Bosses.Count;
    private CheatManager.InvincibilityStates invincibleBefore = CheatManager.InvincibilityStates.Off;

    /// <summary>
    /// 无敌开着（开战时据此直接记成调试辅助）：我们打开的，或者别的东西（游戏的作弊管理器）早就开着的都算
    /// （Codex 审查 0.8.0：原来只看我们开的，事先开着无敌就能记完成和无伤）。
    /// </summary>
    internal bool InvincibleActive => weSetInvincible || CheatManager.Invincibility != CheatManager.InvincibilityStates.Off;

    /// <summary>自检要大黄蜂真的挨打、死一次时打开（前作巡视的“战斗中死亡”）：这期间不开调试无敌。</summary>
    internal static bool AllowHeroDamage { get; set; }

    /// <summary>自动读档这一步结束了（读了，或者被拒绝了）。</summary>
    private bool autoLoadDone;
    /// <summary>发起了自动读档、还没确认读进来的槽（0 = 没有）。</summary>
    private int autoLoadPending;
    private float autoLoadAt;
    /// <summary>确认读进来了的自动读档槽（0 = 没有）。自动自检只在当前就是这个槽时开始（Codex 审查 #24、复审）。</summary>
    private int autoLoadedSlot;
    /// <summary>正在问游戏自动读档的槽里有没有存档（回调还没回来）。</summary>
    private bool autoLoadChecking;
    private float titleSince = -1f;
    private bool autoSelfTestDone;
    private float playingSince = -1f;

    /// <summary>
    /// 自检协程的心跳，按帧数算（Codex 复审：按时间算的话，游戏切到后台再回来会误判）。
    /// 协程每帧都会醒来一次并心跳；连续 600 帧（60 帧/秒时约 10 秒）没有心跳，就当协程已经停了：
    /// 停掉它、清掉 SelfTestRunning（无敌随之关闭），换一个编号，旧协程就算醒来也不再动手（Codex 审查 #11）。
    /// </summary>
    private int selfTestBeatFrame;
    private const int BeatTimeoutFrames = 600;
    private Coroutine? selfTestRoutine;
    /// <summary>自检的编号。每开一轮、每次中止都加一；协程退出游戏前核对，不是自己这一轮就不动手。</summary>
    private int selfTestRun;

    private void Beat() => selfTestBeatFrame = Time.frameCount;

    private void Awake() => Instance = this;

    /// <summary>插件组件被禁用或销毁：停掉自检，把我们打开的无敌恢复成原来的值（Codex 审查 #11、复审）。</summary>
    private void OnDisable() => ReleaseAll("disabled / 禁用");

    private void OnDestroy() => ReleaseAll("destroyed / 销毁");

    private void ReleaseAll(string why)
    {
        if (SelfTestRunning)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 调试组件{why}，停止自检 / debug tools {why}, stopping the self-test");
        }
        StopSelfTest();
        StopPromo(why);
        // 巡视截图途中被停：把改画进临时图的镜头还原（Codex 审查 v0.7.0 #6）。
        Shots.RestoreCameras();
        if (weSetInvincible)
        {
            CheatManager.Invincibility = invincibleBefore;
            weSetInvincible = false;
        }
    }

    /// <summary>停掉正在跑的自检（如果有），换编号。</summary>
    private void StopSelfTest()
    {
        if (selfTestRoutine != null)
        {
            StopCoroutine(selfTestRoutine);
            selfTestRoutine = null;
        }
        selfTestRun++;
        SelfTestRunning = false;
        Hk1Battle.SetShotDir(null);
    }

    private static bool DebugOn => GodhomeConfig.DebugMode.Value;

    /// <summary>自检的特权（没解锁的万神殿也能开）：自检在跑，而且调试开着、存档槽允许（Codex 审查）。</summary>
    internal bool SelfTestPrivileged => SelfTestRunning && Allowed();

    /// <summary>自检每一步都要满足：调试开着、当前槽在 Safety.EnabledSlots 里。中途有一项不满足就中止。</summary>
    private static bool Allowed() => DebugOn && SlotOk;

    /// <summary>调试开着、当前存档槽允许调试（给别的调试工具用，如进场检查 ArrivalWatch）。</summary>
    internal static bool DebugAllowed => Allowed();

    private static bool SlotOk
    {
        get
        {
            GameManager gm = GameManager.UnsafeInstance;
            return gm != null && GodhomeConfig.IsSlotEnabled(gm.profileID);
        }
    }

    private string lastFader = "";

    private void Update()
    {
        ModeController? mc = ModeController.Instance;
        if (mc == null)
        {
            return;
        }
        if (SelfTestRunning || Hk1Tour.Running)
        {
            // 调试：整屏淡入淡出（Screen Fader）的状态每变一次记一行（查万神殿打完回门前一直黑）。
            PlayMakerFSM? sf = GameManager.instance != null ? GameManager.instance.screenFader_fsm : null;
            string now = sf != null ? sf.ActiveStateName : "-";
            if (now != lastFader)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome]   淡入淡出 / screen fader {lastFader} → {now}（{Time.unscaledTime:0.00}s，{GameManager.instance?.GetSceneNameString()}，state={mc.State}，waitingForScene={GameManager.IsWaitingForSceneReady}）");
                lastFader = now;
            }
        }
        if (SelfTestRunning && Time.frameCount - selfTestBeatFrame > BeatTimeoutFrames)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 自检协程 {BeatTimeoutFrames} 帧没有心跳，停止自检 / self-test coroutine stopped beating, stopping it");
            StopSelfTest();
            SelfTestStatus = "中止 / aborted (no heartbeat)";
        }
        // 调试关掉了、换到不允许的存档槽：自检马上停（它等着的时候也算），开锁之类的特权跟着收回（Codex 审查）。
        if (SelfTestRunning && !Allowed())
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 调试关了或存档槽不允许，停止自检 / debug off or slot not allowed, stopping the self-test");
            StopSelfTest();
            SelfTestStatus = "中止 / aborted (debug off or slot)";
        }
        // 宣传片：调试关了、槽不允许了、换了档就停（调试关着时也要跑这一步，Codex 审计 0.10.3 D03）。看门狗也在这里跑。
        PromoGuard();
        PromoWatchdog();
        UpdateInvincible(mc);
        if (!DebugOn)
        {
            return;
        }
        // 秒杀键也受存档槽限制（Codex 审查 #8）。
        if (GodhomeConfig.DebugKillKey.Value.IsDown() && mc.State == LoomState.Fight && Allowed())
        {
            mc.MarkAssisted("kill key / 秒杀键");
            int hits = KillBoss(mc, verbose: true, out _);
            int steps = 0;
            if (mc.CurrentBoss is BossDef cb)
            {
                List<string> scenes = ModeController.ScenesOf(cb);
                steps = cb.DebugKill.Count(s => SceneTools.RunStep(s, scenes));
            }
            // 打了几个目标、执行了几个表里的步骤分开报（Codex 复审）。
            mc.ShowBanner(L.F("DEBUG_KILLED", hits, steps), 2f);
        }
        if (GodhomeConfig.DebugSelfTestKey.Value.IsDown() && !SelfTestRunning)
        {
            StartSelfTest();
        }
        TickAutoLoad();
        // 宣传片（Debug.Promo，默认关）。
        if (GameManager.UnsafeInstance != null)
        {
            TickPromo(GameManager.UnsafeInstance);
        }
    }

    /// <summary>
    /// 调试无敌：只在调试开着、存档槽允许、人在圣所里时打开（Codex 审查 #8）。
    /// 关的时候恢复成打开前的值（不一律关成 Off，别的工具开的无敌不动）。无敌下打的这场不记战绩。
    /// </summary>
    private void UpdateInvincible(ModeController mc)
    {
        bool want = Allowed() && mc.InMode && (GodhomeConfig.DebugInvincible.Value || SelfTestRunning) && !AllowHeroDamage;
        if (want && CheatManager.Invincibility != CheatManager.InvincibilityStates.FullInvincible)
        {
            // 被别的东西改掉了也重新打开；只有第一次记下原来的值。
            if (!weSetInvincible)
            {
                invincibleBefore = CheatManager.Invincibility;
                GodhomePlugin.Log.LogInfo($"[Godhome] 调试无敌打开 / debug invincibility on (was {invincibleBefore})");
            }
            CheatManager.Invincibility = CheatManager.InvincibilityStates.FullInvincible;
            weSetInvincible = true;
        }
        else if (!want && weSetInvincible)
        {
            CheatManager.Invincibility = invincibleBefore;
            weSetInvincible = false;
            GodhomePlugin.Log.LogInfo($"[Godhome] 调试无敌关闭 / debug invincibility off (restored {invincibleBefore})");
        }
        if (InvincibleActive && mc.State is LoomState.ToFight or LoomState.Fight)
        {
            mc.MarkAssisted("invincible / 调试无敌");
        }
    }

    /// <summary>
    /// 对当前 Boss 的每个血量组件打一下“剩余血量”那么多的伤害。返回打了几个；lethal = 这一下有没有把谁打空。
    /// 用游戏自己的受击入口 HealthManager.Hit（以大黄蜂为攻击来源），和真实攻击走同一条路：
    /// 会发 TOOK DAMAGE / HIT（多阶段 Boss 靠它换阶段、判死亡，例如失心蕾丝的 Death Control、螺蝇的 Death Checker），
    /// 无敌时被挡下、发 BLOCKED HIT（例如幽影最后一段），伤害转给共享血量（sendDamageTo）也照常处理。
    /// 不用 ApplyExtraDamage：它不发这些事件，还会无视无敌，反复把 Boss 打回“倒地”状态。
    /// </summary>
    internal static int KillBoss(ModeController mc, bool verbose, out bool lethal) => KillBoss(mc, verbose, out lethal, out _);

    /// <param name="dealt">这一轮有没有哪一击真的让血量变了。</param>
    /// <param name="fraction">每击最多打掉见过的最高血量的几分之几（1 = 一下打空，秒杀键用；自检用 0.25）。</param>
    /// <param name="onlyOnScreen">只打出现在画面里的本体（自检用）。</param>
    internal static int KillBoss(ModeController mc, bool verbose, out bool lethal, out bool dealt, float fraction = 1f, bool onlyOnScreen = false)
    {
        mc.ScanBoss();
        int n = 0;
        int placeholder = 0;
        lethal = false;
        dealt = false;
        var hitTargets = new HashSet<HealthManager>();
        foreach (HealthManager hm in mc.BossHealth)
        {
            if (hm == null || !hm.gameObject.activeInHierarchy || hm.isDead || (onlyOnScreen && !OnScreen(hm.transform.position)))
            {
                continue;
            }
            HealthManager target = Traverse.Create(hm).Field("sendDamageTo").GetValue<HealthManager>() ?? hm;
            // 共享血量：同一轮只打一次，不然第二下会落在 Boss 刚重设的血量上（舞者倒地时把血设成 99999）。
            if (!hitTargets.Add(target))
            {
                continue;
            }
            // 换阶段 / 倒地时游戏把血量设成 99999 当“暂时打不死”的标记，这时打它只会让倒地重来。
            if (target.hp >= PlaceholderHp)
            {
                placeholder++;
                continue;
            }
            int before = target.hp;
            int peak = peakHp.TryGetValue(target, out int p) ? Math.Max(p, before) : before;
            peakHp[target] = peak;
            int damage = fraction >= 1f ? Math.Max(before, 1) : Math.Min(Math.Max(before, 1), Math.Max(1, (int)Math.Ceiling(peak * fraction)));
            // 打进去以后血量 ≤0，或者反而变多（同一帧被重设成新阶段 / 倒地的血量，例如舞者的 99999），都算打空了。
            // 血量没变不算打进去：Hit 只要进了 TakeDamage 就返回 DamageEnemy，但 TakeDamage 里 IsImmuneTo 会直接返回
            // （颅骨暴君玩家不在上方时就是这样，0.2.6 全量里每一击都“打空”，血一直是 450）。
            bool hit = HitFor(hm, damage);
            dealt |= hit && target.hp != before;
            lethal |= hit && (target.hp <= 0 || target.hp > before);
            n++;
        }
        if (verbose || n > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 调试秒杀 / debug kill: {n} 个血量组件 (共找到 {mc.BossHealth.Count}{(placeholder > 0 ? $"，{placeholder} 个在换阶段 / in transition" : "")}){(lethal ? "，打空 / emptied" : dealt ? "" : "，没扣血 / no damage")}");
        }
        return n;
    }

    /// <summary>血量不低于这个数就当作游戏设的“暂时打不死”标记（Boss 正常血量都在几百到一两千）。</summary>
    private const int PlaceholderHp = 9999;

    /// <summary>每个血量组件见过的最高血量（换阶段时游戏会重设血量），按比例出伤害用。每个 Boss 开打前清空。</summary>
    private static readonly Dictionary<HealthManager, int> peakHp = new();

    private static void ResetPeaks() => peakHp.Clear();


    /// <summary>
    /// 观察一场战斗（Debug.SelfTestWatch）：不打，每半秒记下场景里所有激活、没死的大体型敌人（血量 ≥ 100）的位置、
    /// 在主镜头画面里的位置、在不在画面里，还有大黄蜂和镜头的位置；每 3 秒截一张图。
    /// 用来看 Boss 会不会跑到画面外、场景里是不是同时出了两个 Boss。
    /// </summary>
    private IEnumerator WatchFight(ModeController mc, string shot, Row row, float seconds)
    {
        float end = Time.unscaledTime + seconds;
        float nextShot = Time.unscaledTime + 1f;
        float nextLog = Time.unscaledTime;
        int samples = 0, off = 0, maxActive = 0, k = 0;
        var seen = new HashSet<string>();
        GodhomePlugin.Log.LogInfo($"[Godhome]   观察开始 / watch started ({seconds:0} s)");
        while (mc.State == LoomState.Fight && Time.unscaledTime < end && Allowed())
        {
            Beat();
            if (Time.unscaledTime >= nextLog)
            {
                nextLog = Time.unscaledTime + 0.5f;
                Camera? cam = GameCameras.SilentInstance != null ? GameCameras.SilentInstance.mainCamera : null;
                HeroController? hero = HeroController.SilentInstance;
                var parts = new List<string>();
                int active = 0;
                foreach (HealthManager h in UnityEngine.Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None))
                {
                    if (h == null || !h.gameObject.activeInHierarchy || h.isDead || h.hp < 100)
                    {
                        continue;
                    }
                    active++;
                    string path = SceneTools.PathOf(h.transform);
                    seen.Add(path);
                    Vector3 p = h.transform.position;
                    bool on = OnScreen(p);
                    samples++;
                    if (!on)
                    {
                        off++;
                    }
                    Vector3 v = cam != null ? cam.WorldToViewportPoint(p) : Vector3.zero;
                    parts.Add($"{path} hp={h.hp} ({p.x:0.0},{p.y:0.0}) vp=({v.x:0.00},{v.y:0.00}) {(on ? "on" : "OFF")}{(h.IsInvincible ? " inv" : "")}");
                }
                maxActive = Mathf.Max(maxActive, active);
                string hp = hero != null ? $"({hero.transform.position.x:0.0},{hero.transform.position.y:0.0})" : "-";
                string cp = cam != null ? $"({cam.transform.position.x:0.0},{cam.transform.position.y:0.0})" : "-";
                GodhomePlugin.Log.LogInfo($"[Godhome]   观察 / watch t={seconds - (end - Time.unscaledTime):0.0} hero={hp} cam={cp}: {(parts.Count == 0 ? "（没有激活的大体型敌人 / none active）" : string.Join(" | ", parts))}");
            }
            if (Time.unscaledTime >= nextShot)
            {
                nextShot = Time.unscaledTime + 3f;
                yield return Shots.Capture($"{shot}-w{++k:00}.jpg", row.Shots);
                continue;
            }
            yield return null;
        }
        string note = $"观察 / watch {seconds:0}s: 同时激活最多 / max active {maxActive}（{string.Join(", ", seen)}），画面外 / off-screen {off}/{samples}";
        row.Note += (row.Note.Length > 0 ? "; " : "") + note;
        GodhomePlugin.Log.LogInfo($"[Godhome]   {note}");
    }

    /// <summary>
    /// 追踪（Debug.SelfTestTrace）：Boss 物体的激活状态、它身上每个 FSM 的当前状态，一变就记一行（带血量、位置）。
    /// 每 0.25 秒看一次，很短的状态可能看不到；够用来找 Boss 卡在哪一步、是不是被重置了。
    /// </summary>
    private static void TraceBoss(BossDef b, ICollection<string> scenes, Dictionary<string, string> last)
    {
        foreach (GameObject go in SceneTools.FindNamed(b.BossObjects, scenes))
        {
            if (go == null)
            {
                continue;
            }
            string path = SceneTools.PathOf(go.transform);
            HealthManager? hm = go.GetComponent<HealthManager>();
            string where = $"hp={(hm != null ? hm.hp : -1)}, pos=({go.transform.position.x:0.0},{go.transform.position.y:0.0})";
            string act = go.activeInHierarchy ? "active" : "inactive";
            if (!last.TryGetValue(path + "#", out string? pa) || pa != act)
            {
                last[path + "#"] = act;
                GodhomePlugin.Log.LogInfo($"[Godhome]   追踪 / trace {path}: {pa ?? "-"} → {act} ({where})");
            }
            foreach (PlayMakerFSM fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (fsm == null)
                {
                    continue;
                }
                string key = path + "::" + fsm.FsmName;
                string st = fsm.enabled ? (fsm.ActiveStateName ?? "-") : "(disabled)";
                if (!last.TryGetValue(key, out string? prev) || prev != st)
                {
                    last[key] = st;
                    GodhomePlugin.Log.LogInfo($"[Godhome]   追踪 / trace {key}: {prev ?? "-"} → {st} ({where})");
                }
            }
        }
    }

    /// <summary>自检报告的“入场”一栏：从哪个门进来、站在哪、场地加载时改了什么。没建门、不是从入场门进来的写清楚。</summary>
    private static string ArenaEntry() =>
        Arena.Door == null ? $"没有入场门 / no door{(Arena.Error.Length > 0 ? ": " + Arena.Error : "")}"
        : Arena.EnteredVia != Arena.EnterGate ? $"没从入场门进来 / came in via '{Arena.EnteredVia}'；{Arena.Summary}"
        : Arena.Summary;

    /// <summary>这个位置在不在主镜头的画面里（四周留一点余量）。</summary>
    internal static bool OnScreen(Vector3 pos)
    {
        GameCameras cams = GameCameras.SilentInstance;
        Camera? cam = cams != null ? cams.mainCamera : null;
        if (cam == null)
        {
            return false;
        }
        Vector3 v = cam.WorldToViewportPoint(pos);
        return v.z > 0f && v.x > -0.05f && v.x < 1.05f && v.y > -0.05f && v.y < 1.05f;
    }

    /// <summary>
    /// 以大黄蜂为来源打一下 damage 点伤害。返回这一击是否真的扣了血（HealthManager.Hit 的结果是 DamageEnemy）。
    /// 伤害等于剩余血量，所以扣了血就是打空了；不能事后看 hp，有的 Boss 在同一帧就把血量重设了。
    /// </summary>
    private static bool HitFor(HealthManager hm, int damage)
    {
        GameObject? hero = HeroController.SilentInstance != null ? HeroController.SilentInstance.gameObject : null;
        if (hero == null)
        {
            // 没有大黄蜂（转场边上）：不打，下一轮再试。不用 ApplyExtraDamage 兜底（Codex 审查 0.9.0 第二轮 #12）。
            return false;
        }
        IHitResponder.Response r = hm.Hit(new HitInstance
        {
            Source = hero,
            AttackType = AttackTypes.Nail,
            DamageDealt = damage,
            DamageScalingLevel = 1,
            Multiplier = 1f,
            MagnitudeMultiplier = 1f,
            Direction = hero.transform.position.x <= hm.transform.position.x ? 0f : 180f,
            HitEffectsType = EnemyHitEffectsProfile.EffectsTypes.Full,
            IsHeroDamage = true,
        });
        return r == IHitResponder.Response.DamageEnemy;
    }

    /// <summary>
    /// 自检用（表里 debug.killAdds）：打掉战斗场景里不是 Boss 的敌人（killAdds 写成路径数组时只打那些路径下的）。
    /// 有的场景要先清完小怪才放 Boss 进下一阶段（失缚怨魂的尸壳死时发 HUSK KILLED，场景数剩几个）。
    /// 不按 Boss 物体推断范围：尸壳出场时会 SetParent(null) 离开 Husks 文件夹，变成场景根物体。
    /// </summary>
    private static int KillAdds(ModeController mc, ICollection<string> scenes, ICollection<string> roots)
    {
        var boss = new HashSet<HealthManager>(mc.BossHealth.Where(h => h != null));
        int n = 0;
        foreach (HealthManager hm in FindObjectsByType<HealthManager>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (hm == null || hm.isDead || boss.Contains(hm) || hm.hp <= 0 || hm.hp >= PlaceholderHp
                || !scenes.Contains(hm.gameObject.scene.name))
            {
                continue;
            }
            string path = SceneTools.PathOf(hm.transform);
            if (roots.Count > 0 && !roots.Any(r => path == r || path.StartsWith(r + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            // 打进去而且血量真的变了才算（免疫时 Hit 也返回 DamageEnemy，血却不掉，Codex 复审）。
            int before = hm.hp;
            if (HitFor(hm, before) && (hm.isDead || hm.hp != before))
            {
                n++;
                GodhomePlugin.Log.LogInfo($"[Godhome]   调试清小怪 / debug killed add: {path}");
            }
        }
        return n;
    }

    /// <summary>
    /// 自检用（表里 debug.react）：FSM 正停在 when 状态（且已经停了 after 秒）就执行这一步，
    /// 例如幽影最后一段停在 Parry Ready 时替玩家招架，崇高圣母灵丝飘向玩家却够不着时结束飘移。
    /// 每帧查一次（这类窗口很短）；同一步 1 秒内只执行一次。返回执行成功的步数（写了 counts 的只数它认的那些）。
    /// </summary>
    private static int RunReacts(BossDef b, ICollection<string> scenes, Dictionary<FsmStep, float> lastRun, Dictionary<FsmStep, float> seenSince, Func<FsmStep, bool>? counts = null)
    {
        int n = 0;
        foreach (FsmStep s in b.DebugReact)
        {
            PlayMakerFSM? fsm = SceneTools.FindFsm(s.Path, s.Fsm, scenes);
            if (fsm == null || !fsm.gameObject.activeInHierarchy || !SceneTools.InState(fsm, s.When))
            {
                seenSince.Remove(s);
                continue;
            }
            if (!seenSince.TryGetValue(s, out float since))
            {
                seenSince[s] = since = Time.unscaledTime;
            }
            if (Time.unscaledTime - since < s.After
                || (lastRun.TryGetValue(s, out float t) && Time.unscaledTime - t < 1f))
            {
                continue;
            }
            lastRun[s] = Time.unscaledTime;
            seenSince.Remove(s);
            GodhomePlugin.Log.LogInfo($"[Godhome]   条件反应 / react: '{fsm.ActiveStateName}' →");
            if (SceneTools.RunStep(s, scenes) && (counts == null || counts(s)))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>挑战位的“挑战”：表里 debug.react 的 INTERACT 事件步骤（像玩家在挑战区按交互）。</summary>
    private static bool IsChallengeStep(FsmStep s) => s.Type == "event" && s.Event == "INTERACT";

    /// <summary>
    /// 自检用（表里 debug.holdKill）：有 FSM 停在 when 状态时返回它，这时不打 Boss。
    /// 我们的伤害不需要碰撞体，Boss 还在休眠或在等场景（玩家这时根本打不到它）也会挨打：
    /// 失缚怨魂休眠时挨打会被打晕，提前通知场景“进第二阶段”，场景和 Boss 就对不上了。
    /// </summary>
    private static string? HoldKill(BossDef b, ICollection<string> scenes)
    {
        foreach (FsmStep s in b.DebugHoldKill)
        {
            PlayMakerFSM? fsm = SceneTools.FindFsm(s.Path, s.Fsm, scenes);
            if (fsm != null && fsm.gameObject.activeInHierarchy && SceneTools.InState(fsm, s.When))
            {
                return $"{s.Path}::{s.Fsm} [{fsm.ActiveStateName}]";
            }
        }
        return null;
    }

    private static bool BossAlive(ModeController mc)
    {
        mc.ScanBoss();
        return mc.BossHealth.Any(h => h != null && h.gameObject.activeInHierarchy && !h.isDead);
    }

    /// <summary>Boss 在场：有活着的血量组件；或者它本来就没有血量组件（纯 FSM 的 Boss），物体激活即可。</summary>
    private static bool BossPresent(ModeController mc, BossDef b, ICollection<string> scenes)
    {
        if (BossAlive(mc))
        {
            return true;
        }
        return mc.BossHealth.Count == 0 && SceneTools.FindNamed(b.BossObjects, scenes).Any(g => g.activeInHierarchy);
    }

    /// <summary>找不到 Boss 时把已加载场景里所有血量组件写进日志，用来修 bosses.json 的 bossObjects。</summary>
    private static void LogSceneHealth()
    {
        var parts = new List<string>();
        foreach (HealthManager hm in FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (hm == null || !hm.gameObject.scene.IsValid())
            {
                continue;
            }
            parts.Add($"{hm.gameObject.scene.name}/{hm.gameObject.name}(hp={hm.hp},{(hm.gameObject.activeInHierarchy ? "on" : "off")}{(hm.isDead ? ",dead" : "")})");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 场景血量组件 / scene health managers ({parts.Count}): {string.Join("; ", parts.Take(40))}");
    }

    /// <summary>
    /// 自检判不出胜利时记下现场，用来查卡在哪：Boss 物体在不在、哪一层没激活，血量组件的状态，
    /// 以及范围内每个 FSM 停在哪个状态、这个状态收不收 ZERO HP（特殊死亡的 Boss 只靠它，见 HealthManager.Die）。
    /// </summary>
    private static void LogDiagnostics(ModeController mc, BossDef b, ICollection<string> scenes)
    {
        var log = GodhomePlugin.Log;
        log.LogInfo($"[Godhome] 诊断 / diagnostics {b.Id}（场地 / arena: {Arena.Summary}；开战 / wake: {(Arena.Started ? Arena.StartNote : "未开战 / not started")}）");
        // 范围：Boss 物体所在的根物体，加上表里开战判定、结束状态所在的根物体。
        var scope = new HashSet<string>();
        void Scope(string? path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                scope.Add(path!.Split('/')[0]);
            }
        }
        Scope(b.Arena.Wake?.Path);
        Scope(b.Arena.Wake?.Battle);
        foreach (FsmRef e in b.Arena.End)
        {
            Scope(e.Path);
        }
        mc.ScanBoss();
        List<GameObject> objs = SceneTools.FindNamed(b.BossObjects, scenes);
        foreach (GameObject go in objs.Take(8))
        {
            log.LogInfo($"[Godhome]   物体 / object {SceneTools.Describe(go)}");
            Scope(SceneTools.PathOf(go.transform));
        }
        if (objs.Count == 0)
        {
            log.LogInfo($"[Godhome]   没找到 Boss 物体 / no boss object named {string.Join(", ", b.BossObjects)}");
        }
        // 位置和在不在画面里：自检只打画面里的本体，打不到时要知道它跑哪去了（0.9.0 全量：沙克拉挨了一下后 70 秒不在画面里）。
        HeroController? hc = HeroController.SilentInstance;
        if (hc != null)
        {
            Vector3 hp0 = hc.transform.position;
            log.LogInfo($"[Godhome]   人 / hero pos=({hp0.x:0.0}, {hp0.y:0.0})");
        }
        foreach (HealthManager hm in mc.BossHealth.Where(h => h != null).Take(8))
        {
            Vector3 p = hm.transform.position;
            log.LogInfo($"[Godhome]   血量 / hm {SceneTools.PathOf(hm.transform)} hp={hm.hp} dead={hm.isDead} invincible={hm.IsInvincible} specialDeath={hm.hasSpecialDeath} active={hm.gameObject.activeInHierarchy} pos=({p.x:0.0}, {p.y:0.0}) onScreen={OnScreen(p)}");
        }
        var lines = new List<(bool onBoss, string text)>();
        foreach (PlayMakerFSM fsm in FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (fsm == null || !scenes.Contains(fsm.gameObject.scene.name))
            {
                continue;
            }
            string path = SceneTools.PathOf(fsm.transform);
            bool onBoss = b.BossObjects.Contains(fsm.gameObject.name.Replace("(Clone)", "").Trim());
            if (!onBoss && !scope.Any(r => path == r || path.StartsWith(r + "/", StringComparison.Ordinal)))
            {
                continue;
            }
            HutongGames.PlayMaker.FsmState? st = fsm.Fsm?.ActiveState;
            bool zero = (st?.Transitions?.Any(t => t.EventName == "ZERO HP") ?? false)
                || (fsm.FsmGlobalTransitions?.Any(t => t.EventName == "ZERO HP") ?? false);
            lines.Add((onBoss, $"{path}::{fsm.FsmName} [{st?.Name ?? "-"}]{(fsm.enabled ? "" : " (disabled)")}{(zero ? " ← ZERO HP ok" : "")}"));
        }
        foreach (var l in lines.OrderByDescending(x => x.onBoss).Take(60))
        {
            log.LogInfo($"[Godhome]   FSM {(l.onBoss ? "*" : " ")} {l.text}");
        }
        if (lines.Count > 60)
        {
            log.LogInfo($"[Godhome]   …还有 / more: {lines.Count - 60}");
        }
    }

    // ───────────────────────────── 自动读档 ─────────────────────────────

    private void TickAutoLoad()
    {
        int slot = GodhomeConfig.DebugAutoLoadSlot.Value;
        GameManager gm = GameManager.UnsafeInstance;
        if (gm == null)
        {
            return;
        }
        if (slot > 0 && !autoLoadDone && gm.IsMenuScene() && UIManager.instance != null)
        {
            if (!GodhomeConfig.IsSlotEnabled(slot))
            {
                autoLoadDone = true;
                GodhomePlugin.Log.LogWarning($"[Godhome] 自动读档被拒绝：槽 {slot} 不在 Safety.EnabledSlots / auto-load refused for slot {slot}");
                return;
            }
            if (titleSince < 0)
            {
                titleSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - titleSince > 8f && !autoLoadChecking)
            {
                // 先问游戏这个槽有没有存档：有就读；没有、而且开了 AutoNewGodhome，就在这个空槽新建神居存档。
                // 绝不覆盖已有的存档。
                autoLoadChecking = true;
                gm.HasSaveFile(slot, exists =>
                {
                    autoLoadDone = true;
                    if (!GodhomeConfig.DebugMode.Value || !GodhomeConfig.IsSlotEnabled(slot) || !gm.IsMenuScene() || UIManager.instance == null)
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 自动读档取消：调试或存档槽设置变了 / auto-load cancelled for slot {slot}");
                        return;
                    }
                    if (exists)
                    {
                        autoLoadPending = slot;
                        autoLoadAt = Time.unscaledTime;
                        GodhomePlugin.Log.LogInfo($"[Godhome] 自动读档 / auto-loading slot {slot}");
                        gm.LoadGameFromUI(slot);
                    }
                    else if (GodhomeConfig.DebugAutoNewGodhome.Value)
                    {
                        autoLoadPending = slot;
                        autoLoadAt = Time.unscaledTime;
                        GodhomePlugin.Log.LogInfo($"[Godhome] 槽 {slot} 是空的，自动新建神居存档 / slot {slot} is empty, creating a new godhome save");
                        gm.profileID = slot;
                        UIManager.instance.StartNewGame(permaDeath: false, bossRush: true);
                    }
                    else
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 槽 {slot} 是空的，不自动读档（要自动新建神居存档请开 Debug.AutoNewGodhome）/ slot {slot} is empty, nothing to load");
                    }
                });
            }
        }
        bool playing = !gm.IsMenuScene() && HeroController.SilentInstance != null && gm.GameState == GlobalEnums.GameState.PLAYING;
        // 自动读档要看到真的读进来了才算（Codex 复审）：120 秒内进入游戏、而且当前槽就是发起的那个槽。
        // 超时、读进来的不是这个槽，就作废；回到标题画面时也作废，之后手动读档不会自动开始自检。
        if (autoLoadPending > 0)
        {
            if (playing && gm.profileID == autoLoadPending)
            {
                autoLoadedSlot = autoLoadPending;
                autoLoadPending = 0;
                GodhomePlugin.Log.LogInfo($"[Godhome] 自动读档完成 / auto-load finished: slot {autoLoadedSlot}");
            }
            else if (Time.unscaledTime - autoLoadAt > 120f || (playing && gm.profileID != autoLoadPending))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 自动读档没有成功，作废 / auto-load of slot {autoLoadPending} did not complete");
                autoLoadPending = 0;
            }
        }
        if (gm.IsMenuScene() && autoLoadedSlot > 0 && !SelfTestRunning)
        {
            autoLoadedSlot = 0;
        }
        if (!playing)
        {
            playingSince = -1f;
        }
        // 长廊巡视（第二版·3）：和自动自检一样只在自动读档的槽里、只跑一次。
        if (GodhomeConfig.DebugHallTour.Value && autoLoadedSlot > 0 && gm.profileID == autoLoadedSlot
            && !autoSelfTestDone && !SelfTestRunning && !HallTour.Running && playing && Allowed())
        {
            if (playingSince < 0)
            {
                playingSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - playingSince > 6f)
            {
                autoSelfTestDone = true;
                StartCoroutine(HallTour.Run(this));
            }
            return;
        }
        // 前作神居巡视：和长廊巡视一样只在自动读档的槽里、只跑一次。
        if (GodhomeConfig.DebugHk1Tour.Value && autoLoadedSlot > 0 && gm.profileID == autoLoadedSlot
            && !autoSelfTestDone && !SelfTestRunning && !Hk1Tour.Running && playing && Allowed())
        {
            if (playingSince < 0)
            {
                playingSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - playingSince > 6f)
            {
                autoSelfTestDone = true;
                StartCoroutine(Hk1Tour.Run(this));
            }
            return;
        }
        // 前作场景试验：和巡视一样只在自动读档的槽里、只跑一次；只在神居存档里跑（普通存档进前作场景，
        // 复活点保护、存档清洗都不管它，前作的复活触发器会把普通存档的复活点写成前作场景，Codex 审查）。
        if (!string.IsNullOrWhiteSpace(GodhomeConfig.DebugHk1Probe.Value) && autoLoadedSlot > 0 && gm.profileID == autoLoadedSlot
            && !autoSelfTestDone && !SelfTestRunning && !Hk1Probe.Running && playing && Allowed()
            && ModeController.Instance != null && ModeController.Instance.InMode)
        {
            if (playingSince < 0)
            {
                playingSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - playingSince > 6f)
            {
                autoSelfTestDone = true;
                if (Hk1Probe.Available)
                {
                    StartCoroutine(Hk1Probe.Run(this));
                }
                else
                {
                    GodhomePlugin.Log.LogWarning("[Godhome] 前作场景试验需要 UnitySceneRepacker 插件 / the hk1 probe needs the UnitySceneRepacker plugin");
                }
            }
            return;
        }
        // 自动自检：只在确认读进来的正是自动读档的那个槽时开始，而且只开一次。
        if (GodhomeConfig.DebugAutoSelfTest.Value && autoLoadedSlot > 0 && gm.profileID == autoLoadedSlot
            && !autoSelfTestDone && !SelfTestRunning && playing)
        {
            if (playingSince < 0)
            {
                playingSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - playingSince > 6f)
            {
                autoSelfTestDone = true;
                StartSelfTest();
            }
        }
    }

    // ───────────────────────────── 自检 ─────────────────────────────

    private sealed class Row
    {
        public BossDef Boss = null!;
        public bool Entered;

        /// <summary>报告的“场地”一栏：从哪个门进来、站在哪、场地加载时改了什么（见 ArenaEntry）。</summary>
        public string Entry = "";

        /// <summary>Boss 自己开战了（不替玩家触发）。</summary>
        public bool Start;
        public string StartHow = "";
        public bool Found;
        public bool Kill;

        /// <summary>倒下以后到回圣所，没有还活着的本体（不是提前判赢）。没看（没赢、胜利转场已经走了）是 null，报告写 N/A。</summary>
        public bool? Clean;
        public bool Return;

        /// <summary>回大厅后主角能正常落地、状态正常（HeroCheck.Fall）。没测（没回到大厅、万神殿）是 null。</summary>
        public bool? Fall;

        /// <summary>入场点专项（DebugTools.Entry.cs：落点、危险复活、死亡重试）。没测是 null。</summary>
        public bool? EntryCheck;

        /// <summary>万神殿自检在这一场放弃（测放弃流程）：开战、找到、击杀、干净都没测，报告写 N/A；只看进场和回到大厅。</summary>
        public bool Abandon;
        public string Scene = "";
        public float Seconds;
        public string Note = "";
        public List<string> Shots = new();
    }

    private readonly List<Row> rows = new();
    private string reportStamp = "";

    /// <summary>放进触发区以后，最多等这么多秒让 Boss 自己开战。</summary>
    private const float StartTimeout = 20f;

    /// <summary>万神殿自检在指定的一场放弃时的回大厅原因（最后按它核对连战是不是因为这次放弃作废的）。</summary>
    private const string AbandonReason = "self-test pantheon abandon / 自检放弃万神殿";

    /// <summary>有挑战位的 Boss：先等这么久，确认没挑战时不会自己开战。</summary>
    private const float ChallengeQuiet = 6f;
    /// <summary>这一轮计划测几个（报告里和实际测了几个一起写，中途中止时看得出差多少，Codex 复审）。</summary>
    private int planned;

    internal void StartSelfTest()
    {
        if (!Allowed())
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 自检被拒绝：当前存档槽不在 Safety.EnabledSlots / self-test refused on this slot");
            ModeController.Instance?.ShowNotice(L.Get("DEBUG_SLOT_REFUSED"), 4f);
            return;
        }
        string pan = GodhomeConfig.SelfTestPantheon.Value?.Trim() ?? "";
        if (pan.Length > 0)
        {
            PantheonDef? pd = PantheonCatalog.Find(pan);
            if (pd == null)
            {
                SelfTestStatus = "没有这个万神殿 / no such pantheon";
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检被拒绝：万神殿表里没有 / self-test refused, pantheon not found: \"{pan}\"");
                return;
            }
            StopSelfTest();
            int prun = ++selfTestRun;
            selfTestRoutine = StartCoroutine(PantheonSelfTest(pd, ParseBindings(GodhomeConfig.SelfTestBindings.Value), Mathf.Max(0, GodhomeConfig.SelfTestPantheonAbortAt.Value), prun));
            return;
        }
        string filter = GodhomeConfig.SelfTestBosses.Value?.Trim() ?? "";
        HashSet<string> only = new(filter.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries));
        List<BossDef> list = BossCatalog.All.Where(b => only.Count == 0 || only.Contains(b.Id)).ToList();
        // 一个都没选中（id 拼错、表是空的）就不开跑（Codex 复审：以前会跑 0 个然后报“完成”，还会退出游戏）。
        if (list.Count == 0)
        {
            SelfTestStatus = "没有要测的 Boss / nothing to test";
            GodhomePlugin.Log.LogWarning($"[Godhome] 自检被拒绝：没有符合 SelfTestBosses 的 Boss / self-test refused, no boss matches SelfTestBosses \"{filter}\"");
            return;
        }
        StopSelfTest();
        int run = ++selfTestRun;
        selfTestRoutine = StartCoroutine(SelfTest(list, run));
    }

    /// <summary>等条件成立，最多 timeout 秒。暂停菜单开着的时间不算（有人按了暂停，自检就跟着停）。</summary>
    /// <summary>
    /// 挑战位：像玩家一样在挑战区按交互（表里 debug.react 的 INTERACT），等到开战（开战判定）或 StartTimeout 秒。
    /// 开战前不打 Boss：挑战位的 Boss 等挑战时可能在背景里（卡梅莉塔的 BG Dance），玩家打不到，自检直接扣血会在开战前就把它打死。
    /// 按场次号看开战（fightNo），只认 INTERACT 步骤算挑战发出（Codex 审查 0.9.0 第二轮 #8：别的反应步骤不算）。
    /// 先后按 Arena.Seq 比：发挑战前记下当前号，离开等待状态的号不比它大就是挑战前就开战了（第三轮 #6：Watch 下一帧才记开战，
    /// 同一帧里先离开、后发挑战靠“已经记了开战”看不出来）。超时退出前再看一次（第三轮 #7）。
    /// result[0] = 挑战真正发出去的时刻（没发出去 -1，Codex 审查 v0.6.0 #15），result[1] = 发出后多久开战（没开战 -1），
    /// result[2] = 1 表示挑战发出前就已经开战了（不是挑战开的）。
    /// </summary>
    private IEnumerator SendChallenge(ModeController mc, BossDef b, List<string> scenes, int fightNo, float[] result)
    {
        result[0] = result[1] = -1f;
        result[2] = 0f;
        var chAt = new Dictionary<FsmStep, float>();
        var chSeen = new Dictionary<FsmStep, float>();
        float chUntil = Time.unscaledTime + StartTimeout;
        float nextLook = 0f;
        int sentAfter = -1;
        // 看这一场开没开战，开了就按先后号分“挑战前”“挑战后”。
        bool Judge()
        {
            if (!Arena.StartedAt(fightNo, out int at))
            {
                return false;
            }
            if (result[0] < 0f || at <= sentAfter)
            {
                result[2] = 1f;
                GodhomePlugin.Log.LogWarning($"[Godhome]   {b.Id}: 挑战发出前就开战了 / started before the challenge was sent");
            }
            else
            {
                result[1] = Time.unscaledTime - result[0];
            }
            return true;
        }
        while (mc.State == LoomState.Fight && Allowed() && Time.unscaledTime < chUntil)
        {
            Beat();
            if (PausedDelta() > 0f)
            {
                chUntil += Time.unscaledDeltaTime;
            }
            else if (result[0] < 0f)
            {
                // 还没发挑战：先看是不是已经开战了。
                if (Judge())
                {
                    yield break;
                }
                int before = Arena.Seq;
                if (RunReacts(b, scenes, chAt, chSeen, IsChallengeStep) > 0)
                {
                    result[0] = Time.unscaledTime;
                    sentAfter = before;
                    GodhomePlugin.Log.LogInfo($"[Godhome]   {b.Id}: 挑战已发出 / challenge sent");
                }
            }
            else
            {
                RunReacts(b, scenes, chAt, chSeen);
                // 开战判断四分之一秒看一次（Codex 审查 v0.6.0 #17）。
                if (Time.unscaledTime >= nextLook)
                {
                    nextLook = Time.unscaledTime + 0.25f;
                    if (Judge())
                    {
                        yield break;
                    }
                }
            }
            yield return null;
        }
        // 超时、离开战斗时最后看一次：最后一次查看之后才记下的开战也算（第三轮 #7）。
        Judge();
    }

    private IEnumerator WaitFor(Func<bool> cond, float timeout)
    {
        float end = Time.unscaledTime + timeout;
        while (true)
        {
            // 暂停中不放行（Codex 审查 v0.5.0 #11）：条件在暂停时成立也要等继续游戏，后面的步骤才不会在暂停中改数据。
            if (IsPaused())
            {
                Beat();
                yield return null;
                end += PausedDelta();
                continue;
            }
            if (cond() || Time.unscaledTime >= end)
            {
                break;
            }
            Beat();
            yield return null;
            end += PausedDelta();
        }
        Beat();
    }

    /// <summary>等待固定秒数，期间照常心跳。暂停的时间不算，暂停中也不结束。</summary>
    private IEnumerator Wait(float seconds)
    {
        float end = Time.unscaledTime + seconds;
        while (Time.unscaledTime < end || IsPaused())
        {
            Beat();
            yield return null;
            end += PausedDelta();
        }
        Beat();
    }

    /// <summary>温泉里前作场景控制器下面那些 FSM（梦境进场 Dream Entry、回程 Dream Return……）现在的状态。</summary>
    private static string SpaEntryState() => "温泉控制器 / spa controller FSMs: " + string.Join("，",
        UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(p => p != null && p.gameObject.scene.name == Hk1Lobby.Spa && FsmDump.PathOf(p.transform).StartsWith("Boss Scene Controller", StringComparison.Ordinal))
            .Select(p => $"{FsmDump.PathOf(p.transform)}|{p.FsmName}={p.ActiveStateName}{(p.enabled && p.gameObject.activeInHierarchy ? "" : "(off)")}"));

    /// <summary>现在看得见的交互提示上写的字（丝之歌的 PromptMarker 用的是 TMProOld）。</summary>
    private static string[] VisiblePrompts() => UnityEngine.Object.FindObjectsByType<PromptMarker>(FindObjectsSortMode.None)
        .Where(m => m != null && m.isActiveAndEnabled && m.IsVisible)
        .Select(m => m.GetComponentInChildren<TMProOld.TMP_Text>(true)?.text ?? "-").ToArray();

    /// <summary>
    /// 温泉里前作的长椅（Bench Control）照玩家坐一次：走到长椅前看提示，按上坐下，截图，按跳起身。
    /// 核对：大黄蜂有长椅要的动画；坐下了；起来后能动、能暂停、重力和刚体还原、没歪着；复活点没被改（Hk1Spa.Prepare 关了 Set Respawn）。
    /// </summary>
    private IEnumerator SpaBenchTest(string shot, List<string> shots)
    {
        HeroController hc = HeroController.instance;
        PlayerData pd = PlayerData.instance;
        PlayMakerFSM? bench = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(f => f.FsmName == "Bench Control" && f.gameObject.scene.name == Hk1Lobby.Spa);
        Check("温泉的长椅 / spa bench", bench != null && hc != null, bench != null ? $"{FsmDump.PathOf(bench.transform)}，{bench.ActiveStateName}" : "没找到 Bench Control / not found");
        if (bench == null || hc == null)
        {
            yield break;
        }
        string Respawn() => $"{pd.respawnScene}@{pd.respawnMarkerName} type {pd.respawnType}";
        string respawnBefore = Respawn();
        bool? setRespawn = bench.FsmVariables.FindFsmBool("Set Respawn")?.Value;
        tk2dSpriteAnimator? anim = hc.GetComponent<tk2dSpriteAnimator>();
        string[] clips = { "Sit", "Get Off", "Sit Fall Asleep", "Wake To Sit", "Idle" };
        string missing = string.Join(",", clips.Where(c => anim == null || anim.Library == null || anim.Library.GetClipByName(c) == null));
        Check("长椅要的动画大黄蜂都有 / hero has the bench clips", missing.Length == 0, missing.Length == 0 ? string.Join(",", clips) : "缺 / missing " + missing);

        // 走到长椅前（站到长椅正下方的地面上），等 FSM 进 In Range。
        Vector3 bp = bench.transform.position;
        RaycastHit2D hit = Physics2D.Raycast(new Vector2(bp.x, bp.y + 2f), Vector2.down, 25f, 1 << 8);
        Sanctum.StandAt(hc, bp.x, hit.collider != null ? hit.point.y : bp.y - 1f);
        yield return Wait(1.2f);
        yield return WaitFor(() => bench.ActiveStateName == "In Range", 4f);
        yield return Wait(0.6f);
        string key = Hk1ShowPrompt.PromptKey("Rest").ToUpperInvariant();
        string want = TeamCherry.Localization.Language.Has(key, "Prompts") ? TeamCherry.Localization.Language.Get(key, "Prompts") : "?";
        string[] shownAll = VisiblePrompts();
        Check("长椅的提示 / bench prompt", bench.ActiveStateName == "In Range" && shownAll.Length == 1 && shownAll[0] == want,
            $"显示 / shows \"{string.Join("|", shownAll)}\"，要 / want \"{want}\"，{bench.ActiveStateName}，人在 / hero at {hc.transform.position}，长椅 / bench {bp}");
        yield return Shots.Capture(shot + "-0-bench-prompt.jpg", shots);
        if (bench.ActiveStateName != "In Range")
        {
            yield break;
        }

        // 照玩家按上：FSM 里 ListenForUp 收到上键发的就是 UP PRESSED。
        bench.SendEvent("UP PRESSED");
        yield return WaitFor(() => bench.ActiveStateName == "Resting", 8f);
        yield return Wait(1f);
        Check("坐上长椅 / sat on the bench", bench.ActiveStateName == "Resting" && pd.atBench && !hc.acceptingInput && Mathf.Abs(hc.transform.position.x - bp.x) < 1f,
            $"{bench.ActiveStateName}，atBench {pd.atBench}，能动 / input {hc.acceptingInput}，动画 / clip {anim?.CurrentClip?.name ?? "-"}，人在 / hero at {hc.transform.position}，"
            + $"血 / hp {pd.health}/{pd.CurrentMaxHealth}，Set Respawn {(setRespawn.HasValue ? setRespawn.Value.ToString() : "-")}");
        yield return Shots.Capture(shot + "-0-bench-sit.jpg", shots);

        // 起身：前作 Resting 里跳、攻击、上、下都发 GET UP。
        bench.SendEvent("GET UP");
        yield return WaitFor(() => hc.acceptingInput && !pd.atBench && bench.ActiveStateName is "Idle" or "In Range", 6f);
        yield return Wait(0.6f);
        Rigidbody2D? rb = hc.Body;
        float tilt = Mathf.Abs(Mathf.DeltaAngle(hc.transform.eulerAngles.z, 0f));
        Check("从长椅起来 / got up", hc.acceptingInput && !pd.atBench && !pd.disablePause && rb != null && rb.bodyType == RigidbodyType2D.Dynamic && rb.gravityScale > 0f && tilt < 0.5f,
            $"{bench.ActiveStateName}，atBench {pd.atBench}，disablePause {pd.disablePause}，能动 / input {hc.acceptingInput}，"
            + $"刚体 / body {(rb != null ? rb.bodyType.ToString() : "-")}，重力 / gravity {(rb != null ? rb.gravityScale : -1f):0.##}，歪 / tilt {tilt:0.##}，动画 / clip {anim?.CurrentClip?.name ?? "-"}");
        yield return Shots.Capture(shot + "-0-bench-up.jpg", shots);
        string respawnAfter = Respawn();
        Check("长椅没改复活点 / the bench left the respawn alone", respawnAfter == respawnBefore, $"之前 / before {respawnBefore}，之后 / after {respawnAfter}");
    }

    private static bool IsPaused()
    {
        GameManager gm = GameManager.UnsafeInstance;
        return gm != null && gm.IsGamePaused();
    }

    /// <summary>游戏暂停中时返回这一帧的真实时长（用来把超时往后推），没暂停返回 0。</summary>
    private static float PausedDelta()
    {
        GameManager gm = GameManager.UnsafeInstance;
        return gm != null && gm.IsGamePaused() ? Time.unscaledDeltaTime : 0f;
    }

    /// <summary>
    /// 打一场（单个 Boss 自检和万神殿自检共用）：用普通大小的伤害打（每击最多四分之一血），让 Boss 走正常的换阶段和死亡流程；
    /// 条件反应每帧查，其余每 0.25 秒一轮。打到不在战斗状态、超时或调试被关掉为止。结果写进 row（Found、Note）。
    /// </summary>
    private IEnumerator Brawl(ModeController mc, BossDef b, List<string> scenes, Row row, float timeout)
    {
        var reactAt = new Dictionary<FsmStep, float>();
        var reactSeen = new Dictionary<FsmStep, float>();
        string? held = null;
        int reacts = 0, adds = 0, hits = 0;
        float until = Time.unscaledTime + (b.DebugTimeout > 0 ? b.DebugTimeout : timeout);
        float nextKill = Time.unscaledTime;
        float nextAdds = nextKill;
        float nextDebugKill = nextKill;
        float nextSlow = nextKill;
        bool everDealt = false;
        var trace = new Dictionary<string, string>();
        // 宣传片（Debug.Promo）：先让自动操作的大黄蜂真打一会儿，这段时间不用调试伤害。
        if (PromoRunning)
        {
            float bot = Mathf.Max(0f, GodhomeConfig.DebugPromoBotSeconds.Value);
            promoHoldUntil = Time.unscaledTime + bot;
            until += bot;
        }
        while (mc.State == LoomState.Fight && Time.unscaledTime < until && Allowed())
        {
            Beat();
            if (PausedDelta() > 0f)
            {
                // 暂停中：不打、不计时。
                until += Time.unscaledDeltaTime;
                yield return null;
                continue;
            }
            // 条件反应每帧查（招架这类窗口很短），其余每 0.25 秒做一轮。
            if (b.DebugReact.Count > 0)
            {
                reacts += RunReacts(b, scenes, reactAt, reactSeen);
            }
            if (Time.unscaledTime < nextSlow)
            {
                yield return null;
                continue;
            }
            nextSlow = Time.unscaledTime + 0.25f;
            if (GodhomeConfig.SelfTestTrace.Value)
            {
                TraceBoss(b, scenes, trace);
            }
            row.Found |= BossPresent(mc, b, scenes);
            string? hold = HoldKill(b, scenes);
            if (hold != held)
            {
                GodhomePlugin.Log.LogInfo(hold != null
                    ? $"[Godhome]   暂停攻击 / attacks held: {hold}"
                    : "[Godhome]   恢复攻击 / attacks resumed");
                held = hold;
            }
            bool promoHold = PromoRunning && Time.unscaledTime < promoHoldUntil;
            if (hold == null && !promoHold && Time.unscaledTime >= nextKill)
            {
                int targets = KillBoss(mc, verbose: false, out bool emptied, out bool dealt, fraction: 0.25f, onlyOnScreen: true);
                if (dealt)
                {
                    hits++;
                }
                everDealt |= dealt;
                // 打空一次后停 debug.killInterval 秒（默认 5）：让死亡 / 换阶段的演出走完；平时每秒一击。
                nextKill = Time.unscaledTime + (emptied ? b.DebugKillInterval : targets > 0 ? 1f : 0.5f);
            }
            if (b.DebugKillAdds && Time.unscaledTime >= nextAdds)
            {
                nextAdds = Time.unscaledTime + 2f;
                adds += KillAdds(mc, scenes, b.DebugAddRoots);
            }
            if (hold == null && !promoHold && b.DebugKill.Count > 0 && Time.unscaledTime >= nextDebugKill)
            {
                nextDebugKill = Time.unscaledTime + 5f;
                foreach (FsmStep s in b.DebugKill)
                {
                    SceneTools.RunStep(s, scenes);
                }
            }
            yield return null;
        }
        row.Note += (row.Note.Length > 0 ? "; " : "") + $"命中 / hits {hits}";
        if (reacts > 0)
        {
            row.Note += $"; 条件反应 / reacts {reacts}";
        }
        if (adds > 0)
        {
            row.Note += $"; 清小怪 / adds {adds}";
        }
        if (Arena.InteractablesOff > 0)
        {
            row.Note += $"; 清场 / cleared {Arena.InteractablesOff}";
        }
        if (!row.Found)
        {
            row.Note += "; " + (mc.BossHealth.Count == 0
                ? "场景里没找到 Boss 物体 / boss object not found"
                : $"Boss 物体未激活 / boss inactive ({mc.BossHealth.Count} found)");
            LogSceneHealth();
        }
        if (!everDealt && b.DebugKill.Count == 0 && mc.BossHealth.Count > 0)
        {
            row.Note += "; 一击都没打进去 / no hit landed";
        }
    }

    private IEnumerator SelfTest(List<BossDef> list, int myRun)
    {
        Beat();
        SelfTestRunning = true;
        rows.Clear();
        extra.Clear();
        resourceLines.Clear();
        resourceFail = false;
        ArrivalWatch.Reset();
        planned = list.Count;
        reportStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string shotDir = Path.Combine(Paths.BepInExRootPath, "Godhome", "shots", reportStamp);
        try
        {
            Directory.CreateDirectory(shotDir);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 建截图目录失败 / failed to create {shotDir}: {e.Message}");
        }
        ModeController mc = ModeController.Instance!;
        float timeout = Mathf.Max(10f, GodhomeConfig.SelfTestStepTimeout.Value);
        GodhomePlugin.Log.LogInfo($"[Godhome] 自检开始 / self-test started: {list.Count} 个 Boss");

        // 第二版只在神居存档里有圣所：读进神居存档后模式自己接管并进入大厅。
        if (!GodhomeSave.IsGodhome(GameManager.instance))
        {
            SelfTestStatus = "不是神居存档 / not a godhome save";
            GodhomePlugin.Log.LogError("[Godhome] 自检中止：当前不是神居存档 / aborted, not a godhome save");
            WriteReport(final: true, abort: SelfTestStatus);
            SelfTestRunning = false;
            selfTestRoutine = null;
            yield break;
        }
        yield return WaitFor(() => mc.State == LoomState.Lobby, 90f);
        if (mc.State != LoomState.Lobby)
        {
            SelfTestStatus = "无法进入大厅 / could not reach the lobby";
            GodhomePlugin.Log.LogError($"[Godhome] 自检中止：进不了大厅 / aborted, state={mc.State}");
            WriteReport(final: true, abort: SelfTestStatus);
            SelfTestRunning = false;
            selfTestRoutine = null;
            yield break;
        }

        string? abort = null;
        for (int i = 0; i < list.Count; i++)
        {
            if (!Allowed())
            {
                abort = "调试被关掉或存档槽不允许 / debug disabled or slot not allowed";
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检中止 / self-test aborted: {abort}");
                break;
            }
            BossDef b = list[i];
            var row = new Row { Boss = b };
            rows.Add(row);
            SelfTestStatus = $"{i + 1}/{list.Count} {b.Id}";
            float t0 = Time.unscaledTime;

            yield return WaitFor(() => mc.State == LoomState.Lobby, 90f);
            if (mc.State != LoomState.Lobby)
            {
                row.Note = $"不在大厅 / not in lobby (state={mc.State})";
                abort = row.Note;
                break;
            }
            yield return Wait(2.5f);
            // 等待期间配置可能被改了：进场前再查一次（Codex 审查 #10）。
            if (!Allowed())
            {
                rows.Remove(row);
                abort = "调试被关掉或存档槽不允许 / debug disabled or slot not allowed";
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检中止 / self-test aborted: {abort}");
                break;
            }
            // 回大厅时的存档还在后台写，StartFight 会拒绝开战（Codex 复审）：等写完再进场。
            if (SaveGuard.SaveQueueBusy())
            {
                yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
                if (SaveGuard.SaveQueueBusy())
                {
                    GodhomePlugin.Log.LogWarning("[Godhome] 存档队列 15 秒还没空 / save queue still busy after 15 s");
                }
            }

            string shot = Path.Combine(shotDir, $"{i + 1:00}-{b.Id}");
            if (!b.ArenaReady)
            {
                // 表里没有场地数据：不能开战（运行时不猜），记下来跳过。
                row.Note = "没有场地数据 / no arena data (arena.door)";
                row.Return = mc.State == LoomState.Lobby;
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检 / self-test {b.Id}: {row.Note}");
                WriteReport(final: false, abort: null);
                continue;
            }
            // 1. 进场：以场地的 door_dreamEnter 为入口
            mc.StartFight(b);
            yield return WaitFor(() => mc.State != LoomState.ToFight, timeout);
            GameManager gm = GameManager.instance;
            row.Scene = gm.GetSceneNameString();
            row.Entered = mc.State == LoomState.Fight && (row.Scene == b.EntryScene || row.Scene == b.Scene) && Arena.EnteredVia == Arena.EnterGate;
            if (!row.Entered)
            {
                row.Note = $"state={mc.State}, scene={row.Scene}; {ArenaEntry()}";
            }
            else
            {
                List<string> scenes = ModeController.ScenesOf(b);
                ResetPeaks();

                // 2. 入场：游戏自己从场地的 door_dreamEnter 把人放进来（和正常游玩完全一样，自检不碰）。
                row.Entry = ArenaEntry();
                yield return Wait(1.2f);
                yield return Shots.Capture(shot + "-1-arena.jpg", row.Shots);
                GodhomePlugin.Log.LogInfo($"[Godhome]   画面 / view {b.Id}: {ViewInfo.Darkness()}；{ViewInfo.Covering(8)}{ViewInfo.MaskOwners()}");
                if (EntryChecked(b) && mc.State == LoomState.Fight && Allowed())
                {
                    yield return Beating(EntryChecks(mc, b, shot, row));
                }

                // 3. 自己开战：不替玩家触发。只认表里的开战判定（arena.wake）：等的 FSM 在等待状态里待过、又离开了，
                //    或者 BattleScene 开战了。没写开战判定的 Boss 算没开战（表要补）。
                int fightNo = mc.FightSerial;
                bool StartSeen() => Arena.StartedIn(fightNo, out _);
                bool challengeEarly = false;
                // 挑战真正发出去（表里的 INTERACT 步骤执行成功）的时刻；没发出去是 -1（Codex 审查 v0.6.0 #15）。
                float challengeAt = -1f;
                float challengeStartedAfter = -1f;
                if (b.Arena.Challenge)
                {
                    // 挑战位（dream.challenge）：先等几秒确认没挑战时不会自己开战，再像玩家一样在挑战区按交互
                    // （表里 debug.react 的 INTERACT），看挑战以后能不能开战。
                    yield return WaitFor(() => mc.State != LoomState.Fight || !Allowed() || StartSeen(), ChallengeQuiet);
                    challengeEarly = StartSeen();
                    if (!challengeEarly)
                    {
                        var ch = new float[3];
                        yield return SendChallenge(mc, b, scenes, fightNo, ch);
                        challengeAt = ch[0];
                        challengeStartedAfter = ch[1];
                        challengeEarly = ch[2] > 0f;
                    }
                }
                else
                {
                    yield return WaitFor(() => mc.State != LoomState.Fight || !Allowed() || StartSeen(), StartTimeout);
                }
                if (challengeEarly)
                {
                    row.StartHow = "没挑战就自己开战了 / started without the challenge";
                    GodhomePlugin.Log.LogWarning($"[Godhome]   {b.Id}: {row.StartHow}");
                }
                else if (b.Arena.Challenge && StartSeen())
                {
                    if (challengeAt >= 0f && challengeStartedAfter >= 0f)
                    {
                        row.Start = true;
                        row.StartHow = $"{ChallengeQuiet:0} 秒内没自己开战，挑战后 {challengeStartedAfter:0.0} 秒开战 / waited {ChallengeQuiet:0} s without starting, started {challengeStartedAfter:0.0} s after the challenge";
                    }
                    else
                    {
                        row.StartHow = "开战了，但挑战没发出去 / started although the challenge was never sent";
                        GodhomePlugin.Log.LogWarning($"[Godhome]   {b.Id}: {row.StartHow}");
                    }
                }
                else if (Arena.Started)
                {
                    row.Start = true;
                    row.StartHow = "开战判定 / wake: " + Arena.StartNote;
                }
                else if (mc.State == LoomState.Fight)
                {
                    row.StartHow = $"{StartTimeout:0} 秒没有开战 / did not start in {StartTimeout:0} s";
                    GodhomePlugin.Log.LogWarning($"[Godhome]   {b.Id}: {row.StartHow}（场地 / arena: {row.Entry}）");
                    LogDiagnostics(mc, b, scenes);
                }
                if (row.Start)
                {
                    yield return Wait(1.5f);
                    yield return Shots.Capture(shot + "-2-start.jpg", row.Shots);
                }
                if (AggroChecked(b) && row.Start && mc.State == LoomState.Fight && Allowed())
                {
                    yield return Beating(AggroChecks(mc, b, scenes, shot, row));
                }
                float watch = GodhomeConfig.SelfTestWatch.Value;
                if (watch > 0f && mc.State == LoomState.Fight)
                {
                    yield return WatchFight(mc, shot, row, watch);
                }

                // 4. 用普通大小的伤害打（每击最多四分之一血），让 Boss 走正常的换阶段和死亡流程。
                //    只打出现在画面里的本体（玩家也只打得到眼前的 Boss）；没开战也照样打，看后面能不能走通，
                //    但不再用无视无敌的兜底击杀，打不进去就是失败。
                //    挑战位没按挑战开战就不打（Codex 审查 0.9.0 第二轮 #8）：它可能还在背景里，打死了也不说明能打。
                bool hadHms = CleanWatch.HasBossHms(mc);
                if (b.Arena.Challenge && !row.Start)
                {
                    row.Note += (row.Note.Length > 0 ? "; " : "") + "挑战位没按挑战开战，不打 / challenge slot did not start from the challenge; not attacked";
                }
                else
                {
                    yield return Brawl(mc, b, scenes, row, timeout);
                }

                // 5. 赢了：倒下后到回圣所这段，不能还有活着的本体（提前判赢），游戏想切去的演出场景都要被改道。
                row.Kill = mc.LastResult == FightResult.Won;
                if (row.Kill)
                {
                    row.Note += $"; 判定 / by {mc.LastWinHow}";
                    // 从判胜那一刻就开始看（第三轮 #5：先等半秒截图，胜利转场可能已经走了，一次没看到就不能写“干净”）。
                    var cleanWatch = new CleanWatch(hadHms);
                    cleanWatch.Sample(mc, b);
                    yield return WaitFor(() => { cleanWatch.Sample(mc, b); return false; }, 0.5f);
                    yield return Shots.Capture(shot + "-3-win.jpg", row.Shots);
                    float end = Time.unscaledTime + 5f;
                    while (mc.State == LoomState.Won && Time.unscaledTime < end)
                    {
                        Beat();
                        cleanWatch.Sample(mc, b);
                        yield return null;
                    }
                    row.Clean = cleanWatch.Result;
                    row.Note += cleanWatch.Note;
                }
                else
                {
                    row.Note += $"; 未判定胜利 / no victory (state={mc.State}, result={mc.LastResult})";
                    if (mc.State == LoomState.Fight && Allowed())
                    {
                        LogDiagnostics(mc, b, scenes);
                        yield return Shots.Capture(shot + "-3-timeout.jpg", row.Shots);
                        mc.ReturnToLobby("self-test timeout");
                    }
                }
            }
            if (mc.State == LoomState.ToFight && Allowed())
            {
                // 进场卡住：强制回大厅
                mc.ReturnToLobby("self-test entry stuck");
            }
            // 中途调试被关掉、换了槽（Codex 复审）：不再替玩家做任何事（不送回大厅、不离开圣所），人留在原地交给玩家。
            if (!Allowed() && mc.State != LoomState.Lobby && mc.State != LoomState.ToLobby)
            {
                abort = "调试被关掉或存档槽不允许 / debug disabled or slot not allowed";
                row.Note += (row.Note.Length > 0 ? "; " : "") + $"中止，留在原地 / aborted, left as is (state={mc.State})";
                row.Seconds = Time.unscaledTime - t0;
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检中止 / self-test aborted during {b.Id}: {abort}");
                break;
            }

            // 4. 回大厅
            yield return WaitFor(() => mc.State == LoomState.Lobby, Mathf.Max(timeout, 60f));
            row.Return = mc.State == LoomState.Lobby;
            if (!row.Return)
            {
                row.Note += (row.Note.Length > 0 ? "; " : "") + $"没回到大厅 / did not return (state={mc.State})";
            }
            else if (row.Entered)
            {
                yield return Wait(1.5f);
                yield return Shots.Capture(shot + "-4-lobby.jpg", row.Shots);
                // 0.10.5：回大厅后真的能落地（Boss 演出改了刚体、重力、显示时这里抓得到，自检别的项都不要求人自己动）。
                var fall = new HeroCheck.Result();
                yield return Beating(HeroCheck.Fall(fall));
                row.Fall = fall.Ok;
                row.Note += (row.Note.Length > 0 ? "; " : "") + (fall.Ok ? "PASS " : "FAIL ") + fall.Detail;
                GodhomePlugin.Log.LogInfo($"[Godhome]   {b.Id} {(fall.Ok ? "PASS" : "FAIL")} {fall.Detail}");
                if (EntryChecked(b) && mc.State == LoomState.Lobby && Allowed())
                {
                    yield return Beating(RetryCheck(mc, b, shot, row, timeout));
                    row.Return = mc.State == LoomState.Lobby;
                }
            }
            if (mc.WinRedirects > 0)
            {
                row.Note += (row.Note.Length > 0 ? "; " : "") + $"倒下后游戏要切场景，已改道回圣所 / scene changes after the win redirected: {mc.WinRedirects}";
            }
            row.Seconds = Time.unscaledTime - t0;
            GodhomePlugin.Log.LogInfo($"[Godhome] 自检 / self-test {b.Id}: entered={row.Entered} arena=[{row.Entry}] start={row.Start} ({row.StartHow}) found={row.Found} kill={row.Kill} clean={row.Clean} return={row.Return} {row.Note}");
            WriteReport(final: false, abort: null);
            if (!row.Return)
            {
                abort = $"{b.Id} 之后没回到大厅 / did not return to the lobby after {b.Id} (state={mc.State})";
                break;
            }
        }

        // 0.10.5：念珠、工具碎片无限（念珠炮、建筑师），走游戏自己的花钱、开炮、现场打造入口。
        if (abort == null && mc.State == LoomState.Lobby && Allowed())
        {
            yield return Beating(ResourceSelfTest());
            WriteReport(final: false, abort: null);
        }

        // 结束时在大厅存一次档，要真的成功才算完成（Codex 复审：以前不等存档就退出游戏）。
        // 只在没中止、人在大厅、调试仍然允许时（Codex 复审：中止了就不再替玩家动手）。
        if (abort == null && mc.State == LoomState.Lobby && Allowed())
        {
            yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
            mc.SaveNow("self-test end / 自检结束");
            yield return WaitFor(() => mc.LastSaveResult != null, 60f);
            if (mc.LastSaveResult != true)
            {
                abort = $"结束时存档没有成功 / the final save did not succeed ({(mc.LastSaveResult == null ? "timeout" : "failed")})";
            }
        }
        else if (abort == null)
        {
            abort = Allowed()
                ? $"结束时不在大厅 / not in the lobby at the end (state={mc.State})"
                : "调试被关掉或存档槽不允许 / debug disabled or slot not allowed";
        }
        WriteReport(final: true, abort: abort);
        SelfTestStatus = abort == null ? "完成 / done" : "中止 / aborted";
        // 和报告用同一个判定（Codex 审查 #29）。
        GodhomePlugin.Log.LogInfo($"[Godhome] 自检结束 / self-test finished: {rows.Count(Passed)}/{rows.Count} 全部通过（计划 / planned {planned}）{(resourceLines.Count > 0 ? (resourceFail ? "，资源 / resources FAIL" : "，资源 / resources PASS") : "")}{(abort != null ? $"，中止 / aborted: {abort}" : "")}");
        ArrivalWatch.Summary();
        bool quit = abort == null && rows.Count == planned && GodhomeConfig.DebugQuitAfterSelfTest.Value;
        SelfTestRunning = false;
        selfTestRoutine = null;
        if (quit)
        {
            yield return new WaitForSecondsRealtime(8f);
            // 等待期间配置可能被改了（Codex 审查 #10），也可能已经开了新的一轮（Codex 复审）。
            if (myRun == selfTestRun && !SelfTestRunning && Allowed() && GodhomeConfig.DebugQuitAfterSelfTest.Value)
            {
                GodhomePlugin.Log.LogInfo("[Godhome] 自检后退出游戏 / quitting after self-test");
                Application.Quit();
            }
        }
    }

    /// <summary>
    /// 万神殿自检在休息点换一个别的纹章（模拟玩家坐长椅换配装，测 Codex 审计 0.10.3 F03），返回换上的纹章 id；换不了返回 null（不测）。
    /// 和物品栏里换纹章一样：AutoEquip 换上、发配装改变事件。
    /// </summary>
    private static string? SwapCrestForTest()
    {
        try
        {
            PlayerData pd = PlayerData.instance;
            string cur = pd.CurrentCrestID;
            List<ToolCrest> all = ToolItemManager.GetAllCrests();
            ToolCrest? pick = null;
            foreach (string want in new[] { "Reaper", "Wanderer", "Warrior", "Toolmaster", "Spell" })
            {
                pick = all.FirstOrDefault(c => c != null && c.name == want && c.IsUnlocked && c.name != cur);
                if (pick != null)
                {
                    break;
                }
            }
            pick ??= all.FirstOrDefault(c => c != null && c.IsVisible && c.IsUnlocked && !c.IsHidden && c.name != cur);
            if (pick == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 自检：没有别的纹章可换，不测配装 / self-test: no other crest to swap to (current {cur})");
                return null;
            }
            ToolItemManager.AutoEquip(pick, false, false);
            ToolItemManager.SendEquippedChangedEvent(force: true);
            bool ok = pd.CurrentCrestID == pick.name;
            GodhomePlugin.Log.LogInfo($"[Godhome] 自检在休息点换纹章 / self-test swapped the crest at the rest stop: {cur} → {pd.CurrentCrestID}{(ok ? "" : $"（没换上 / did not take: {pick.name}）")}");
            return ok ? pick.name : null;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 自检换纹章出错，不测配装 / self-test crest swap failed: {e.Message}");
            return null;
        }
    }

    private static bool Passed(Row r) => r.Abandon
        ? r.Entered && r.Return
        : r.Entered && r.Start && r.Found && r.Kill && r.Clean != false && r.Return && r.Fall != false && r.EntryCheck != false;

    /// <summary>
    /// “打完干净”：胜利后（这一场还在 Won）还有没有活着的本体（有就是可能提前判赢）。只看 Boss 的血量组件。
    /// 开打前找到过血量组件、胜利后都没了（倒下后被销毁，靠 OnDeath 判胜的大多这样），也算看过、没有活着的。
    /// 一次都没在胜利状态里看到，或者开打前就没有 Boss 的血量组件（纯 FSM 的 Boss，例如炽焰之父），没法判断，写 N/A（第三轮 #5）。
    /// </summary>
    private sealed class CleanWatch
    {
        private readonly bool hadHms;
        private bool seen;
        private bool won;
        private string? alive;

        /// <param name="hadHms">开打前（打之前扫一次）有没有找到 Boss 的血量组件。</param>
        internal CleanWatch(bool hadHms)
        {
            this.hadHms = hadHms;
        }

        /// <summary>开打前扫一次：这一场有没有 Boss 的血量组件。</summary>
        internal static bool HasBossHms(ModeController mc)
        {
            mc.ScanBoss();
            return mc.BossHealth.Any(h => h != null);
        }

        internal void Sample(ModeController mc, BossDef b)
        {
            if (mc.State != LoomState.Won || mc.CurrentBoss != b)
            {
                return;
            }
            won = true;
            mc.ScanBoss();
            List<HealthManager> hms = mc.BossHealth.Where(h => h != null).ToList();
            if (hms.Count == 0)
            {
                // 开打前有、现在都没了：被销毁的不会是活着的本体。
                seen |= hadHms;
                return;
            }
            seen = true;
            HealthManager? hm = hms.FirstOrDefault(h => h.gameObject.activeInHierarchy && !h.isDead && h.hp > 0 && h.hp < PlaceholderHp);
            if (hm != null && alive == null)
            {
                alive = $"{SceneTools.PathOf(hm.transform)} hp={hm.hp}";
            }
        }

        internal bool? Result => seen ? alive == null : (bool?)null;

        internal string Note => alive != null ? $"; 倒下后还有活着的本体，可能提前判赢 / a boss body was still alive after the win: {alive}"
            : seen ? ""
            : won ? "; 打完干净没法看：这一场没有 Boss 的血量组件（纯 FSM 的 Boss）/ clean not observable: the boss has no HealthManager"
            : "; 打完干净没法看：没在胜利状态里看到 / clean not observable: the win state was already over";
    }

    // ───────────────────────────── 万神殿自检 ─────────────────────────────

    /// <summary>万神殿自检的检查项（报告末尾一节）。</summary>
    private readonly List<string> extra = new();

    private static Binding ParseBindings(string? s)
    {
        Binding b = Binding.None;
        foreach (string part in (s ?? "").Split(new[] { ',', ' ', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "all":
                    b = Binding.All;
                    break;
                case "needle":
                case "nail":
                    b |= Binding.Needle;
                    break;
                case "mask":
                case "shell":
                    b |= Binding.Mask;
                    break;
                case "tools":
                case "tool":
                case "charms":
                    b |= Binding.Tools;
                    break;
                case "silk":
                case "soul":
                    b |= Binding.Silk;
                    break;
            }
        }
        return b;
    }

    private void Check(string what, bool ok, string detail = "")
    {
        string line = $"{(ok ? "PASS" : "FAIL")} {what}{(detail.Length > 0 ? "：" + detail : "")}";
        extra.Add(line);
        GodhomePlugin.Log.LogInfo($"[Godhome]   万神殿自检 / pantheon check: {line}");
    }

    /// <summary>
    /// 丝技能不能放（万神殿自检“束缚生效”用）：丝给到上限，三个方向绑着的丝技逐个过 HeroController.CanThrowTool（私有，按键放时也是它）。
    /// 没绑丝技的存档记“没有丝技”，不算失败。丝原来多少就还原多少。
    /// </summary>
    private static string SkillCheck(PlayerData pd, out bool ok)
    {
        ok = true;
        HeroController? hc = HeroController.instance;
        if (hc == null)
        {
            return "";
        }
        var skills = new List<(AttackToolBinding Dir, ToolItem Tool)>();
        foreach (AttackToolBinding d in new[] { AttackToolBinding.Neutral, AttackToolBinding.Up, AttackToolBinding.Down })
        {
            if (ToolItemManager.GetBoundAttackTool(d, ToolEquippedReadSource.Active, out _) is ToolItem t && Bindings.IsSkill(t))
            {
                skills.Add((d, t));
            }
        }
        if (skills.Count == 0)
        {
            return "，丝技 / silk skills 没有绑 / none bound";
        }
        int silk = pd.silk;
        try
        {
            pd.silk = pd.CurrentSilkMax;
            int can = skills.Count(s => (bool)AccessTools.Method(typeof(HeroController), "CanThrowTool").Invoke(hc, new object[] { s.Tool, s.Dir, false }));
            ok = can == skills.Count;
            return $"，丝技 / silk skills 能放 / castable {can}/{skills.Count}（{string.Join("、", skills.Select(s => $"{s.Dir}:{s.Tool.name}"))}，丝 / silk {pd.silk}/{pd.CurrentSilkMax}，要 / cost {pd.SilkSkillCost}）";
        }
        catch (Exception e)
        {
            ok = false;
            return $"，丝技检查出错 / silk skill check failed: {e.GetBaseException().Message}";
        }
        finally
        {
            pd.silk = silk;
        }
    }

    private static int RawNeedle(PlayerData pd) => pd.nailUpgrades switch
    {
        0 => 5,
        1 => 9,
        2 => 13,
        3 => 17,
        _ => 21,
    };

    /// <summary>
    /// 万神殿自检（Debug.SelfTestPantheon）：巡视前厅（圣所的丝门走过去、每扇门前截图）→ 在门前的面板里套上束缚、按“开始”
    /// → 逐场打（和单个 Boss 自检同一套打法，经过休息点时自己走出去）→ 回到门前看结算 → 按键关掉。
    /// 自检算调试辅助：不记完成情况（存档里的万神殿记录不会变）。
    /// </summary>
    private IEnumerator PantheonSelfTest(PantheonDef p, Binding bindings, int abortAt, int myRun)
    {
        Beat();
        SelfTestRunning = true;
        rows.Clear();
        extra.Clear();
        resourceLines.Clear();
        resourceFail = false;
        ArrivalWatch.Reset();
        planned = abortAt > 0 ? Mathf.Min(abortAt, p.Bosses.Count) : p.Bosses.Count;
        reportStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string shotDir = Path.Combine(Paths.BepInExRootPath, "Godhome", "shots", reportStamp);
        try
        {
            Directory.CreateDirectory(shotDir);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 建截图目录失败 / failed to create {shotDir}: {e.Message}");
        }
        var junk = new List<string>();
        ModeController mc = ModeController.Instance!;
        float timeout = Mathf.Max(10f, GodhomeConfig.SelfTestStepTimeout.Value);
        string? abort = null;
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检开始 / pantheon self-test started: {p.Id}（{p.Bosses.Count} 场，束缚 / bindings {Bindings.Describe(bindings)}{(abortAt > 0 ? $"，第 {abortAt} 场放弃 / abandon at fight {abortAt}" : "")}）");
        // 前作神居当大厅时（Hk1Lobby.Active）没有圣所：大厅就绪看 Lobby.Ready，门前的走动和挑战界面由前作巡视（Hk1Tour）测，这里直接开始连战。
        bool hk1 = Hk1Lobby.Active;
        // 前作的连战转场：每种放到一半截一张（tr-in / tr-out / tr-final.jpg）。
        Hk1Battle.SetShotDir(hk1 ? shotDir : null);
        void LogDoorView(string when)
        {
            HeroController? h = HeroController.SilentInstance;
            Camera? cam = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
            GodhomePlugin.Log.LogInfo($"[Godhome]   画面 / view door {when}: hero={(h != null ? $"({h.transform.position.x:0.0}, {h.transform.position.y:0.0}) input={h.acceptingInput} relinquished={h.controlReqlinquished} inPos={h.isHeroInPosition}" : "-")} "
                + $"camera={(cam != null ? $"({cam.transform.position.x:0.0}, {cam.transform.position.y:0.0})" : "-")} cover={LoomUI.Instance?.CoverAlpha:0.00} result={LoomUI.Instance?.ResultBusy} "
                + $"{ViewInfo.CameraLuma()} {ViewInfo.HudOpaque()} {ViewInfo.GateFsms(Hk1Lobby.ReturnTo(ModeController.DoorSpot(p.Id))?.Gate ?? "-")} {ViewInfo.Darkness()}；{ViewInfo.Covering(8)}；{ViewInfo.Canvases()}");
        }
        bool LobbyReady() => mc.State == LoomState.Lobby && Lobby.Ready && LoomUI.Instance != null && LoomUI.Instance.CoverAlpha < 0.02f
            && HeroController.SilentInstance != null && HeroController.SilentInstance.acceptingInput && !HeroController.SilentInstance.controlReqlinquished;

        if (!GodhomeSave.IsGodhome(GameManager.instance))
        {
            abort = "不是神居存档 / not a godhome save";
        }
        if (abort == null)
        {
            yield return WaitFor(() => mc.State == LoomState.Lobby && (hk1 ? Lobby.Ready : Sanctum.Built) && LoomUI.Instance != null && LoomUI.Instance.CoverAlpha < 0.02f, 90f);
            // 读档后坐在长椅上：先起身（和长廊巡视一样，给长椅 FSM 发 GET UP）。
            float upUntil = Time.unscaledTime + 15f;
            while (Time.unscaledTime < upUntil && HeroController.SilentInstance != null && (PlayerData.instance.atBench || !HeroController.SilentInstance.acceptingInput))
            {
                if (PlayerData.instance.atBench)
                {
                    PlayMakerFSM? benchFsm = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)
                        .FirstOrDefault(f => f != null && f.FsmName == "Bench Control");
                    benchFsm?.SendEvent("GET UP");
                }
                yield return Wait(0.5f);
            }
            yield return WaitFor(LobbyReady, 20f);
            if (!LobbyReady())
            {
                HeroController? hh = HeroController.SilentInstance;
                abort = $"大厅没准备好 / lobby not ready (state={mc.State}, built={Sanctum.Built}, busy={Sanctum.Busy}, cover={LoomUI.Instance?.CoverAlpha:0.00}, atBench={PlayerData.instance.atBench}, input={hh?.acceptingInput}, relinquished={hh?.controlReqlinquished})";
            }
        }
        // 开着前作神居、前作和打包插件都在：大厅必须是前作神居（打包失败时会退回织忆圣所，其余检查照样能过，这里要报出来）。
        if (abort == null && GodhomeConfig.Hk1Enabled.Value && Hk1Probe.Available)
        {
            Check("大厅是前作神居 / the lobby is the hk1 godhome", hk1, hk1 ? GameManager.instance.GetSceneNameString() : $"没用上 / not in use: {Hk1Assets.Failure}");
            if (hk1)
            {
                Check("前作连战转场特效 / hk1 battle transitions", Hk1Battle.Usable, Hk1Battle.Usable ? Hk1Battle.PrefabName : Hk1Battle.Failure);
            }
        }
        PantheonRecord before = GodhomePlugin.Instance.Records.Pantheon(p.Id);
        int attemptsBefore = before.Attempts, clearsBefore = before.Clears;
        // 临时加一个休息点：不改共用的万神殿表（Codex 审查 0.8.0：自检被停掉时撤不掉，之后正式的连战也会多一个休息点），
        // 只在自检跑着、打的是这个殿时由 ExtraRest 回答。
        int restAfter = GodhomeConfig.SelfTestPantheonRestAfter.Value;
        extraRest = restAfter > 0 && restAfter < p.Bosses.Count && !p.Rests.Contains(restAfter) ? (p.Id, restAfter) : null;
        if (extraRest != null)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检：临时在第 {restAfter} 场之后加休息点 / pantheon self-test: temporary rest stop after fight {restAfter}");
        }

        // 1. 前厅：从圣所的丝门走过去，每扇门前截图。
        if (abort == null && hk1)
        {
            yield return Shots.Capture(Path.Combine(shotDir, "00-hk1-lobby.jpg"), junk);
            yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
            // 照玩家：这扇门开着、就在这间房里时，走到门前按上，在前作的挑战界面里套束缚、按开始。
            // 前作的门 FSM 会记下“从这扇门出去”，打完回来时回程门上的 FSM（Boss Sequence Finish）才放出门动画、淡入；
            // 直接 StartPantheon 的回来时没人淡入（自检里看到过整屏黑，靠 ModeController 的兜底淡入）。门锁着、不在这间房才直接开始。
            BossSequenceDoor? hd = UnityEngine.Object.FindObjectsByType<BossSequenceDoor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(d => Hk1Doors.DefOf(d)?.Id == p.Id && d.gameObject.scene.name == GameManager.instance.GetSceneNameString());
            Transform? inspect = hd != null && hd.unlockedSet != null && hd.unlockedSet.activeInHierarchy ? hd.unlockedSet.transform.Find("Inspect") : null;
            PlayMakerFSM? npc = inspect != null ? inspect.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "npc_control") : null;
            if (hd != null && npc != null)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检：大厅是前作神居，走前作的门 / pantheon self-test: hk1 lobby, going through the hk1 door {hd.name}");
                RaycastHit2D hit = Physics2D.Raycast(new Vector2(hd.transform.position.x, hd.transform.position.y + 2f), Vector2.down, 25f, 1 << 8);
                Sanctum.StandAt(HeroController.instance, hd.transform.position.x, hit.collider != null ? hit.point.y : hd.transform.position.y - 1f);
                yield return Wait(1.2f);
                yield return WaitFor(() => npc.ActiveStateName == "In Range", 4f);
                // 门前的交互提示：前作这里写的是 Challenge（丝之歌 Prompts 表里有），不能是“交谈”。截一张连界面的图看。
                yield return Wait(0.6f);
                {
                    string want = TeamCherry.Localization.Language.Has("CHALLENGE", "Prompts") ? TeamCherry.Localization.Language.Get("CHALLENGE", "Prompts") : "?";
                    // “交谈”在丝之歌的 Prompts 表里是 SPEAK（InteractableBase.PromptLabels.Speak）。
                    string talk = TeamCherry.Localization.Language.Has("SPEAK", "Prompts") ? TeamCherry.Localization.Language.Get("SPEAK", "Prompts") : "?";
                    string[] shownAll = VisiblePrompts();
                    string shown = string.Join("|", shownAll);
                    Check("门前的提示 / door prompt", shownAll.Length == 1 && shown == want,
                        $"显示 / shows \"{shown}\"（{shownAll.Length} 个 / prompts），要 / want \"{want}\"（交谈 / talk = \"{talk}\"），npc_control {npc.ActiveStateName}，Prompt Name \"{npc.FsmVariables.FindFsmString("Prompt Name")?.Value}\"");
                    yield return Shots.Capture(Path.Combine(shotDir, "05-door-prompt.jpg"), junk);
                }
                npc.SendEvent("UP PRESSED");
                BossDoorChallengeUI? ui = null;
                yield return WaitFor(() => (ui = UnityEngine.Object.FindObjectsByType<BossDoorChallengeUI>(FindObjectsSortMode.None).FirstOrDefault(u => u.isActiveAndEnabled)) != null, 6f);
                Check("前作门前的挑战界面 / hk1 door challenge UI", ui != null, $"npc_control {npc.ActiveStateName}");
                if (ui != null)
                {
                    yield return Wait(2f);
                    BossDoorChallengeUIBindingButton[] buttons = { ui.boundNailButton, ui.boundHeartButton, ui.boundCharmsButton, ui.boundSoulButton };
                    Binding[] order = { Binding.Needle, Binding.Mask, Binding.Tools, Binding.Silk };
                    for (int i = 0; i < 4; i++)
                    {
                        if ((bindings & order[i]) != 0 && buttons[i] != null && !buttons[i].Selected)
                        {
                            buttons[i].OnSubmit(null);
                            yield return Wait(0.4f);
                        }
                    }
                    yield return Shots.Capture(Path.Combine(shotDir, "10-panel.jpg"), junk);
                    ui.Begin();
                }
                yield return WaitFor(() => mc.Run != null || mc.State != LoomState.Lobby, 20f);
            }
            else
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检：大厅是前作神居，门锁着或不在这间房，直接开始 / pantheon self-test: hk1 lobby, the door is locked or elsewhere; starting the run directly（{GameManager.instance.GetSceneNameString()}）");
                mc.StartPantheon(p, bindings, ignoreLock: true);
                yield return WaitFor(() => mc.Run != null || mc.State != LoomState.Lobby, 8f);
            }
            Check("开始连战 / run started", mc.Run != null && mc.Run.Def == p && mc.Run.Bindings == bindings, mc.Run?.ToString() ?? $"state={mc.State}");
            if (mc.Run == null)
            {
                abort = "没开始连战 / run did not start";
            }
        }
        else if (abort == null)
        {
            HeroController hero = HeroController.instance;
            Sanctum.StandAt(hero, Sanctum.HubAtriumGateX + 2.5f, Sanctum.FloorY);
            yield return Wait(1.2f);
            yield return Shots.Capture(Path.Combine(shotDir, "00-hub-gate.jpg"), junk);
            SilkGate? gate = UnityEngine.Object.FindObjectsByType<SilkGate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(g => g.name == "Gate Hub→Atrium");
            if (gate != null)
            {
                gate.Interact();
                yield return WaitFor(() => Sanctum.Busy, 2f);
                yield return WaitFor(() => !Sanctum.Busy, 6f);
                yield return Wait(0.6f);
                float hx = HeroController.instance.transform.position.x;
                Check("丝门去前厅 / silk gate to the atrium", Mathf.Abs(hx - (Sanctum.AtriumLeft + Sanctum.AtriumGateOffset + 2.5f)) < 2f, $"x={hx:0.0}，前厅 / atrium {Sanctum.AtriumLeft:0.0}..{Sanctum.AtriumRight:0.0}");
                yield return Shots.Capture(Path.Combine(shotDir, "01-atrium-entry.jpg"), junk);
            }
            else
            {
                Check("丝门去前厅 / silk gate to the atrium", false, "找不到丝门 / gate not found");
            }
            Check("门的个数 / doors", Sanctum.Doors.Count == PantheonCatalog.All.Count, $"{Sanctum.Doors.Count}/{PantheonCatalog.All.Count}");
            int n = 2;
            foreach (PantheonDoor d in Sanctum.Doors.ToList())
            {
                if (d == null)
                {
                    continue;
                }
                Sanctum.StandAt(HeroController.instance, d.X - 3.6f * d.Size, d.Floor);
                HeroController.instance.FaceRight();
                yield return Wait(1.2f);
                yield return Shots.Capture(Path.Combine(shotDir, $"{n++:00}-door-{d.Def.Id}.jpg"), junk);
                bool expectLocked = !ModeController.PantheonUnlocked(d.Def);
                Check($"门 / door {d.Def.Id}", d.Locked == expectLocked && (d.Interact == null || d.Interact.enabled == !d.Locked),
                    $"第 {d.Level + 1} 层 / level {d.Level + 1}，x={d.X:0.0}，{(d.Locked ? "封着 / locked" : "开着 / open")}");
            }
        }

        // 2. 门前的面板：套上束缚，按“开始”。
        PantheonDoor? door = abort == null && !hk1 ? Sanctum.FindDoor(p.Id) : null;
        if (abort == null && !hk1 && (door == null || door.Locked || door.Interact == null))
        {
            abort = door == null ? "找不到门 / door not found" : "门被封着 / door locked";
        }
        if (abort == null && !hk1)
        {
            Sanctum.StandAt(HeroController.instance, door!.X - 1f, door.Floor);
            yield return Wait(1f);
            // 回大厅时的存档可能还在写：StartPantheon 会拒绝开始，等写完（和单个 Boss 自检一样）。
            yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
            door.Interact!.Interact();
            yield return WaitFor(() => LoomUI.Instance!.DoorChallenge != null && (!LoomUI.Instance.ChallengeNative || PantheonMenu.Selected >= 0), 6f);
            bool native = LoomUI.Instance!.ChallengeNative;
            Check("面板打开 / panel opened", LoomUI.Instance.DoorChallenge != null, native ? $"原版界面 / native：{PantheonMenu.TitleText}" : "简易面板 / fallback");
            yield return Wait(0.8f);
            yield return Shots.Capture(Path.Combine(shotDir, "10-panel.jpg"), junk);
            if (native)
            {
                int sid = LoomUI.Instance.ChallengeSession;
                for (int i = 0; i < 4; i++)
                {
                    if ((bindings & PantheonMenu.Order[i]) != 0)
                    {
                        PantheonMenu.PressForTest(i, sid);
                        yield return Wait(0.5f);
                    }
                }
                Check("面板里套上束缚 / bindings set in the panel", PantheonMenu.Chosen == bindings, $"{Bindings.Describe(PantheonMenu.Chosen)}");
                PantheonMenu.MoveForTest(UnityEngine.EventSystems.MoveDirection.Down, sid);
                yield return Wait(0.4f);
                yield return Shots.Capture(Path.Combine(shotDir, "11-panel-bound.jpg"), junk);
                Check("“开始”的说明 / begin hint", PantheonMenu.Selected == 4 && PantheonMenu.HintText.Length > 0, PantheonMenu.HintText);
                PantheonMenu.PressForTest(4, sid);
            }
            else
            {
                LoomUI.Instance.ChooseBindings(bindings, true);
            }
            yield return WaitFor(() => mc.Run != null || mc.State != LoomState.Lobby, 8f);
            Check("开始连战 / run started", mc.Run != null && mc.Run.Def == p && mc.Run.Bindings == bindings, mc.Run?.ToString() ?? $"state={mc.State}");
            if (mc.Run == null)
            {
                abort = "没开始连战 / run did not start";
            }
        }

        // 3. 逐场打。
        bool abandoned = false;
        int carried = -1;
        int carryOk = 0, carryChecks = 0;
        // Codex 审计 0.10.3 F03 / F04：休息点换的纹章要带到之后的每一场、回大厅变回开殿前的；直接接下一场时蓝血要带过去。
        string crestAtStart = PlayerData.instance.CurrentCrestID;
        string? expectCrest = null;
        int crestOk = 0, crestChecks = 0;
        // 0.10.6：配装只在开殿和休息点变；战斗里被游戏改的（幽影倒下 AutoEquipTool 装上十字缝）不带到下一场。
        RunGear? expectGear = null;
        int gearOk = 0, gearChecks = 0;
        string gearBad = "";
        int blueSet = -1;
        int blueOk = 0, blueChecks = 0;
        int heroOk = 0, heroChecks = 0;
        for (int i = 0; abort == null && i < p.Bosses.Count; i++)
        {
            BossDef b = p.Bosses[i];
            var row = new Row { Boss = b };
            rows.Add(row);
            SelfTestStatus = $"{p.Id} {i + 1}/{p.Bosses.Count} {b.Id}";
            float t0 = Time.unscaledTime;
            string shot = Path.Combine(shotDir, $"{20 + i:00}-{b.Id}");
            yield return WaitFor(() => mc.State is LoomState.Fight or LoomState.Rest or LoomState.Lobby || !Allowed(), timeout + 30f);
            bool rested = false;
            if (mc.State == LoomState.Rest)
            {
                rested = true;
                if (hk1)
                {
                    // 温泉到了以后的画面：前作转场揭开后界面镜头上不该还盖着白光。0.3、1.2 秒记一下样子（查间歇的白屏），2.5 秒时判。
                    yield return Wait(0.3f);
                    GodhomePlugin.Log.LogInfo($"[Godhome]   温泉画面 +0.3s / spa view: 界面镜头画到 / hud drawn {ViewInfo.HudCover()}%；{ViewInfo.HudOpaque(6)}；{Hk1Battle.Describe()}；{SpaEntryState()}");
                    yield return Wait(0.9f);
                    GodhomePlugin.Log.LogInfo($"[Godhome]   温泉画面 +1.2s / spa view: 界面镜头画到 / hud drawn {ViewInfo.HudCover()}%；{ViewInfo.HudOpaque(6)}；{Hk1Battle.Describe()}；{SpaEntryState()}");
                    yield return Wait(1.3f);
                }
                else
                {
                    yield return Wait(2.5f);
                }
                PlayerData rpd = PlayerData.instance;
                Check($"休息点 / rest stop before {b.Id}", rpd.health == rpd.CurrentMaxHealth, $"{GameManager.instance.GetSceneNameString()}，hp {rpd.health}/{rpd.CurrentMaxHealth}，束缚 / bindings {Bindings.Describe(Bindings.Active)}");
                if (hk1)
                {
                    // 前作神居：休息点照前作是温泉。
                    Check($"休息点是前作温泉 / rest stop is {Hk1Lobby.Spa}", GameManager.instance.GetSceneNameString() == Hk1Lobby.Spa,
                        $"{GameManager.instance.GetSceneNameString()}，人在 / hero at {HeroController.instance.transform.position}，能动 / input {HeroController.instance.acceptingInput}，{ViewInfo.CameraLuma()}");
                    int hudCover = ViewInfo.HudCover();
                    Check("温泉的画面没被盖住 / spa view not covered", hudCover >= 0 && hudCover < 50,
                        $"界面镜头画到 / hud drawn {hudCover}%；{ViewInfo.HudOpaque(6)}；{Hk1Battle.Describe()}；{SpaEntryState()}");
                }
                yield return Shots.Capture(shot + "-0-rest.jpg", row.Shots);
                if (hk1 && GameManager.instance.GetSceneNameString() == Hk1Lobby.Spa)
                {
                    yield return SpaBenchTest(shot, row.Shots);
                }
                // 像玩家在长椅上那样换个纹章（第一次休息点换一次），看之后每一场都还是它。
                expectCrest ??= SwapCrestForTest();
                expectGear = RunGear.Copy(PlayerData.instance);
                mc.DebugLeaveRest();
                yield return WaitFor(() => mc.State is LoomState.Fight or LoomState.Lobby || !Allowed(), timeout + 30f);
            }
            GameManager gm = GameManager.instance;
            row.Scene = gm.GetSceneNameString();
            row.Entered = mc.State == LoomState.Fight && mc.CurrentBoss == b && (row.Scene == b.EntryScene || row.Scene == b.Scene) && Arena.EnteredVia == Arena.EnterGate;
            if (!row.Entered)
            {
                row.Note = $"state={mc.State}, scene={row.Scene}, boss={mc.CurrentBoss?.Id}";
                abort = $"{b.Id} 没进战场 / did not reach the arena";
                break;
            }
            PlayerData pd = PlayerData.instance;
            if (i == 0)
            {
                int raw = RawNeedle(pd);
                bool needleOk = (bindings & Binding.Needle) == 0 ? pd.nailDamage == raw : pd.nailDamage == Bindings.NeedleDamage(raw);
                bool maskOk = (bindings & Binding.Mask) == 0 || pd.CurrentMaxHealth <= PantheonCatalog.Bindings.MaxMasks;
                bool silkOk = (bindings & Binding.Silk) == 0 || pd.CurrentSilkMax <= PantheonCatalog.Bindings.MaxSilk;
                // 0.10.5：念珠、碎片无限以后，工具束缚照样要让所有攻击工具扔不出去（三个方向都没有绑定的工具，主角也拿不到要扔的工具）。
                bool toolsOk = true;
                string toolsNote = "";
                if ((bindings & Binding.Tools) != 0)
                {
                    // 0.10.6：丝技（白色）不归工具束缚管，只数红、蓝、黄。
                    HeroController hc = HeroController.instance;
                    int bound = new[] { AttackToolBinding.Neutral, AttackToolBinding.Up, AttackToolBinding.Down }
                        .Count(d => ToolItemManager.GetBoundAttackTool(d, ToolEquippedReadSource.Active, out _) is ToolItem t && !Bindings.IsSkill(t));
                    bool willThrow = hc != null && hc.GetWillThrowTool(false) && !Bindings.IsSkill(Traverse.Create(hc).Field("willThrowTool").GetValue<ToolItem>());
                    toolsOk = bound == 0 && !willThrow;
                    toolsNote = $"，工具 / tools 能扔的方向 / throwable bindings {bound}，主角要扔 / hero will throw {willThrow}，念珠 / rosaries {pd.geo}，碎片 / shards {pd.ShellShards}";
                }
                // 0.10.6 实测清单第 8 条：开着灵丝束缚放不了法术。丝给到上限（束缚下 9），每个方向绑着的丝技都要能放（CanThrowTool，和按键放时同一个判断）。
                string skillNote = SkillCheck(pd, out bool skillsOk);
                toolsOk &= skillsOk;
                toolsNote += skillNote;
                Check("束缚生效 / bindings in effect", Bindings.Active == bindings && needleOk && maskOk && silkOk && toolsOk && pd.health <= pd.CurrentMaxHealth,
                    $"织针 / needle {pd.nailDamage}（原 / raw {raw}），面具 / masks {pd.health}/{pd.CurrentMaxHealth}，丝 / silk {pd.silk}/{pd.CurrentSilkMax}，Active={Bindings.Describe(Bindings.Active)}{toolsNote}");
            }
            else if (!rested && carried >= 0)
            {
                carryChecks++;
                if (pd.health == carried)
                {
                    carryOk++;
                }
                row.Note = $"带过来的血 / carried hp {pd.health}（上一场结束 / end of last {carried}）";
            }
            if (i == 0)
            {
                expectGear = RunGear.Copy(pd);
            }
            else if (expectGear != null)
            {
                gearChecks++;
                if (!expectGear.Differs(pd))
                {
                    gearOk++;
                }
                else
                {
                    gearBad += (gearBad.Length > 0 ? "，" : "") + b.Id;
                    row.Note += (row.Note.Length > 0 ? "; " : "") + "配装和开殿 / 休息点时的不一样 / loadout differs from the run start / rest stop";
                }
            }
            if (expectCrest != null)
            {
                crestChecks++;
                if (pd.CurrentCrestID == expectCrest)
                {
                    crestOk++;
                }
                else
                {
                    row.Note += (row.Note.Length > 0 ? "; " : "") + $"纹章 / crest {pd.CurrentCrestID}（休息点换成 / swapped to {expectCrest}）";
                }
            }
            if (i > 0 && HeroController.SilentInstance is HeroController heroNow)
            {
                // 上一场的演出不能把运动学刚体、隐身、无敌、无限空跳带进这一场（0.10.5：崇高圣母灵丝之后是第五万神殿最后一场）。
                heroChecks++;
                List<string> heroBad = HeroCheck.Problems(heroNow, full: false);
                if (heroBad.Count == 0)
                {
                    heroOk++;
                }
                else
                {
                    row.Note += (row.Note.Length > 0 ? "; " : "") + $"主角状态 / hero state: {string.Join("、", heroBad)}（{HeroCheck.State(heroNow)}）";
                }
            }
            if (blueSet >= 0)
            {
                if (!rested)
                {
                    blueChecks++;
                    if (pd.healthBlue == blueSet)
                    {
                        blueOk++;
                    }
                    row.Note += (row.Note.Length > 0 ? "; " : "") + $"蓝血 / blue {pd.healthBlue}（上一场结束 / end of last {blueSet}）";
                }
                blueSet = -1;
            }
            List<string> scenes = ModeController.ScenesOf(b);
            ResetPeaks();
            row.Entry = ArenaEntry();
            // 这一场的场次号：挑战位要打完才知道开没开战，那时 Arena 可能已经换成下一场了（按场次号查，Codex 审查 0.9.0 #13）。
            int fightNo = mc.FightSerial;
            yield return Wait(1.2f);
            yield return Shots.Capture(shot + "-1-arena.jpg", row.Shots);
            GodhomePlugin.Log.LogInfo($"[Godhome]   画面 / view {b.Id}: {ViewInfo.Darkness()}；{ViewInfo.Covering(8)}{ViewInfo.MaskOwners()}");
            if (!b.Arena.Challenge)
            {
                yield return WaitFor(() => mc.State != LoomState.Fight || !Allowed() || Arena.Started, StartTimeout);
                row.Start = Arena.StartedIn(fightNo, out string how);
                row.StartHow = row.Start ? "开战判定 / wake: " + how : "没开战 / not started";
            }
            else if (abortAt != i + 1)
            {
                // 挑战位：和单场一样先发挑战、等开战再打（开战前它可能在背景里，玩家打不到）。
                // 要“本场先发挑战、之后才开战”（Codex 审查 0.9.0 第二轮 #8）。
                var ch = new float[3];
                yield return SendChallenge(mc, b, scenes, fightNo, ch);
                bool started = Arena.StartedIn(fightNo, out string how);
                row.Start = ch[0] >= 0f && ch[1] >= 0f && started;
                row.StartHow = row.Start ? $"挑战后 {ch[1]:0.0} 秒开战 / started {ch[1]:0.0} s after the challenge: {how}"
                    : ch[2] > 0f ? "挑战发出前就开战了 / started before the challenge was sent"
                    : ch[0] < 0f ? "挑战没发出去 / the challenge was never sent" : "挑战后没开战 / no start after the challenge";
                if (!row.Start)
                {
                    // 没按挑战开战就不打（单场同样）：这一场算失败，连战中止。
                    row.Note += (row.Note.Length > 0 ? "; " : "") + "挑战位没按挑战开战，不打 / challenge slot did not start from the challenge; not attacked";
                    abort = $"{b.Id} 挑战位没按挑战开战 / challenge slot did not start from the challenge";
                    row.Seconds = Time.unscaledTime - t0;
                    break;
                }
            }
            // 开战后一张（拼总图看每场的样子用，和单场自检一样等 1.5 秒）。
            if (row.Start && abortAt != i + 1 && mc.State == LoomState.Fight)
            {
                yield return Wait(1.5f);
                yield return Shots.Capture(shot + "-2-start.jpg", row.Shots);
            }
            if (abortAt == i + 1)
            {
                // 放弃前核对还在这一场战斗里（第三轮 #4）：场地出错已经回大厅的，不能算成“放弃通过”。
                if (mc.State != LoomState.Fight || mc.Run == null || mc.Run.Def != p || mc.CurrentBoss != b || mc.FightSerial != fightNo)
                {
                    row.Note += (row.Note.Length > 0 ? "; " : "") + $"放弃前已经不在这一场战斗里 / no longer in this fight before the abandon (state={mc.State}, boss={mc.CurrentBoss?.Id}, fail={mc.LastPantheonFail})";
                    abort = $"{b.Id} 放弃前已经不在这一场 / left the fight before the abandon";
                    row.Seconds = Time.unscaledTime - t0;
                    break;
                }
                // 测放弃流程：开战、找到、击杀、干净都不测（报告写 N/A），回没回到大厅在最后看（Codex 审查 0.9.0 第二轮 #10）。
                abandoned = true;
                row.Abandon = true;
                row.Note += (row.Note.Length > 0 ? "; " : "") + "自检在这里放弃 / abandoned here by the self-test";
                row.StartHow = "放弃，不测 / abandoned, not tested";
                mc.ReturnToLobby(AbandonReason);
                row.Seconds = Time.unscaledTime - t0;
                break;
            }
            bool hadHms = CleanWatch.HasBossHms(mc);
            yield return Brawl(mc, b, scenes, row, timeout);
            // 游戏自己的胜利转场可能已经把连战推到下一场（LastResult、CurrentBoss 都换成下一场的了）：
            // 连战往前走了、或者最后一场已经结算，也算赢了这一场（Codex 审查 0.8.0）。
            bool advanced = (mc.Run != null && mc.Run.Def == p && mc.Run.Index > i)
                || (i == p.Bosses.Count - 1 && mc.Run == null && mc.LastPantheonResult != null && mc.LastPantheonResult.Def == p);
            row.Kill = (mc.LastResult == FightResult.Won && mc.CurrentBoss == b) || advanced;
            if (!row.Kill)
            {
                row.Note += $"; 未判定胜利 / no victory (state={mc.State}, result={mc.LastResult})";
                if (mc.State == LoomState.Fight && Allowed())
                {
                    yield return Shots.Capture(shot + "-3-timeout.jpg", row.Shots);
                    mc.ReturnToLobby("self-test timeout");
                }
                abort = $"{b.Id} 没打赢 / not won";
                row.Seconds = Time.unscaledTime - t0;
                break;
            }
            row.Note += $"; 判定 / by {mc.LastWinHow}";
            carried = PlayerData.instance.health;
            // 蓝血：赢了、还没离开这一场时给 2 格，看下一场还在不在（下一场前是休息点的不看：长椅照游戏的规矩清掉）。
            // 胜利转场已经把连战推到下一场的不给（那就是在下一场里给的，测不出东西）。
            // 直接改数，不发 UPDATE BLUE HEALTH：丝之歌里那个事件是“清零”（HUD 的 Blue Health Control → GameManager.UpdateBlueHealth）。
            if (!advanced && mc.State == LoomState.Won && i < p.Bosses.Count - 1)
            {
                PlayerData.instance.healthBlue = 2;
                blueSet = 2;
            }
            // 等的时间要盖过运行时的兜底回程（ModeController.WonTimeout），不然兜底能正常走的也报卡住（Codex 审查 0.9.0 #13）。
            // 等的同时看倒下后还有没有活着的本体（和单场一样，Codex 审查 0.9.0 第二轮 #10）；胜利转场已经走了的看不到，写 N/A。
            var cleanWatch = new CleanWatch(hadHms);
            yield return WaitFor(() =>
            {
                cleanWatch.Sample(mc, b);
                return mc.State != LoomState.Won;
            }, ModeController.WonTimeout + 5f);
            row.Clean = cleanWatch.Result;
            row.Note += cleanWatch.Note;
            if (mc.LastWonFallback)
            {
                row.Note += "; 兜底回程 / left via the fallback";
            }
            row.Return = mc.State is LoomState.ToFight or LoomState.ToRest or LoomState.Rest or LoomState.Fight or LoomState.ToLobby or LoomState.Lobby;
            row.Seconds = Time.unscaledTime - t0;
            GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检 / pantheon self-test {p.Id} {i + 1}/{p.Bosses.Count} {b.Id}: entered={row.Entered} start={row.Start} found={row.Found} kill={row.Kill} clean={(row.Clean is bool c ? c.ToString() : "N/A")} next={row.Return} {row.Note}");
            WriteReport(final: false, abort: null);
            if (!row.Return)
            {
                abort = $"{b.Id} 之后没进下一步 / stuck after {b.Id} (state={mc.State})";
                break;
            }
        }
        if (carryChecks > 0)
        {
            Check("血带到下一场 / health carried", carryOk == carryChecks, $"{carryOk}/{carryChecks}");
        }
        if (crestChecks > 0)
        {
            Check("休息点换的纹章带到之后各场 / crest from the rest stop kept", crestOk == crestChecks, $"{expectCrest}：{crestOk}/{crestChecks}");
        }
        if (gearChecks > 0)
        {
            Check("配装只在开殿、休息点变 / loadout only changes at the start and rest stops", gearOk == gearChecks,
                $"{gearOk}/{gearChecks}{(gearBad.Length > 0 ? "，不一样的 / differs at " + gearBad : "")}");
        }
        if (blueChecks > 0)
        {
            Check("蓝血带到下一场 / blue health carried", blueOk == blueChecks, $"{blueOk}/{blueChecks}");
        }
        if (heroChecks > 0)
        {
            Check("每场进场时主角状态正常 / hero state normal at each fight", heroOk == heroChecks, $"{heroOk}/{heroChecks}");
        }
        if (abort == null && p.Bosses.Count > 0)
        {
            // 从门直接进的第一场上面那项不查（i > 0）。前作的门转场前设了“进场不接输入”，0.10.6 以前没清，第一场落地后一直动不了。
            float first = ArrivalWatch.ControlAfter(p.Bosses[0].Id);
            Check("第一场落地后能操作 / control at the first fight", first >= 0f,
                $"{p.Bosses[0].Id}：落地后 / after landing {(float.IsNaN(first) ? "没记到 / not measured" : first >= 0f ? first.ToString("0.00") + " 秒 / s" : $"{ArrivalWatch.Window:0} 秒内一直没有 / none within {ArrivalWatch.Window:0} s")}");
        }

        // 4. 回到门前：结算（完成时）、记录、束缚解开。
        if (abort == null)
        {
            yield return WaitFor(() => mc.State == LoomState.Lobby && (hk1 ? Lobby.Ready : Sanctum.Built), 90f);
            Check("回到大厅 / back in the lobby", mc.State == LoomState.Lobby, $"state={mc.State}");
            if (expectCrest != null)
            {
                // 连战结束，配装和别的改动一样回到开殿前（RunGear 的约定）。
                Check("回大厅纹章变回开殿前的 / crest back to the pre-run one", PlayerData.instance.CurrentCrestID == crestAtStart,
                    $"{PlayerData.instance.CurrentCrestID}（开殿前 / before the run {crestAtStart}）");
            }
            yield return Shots.Capture(Path.Combine(shotDir, "79-door-arrive.jpg"), junk);
            LogDoorView("arrive");
            // 放弃的那一场：回到了大厅、而且连战是因为这次自检放弃作废的（场次和原因都对得上）才算这一行通过
            // （不再预先填 PASS，Codex 审查 0.9.0 第二轮 #10、第三轮 #4）。
            string expectedFail = $"{p.Id}@{abortAt}: {AbandonReason}";
            foreach (Row ar in rows.Where(r => r.Abandon))
            {
                ar.Return = mc.State == LoomState.Lobby && mc.LastPantheonFail == expectedFail;
            }
            Check("束缚解开 / bindings cleared", Bindings.Active == Binding.None && mc.Run == null, Bindings.Describe(Bindings.Active));
            if (abandoned)
            {
                Check("作废记下来了 / failed run noted", mc.LastPantheonFail == $"{p.Id}@{abortAt}: {AbandonReason}", mc.LastPantheonFail);
                yield return WaitFor(LobbyReady, 20f);
            }
            else
            {
                Check("结算数据 / result data", mc.LastPantheonResult != null && mc.LastPantheonResult.Def == p && mc.LastPantheonResult.Bindings == bindings && mc.LastPantheonResult.Assisted,
                    mc.LastPantheonResult == null ? "null" : $"{L.Time(mc.LastPantheonResult.Seconds)}，{(mc.LastPantheonResult.Damaged ? "受过伤 / damaged" : "无伤 / no hits")}，assisted={mc.LastPantheonResult.Assisted}");
                yield return WaitFor(() => LoomUI.Instance != null && LoomUI.Instance.ResultBusy, 30f);
                bool shown = LoomUI.Instance != null && LoomUI.Instance.ResultBusy;
                Check("结算弹出 / result shown", shown, LoomUI.Instance != null && LoomUI.Instance.ResultNative ? "原版界面 / native" : "简易 / fallback");
                if (shown)
                {
                    yield return Wait(1.5f);
                    yield return Shots.Capture(Path.Combine(shotDir, "80-result-mid.jpg"), junk);
                    yield return WaitFor(() => PantheonResultScreen.Waiting || !LoomUI.Instance!.ResultNative, 20f);
                    yield return Shots.Capture(Path.Combine(shotDir, "81-result.jpg"), junk);
                    Check("结算放完 / result sequence finished", !LoomUI.Instance!.ResultNative || PantheonResultScreen.Waiting, $"计时 / timer {PantheonResultScreen.TimerText}");
                    PantheonResultScreen.SkipForTest = true;
                    yield return WaitFor(() => !LoomUI.Instance.ResultBusy, 10f);
                    Check("结算关掉 / result closed", !LoomUI.Instance.ResultBusy);
                }
                yield return WaitFor(LobbyReady, 10f);
            }
            if (hk1)
            {
                // 前作的门：回大厅时从这扇门前的回程门进来（ModeController 的 returnStatue = DoorSpot），死亡复活点也放在那里。
                (string Scene, string Gate)? want = Hk1Lobby.ReturnTo(ModeController.DoorSpot(p.Id));
                string here = GameManager.instance.GetSceneNameString();
                string marker = Hk1Lobby.DeathReturnPrefix + ModeController.DoorSpot(p.Id);
                GameObject? m = want != null && here == want.Value.Scene
                    ? UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == marker) : null;
                float dm = m != null ? Vector2.Distance(m.transform.position, HeroController.instance.transform.position) : -1f;
                Check("站回门前 / back at the door", dm >= 0f && dm < 4f, $"{here}，要 / want {want?.Scene}@{want?.Gate}，离门 / from the door {dm:0.0}");
            }
            else
            {
                PantheonDoor? back = Sanctum.FindDoor(p.Id);
                float hx = HeroController.instance.transform.position.x;
                Check("站回门前 / back at the door", back != null && Mathf.Abs(hx - (back.X - 3.6f * back.Size)) < 1.5f, back != null ? $"x={hx:0.0}，门 / door {back.X:0.0}" : "没有门 / no door");
            }
            if (mc.State == LoomState.Lobby)
            {
                // 结算界面关掉、人站回门前以后才测：结算期间人本来就不能动（0.10.5 第一次 p1：放在结算弹出之前，
                // 测的时候结算还拿着操作，判“没站稳、不能操作”，刚体和重力都正常）。站回门前的检查要在这之前，落地检查会把人抬高。
                yield return WaitFor(LobbyReady, 30f);
                var fall = new HeroCheck.Result();
                yield return Beating(HeroCheck.Fall(fall));
                Check("回大厅能正常落地 / hero falls normally back in the lobby", fall.Ok, fall.Detail);
            }
            yield return Shots.Capture(Path.Combine(shotDir, "90-door-after.jpg"), junk);
            LogDoorView("after");
            yield return Wait(2f);
            yield return Shots.Capture(Path.Combine(shotDir, "91-door-after-2s.jpg"), junk);
            yield return Wait(3f);
            yield return Shots.Capture(Path.Combine(shotDir, "92-door-after-5s.jpg"), junk);
            LogDoorView("after 5s");
            PantheonRecord after = GodhomePlugin.Instance.Records.Pantheon(p.Id);
            Check("调试辅助不记录 / assisted run not recorded", after.Attempts == attemptsBefore && after.Clears == clearsBefore, $"尝试 / attempts {attemptsBefore}→{after.Attempts}，完成 / clears {clearsBefore}→{after.Clears}");
        }
        else if (mc.Run != null && Allowed())
        {
            mc.ReturnToLobby("self-test pantheon aborted");
        }

        if (abort == null && mc.State == LoomState.Lobby && Allowed())
        {
            yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
            mc.SaveNow("self-test end / 自检结束");
            yield return WaitFor(() => mc.LastSaveResult != null, 60f);
            if (mc.LastSaveResult != true)
            {
                abort = $"结束时存档没有成功 / the final save did not succeed ({(mc.LastSaveResult == null ? "timeout" : "failed")})";
            }
        }
        else if (abort == null)
        {
            abort = $"结束时不在大厅 / not in the lobby at the end (state={mc.State})";
        }
        extraRest = null;
        bool checksOk = extra.All(l => l.StartsWith("PASS", StringComparison.Ordinal));
        WriteReport(final: true, abort: abort);
        SelfTestStatus = abort == null && checksOk ? "完成 / done" : "中止或有失败 / aborted or failed";
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿自检结束 / pantheon self-test finished: 场次 / fights {rows.Count(Passed)}/{rows.Count}，检查 / checks {extra.Count(l => l.StartsWith("PASS", StringComparison.Ordinal))}/{extra.Count}{(abort != null ? $"，中止 / aborted: {abort}" : "")}");
        ArrivalWatch.Summary();
        bool quit = abort == null && GodhomeConfig.DebugQuitAfterSelfTest.Value;
        SelfTestRunning = false;
        Hk1Battle.SetShotDir(null);
        selfTestRoutine = null;
        if (quit)
        {
            yield return new WaitForSecondsRealtime(8f);
            if (myRun == selfTestRun && !SelfTestRunning && Allowed() && GodhomeConfig.DebugQuitAfterSelfTest.Value)
            {
                GodhomePlugin.Log.LogInfo("[Godhome] 自检后退出游戏 / quitting after self-test");
                Application.Quit();
            }
        }
    }

    private void WriteReport(bool final, string? abort)
    {
        try
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "Godhome");
            Directory.CreateDirectory(dir);
            static string P(bool ok) => ok ? "PASS" : "FAIL";
            var sb = new StringBuilder();
            sb.AppendLine($"# [Godhome] 自检结果 / Self-test {(final ? "" : "(进行中 / running)")}");
            sb.AppendLine();
            sb.AppendLine($"- 时间 / time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- 版本 / version: {GodhomePlugin.Version}");
            sb.AppendLine($"- 存档槽 / slot: {GameManager.UnsafeInstance?.profileID}");
            int pass = rows.Count(Passed);
            sb.AppendLine($"- 全部通过 / all pass: {pass}/{rows.Count}（计划 / planned {planned}）");
            // 进场挨打不算失败（自检开着无敌），但要写进报告，不能只在日志里（Codex 审计 0.10.3 第 6.4 节）。
            IReadOnlyList<string> arrival = ArrivalWatch.Flagged;
            sb.AppendLine(arrival.Count == 0
                ? $"- 进场检查 / arrival: 没有场次进场 {ArrivalWatch.Window:0} 秒内碰到伤害 / no damage on arrival"
                : $"- 进场检查 / arrival: **警告 / WARN** {arrival.Count} 场进场 {ArrivalWatch.Window:0} 秒内碰到伤害，其中 {ArrivalWatch.FlaggedBeforeControl} 场在恢复操作前（玩家躲不开，要改落点）/ {arrival.Count} fights hit on arrival, {ArrivalWatch.FlaggedBeforeControl} before control（见下面 / see below）");
            if (abort != null)
            {
                sb.AppendLine($"- 中止 / aborted: {abort}");
            }
            sb.AppendLine();
            sb.AppendLine("- 做法 / method: 从入场门 door_dreamEnter 进原场景（加载时已改成神居版：封出口、清交互、表里的 FSM 补丁、BossSceneController 判胜）；自检不替玩家触发、不用兜底击杀；每击最多四分之一血。截图在 shots/" + reportStamp);
            sb.AppendLine();
            sb.AppendLine("| # | Boss | id | 进场 | 自己开战 | 找到 Boss | 击杀判定 | 打完干净 | 回圣所 | 落地 | 场地 | 开战方式 | 用时(s) | 截图 | 备注 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                static string C(string s) => s.Replace("|", "/");
                // 没测的写 N/A（放弃的那一场只测进场和回大厅；没看“打完干净”的也是），不写 PASS。
                string T(bool v) => r.Abandon ? "N/A" : P(v);
                string clean = r.Abandon || r.Clean == null ? "N/A" : P(r.Clean.Value);
                string fall = r.Fall == null ? "N/A" : P(r.Fall.Value);
                sb.AppendLine($"| {i + 1} | {r.Boss.NameZh} | {r.Boss.Id} | {P(r.Entered)} | {T(r.Start)} | {T(r.Found)} | {T(r.Kill)} | {clean} | {P(r.Return)} | {fall} | {C(r.Entry)} | {C(r.StartHow)} | {r.Seconds:0} | {r.Shots.Count} | {C(r.Note)} |");
            }
            if (extra.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 万神殿 / pantheon");
                sb.AppendLine();
                foreach (string l in extra)
                {
                    sb.AppendLine("- " + l.Replace("|", "/"));
                }
            }
            if (resourceLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## 资源 / resources（{(resourceFail ? "有 FAIL / has failures" : "全部 PASS / all pass")}）");
                sb.AppendLine();
                foreach (string l in resourceLines)
                {
                    sb.AppendLine(l.Replace("|", "/"));
                }
            }
            if (arrival.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 进场挨打 / arrival hits");
                sb.AppendLine();
                foreach (string l in arrival)
                {
                    sb.AppendLine("- " + l.Replace("|", "/"));
                }
            }
            string text = sb.ToString();
            File.WriteAllText(Path.Combine(dir, "selftest-latest.md"), text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, $"selftest-{reportStamp}.md"), text, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 写自检报告失败 / failed to write report: {e}");
        }
    }
}
