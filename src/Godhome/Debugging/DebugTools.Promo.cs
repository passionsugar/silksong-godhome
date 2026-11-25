using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：宣传片导演（Debug.Promo，默认关）。按配置的段落顺序替玩家走一遍，同时用 PromoRecorder 录像：
/// - menu：标题画面 → 存档列表 → 空的槽（Safety.EnabledSlots 的第一个，必须是空槽）→“选择模式”→ 织忆圣所 → 新建神居存档、进前厅；
/// - tools：打开物品栏的工具页（一开始就有全部工具和纹章）；
/// - atrium：在前厅走过几扇门，打开一扇门的挑战界面、套两个束缚、关上；
/// - hall：走门去手办屋，沿着雕像走，敲拉杆，在一尊雕像前打开挑战面板、依次看三档难度、开战；
/// - fights：Debug.PromoBosses 里的 Boss 依次从各自的雕像开战；
/// - pantheon：走到门前，跑万神殿自检（Debug.SelfTestPantheon 等配置），每场让大黄蜂真打一会儿；
/// - roof：去屋顶看第五扇门。
/// 每场战斗先由 PromoBot 替玩家真打 Debug.PromoBotSeconds 秒，之后照自检的办法用调试伤害收尾。跑完退出游戏。
/// 只在 Debug.Enabled、存档槽在 Safety.EnabledSlots 里时动手（menu 段只用空槽，绝不覆盖已有存档）。
/// </summary>
internal sealed partial class DebugTools
{
    internal static bool PromoRunning { get; private set; }

    private bool promoStarted;
    private float promoSince = -1f;
    private bool promoSlotChecking;
    private bool? promoSlotExists;
    private float promoSlotAskedAt;
    private float promoLogAt;

    /// <summary>Brawl 在这个时刻之前不用调试伤害（宣传片：让大黄蜂先真打一会儿）。</summary>
    private float promoHoldUntil;

    private static readonly List<string> NoPromoSteps = new();

