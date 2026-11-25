using System;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 界面：黑幕（搭建、传送时遮住画面）和雕像的挑战面板。界面上不画别的文字
/// （名牌、提示条、战斗计时、调试行、织忆名录都去掉了）。
/// </summary>
internal sealed class LoomUI : MonoBehaviour
{
    private const float RefHeight = 1080f;

    internal static LoomUI? Instance { get; private set; }

    private bool legacyInputOk = true;

    private Font? font;
    private GUIStyle? textStyle;
    private GUIStyle? rowStyle;
    private GUIStyle? rowSelStyle;
    private GUIStyle? titleStyle;
    private GUIStyle? cancelStyle;
    private Texture2D? panelTex;
    private Texture2D? selTex;
    private Texture2D? coverTex;

    // 全屏黑幕（圣所搭建、丝门传送时遮住画面）。超过 CoverWatchdog 秒没人动它就自己掀开，免得卡黑屏；
    // 拉下黑幕的人可以给一个 hold：它返回 true（比如搭建任务还有效）就不揭，但最多挡 CoverCap 秒（Codex 审查 v0.6.0 #7）。
    private const float CoverWatchdog = 12f;
    private const float CoverCap = 60f;
    private float coverFrom;
    private float coverTo;
    private float coverStart;
    private float coverDur;
    private float coverSetAt;
    private Func<bool>? coverHold;

    private void Awake() => Instance = this;

    // ───────────────────────────── 黑幕 ─────────────────────────────

    /// <summary>立刻把黑幕设成这个不透明度（1 = 全黑）。hold 见上。</summary>
    internal void Cover(float alpha, Func<bool>? hold = null)
    {
        coverFrom = coverTo = Mathf.Clamp01(alpha);
        coverDur = 0f;
        coverSetAt = Time.unscaledTime;
        coverHold = coverTo > 0f ? hold : null;
    }

    /// <summary>黑幕在 seconds 秒内渐变到 target（不受暂停和时间缩放影响）。</summary>
    internal void FadeCover(float target, float seconds, Func<bool>? hold = null)
    {
        float now = Time.unscaledTime;
        coverFrom = CoverNow();
        coverTo = Mathf.Clamp01(target);
        coverStart = now;
        coverDur = Mathf.Max(0.01f, seconds);
        coverSetAt = now;
        coverHold = coverTo > 0f ? hold : null;
    }

    /// <summary>直接掀掉黑幕（重置、回标题）。</summary>
    internal void ClearCover() => Cover(0f);

    internal float CoverAlpha => CoverNow();

    private bool Held()
    {
        try
        {
            return coverHold != null && coverHold();
        }
        catch
        {
            return false;
        }
    }