    /// <summary>配置的段落。没配（默认）时直接返回空表，调试开着时也不必每帧拆字符串（Codex 审查）；返回的表只读。</summary>
    private static List<string> PromoSteps()
    {
        string v = GodhomeConfig.DebugPromo.Value ?? "";
        if (string.IsNullOrWhiteSpace(v))
        {
            return NoPromoSteps;
        }
        return v.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>menu 段用的槽：Safety.EnabledSlots 里的第一个（写 all 不算）。</summary>
    private static int PromoSlot()
    {
        foreach (string s in (GodhomeConfig.EnabledSlots.Value ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(s, out int n) && GodhomeConfig.IsSlotEnabled(n))
            {
                return n;
            }
        }
        return 0;
    }

    private void TickPromo(GameManager gm)
    {
        if (promoStarted)
        {
            return;
        }
        List<string> steps = PromoSteps();
        if (steps.Count == 0)
        {
            return;
        }
        if (steps[0] == "menu")
        {
            int slot = PromoSlot();
            if (slot <= 0)
            {
                promoStarted = true;
                GodhomePlugin.Log.LogWarning("[Godhome] 宣传片：Safety.EnabledSlots 里没有可用的槽，不录 / promo: no enabled slot, not recording");
                return;
            }
            UIManager? ui = UIManager.instance;
            bool title = gm.IsMenuScene() && ui != null && ui.menuState == MainMenuState.MAIN_MENU;
            if (Time.unscaledTime >= promoLogAt)
            {
                promoLogAt = Time.unscaledTime + 5f;
                GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片等标题画面 / promo waiting for the title: menuScene={gm.IsMenuScene()} menuState={(ui != null ? ui.menuState.ToString() : "-")} gameState={gm.GameState}");
            }
            if (!title)
            {
                promoSince = -1f;
                return;
            }
            if (promoSince < 0f)
            {
                promoSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - promoSince < 6f)
            {
                return;
            }
            if (promoSlotExists == null)
            {
                if (!promoSlotChecking)
                {
                    promoSlotChecking = true;
                    gm.HasSaveFile(slot, exists => promoSlotExists = exists);
                }
                return;
            }
            if (promoSlotExists == true)
            {
                // 槽不是空的：在标题画面再等一会儿（录制脚本可能正在把这个槽的存档挪走），每 3 秒再问一次；3 分钟还不空就不录。
                if (Time.unscaledTime - promoSince < 180f)
                {
                    if (Time.unscaledTime - promoSlotAskedAt > 3f)
                    {
                        GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片：槽 {slot} 还不是空的，等 / promo: slot {slot} not empty yet, waiting");
                        promoSlotAskedAt = Time.unscaledTime;
                        promoSlotExists = null;
                        promoSlotChecking = false;
                    }
                    return;
                }
                promoStarted = true;
                GodhomePlugin.Log.LogWarning($"[Godhome] 宣传片：槽 {slot} 不是空的，menu 段不新建存档，不录 / promo: slot {slot} is not empty; refusing the menu segment");
                return;
            }
            promoStarted = true;
            StartCoroutine(PromoRun(steps, slot));
            return;
        }
        bool playing = !gm.IsMenuScene() && HeroController.SilentInstance != null && gm.GameState == GameState.PLAYING;
        if (autoLoadedSlot > 0 && gm.profileID == autoLoadedSlot && playing && Allowed() && ModeController.Instance != null && ModeController.Instance.InMode)
        {
            if (promoSince < 0f)
            {
                promoSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - promoSince > 4f)
            {
                promoStarted = true;
                StartCoroutine(PromoRun(steps, gm.profileID));
            }
        }
        else
        {
            promoSince = -1f;
        }
    }

    /// <summary>导演的协程停了（异常）：别一直录下去。</summary>
    private void PromoWatchdog()
    {
        if (PromoRunning && Time.frameCount - selfTestBeatFrame > 3600)
        {
            GodhomePlugin.Log.LogWarning("[Godhome] 宣传片导演 3600 帧没有心跳，停止录像 / promo director stopped beating, ending the recording");
            PromoRunning = false;
            PromoBot.On = false;
            Puppet.Clear();
            Puppet.Active = false;
            SelfTestRunning = false;
            if (PromoRecorder.Instance != null)
            {
                StartCoroutine(PromoQuitAfter(PromoRecorder.Instance.End()));
            }
        }
    }

    private IEnumerator PromoQuitAfter(IEnumerator end)
    {
        yield return end;
        yield return new WaitForSecondsRealtime(3f);
        Application.Quit();
    }

    private bool PromoOk(int slot) => DebugOn && GameManager.instance != null && GameManager.instance.profileID == slot && GodhomeConfig.IsSlotEnabled(slot)
        && ModeController.Instance != null && ModeController.Instance.InMode;

    private static string PScene() => GameManager.instance != null ? GameManager.instance.GetSceneNameString() : "";

    private static bool PHubReady()
    {
        ModeController? mc = ModeController.Instance;
        HeroController? h = HeroController.SilentInstance;
        return mc != null && mc.State == LoomState.Lobby && Lobby.Ready && LoomUI.Instance != null && LoomUI.Instance.CoverAlpha < 0.02f
            && h != null && h.acceptingInput && !h.controlReqlinquished && GameManager.instance != null && !GameManager.instance.IsInSceneTransition;
    }

    private static void Mark(string ev, string detail = "") => PromoRecorder.Mark(ev, detail);

    private IEnumerator PromoRun(List<string> steps, int slot)
    {
        PromoRunning = true;
        Beat();
        PromoRecorder rec = PromoRecorder.Ensure(gameObject);
        if (gameObject.GetComponent<PromoBot>() == null)
        {
            gameObject.AddComponent<PromoBot>();
        }
        string name = string.Join("-", steps);
        if (!rec.Begin(name, out string err))
        {
            GodhomePlugin.Log.LogError($"[Godhome] 宣传片：录像开不了，不跑 / promo: recording could not start: {err}");
            PromoRunning = false;
            yield break;
        }
        Puppet.Active = true;
        foreach (string step in steps)
        {
            Beat();
            if (step != "menu" && !PromoOk(slot))
            {
                Mark("abort", $"slot/debug not ok before {step} (profile {GameManager.instance?.profileID}, inMode {ModeController.Instance?.InMode})");
                break;
            }
            if (step != "menu")
            {
                SelfTestRunning = true;
            }
            Mark("step", step);
            switch (step)
            {
                case "menu":
                    yield return PromoMenu(slot);
                    break;
                case "tools":
                    yield return PromoTools();
                    break;
                case "atrium":
                    yield return PromoAtrium();
                    break;
                case "hall":
                    yield return PromoHall();
                    break;
                case "fights":
                    yield return PromoFights();
                    break;
                case "pantheon":
                    yield return PromoPantheon();
                    break;
                case "roof":
                    yield return PromoRoof();
                    break;
                default:
                    Mark("unknown-step", step);
                    break;
            }
            Puppet.Clear();
            Mark("step-done", step);
        }
        PromoBot.On = false;
        Puppet.Clear();
        Puppet.Active = false;
        SelfTestRunning = false;
        Beat();
        yield return rec.End();
        PromoRunning = false;
        GodhomePlugin.Log.LogInfo($"[Godhome] 宣传片录完，退出游戏 / promo finished, quitting: {rec.BaseName}");
        yield return new WaitForSecondsRealtime(3f);
        Application.Quit();
    }

    // ───────────────────────────── 走路 ─────────────────────────────

    /// <summary>按住左右走到 x（卡住了跳一下，跳三次还卡就放弃）。</summary>
    private IEnumerator PromoWalkTo(float x, float timeout, float tol = 0.35f)
    {
        HeroActions? a = Puppet.Actions;
        HeroController? h = HeroController.SilentInstance;
        if (a == null || h == null)
        {
            yield break;
        }
        float end = Time.unscaledTime + timeout;
        float lastX = h.transform.position.x;
        float startY = h.transform.position.y;
        string scene = PScene();
        float moved = Time.unscaledTime;
        int jumps = 0;
        while (Time.unscaledTime < end && h != null)
        {
            Beat();
            if (PScene() != scene || h.transform.position.y < startY - 6f)
            {
                Mark("walk-left-area", $"scene={PScene()} y={h.transform.position.y:0.0} (start {startY:0.0})");
                break;
            }
            if (!h.acceptingInput || h.controlReqlinquished)
            {
                moved = Time.unscaledTime;
                yield return null;
                continue;
            }
            float dx = x - h.transform.position.x;
            if (Mathf.Abs(dx) < tol)
            {
                break;
            }
            Puppet.Hold(a.Right, dx > 0f);
            Puppet.Hold(a.Left, dx < 0f);
            if (Mathf.Abs(h.transform.position.x - lastX) > 0.05f)
            {
                lastX = h.transform.position.x;
                moved = Time.unscaledTime;
            }
            else if (Time.unscaledTime - moved > 0.8f)
            {
                if (++jumps > 3)
                {
                    Mark("walk-stuck", $"x={h.transform.position.x:0.0} target={x:0.0}");
                    break;
                }
                Puppet.Tap(a.Jump, 14);
                moved = Time.unscaledTime;
            }
            yield return null;
        }
        Puppet.Hold(a.Right, false);
        Puppet.Hold(a.Left, false);
        yield return null;
    }

    /// <summary>直接站到 x 处的地面上（往下找地面；剪辑时这里是切镜头）。</summary>
    private IEnumerator PromoStand(float x, float yHint, bool faceRight = true)
    {
        HeroController? h = HeroController.SilentInstance;
        if (h == null)
        {
            yield break;
        }
        RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, yHint + 3f), Vector2.down, 25f, 1 << 8);
        float floor = hit.collider != null ? hit.point.y : yHint;
        Sanctum.StandAt(h, x, floor);
        if (faceRight)
        {
            h.FaceRight();
        }
        else
        {
            h.FaceLeft();
        }
        Mark("stand", $"x={x:0.0} floor={floor:0.0}{(hit.collider != null ? "" : " (no ground hit)")}");
        yield return Wait(0.8f);
    }

    /// <summary>朝一个方向走 seconds 秒（卡住了跳一下）。</summary>
    private IEnumerator PromoWalkFor(int dir, float seconds)
    {
        HeroController? h = HeroController.SilentInstance;
        if (h == null)
        {
            yield break;
        }
        yield return PromoWalkTo(h.transform.position.x + dir * 400f, seconds, 0.1f);
    }

    /// <summary>在门、雕像前照玩家按上（npc_control 进了 In Range 才按）；没打开就按 FSM 发 UP PRESSED 兜底。</summary>
    private IEnumerator PromoPressUp(GameObject? inspect, Func<bool> opened)
    {
        HeroActions? a = Puppet.Actions;
        PlayMakerFSM? fsm = inspect != null ? inspect.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "npc_control") : null;
        if (fsm != null)
        {
            yield return WaitFor(() => fsm.ActiveStateName == "In Range", 3f);
        }
        yield return Wait(0.5f);
        Puppet.Tap(a?.Up, 4);
        yield return WaitFor(opened, 2.5f);
        if (!opened() && fsm != null)
        {
            Mark("press-up-fallback", fsm.ActiveStateName);
            fsm.SendEvent("UP PRESSED");
            yield return WaitFor(opened, 4f);
        }
    }

    // ───────────────────────────── 段落 ─────────────────────────────

    private IEnumerator PromoMenu(int slot)
    {
        UIManager ui = UIManager.instance;
        GameManager gm = GameManager.instance;
        ModeController mc = ModeController.Instance!;
        Mark("menu-title");
        yield return Wait(4f);
        ui.UIGoToProfileMenu();
        Mark("menu-profiles");
        yield return Wait(3.5f);
        UnityEngine.UI.SaveSlotButton? button = slot switch
        {
            1 => ui.slotOne,
            2 => ui.slotTwo,
            3 => ui.slotThree,
            4 => ui.slotFour,
            _ => null,
        };
        if (button == null)
        {
            Mark("menu-abort", "no slot button");
            yield break;
        }
        // 提交前再问一次这个槽是不是空的（绝不覆盖已有存档）。
        bool? exists = null;
        gm.HasSaveFile(slot, e => exists = e);
        yield return WaitFor(() => exists != null, 10f);
        if (exists != false)
        {
            Mark("menu-abort", $"slot {slot} exists={exists}");
            yield break;
        }
        EventSystem.current.SetSelectedGameObject(button.gameObject);
        Mark("menu-slot-selected", slot.ToString());
        yield return Wait(1.5f);
        ExecuteEvents.Execute(button.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        Mark("menu-slot-submit", slot.ToString());
        yield return WaitFor(() => ui.menuState == MainMenuState.PLAY_MODE_MENU, 10f);
        yield return Wait(1.8f);
        if (gm.profileID != slot || ui.menuState != MainMenuState.PLAY_MODE_MENU)
        {
            Mark("menu-abort", $"profile {gm.profileID}, menu {ui.menuState}");
            yield break;
        }
        Mark("menu-playmode");
        Transform? gg = ui.playModeMenuScreen != null ? ui.playModeMenuScreen.transform.Find("Content/GGButton") : null;
        if (gg == null && ui.playModeMenuScreen != null)
        {
            gg = ui.playModeMenuScreen.GetComponentsInChildren<StartGameEventTrigger>(true)
                .FirstOrDefault(t => Traverse.Create(t).Field("bossRush").GetValue<bool>())?.transform;
        }
        if (gg == null)
        {
            Mark("menu-abort", "no GGButton");
            yield break;
        }
        EventSystem.current.SetSelectedGameObject(gg.gameObject);
        Mark("menu-gg-selected");
        yield return Wait(3.5f);
        ExecuteEvents.Execute(gg.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        Mark("menu-gg-submit");
        yield return WaitFor(() => !gm.IsMenuScene() && HeroController.SilentInstance != null && gm.GameState == GameState.PLAYING && gm.profileID == slot, 180f);
        Mark("menu-in-game", PScene());
        SelfTestRunning = Allowed();
        yield return WaitFor(PHubReady, 120f);
        Mark("lobby-ready", $"{PScene()} hk1={Hk1Lobby.Active} state={mc.State}");
        yield return Wait(2.5f);
    }

    private IEnumerator PromoTools()
    {
        yield return WaitFor(PHubReady, 60f);
        HeroActions? a = Puppet.Actions;
        if (a == null)
        {
            yield break;
        }
        Mark("tools-open");
        Puppet.Tap(a.OpenInventoryTools, 4);
        yield return Wait(5.5f);
        Mark("tools-close");
        Puppet.Tap(a.OpenInventoryTools, 4);
        yield return Wait(2f);
        if (!PHubReady())
        {
            Puppet.Tap(a.MenuCancel, 4);
            yield return Wait(2f);
        }
        if (!PHubReady())
        {
            Puppet.Tap(a.OpenInventory, 4);
            yield return Wait(2f);
        }
        yield return WaitFor(PHubReady, 10f);
        Mark("tools-done", PHubReady() ? "ready" : "not ready");
    }

    private static List<BossSequenceDoor> PromoDoors() => Object.FindObjectsByType<BossSequenceDoor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
        .Where(d => Hk1Doors.DefOf(d) != null && d.gameObject.scene.name == PScene()).OrderBy(d => d.transform.position.x).ToList();

    private IEnumerator PromoAtrium()
    {
        yield return WaitFor(PHubReady, 60f);
        if (PScene() != Hk1Lobby.Atrium)
        {
            Mark("atrium-skip", PScene());
            yield break;
        }
        HeroController h = HeroController.instance;
        List<BossSequenceDoor> doors = PromoDoors();
        if (doors.Count == 0)
        {
            Mark("atrium-no-doors");
            yield break;
        }
        Mark("atrium-doors", $"hero x={h.transform.position.x:0.0} doors {string.Join(",", doors.Select(d => $"{Hk1Doors.DefOf(d)!.Id}@{d.transform.position.x:0.0},{d.transform.position.y:0.0}"))}");
        // 出生的地方在左边的平台上，和门之间隔着空：直接站到第一扇门左边的地面上，再从左往右走过每一扇门。
        yield return PromoStand(doors[0].transform.position.x - 9f, doors[0].transform.position.y);
        // 只走和第一扇门连着同一块地面的门（第四扇和前三扇之间隔着空，走过去会掉到下面）。
        float floorY = h.transform.position.y;
        bool Ground(float x0, float x1)
        {
            for (float x = x0; x <= x1; x += 0.75f)
            {
                if (Physics2D.Raycast(new Vector2(x, floorY + 1f), Vector2.down, 4f, 1 << 8).collider == null)
                {
                    return false;
                }
            }
            return true;
        }
        var walk = new List<BossSequenceDoor> { doors[0] };
        for (int i = 1; i < doors.Count && Ground(doors[i - 1].transform.position.x, doors[i].transform.position.x); i++)
        {
            walk.Add(doors[i]);
        }
        Mark("atrium-walk", string.Join(",", walk.Select(d => Hk1Doors.DefOf(d)!.Id)));
        foreach (BossSequenceDoor d in walk)
        {
            if (d == null || Hk1Doors.DefOf(d) == null)
            {
                Mark("atrium-doors-gone");
                yield break;
            }
            Mark("atrium-door", Hk1Doors.DefOf(d)!.Id);
            yield return PromoWalkTo(d.transform.position.x, 8f);
            yield return Wait(0.7f);
        }
        // 打开走过的门里最后一扇开着的，套两个束缚，关上。
        BossSequenceDoor? show = walk.LastOrDefault(d => d != null && d.unlockedSet != null && d.unlockedSet.activeInHierarchy);
        Transform? inspect = show != null && show.unlockedSet != null && show.unlockedSet.activeInHierarchy ? show.unlockedSet.transform.Find("Inspect") : null;
        if (show == null || inspect == null)
        {
            Mark("atrium-no-door");
            yield break;
        }
        yield return PromoWalkTo(show.transform.position.x, 8f);
        BossDoorChallengeUI? ui = null;
        bool Opened() => (ui = Object.FindObjectsByType<BossDoorChallengeUI>(FindObjectsSortMode.None).FirstOrDefault(u => u.isActiveAndEnabled)) != null;
        yield return PromoPressUp(inspect.gameObject, Opened);
        if (ui == null)
        {
            Mark("atrium-ui-missing");
            yield break;
        }
        Mark("door-ui", Hk1Doors.DefOf(show)!.Id);
        yield return Wait(2.2f);
        BossDoorChallengeUIBindingButton[] buttons = { ui.boundNailButton, ui.boundHeartButton, ui.boundCharmsButton, ui.boundSoulButton };
        foreach (int i in new[] { 0, 3 })
        {
            if (buttons[i] != null)
            {
                EventSystem.current.SetSelectedGameObject(buttons[i].gameObject);
                yield return Wait(0.5f);
                buttons[i].OnSubmit(null);
                Mark("binding", i.ToString());
                yield return Wait(0.9f);
            }
        }
        yield return Wait(1.5f);
        ui.Hide();
        Mark("door-ui-close");
        yield return WaitFor(PHubReady, 10f);
        yield return Wait(1f);
    }

    private IEnumerator PromoHall()
    {
        ModeController mc = ModeController.Instance!;
        yield return WaitFor(PHubReady, 60f);
        if (PScene() == Hk1Lobby.Atrium)
        {
            TransitionPoint? tp = Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None).FirstOrDefault(t => t.name == "Door_Workshop");
            if (tp != null)
            {
                Mark("hall-walk-door", $"door x={tp.transform.position.x:0.0},{tp.transform.position.y:0.0}");
                HeroController hh = HeroController.instance;
                if (Mathf.Abs(hh.transform.position.x - tp.transform.position.x) > 8f || Mathf.Abs(hh.transform.position.y - tp.transform.position.y) > 3f)
                {
                    float side = hh.transform.position.x < tp.transform.position.x ? -5f : 5f;
                    yield return PromoStand(tp.transform.position.x + side, tp.transform.position.y, side < 0f);
                }
                yield return PromoWalkTo(tp.transform.position.x, 15f);
                yield return Wait(0.6f);
                Puppet.Tap(Puppet.Actions?.Up, 4);
                yield return WaitFor(() => PScene() != Hk1Lobby.Atrium || GameManager.instance.IsInSceneTransition, 2.5f);
                if (PScene() == Hk1Lobby.Atrium && !GameManager.instance.IsInSceneTransition && tp != null && tp.isADoor)
                {
                    Mark("hall-door-fallback");
                    tp.Interact();
                }
                yield return WaitFor(() => PScene() == Hk1Lobby.Workshop && PHubReady(), 60f);
            }
        }
        if (PScene() != Hk1Lobby.Workshop)
        {
            Mark("hall-skip", PScene());
            yield break;
        }
        Mark("hall-arrive");
        yield return Wait(1.5f);
        // 沿着下层往右走。
        Mark("hall-walk");
        yield return PromoWalkFor(+1, 9f);
        yield return Wait(0.5f);
        HeroController h = HeroController.instance;
        // 拉杆：同一层、离得最近的一根，走过去挥针敲它。
        List<Hk1Hall.Slot> all = Hk1Hall.Slots.ToList();
        float hy = h.transform.position.y;
        Hk1Hall.Slot? multi = all.Where(s => s.Def.Variants.Count > 1 && s.Statue.GetComponentInChildren<BossStatueLever>(false) != null
                && Mathf.Abs(s.Statue.transform.position.y - hy) < 4f)
            .OrderBy(s => Mathf.Abs(s.Statue.transform.position.x - h.transform.position.x)).FirstOrDefault();
        if (multi != null)
        {
            BossStatueLever lever = multi.Statue.GetComponentInChildren<BossStatueLever>(false);
            string before = multi.Shown.Id;
            Mark("lever-walk", $"{multi.Name} {before}");
            yield return PromoWalkTo(lever.transform.position.x - 1.3f, 8f, 0.25f);
            HeroController.instance.FaceRight();
            yield return Wait(0.4f);
            Puppet.Tap(Puppet.Actions?.Attack, 3);
            yield return Wait(2.5f);
            if (multi.Shown.Id == before)
            {
                Puppet.Tap(Puppet.Actions?.Attack, 3);
                yield return Wait(2.5f);
            }
            Mark("lever", $"{before} -> {multi.Shown.Id}");
            yield return Wait(2f);
        }
        // 雕像：同一层离得最近、有场地数据的一尊；打开挑战面板，依次看三档，选断丝开战。
        h = HeroController.instance;
        // 优先挑断丝已经解锁（重织打过）的，面板上三档都看得到。
        Hk1Hall.Slot? target = Hk1Hall.Slots.Where(s => s.Shown.ArenaReady && Mathf.Abs(s.Statue.transform.position.y - h.transform.position.y) < 4f
                && s.Statue.transform.position.x > h.transform.position.x + 3f)
            .OrderByDescending(s => Hk1Hall.CompletionOf(s.Shown, s.UsingAlt).completedTier2).ThenBy(s => s.Statue.transform.position.x).FirstOrDefault()
            ?? Hk1Hall.Slots.Where(s => s.Shown.ArenaReady).OrderBy(s => Mathf.Abs(s.Statue.transform.position.x - h.transform.position.x)).FirstOrDefault();
        if (target == null)
        {
            Mark("hall-no-statue");
            yield break;
        }
        bool Tier2(Hk1Hall.Slot s) => Hk1Hall.CompletionOf(s.Shown, s.UsingAlt).completedTier2;
        if (!Tier2(target))
        {
            // 右边没有三档全开的：同一层别处有的话，切镜头站到它左边几步再走过去。
            float y0 = h.transform.position.y, x0 = h.transform.position.x;
            Hk1Hall.Slot? t2 = Hk1Hall.Slots.Where(s => s.Shown.ArenaReady && Tier2(s) && Mathf.Abs(s.Statue.transform.position.y - y0) < 4f)
                .OrderBy(s => Mathf.Abs(s.Statue.transform.position.x - x0)).FirstOrDefault();
            if (t2 != null)
            {
                target = t2;
                if (Mathf.Abs(t2.Statue.transform.position.x - x0) > 10f)
                {
                    yield return PromoStand(t2.Statue.transform.position.x - 6f, t2.Statue.transform.position.y);
                }
            }
        }
        Mark("statue-walk", $"{target.Name} {target.Shown.Id} tier3={Tier2(target)}");
        yield return PromoWalkTo(target.Statue.transform.position.x - 0.8f, 10f);
        Transform? inspect = target.Statue.transform.Find("Inspect");
        BossChallengeUI? ui = null;
        bool Opened() => (ui = Object.FindObjectsByType<BossChallengeUI>(FindObjectsSortMode.None).FirstOrDefault(u => u.isActiveAndEnabled)) != null;
        yield return PromoPressUp(inspect != null ? inspect.gameObject : null, Opened);
        if (ui == null)
        {
            Mark("statue-ui-missing");
            yield break;
        }
        BossDef boss = target.Shown;
        Mark("statue-ui", boss.Id);
        yield return Wait(1.6f);
        foreach (BossChallengeUI.ButtonDisplay? bd in new[] { ui.tier1Button, ui.tier2Button, ui.tier3Button })
        {
            if (bd?.button != null)
            {
                EventSystem.current.SetSelectedGameObject(bd.button.gameObject);
                yield return Wait(1.1f);
            }
        }
        yield return Wait(0.6f);
        Mark("statue-load", "tier 2");
        ui.LoadBoss(2, true);
        yield return PromoFightBody(boss);
    }

    private IEnumerator PromoFights()
    {
        ModeController mc = ModeController.Instance!;
        List<BossDef> list = (GodhomeConfig.DebugPromoBosses.Value ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => BossCatalog.Find(s.Trim())).Where(b => b != null && b.ArenaReady).Cast<BossDef>().ToList();
        Mark("fights", string.Join(",", list.Select(b => b.Id)));
        int i = 0;
        foreach (BossDef b in list)
        {
            Beat();
            if (!Allowed())
            {
                break;
            }
            yield return WaitFor(PHubReady, 90f);
            yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
            string? statue = Hk1Hall.StatueOf(b);
            Hk1Hall.Slot? slot = statue != null ? Hk1Hall.SlotNamed(statue) : null;
            if (slot != null && PScene() == Hk1Lobby.Workshop)
            {
                // 先站到这尊雕像前（剪辑时这里是切镜头）。
                Sanctum.StandAt(HeroController.instance, slot.Statue.transform.position.x - 1f, slot.Statue.transform.position.y);
                HeroController.instance.FaceRight();
                yield return Wait(1.2f);
            }
            int tier = i % 3;
            Mark("fight-begin", $"{b.Id} tier {tier} statue {statue ?? "-"}");
            mc.StartFight(b, tier, statue);
            yield return PromoFightBody(b);
            i++;
        }
    }

    /// <summary>一场：等进场、开战（挑战位先发挑战），让大黄蜂真打，再收尾，等回到大厅。</summary>
    private IEnumerator PromoFightBody(BossDef b)
    {
        ModeController mc = ModeController.Instance!;
        yield return WaitFor(() => mc.State == LoomState.Fight || (mc.State == LoomState.Lobby && !mc.FightLoading && PHubReady()), 90f);
        yield return WaitFor(() => mc.State == LoomState.Fight, 60f);
        if (mc.State != LoomState.Fight || mc.CurrentBoss != b)
        {
            Mark("fight-missing", $"{b.Id} state={mc.State} boss={mc.CurrentBoss?.Id}");
            yield break;
        }
        int fightNo = mc.FightSerial;
        List<string> scenes = ModeController.ScenesOf(b);
        ResetPeaks();
        Mark("fight", $"{b.Id} {PScene()}");
        if (b.Arena.Challenge)
        {
            yield return WaitFor(() => mc.State != LoomState.Fight || !Allowed() || Arena.StartedIn(fightNo, out _), 3f);
            if (!Arena.StartedIn(fightNo, out _))
            {
                var ch = new float[3];
                yield return SendChallenge(mc, b, scenes, fightNo, ch);
            }
        }
        else
        {
            yield return WaitFor(() => mc.State != LoomState.Fight || !Allowed() || Arena.StartedIn(fightNo, out _), StartTimeout);
        }
        Mark("wake", $"{b.Id} started={Arena.StartedIn(fightNo, out _)}");
        PromoBot.On = true;
        var row = new Row { Boss = b };
        yield return Brawl(mc, b, scenes, row, 90f);
        PromoBot.On = false;
        Puppet.Clear();
        Mark(mc.LastResult == FightResult.Won ? "won" : "no-win", $"{b.Id} {row.Note}");
        if (mc.State == LoomState.Fight && Allowed())
        {
            mc.ReturnToLobby("promo timeout / 宣传片超时");
        }
        yield return WaitFor(() => mc.State == LoomState.Lobby, 90f);
        yield return WaitFor(PHubReady, 30f);
        Mark("back", $"{b.Id} {PScene()}");
        yield return Wait(2f);
    }

    private IEnumerator PromoPantheon()
    {
        yield return WaitFor(PHubReady, 60f);
        string pid = GodhomeConfig.SelfTestPantheon.Value?.Trim() ?? "";
        PantheonDef? p = PantheonCatalog.Find(pid.Length > 0 ? pid : "p1");
        if (p == null)
        {
            Mark("pantheon-missing", pid);
            yield break;
        }
        BossSequenceDoor? d = PromoDoors().FirstOrDefault(x => Hk1Doors.DefOf(x)?.Id == p.Id);
        if (d != null)
        {
            // 门和出生点之间可能隔着空（第四扇门）：切镜头站到门边同一块地面上，再走过去。
            float dx = d.transform.position.x, dy = d.transform.position.y;
            RaycastHit2D at = Physics2D.Raycast(new Vector2(dx, dy + 2f), Vector2.down, 25f, 1 << 8);
            float floor = at.collider != null ? at.point.y : dy - 2.8f;
            float? standX = null;
            foreach (float off in new[] { -6f, 6f, -5f, 5f, -4f, 4f, -3f, 3f })
            {
                RaycastHit2D g = Physics2D.Raycast(new Vector2(dx + off, floor + 1.5f), Vector2.down, 3f, 1 << 8);
                if (g.collider != null && Mathf.Abs(g.point.y - floor) < 0.8f)
                {
                    standX = dx + off;
                    break;
                }
            }
            Mark("pantheon-walk", $"{p.Id} door x={dx:0.0} floor={floor:0.0} stand={(standX.HasValue ? standX.Value.ToString("0.0") : "-")}");
            if (standX.HasValue)
            {
                yield return PromoStand(standX.Value, floor + 0.5f, standX.Value < dx);
                yield return PromoWalkTo(dx, 6f);
                yield return Wait(0.6f);
            }
        }
        Mark("pantheon", p.Id);
        PromoBot.On = true;
        yield return PantheonSelfTest(p, ParseBindings(GodhomeConfig.SelfTestBindings.Value), Mathf.Max(0, GodhomeConfig.SelfTestPantheonAbortAt.Value), ++selfTestRun);
        PromoBot.On = false;
        SelfTestRunning = Allowed();
        Mark("pantheon-done", $"{p.Id} {SelfTestStatus}");
        yield return Wait(2f);
    }

    private IEnumerator PromoRoof()
    {
        yield return WaitFor(PHubReady, 60f);
        if (PScene() != Hk1Lobby.Atrium && PScene() != Hk1Lobby.Roof)
        {
            Mark("roof-skip", PScene());
            yield break;
        }
        if (PScene() == Hk1Lobby.Atrium)
        {
            TransitionPoint? up = Object.FindObjectsByType<TransitionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(t => t.name == "top2");
            if (up != null && up.gameObject.activeInHierarchy)
            {
                Sanctum.StandAt(HeroController.instance, up.transform.position.x, up.transform.position.y - 0.5f);
            }
            else
            {
                Mark("roof-transition");
                GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo
                {
                    SceneName = Hk1Lobby.Roof,
                    EntryGateName = "bot2",
                    PreventCameraFadeOut = false,
                    WaitForSceneTransitionCameraFade = true,
                    Visualization = GameManager.SceneLoadVisualizations.Default,
                });
            }
            yield return WaitFor(() => PScene() == Hk1Lobby.Roof && PHubReady(), 60f);
        }
        if (PScene() != Hk1Lobby.Roof)
        {
            Mark("roof-failed", PScene());
            yield break;
        }
        Mark("roof-arrive");
        yield return Wait(1.5f);
        BossSequenceDoor? fifth = PromoDoors().FirstOrDefault();
        if (fifth != null)
        {
            Mark("roof-walk", fifth.name);
            yield return PromoWalkTo(fifth.transform.position.x - 1.5f, 12f);
            yield return Wait(3f);
        }
    }
}