    private float CoverNow()
    {
        float now = Time.unscaledTime;
        float a = coverDur <= 0f ? coverTo : Mathf.Lerp(coverFrom, coverTo, Mathf.Clamp01((now - coverStart) / coverDur));
        float held = now - coverSetAt;
        if (coverTo > 0f && (held > CoverCap || (held > CoverWatchdog && !Held())))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 黑幕太久没掀开，自动淡出 / cover watchdog: fading out after {held:0} s");
            coverHold = null;
            coverFrom = a;
            coverTo = 0f;
            coverStart = now;
            coverDur = 0.5f;
            coverSetAt = now;
        }
        return a;
    }

    // ───────────────────────────── 挑战面板（雕像） ─────────────────────────────

    // 挑战面板的状态单独记（Codex 审查 v0.5.0 #2）：雕像可能随大厅一起被销毁，
    // 交还暂停、鼠标、交互不能依赖它还活着；disablePause 只还给打开面板时的那份存档数据。
    private bool challengeOpen;
    private Statue? challengeStatue;
    private string challengeId = "";
    private BossDef? challengeBoss;
    private PlayerData? challengePd;
    private bool challengePausePrev;
    private int challengeFrame;
    private int tierSel;
    // 雕像收走控制时的角色和 ControlVersion：雕像先没了也能按游戏的规则交还（Codex 审查 v0.7.0 #7）。
    private HeroController? challengeHero;
    private int challengeControl;

    // 万神殿的门：同一套挑战面板流程（打开、淡出、交还、开始），面板换成万神殿的（PantheonMenu），选的是束缚。
    private PantheonDoor? challengeDoor;
    private PantheonDef? challengePantheon;
    private Binding bindSel;
    private int panRow;

    /// <summary>这次打开的是万神殿的面板（不是雕像的）。</summary>
    private bool PantheonPanel => challengePantheon != null;

    // 挑战面板用游戏自己的界面（ChallengeMenu，原版样式）；搭不出来才退回下面的简易面板。
    private bool nativeOpen;
    private int nativeSession;

    // 原版面板正在淡出：暂停、交互、操作都还扣着，淡出完、界面输入还原以后才交还（再开战）（Codex 审查 v0.7.0 #1 #2）。
    private bool challengeClosing;
    private float closingSince;
    private bool pendingStart;
    private const float CloseTimeout = 3f;

    /// <summary>挑战面板是游戏自己的界面（不是退回的简易面板）。</summary>
    internal bool ChallengeNative => challengeOpen && nativeOpen;

    /// <summary>原版面板这次打开的编号（巡视用：确认还是同一次打开）。</summary>
    internal int ChallengeSession => ChallengeNative ? nativeSession : 0;

    /// <summary>挑战面板开着。</summary>
    internal bool ChallengeOpen => challengeOpen;

    /// <summary>挑战面板开着或者还没关完（大厅的交互要等它）。</summary>
    internal bool ChallengeBusy => challengeOpen || challengeClosing;

    /// <summary>正在看的雕像（挑战面板开着、雕像还在）。</summary>
    internal Statue? Challenge => challengeOpen && challengeStatue != null ? challengeStatue : null;

    /// <summary>正在看的万神殿门（面板开着、门还在）。</summary>
    internal PantheonDoor? DoorChallenge => challengeOpen && challengeDoor != null ? challengeDoor : null;

    /// <summary>
    /// 打开万神殿的挑战面板（照前作 BossDoorChallengeUI：标题、说明、四个束缚、开始）。
    /// 门的交互已经收走了控制（和雕像一样），关面板时交还；选了“开始”就按选中的束缚开始连战。
    /// </summary>
    internal void OpenPantheon(PantheonDoor d)
    {
        if (challengeOpen || challengeClosing)
        {
            AbortChallenge("reopened");
        }
        challengeOpen = true;
        challengeStatue = null;
        challengeBoss = null;
        challengeDoor = d;
        challengePantheon = d.Def;
        challengeId = d.Def.Id;
        challengePd = PlayerData.instance;
        challengeFrame = Time.frameCount;
        challengeHero = HeroController.instance;
        challengeControl = HeroController.ControlVersion;
        bindSel = Binding.None;
        panRow = 0;
        pendingStart = false;
        challengePausePrev = challengePd.disablePause;
        challengePd.disablePause = true;
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        PantheonRecord? pr = rec.Pantheons.TryGetValue(d.Def.Id, out PantheonRecord? x) ? x : null;
        nativeSession = PantheonMenu.Show(this, d.Def, pr, b =>
        {
            bindSel = b;
            CloseChallenge(true);
        }, () => CloseChallenge(false));
        nativeOpen = nativeSession > 0;
        if (!nativeOpen)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 游戏界面做的万神殿面板打不开，退回简易面板 / native pantheon menu unavailable, using the fallback panel");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿门 / pantheon door {d.Def.Id}{(nativeOpen ? "（原版界面 / native menu）" : "")}");
    }

    /// <summary>自检用：按指定束缚关万神殿面板（start = 开始，和在面板里选一样）。</summary>
    internal void ChooseBindings(Binding b, bool start)
    {
        bindSel = b & Binding.All;
        CloseChallenge(start);
    }

    private bool MenuOpenNow => PantheonPanel ? PantheonMenu.Open && PantheonMenu.Session == nativeSession : ChallengeMenu.Open && ChallengeMenu.Session == nativeSession;

    private bool MenuHide(Action closed) => PantheonPanel ? PantheonMenu.Hide(closed) : ChallengeMenu.Hide(closed);

    internal void OpenChallenge(Statue s)
    {
        if (challengeOpen || challengeClosing)
        {
            AbortChallenge("reopened");
        }
        challengeOpen = true;
        challengeDoor = null;
        challengePantheon = null;
        challengeStatue = s;
        challengeId = s.Def.Id;
        challengeBoss = s.Current;
        challengePd = PlayerData.instance;
        challengeFrame = Time.frameCount;
        challengeHero = HeroController.instance;
        challengeControl = HeroController.ControlVersion;
        tierSel = 0;
        pendingStart = false;
        challengePausePrev = challengePd.disablePause;
        challengePd.disablePause = true;
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        BossRecord? br = rec.Bosses.TryGetValue(s.Current.Id, out BossRecord? x) ? x : null;
        int[] clears = { br?.ClearsAt(0) ?? 0, br?.ClearsAt(1) ?? 0, br?.ClearsAt(2) ?? 0 };
        nativeSession = ChallengeMenu.Show(this, s.Current, s.CurrentIsBlack, clears, t =>
        {
            tierSel = t;
            CloseChallenge(true);
        }, () => CloseChallenge(false));
        nativeOpen = nativeSession > 0;
        if (!nativeOpen)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 游戏界面做的挑战面板打不开，退回简易面板 / native challenge menu unavailable, using the fallback panel");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 雕像 / statue {s.Def.Id}: {s.Current.Id}{(nativeOpen ? "（原版界面 / native menu）" : "")}");
    }

    /// <summary>
    /// 不开战、马上关掉挑战面板（模式重置、开战前、雕像没了、离开大厅）：原版面板同步撤掉，暂停和操作马上交还。
    /// 面板没开、也没在关就什么都不做。
    /// </summary>
    internal void AbortChallenge(string why)
    {
        if (!challengeOpen && !challengeClosing)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 挑战面板关闭 / challenge panel closed ({why})");
        challengeOpen = false;
        nativeOpen = false;
        pendingStart = false;
        ChallengeMenu.ForceClose(why);
        PantheonMenu.ForceClose(why);
        FinishChallenge();
    }

    /// <summary>关掉挑战面板；start = 按选中的难度开战。原版面板等淡出完再交还、再开战。</summary>
    internal void CloseChallenge(bool start)
    {
        if (!challengeOpen)
        {
            return;
        }
        challengeOpen = false;
        pendingStart = start;
        if (nativeOpen)
        {
            nativeOpen = false;
            challengeClosing = true;
            closingSince = Time.unscaledTime;
            int id = nativeSession;
            if (MenuHide(() =>
                {
                    if (challengeClosing && nativeSession == id)
                    {
                        FinishChallenge();
                    }
                }))
            {
                return;
            }
        }
        FinishChallenge();
    }

    /// <summary>收尾：还暂停、藏鼠标、交还操作，需要的话开战。</summary>
    private void FinishChallenge()
    {
        challengeClosing = false;
        bool start = pendingStart;
        pendingStart = false;
        Statue? s = challengeStatue;
        challengeStatue = null;
        BossDef? boss = s != null && s.Current != null ? s.Current : challengeBoss;
        challengeBoss = null;
        PantheonDoor? door = challengeDoor;
        challengeDoor = null;
        PantheonDef? pantheon = challengePantheon;
        challengePantheon = null;
        if (challengePd != null && ReferenceEquals(challengePd, PlayerData.instance))
        {
            challengePd.disablePause = challengePausePrev;
        }
        challengePd = null;
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
        ReleaseControl(s, door);
        // 雕像（门）已经不在（大厅卸载了）、已经不在大厅就不开战。
        ModeController? mc = ModeController.Instance;
        if (start && pantheon != null && door != null && mc != null && mc.State == LoomState.Lobby)
        {
            mc.StartPantheon(pantheon, bindSel);
            return;
        }
        if (start && boss != null && s != null && mc != null && mc.State == LoomState.Lobby)
        {
            mc.StartFight(boss, tierSel, challengeId);
        }
    }

    /// <summary>
    /// 交还雕像（门）收走的控制：还在就走它的 EnableInteraction（游戏的 ControlVersion 规则）；
    /// 连同交互组件已经销毁，就按同一条规则自己还：同一个角色、期间没有别处收走过控制才还。
    /// </summary>
    private void ReleaseControl(Statue? s, PantheonDoor? door)
    {
        StatueInteract? it = s != null ? s.Interact : null;
        PantheonDoorInteract? di = door != null ? door.Interact : null;
        HeroController? hero = challengeHero;
        challengeHero = null;
        if (it != null)
        {
            it.Release();
            return;
        }
        if (di != null)
        {
            di.Release();
            return;
        }
        try
        {
            if (hero != null && ReferenceEquals(hero, HeroController.instance) && HeroController.ControlVersion == challengeControl && hero.controlReqlinquished)
            {
                hero.RegainControl(allowInput: false);
                GodhomePlugin.Log.LogInfo("[Godhome] 雕像已经不在，直接交还操作 / statue gone, control returned directly");
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 交还操作出错 / returning control failed: {e.Message}");
        }
    }

    /// <summary>自检用：按指定难度关面板（start = 开战，和在面板里选一样）。</summary>
    internal void ChooseTier(int tier, bool start)
    {
        tierSel = Mathf.Clamp(tier, 0, 2);
        CloseChallenge(start);
    }

    private void UpdateChallenge()
    {
        if (ModeController.Instance == null || ModeController.Instance.State != LoomState.Lobby)
        {
            AbortChallenge("left the lobby state");
            return;
        }
        if (PantheonPanel ? challengeDoor == null : challengeStatue == null)
        {
            AbortChallenge(PantheonPanel ? "door gone" : "statue gone");
            return;
        }
        if (challengeClosing)
        {
            // 淡出不到半秒；太久没关完（协程被停了）就直接收尾，免得一直扣着操作。
            if (Time.unscaledTime - closingSince > CloseTimeout)
            {
                AbortChallenge("close timed out");
            }
            return;
        }
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        if (Time.frameCount == challengeFrame)
        {
            // 打开面板的这一帧：交互键（上）还算“刚按下”，不能当成选难度。
            return;
        }
        if (nativeOpen)
        {
            if (PantheonPanel)
            {
                PantheonMenu.Tick();
            }
            else
            {
                ChallengeMenu.Tick();
            }
            if (!MenuOpenNow)
            {
                // 原版面板中途出错撤掉了：退回简易面板（还开着，操作、暂停还扣着）。
                GodhomePlugin.Log.LogWarning("[Godhome] 原版挑战面板中途关掉了，退回简易面板 / native challenge menu went away, using the fallback panel");
                nativeOpen = false;
                return;
            }
            // 上下、确认、取消都由游戏的界面自己处理；只兜一种情况：没有选中任何按钮时按返回也能关。
            HeroActions? na = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
            if (na != null && na.MenuCancel.WasPressed && UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == null)
            {
                CloseChallenge(false);
            }
            return;
        }
        // 游戏自己的按键（手柄也能用）：上下选难度，确认开战，返回取消（Codex 审查 v0.5.0 #10）。
        HeroActions? ia = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
        bool up = ia != null && ia.Up.WasPressed;
        bool down = ia != null && ia.Down.WasPressed;
        bool ok = ia != null && (ia.MenuSubmit.WasPressed || ia.Jump.WasPressed);
        bool cancel = ia != null && ia.MenuCancel.WasPressed;
        if (legacyInputOk)
        {
            try
            {
                up |= Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W);
                down |= Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S);
                ok |= Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space);
                cancel |= Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace);
            }
            catch (InvalidOperationException)
            {
                legacyInputOk = false;
            }
        }
        if (PantheonPanel)
        {
            // 简易面板：五行（四个束缚、开始），上下选，确认套上 / 解开或开始。
            if (cancel)
            {
                CloseChallenge(false);
            }
            else if (ok)
            {
                PanRow(panRow);
            }
            else if (up)
            {
                panRow = (panRow + 4) % 5;
            }
            else if (down)
            {
                panRow = (panRow + 1) % 5;
            }
            return;
        }
        if (cancel)
        {
            CloseChallenge(false);
        }
        else if (ok)
        {
            CloseChallenge(true);
        }
        else if (up)
        {
            tierSel = (tierSel + 2) % 3;
        }
        else if (down)
        {
            tierSel = (tierSel + 1) % 3;
        }
    }

    private void PanRow(int row)
    {
        panRow = row;
        if (row < 4)
        {
            bindSel ^= PantheonMenu.Order[row];
            return;
        }
        CloseChallenge(true);
    }

    private void DrawPantheon(float w, PantheonDoor d)
    {
        float pw = Mathf.Min(1100f, w - 80f);
        const float ph = 760f;
        var panel = new Rect((w - pw) / 2f, (RefHeight - ph) / 2f, pw, ph);
        GUI.DrawTexture(panel, panelTex);
        GUI.Label(new Rect(panel.x, panel.y + 18, pw, 56), d.Def.DisplayName, titleStyle);
        GUI.Label(new Rect(panel.x + 40, panel.y + 80, pw - 80, 90), $"<size=24>{d.Def.Description}</size>", rowStyle);
        for (int r = 0; r < 5; r++)
        {
            var rect = new Rect(panel.x + 40, panel.y + 180 + r * 92, pw - 80, 80);
            string label;
            if (r < 4)
            {
                Binding b = PantheonMenu.Order[r];
                bool on = (bindSel & b) != 0;
                label = $"{(on ? "<color=#f0c75a>■</color>" : "□")}　<size=32>{L.Get("BIND_" + b.ToString().ToUpperInvariant())}</size>";
            }
            else
            {
                label = $"<color=#f2f2ea><size=34>{L.Get("PAN_BEGIN")}</size></color>";
            }
            if (GUI.Button(rect, label, r == panRow ? rowSelStyle : rowStyle))
            {
                PanRow(r);
                return;
            }
        }
        if (GUI.Button(new Rect(panel.x + pw / 2f - 120f, panel.y + ph - 90f, 240f, 64f), L.Get("CH_CANCEL"), cancelStyle))
        {
            CloseChallenge(false);
        }
    }

    private void DrawChallenge(float w, Statue s)
    {
        float pw = Mathf.Min(980f, w - 80f);
        const float ph = 600f;
        var panel = new Rect((w - pw) / 2f, (RefHeight - ph) / 2f, pw, ph);
        GUI.DrawTexture(panel, panelTex);
        BossDef b = s.Current;
        GUI.Label(new Rect(panel.x, panel.y + 18, pw, 56), b.DisplayName, titleStyle);
        string[] colors = { "#f2f2ea", "#f0c75a", "#d9473c" };
        for (int t = 0; t < 3; t++)
        {
            var r = new Rect(panel.x + 40, panel.y + 120 + t * 110, pw - 80, 96);
            string label = $"<color={colors[t]}><size=34>{L.Get("TIER_" + t)}</size></color>　　{L.Get("TIER_DESC_" + t)}";
            if (GUI.Button(r, label, t == tierSel ? rowSelStyle : rowStyle))
            {
                tierSel = t;
                CloseChallenge(true);
                return;
            }
        }
        // 只用鼠标的玩家也要能离开（Codex 审查 v0.7.0 低 #1）。
        if (GUI.Button(new Rect(panel.x + pw / 2f - 120f, panel.y + ph - 96f, 240f, 64f), L.Get("CH_CANCEL"), cancelStyle))
        {
            CloseChallenge(false);
        }
    }

    private void Update()
    {
        if (challengeOpen || challengeClosing)
        {
            UpdateChallenge();
        }
        if (resultOpen)
        {
            UpdateResult();
        }
    }

    // ───────────────────────────── 万神殿结算 ─────────────────────────────

    // 结算期间收走操作、禁止暂停（和挑战面板一样只还给当时的那份存档数据、按 ControlVersion 交还）。
    private bool resultOpen;
    private bool resultNative;
    private PantheonResult? result;
    private PlayerData? resultPd;
    private bool resultPausePrev;
    private HeroController? resultHero;
    private int resultControl;
    private bool resultWasRelinquished;
    private float resultSince;

    /// <summary>结算开着（大厅的交互要等它）。</summary>
    internal bool ResultBusy => resultOpen;

    /// <summary>结算是游戏界面做的那个（不是退回的简易结算）。</summary>
    internal bool ResultNative => resultOpen && resultNative;

    /// <summary>回到门前以后弹出结算（照前作 BossDoorChallengeCompleteUI）。按任意键关掉。</summary>
    internal void ShowResult(PantheonResult r)
    {
        if (resultOpen)
        {
            AbortResult("replaced");
        }
        HeroController hero = HeroController.instance;
        if (hero == null)
        {
            return;
        }
        resultOpen = true;
        result = r;
        resultSince = Time.unscaledTime;
        // 打开前操作已经在别人手里（例如游戏让大黄蜂坐在长椅上）：不再收一次、关结算时也不替别人交还（审查）。
        // RelinquishControl 每调一次都会把 ControlVersion 加一，原来拿着操作的那方再交还时版本对不上，就交还不了（Codex 复查）。
        resultWasRelinquished = hero.controlReqlinquished;
        if (!resultWasRelinquished)
        {
            hero.RelinquishControl();
        }
        resultHero = hero;
        resultControl = HeroController.ControlVersion;
        resultPd = PlayerData.instance;
        resultPausePrev = resultPd.disablePause;
        resultPd.disablePause = true;
        resultNative = PantheonResultScreen.Show(this, r, () => FinishResult("closed / 关闭"));
        if (!resultNative)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 游戏界面做的结算打不开，退回简易结算 / native result screen unavailable, using the fallback");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿结算 / pantheon result: {r.Def.Id}，束缚 / bindings {Bindings.Describe(r.Bindings)}，用时 / time {L.Time(r.Seconds)}，{(r.Damaged ? "受过伤 / damaged" : "无伤 / no hits")}{(resultNative ? "（原版界面 / native）" : "")}");
    }

    /// <summary>不等按键、马上撤掉结算（离开大厅、模式重置）。没开着就什么都不做。</summary>
    internal void AbortResult(string why)
    {
        if (!resultOpen)
        {
            return;
        }
        PantheonResultScreen.ForceClose(why);
        FinishResult(why);
    }

    private void FinishResult(string why)
    {
        if (!resultOpen)
        {
            return;
        }
        resultOpen = false;
        resultNative = false;
        result = null;
        if (resultPd != null && ReferenceEquals(resultPd, PlayerData.instance))
        {
            resultPd.disablePause = resultPausePrev;
        }
        resultPd = null;
        HeroController? hero = resultHero;
        resultHero = null;
        try
        {
            if (hero != null && !resultWasRelinquished && ReferenceEquals(hero, HeroController.instance) && HeroController.ControlVersion == resultControl && hero.controlReqlinquished)
            {
                hero.RegainControl(allowInput: false);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 结算后交还操作出错 / returning control after the result failed: {e.Message}");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿结算结束 / pantheon result closed ({why})");
    }

    private void UpdateResult()
    {
        if (ModeController.Instance == null || ModeController.Instance.State != LoomState.Lobby)
        {
            AbortResult("left the lobby state");
            return;
        }
        if (resultNative)
        {
            if (!PantheonResultScreen.Busy)
            {
                // 原版结算中途撤掉了（不该发生）：直接收尾。
                FinishResult("native result gone / 原版结算没了");
            }
            return;
        }
        // 简易结算：1.5 秒后任意键关。
        if (Time.unscaledTime - resultSince < 1.5f)
        {
            return;
        }
        bool any = false;
        HeroActions? ia = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
        if (ia != null)
        {
            any = ia.MenuSubmit.WasPressed || ia.MenuCancel.WasPressed || ia.Jump.WasPressed || ia.Attack.WasPressed;
        }
        if (!any && legacyInputOk)
        {
            try
            {
                any = Input.anyKeyDown;
            }
            catch (InvalidOperationException)
            {
                legacyInputOk = false;
            }
        }
        if (any || PantheonResultScreen.SkipForTest)
        {
            PantheonResultScreen.SkipForTest = false;
            FinishResult("key / 按键");
        }
    }

    private void DrawResult(float w, PantheonResult r)
    {
        float pw = Mathf.Min(900f, w - 80f);
        const float ph = 520f;
        var panel = new Rect((w - pw) / 2f, (RefHeight - ph) / 2f, pw, ph);
        GUI.DrawTexture(panel, panelTex);
        GUI.Label(new Rect(panel.x, panel.y + 24, pw, 60), r.Def.DisplayName, titleStyle);
        string marks = "";
        foreach (Binding b in PantheonMenu.Order)
        {
            bool on = (r.Bindings & b) != 0;
            marks += (on ? "<color=#f0c75a>" : "<color=#555560>") + L.Get("BIND_" + b.ToString().ToUpperInvariant()) + "</color>　";
        }
        GUI.Label(new Rect(panel.x, panel.y + 150, pw, 60), "<size=34>" + marks + "</size>", titleStyle);
        int s = Mathf.RoundToInt(r.Seconds);
        GUI.Label(new Rect(panel.x, panel.y + 250, pw, 90), $"<size=64>{s / 60:00}:{s % 60:00}</size>", titleStyle);
        GUI.Label(new Rect(panel.x, panel.y + ph - 90, pw, 50), $"<size=26><color=#aaaaaa>{L.Get("PAN_RESULT_KEY")}</color></size>", titleStyle);
    }

    // ───────────────────────────── 绘制 ─────────────────────────────

    private void EnsureStyles()
    {
        if (textStyle != null)
        {
            return;
        }
        try
        {
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, 28);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 系统字体加载失败 / OS font failed: {e.Message}");
        }
        panelTex = Solid(new Color(0.04f, 0.03f, 0.05f, 0.86f));
        coverTex = Solid(Color.black);
        selTex = Solid(new Color(0.85f, 0.78f, 0.62f, 0.22f));
        textStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 28, richText = true, wordWrap = false };
        textStyle.normal.textColor = new Color(0.95f, 0.92f, 0.86f);
        titleStyle = new GUIStyle(textStyle) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
        rowStyle = new GUIStyle(textStyle) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(18, 18, 4, 4) };
        rowStyle.hover.background = selTex;
        rowStyle.hover.textColor = Color.white;
        rowSelStyle = new GUIStyle(rowStyle);
        rowSelStyle.normal.background = selTex;
        rowSelStyle.normal.textColor = Color.white;
        cancelStyle = new GUIStyle(rowStyle) { alignment = TextAnchor.MiddleCenter };
    }

    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    private void OnGUI()
    {
        // 界面上不画任何文字（名牌、提示条、计时、调试行都去掉了）：只有黑幕和挑战面板。
        float cover = CoverNow();
        // 暂停时不画黑幕：OnGUI 画在所有 uGUI 画布上面，会把暂停菜单整个盖住（审查）。黑幕本身的计时照旧。
        if (GameManager.UnsafeInstance is GameManager gm && gm.IsGamePaused())
        {
            cover = 0f;
        }
        bool fallbackPanel = (Challenge != null || DoorChallenge != null) && !nativeOpen;
        bool fallbackResult = resultOpen && !resultNative && result != null;
        if (!fallbackPanel && !fallbackResult && cover <= 0.001f)
        {
            return;
        }
        EnsureStyles();
        if (cover > 0.001f && coverTex != null)
        {
            Color oc = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, cover);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), coverTex);
            GUI.color = oc;
        }
        if (fallbackPanel || fallbackResult)
        {
            float scale = Screen.height / RefHeight;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            if (Challenge != null && !nativeOpen)
            {
                DrawChallenge(Screen.width / scale, Challenge);
            }
            else if (DoorChallenge != null && !nativeOpen)
            {
                DrawPantheon(Screen.width / scale, DoorChallenge);
            }
            else if (fallbackResult)
            {
                DrawResult(Screen.width / scale, result!);
            }
            GUI.matrix = old;
        }
    }
}
