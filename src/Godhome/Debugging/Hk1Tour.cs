using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using GlobalEnums;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：前作神居巡视（配置 Debug.Hk1Tour，分支 hk1-godhome）。自动读档后：
/// 1. 核对进了前作的前厅、站在死亡复活点，存档里的复活点还是织忆圣所的长椅；
/// 2. 五扇门（前厅四扇）：锁和开的样子对不对，门前截图；
/// 3. 第一扇门：照玩家的按法（给门的 npc_control 发 UP PRESSED）打开前作的挑战界面截图，按“开始”，
///    等前作的开门动画放完进第一场，放弃回来，核对从这扇门的回程门进来；
/// 4. 走前作的门去手办屋，核对雕像都换成了本作的石像，沿着两层截图；
/// 5. 敲一次拉杆（多版本的雕像），核对换了版本；
/// 6. 在一尊雕像前打开前作的挑战面板截图，选重织开战，秒杀，回来核对站在这尊雕像前；
/// 7. 走回前厅，再走去屋顶看第五扇门。
/// 结果写进日志和 BepInEx\Godhome\hk1tour-*.md，截图在 shots\hk1-*。
/// </summary>
internal static class Hk1Tour
{
    internal static bool Running { get; private set; }

    private static readonly System.Reflection.MethodInfo LeverHit = AccessTools.Method(typeof(BossStatueLever), "OnTriggerEnter2D");

    internal static IEnumerator Run(DebugTools host)
    {
        Running = true;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = Path.Combine(Paths.BepInExRootPath, "Godhome", "shots", "hk1-" + stamp);
        Directory.CreateDirectory(dir);
        var lines = new List<string> { $"# 前作神居巡视 / hk1 godhome tour {stamp}", "", $"- 版本 / version: {GodhomePlugin.Version}" };
        // 每一步都马上写进报告（协程里抛了异常 finally 不会跑，报告也要留得下来）。
        string report = Path.Combine(Paths.BepInExRootPath, "Godhome", $"hk1tour-{stamp}.md");
        File.WriteAllText(report, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        int fails = 0;
        bool completed = false;
        GameManager gm0 = GameManager.instance;
        int slot = gm0 != null ? gm0.profileID : -1;
        PlayerData? pd0 = PlayerData.instance;
        bool Same() => GameManager.instance != null && GameManager.instance.profileID == slot && ReferenceEquals(PlayerData.instance, pd0)
            && GodhomeConfig.DebugMode.Value && GodhomeConfig.IsSlotEnabled(slot) && ModeController.Instance != null;
        void Log(string s)
        {
            lines.Add("- " + s);
            GodhomePlugin.Log.LogInfo("[Godhome] 前作巡视 / hk1 tour: " + s);
            try
            {
                File.AppendAllText(report, "- " + s + "\n", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 写不进去就只留日志。
            }
        }
        // 调游戏的东西（发 FSM 事件、按界面按钮）出了异常：记下来接着走，不让异常把巡视带走。
        bool Try(string what, Action a)
        {
            try
            {
                a();
                return true;
            }
            catch (Exception e)
            {
                Check(false, $"{what} 出错 / threw: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}");
                return false;
            }
        }
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                fails++;
            }
            Log((ok ? "✓ " : "✗ ") + what);
        }
        ModeController mc = ModeController.Instance!;
        int k = 0;
        HeroController Hero() => HeroController.instance;
        string Scene() => GameManager.instance.GetSceneNameString();
        string Where()
        {
            Vector3 p = Hero().transform.position;
            return $"{Scene()} 大黄蜂 / hero ({p.x:0.0}, {p.y:0.0})";
        }
        IEnumerator Shot(string name, bool ui = false)
        {
            Log($"{name}: {Where()}");
            string file = Path.Combine(dir, $"{++k:00}-{name}.jpg");
            yield return ui ? Shots.CaptureWithUi(file) : Shots.CaptureWorld(file);
        }
        IEnumerator Until(Func<bool> ok, float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until && !ok())
            {
                yield return null;
            }
        }
        // 站到 x（往下找地面；找不到就站在 yHint）。
        IEnumerator Go(float x, float yHint, float settle = 1.2f)
        {
            RaycastHit2D hit = Physics2D.Raycast(new Vector2(x, yHint + 3f), Vector2.down, 25f, 1 << 8);
            float floor = hit.collider != null ? hit.point.y : yHint;
            Sanctum.StandAt(Hero(), x, floor);
            yield return new WaitForSecondsRealtime(settle);
        }
        bool HubReady() => mc.State == LoomState.Lobby && Lobby.Ready && LoomUI.Instance != null && LoomUI.Instance.CoverAlpha < 0.02f
            && Hero() != null && Hero().acceptingInput && !Hero().controlReqlinquished && !GameManager.instance.IsInSceneTransition;
        // 照玩家的按法：npc_control 进了“In Range”后发 UP PRESSED（FSM 里 ListenForUp 收到上键发的就是它）。
        IEnumerator PressUp(GameObject inspect, string what)
        {
            PlayMakerFSM? fsm = inspect.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "npc_control");
            if (fsm == null)
            {
                Check(false, $"{what}: 找不到 npc_control / no npc_control on {FsmDump.PathOf(inspect.transform)}");
                yield break;
            }
            yield return Until(() => fsm.ActiveStateName == "In Range", 4f);
            Log($"{what}: npc_control 状态 / state {fsm.ActiveStateName}，提示 / prompt {(InteractManager.IsPromptVisible ? "shown" : "-")}");
            Try(what + " UP PRESSED", () => fsm.SendEvent("UP PRESSED"));
            yield return new WaitForSecondsRealtime(0.3f);
            Log($"{what}: 按上以后 / after UP {fsm.ActiveStateName}");
        }
        string Mat(Renderer r) => r.sharedMaterial != null ? $"{r.sharedMaterial.name}/{(r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-")}" : "-";
        // 精灵的像素存成 PNG（图集贴图不可读：先用 GPU 拷进临时图，再读回这张精灵占的那块，和雕石像的读法一样）。
        void DumpSprite(Sprite sp, string path)
        {
            RenderTexture? rt = null;
            Texture2D? tex = null;
            RenderTexture? prev = RenderTexture.active;
            try
            {
                Texture t = sp.texture;
                Rect rc = sp.rect;
                try
                {
                    rc = sp.textureRect;
                }
                catch
                {
                }
                rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(Mathf.Max(1, (int)rc.width), Mathf.Max(1, (int)rc.height), TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(rc.x, rc.y, tex.width, tex.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                Log($"导出失败 / dump failed {Path.GetFileName(path)}: {e.Message}");
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null)
                {
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (tex != null)
                {
                    Object.Destroy(tex);
                }
            }
        }
        // 前作的雕像怎么做的：同一尊前作原图和我们的石像各截一张，导出原图、材质、灯（雕刻参考）。
        IEnumerator CompareStatue(Hk1Hall.Slot s)
        {
            string tag = s.Name.Replace("GG_Statue_", "");
            GameObject? disp = s.Statue.statueDisplay;
            if (disp == null)
            {
                yield break;
            }
            List<SpriteRenderer> orig = disp.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r != null && r.sprite != null && r.name != "Godhome Statue").ToList();
            yield return Go(s.Statue.transform.position.x - 3f, s.Statue.transform.position.y);
            yield return Shot("cmp-" + tag + "-ours");
            foreach (SpriteRenderer r in orig)
            {
                r.enabled = true;
            }
            if (s.MainArt != null)
            {
                s.MainArt.enabled = false;
            }
            yield return null;
            yield return Shot("cmp-" + tag + "-hk1");
            foreach (SpriteRenderer r in orig)
            {
                r.enabled = false;
            }
            if (s.MainArt != null)
            {
                s.MainArt.enabled = true;
            }
            foreach (SpriteRenderer r in orig)
            {
                Log($"前作雕像 / hk1 statue {tag}/{r.name}: sprite {r.sprite.name} {r.sprite.rect.size} ppu {r.sprite.pixelsPerUnit} mat {Mat(r)} color {r.color} layer {r.sortingLayerName}/{r.sortingOrder} scale {r.transform.lossyScale} pos {r.transform.position}");
                DumpSprite(r.sprite, Path.Combine(dir, $"hk1-{tag}-{r.name}.png"));
            }
            if (s.MainArt != null && s.MainArt.sprite != null)
            {
                Log($"我们的石像 / our statue {tag}: {s.MainArt.sprite.rect.size} ppu {s.MainArt.sprite.pixelsPerUnit} mat {Mat(s.MainArt)} scale {s.MainArt.transform.lossyScale} pos {s.MainArt.transform.position}");
                DumpSprite(s.MainArt.sprite, Path.Combine(dir, $"ours-{tag}.png"));
            }
            foreach (Light l in s.Statue.GetComponentsInChildren<Light>(true))
            {
                Log($"雕像的灯 / statue light {tag}/{l.name}: {l.type} color {l.color} intensity {l.intensity} range {l.range} pos {l.transform.position} on {l.isActiveAndEnabled}");
            }
            foreach (SpriteRenderer r in s.Statue.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.sprite != null && r.transform.parent != null && r.transform.parent.name.Contains("Spotlight")))
            {
                Log($"雕像的光柱 / statue beam {tag}/{r.name}: mat {Mat(r)} color {r.color}");
            }
        }
        // 画面为什么黑：屏幕淡入淡出的颜色、镜头淡入的 FSM、我们的黑幕、大黄蜂显不显示、镜头和人的位置。
        void LogView(string what)
        {
            GameCameras? gc = GameCameras.SilentInstance;
            Camera? cam = gc != null ? gc.mainCamera : null;
            HeroController? hh = HeroController.SilentInstance;
            Renderer? hr = hh != null ? hh.GetComponent<Renderer>() : null;
            Color fader = Color.clear;
            try { fader = ScreenFaderUtils.GetColour(); } catch { }
            Log($"{what} 画面 / view: fader {fader}，camera fade {(gc != null && gc.cameraFadeFSM != null ? gc.cameraFadeFSM.ActiveStateName : "-")}，"
                + $"黑幕 / cover {(LoomUI.Instance != null ? LoomUI.Instance.CoverAlpha : -1f):0.00}，人 / hero {(hh != null ? hh.transform.position.ToString() : "-")} 显示 / shown {(hr != null && hr.enabled)}，"
                + $"镜头 / camera {(cam != null ? cam.transform.position.ToString() : "-")}，场景 / scene {Scene()}");
            Log($"{what} 暗处 / darkness: {ViewInfo.Darkness()}");
            Log($"{what} 挡在前面的 / in front: {ViewInfo.Covering()}");
        }
        // 房间遮罩（Remasker：玩家进了它的触发区才淡出）和全黑罩（MaskerBlackout）：哪块盖着、人在不在它的触发区里。
        void LogMasks(string what)
        {
            HeroController? hh = HeroController.SilentInstance;
            Vector2 hp = hh != null ? (Vector2)hh.transform.position : Vector2.zero;
            Collider2D? hcol = hh != null ? hh.GetComponent<Collider2D>() : null;
            int n = 0;
            foreach (MaskerBase m in Object.FindObjectsByType<MaskerBase>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (m == null || m.gameObject.scene.name != Scene())
                {
                    continue;
                }
                n++;
                Traverse t = Traverse.Create(m);
                string st = m is Remasker
                    ? $"covered {t.Field("isCovered").GetValue()} inside {t.Field("isInside").GetValue()} frozen {t.Field("isFrozen").GetValue()} blackout {t.Field("blackoutMask").GetValue()} inverse {t.Field("inverse").GetValue()} uncoveredOnce {t.Field("hasBeenUncovered").GetValue()} force {((Remasker)m).ForceUncovered}"
                    : "";
                object? alpha = null;
                try
                {
                    alpha = t.Property("AlphaSelf").GetValue();
                }
                catch
                {
                }
                string cols = string.Join("; ", m.GetComponents<Collider2D>().Select(c =>
                    $"{c.GetType().Name} {(c.enabled ? "on" : "off")} {c.bounds.min.x:0.0},{c.bounds.min.y:0.0}~{c.bounds.max.x:0.0},{c.bounds.max.y:0.0} 人 / hero {(c.OverlapPoint(hp) ? "IN" : "out")}"
                    + (hcol != null && hcol.enabled && c.enabled && c.IsTouching(hcol) ? " touching" : "")));
                Log($"{what} 遮罩 / mask {FsmDump.PathOf(m.transform)} [{m.GetType().Name}] active {m.isActiveAndEnabled} alpha {alpha} {st} 触发区 / colliders {(cols.Length > 0 ? cols : "-")}");
            }
            foreach (MaskerBlackout b in Object.FindObjectsByType<MaskerBlackout>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                SpriteRenderer? sr = Traverse.Create(b).Field("spriteRenderer").GetValue<SpriteRenderer>();
                Log($"{what} 全黑罩 / blackout {FsmDump.PathOf(b.transform)} active {b.isActiveAndEnabled} color {(sr != null ? sr.color.ToString() : "-")}");
            }
            Log($"{what} 遮罩 / masks {n}，大黄蜂碰撞体 / hero collider {(hcol != null ? $"{hcol.GetType().Name} enabled {hcol.enabled} {hcol.bounds.min.x:0.0},{hcol.bounds.min.y:0.0}~{hcol.bounds.max.x:0.0},{hcol.bounds.max.y:0.0}" : "-")}"
                + $" 触发暂停 / trigger paused {(hh != null && hh.cState.isTriggerEventsPaused)} tag {(hh != null ? hh.tag : "-")}");
        }
        string Texts(Component ui) =>string.Join(" | ", ui.GetComponentsInChildren<Text>(false).Where(t => !string.IsNullOrWhiteSpace(t.text)).Select(t => t.text.Replace("\n", " ")));

        try
        {
            // 0. 到了前厅。
            yield return Until(HubReady, 60f);
            Check(Hk1Lobby.Active, $"用前作的神居 / hk1 godhome active（{Hk1Assets.GameDir ?? "-"}{(Hk1Assets.Failed ? "，失败 / failed: " + Hk1Assets.Failure : "")}）");
            if (!Hk1Lobby.Active)
            {
                yield break;
            }
            Check(HubReady() && Scene() == Hk1Lobby.Atrium, $"到了前厅 / in the atrium: state={mc.State} scene={Scene()} ready={Lobby.Ready} cover={LoomUI.Instance?.CoverAlpha:0.00} input={Hero().acceptingInput}");
            GameObject? marker = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == Hk1Lobby.ArriveMarker);
            if (marker != null)
            {
                float d = Vector2.Distance(marker.transform.position, Hero().transform.position);
                Check(d < 3f, $"站在死亡复活点 / at the death respawn marker（距离 / distance {d:0.0}）");
            }
            PlayerData pd = PlayerData.instance;
            Check(!Hk1Lobby.IsHub(pd.respawnScene) && !Hk1Lobby.IsHub(pd.nonLethalRespawnScene),
                $"复活点还是本作场景 / respawn stays in Silksong: {pd.respawnScene}@{pd.respawnMarkerName}，non-lethal {pd.nonLethalRespawnScene}@{pd.nonLethalRespawnMarker}");
            Check(!pd.disablePause, "能暂停 / pause allowed");
            BlurPlane? bp = Object.FindObjectsByType<BlurPlane>(FindObjectsSortMode.None).FirstOrDefault(b => b.gameObject.scene.name == Scene());
            MeshRenderer? bpr = bp != null ? bp.GetComponent<MeshRenderer>() : null;
            Check(bpr == null || bpr.sortingOrder == short.MinValue, $"模糊背景板排在最底下 / blur plane sorted to the bottom: {(bpr != null ? bpr.sortingOrder.ToString() : "-")}");
            yield return Shot("atrium-arrive");

            // 1. 门。
            List<BossSequenceDoor> doors = Object.FindObjectsByType<BossSequenceDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => Hk1Doors.DefOf(d) != null).OrderBy(d => d.transform.position.x).ToList();
            Check(doors.Count == 4, $"前厅接上的门 / mapped doors in the atrium: {doors.Count}（{string.Join("，", doors.Select(d => d.name + "→" + Hk1Doors.DefOf(d)!.Id))}）");
            foreach (BossSequenceDoor d in doors)
            {
                PantheonDef p = Hk1Doors.DefOf(d)!;
                bool open = ModeController.PantheonUnlocked(p);
                bool lockShown = d.lockSet != null && d.lockSet.activeSelf;
                bool openShown = d.unlockedSet != null && d.unlockedSet.activeSelf;
                Check(open == openShown && open != lockShown, $"{d.name}（{p.Id}）{(open ? "开着 / open" : "锁着 / locked")}：锁 / lock set {lockShown}，门 / unlocked set {openShown}");
                yield return Go(d.transform.position.x - 1.5f, d.transform.position.y - 1f);
                yield return Shot("door-" + p.Id);
            }

            // 2. 第一扇门：前作的挑战界面 → 开始 → 第一场 → 放弃回来。
            BossSequenceDoor? first = doors.FirstOrDefault(d => Hk1Doors.DefOf(d)!.Id == "p1");
            if (first != null && Same())
            {
                PantheonDef p1 = Hk1Doors.DefOf(first)!;
                // 打完回来前厅重新加载过，这扇门是新的物体：先记下名字和位置。
                string doorName = first.name;
                float doorX = first.transform.position.x;
                yield return Go(first.transform.position.x, first.transform.position.y - 1f);
                Transform? inspect = first.unlockedSet != null ? first.unlockedSet.transform.Find("Inspect") : null;
                if (inspect == null)
                {
                    Check(false, "门的 Inspect 找不到 / door Inspect not found");
                }
                else
                {
                    yield return PressUp(inspect.gameObject, "门 / door p1");
                    BossDoorChallengeUI? ui = null;
                    yield return Until(() => (ui = Object.FindObjectsByType<BossDoorChallengeUI>(FindObjectsSortMode.None).FirstOrDefault(u => u.isActiveAndEnabled)) != null, 6f);
                    Check(ui != null, "前作门前的挑战界面打开了 / hk1 door challenge UI opened");
                    if (ui != null)
                    {
                        yield return new WaitForSecondsRealtime(1.5f);
                        Log("界面上的字 / UI texts: " + Texts(ui));
                        Check(ui.titleTextMain != null && ui.titleTextMain.text == p1.DisplayName, $"标题是本作的名字 / title is ours: {ui.titleTextMain?.text}");
                        Log("门的界面打开时的画布 / canvases with the door UI open: " + ViewInfo.Canvases()); Log("门的界面打开时 / with the door UI open: " + ViewInfo.UiVisible());
                        yield return Shot("door-p1-ui", ui: true);
                        yield return new WaitForSecondsRealtime(1.5f);
                        Log("门的界面 3 秒 / door UI at 3 s: " + ViewInfo.UiVisible()); yield return Shot("door-p1-ui-3s", ui: true);
                        BossDoorChallengeUI begin = ui;
                        Try("开始 / Begin", () => begin.Begin());
                        yield return Until(() => mc.State is LoomState.ToFight or LoomState.Fight, 20f);
                        Check(mc.State is LoomState.ToFight or LoomState.Fight && mc.Run?.Def == p1, $"开门动画后开始万神殿 / pantheon started after the door animation: state={mc.State} run={mc.Run}");
                        // 巡视打的不记战绩（关着调试无敌时也一样；Codex 审查）。
                        mc.MarkAssisted("hk1 tour / 前作巡视");
                        yield return Until(() => mc.State == LoomState.Fight, 60f);
                        Check(mc.State == LoomState.Fight && mc.CurrentBoss == p1.Bosses[0], $"进了第一场 / first fight entered: {mc.CurrentBoss?.Id}（{Scene()}）");
                        yield return new WaitForSecondsRealtime(2f);
                        LogView("pantheon-first-fight");
                        yield return Shot("pantheon-first-fight");
                        // 你看到苔藓之母是黑屏：再等几秒各截一张（连界面），看是没淡入完还是一直黑。
                        yield return new WaitForSecondsRealtime(3f);
                        LogView("pantheon-first-fight-5s");
                        LogMasks("pantheon-first-fight-5s");
                        yield return Shot("pantheon-first-fight-5s", ui: true);
                        // （以前这里试过强行揭开房间遮罩查苔藓之母黑屏；黑屏已经由场地表修好，试验去掉：巡视不改游戏里的东西，Codex 审查。）
                        if (mc.State == LoomState.Fight && Same())
                        {
                            mc.ReturnToLobby("hk1 tour / 前作巡视");
                        }
                        yield return Until(HubReady, 90f);
                        string want = Hk1Lobby.ReturnGatePrefix + doorName;
                        string got = Hero().GetEntryGateName() ?? "";
                        float off = Hero().transform.position.x - doorX;
                        Check(HubReady() && Scene() == Hk1Lobby.Atrium && got == want && Mathf.Abs(off) < 4f && !PlayerData.instance.disablePause,
                            $"放弃后回到门前 / back at the door: state={mc.State} scene={Scene()} 进门 / entry {got}（预期 / expected {want}） 离门 / from door {off:+0.0;-0.0} 暂停 / disablePause={PlayerData.instance.disablePause}");
                        yield return new WaitForSecondsRealtime(1f);
                        Log($"回到门前的画面 / view back at the door: {ViewInfo.HudOpaque()}"); yield return Shot("back-at-door-p1", ui: true);
                    }
                }
            }

            // 3. 走去手办屋（前作自己的门 Door_Workshop）。
            if (Same() && HubReady())
            {
                TransitionPoint? tp = Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None).FirstOrDefault(t => t.name == "Door_Workshop");
                Check(tp != null, "前厅去手办屋的门 / Door_Workshop found");
                if (tp != null)
                {
                    Sanctum.StandAt(Hero(), tp.transform.position.x, tp.transform.position.y - 1f);
                    yield return new WaitForSecondsRealtime(1f);
                    // 前作的门型出口（isADoor）：丝之歌里是交互物，玩家按交互键进门。
                    Log($"Door_Workshop: 门型 / door type {tp.isADoor}，提示 / prompt {(InteractManager.IsPromptVisible ? "shown" : "-")}");
                    if (tp.isADoor)
                    {
                        TransitionPoint door = tp;
                        Try("进门 / enter door", () => door.Interact());
                    }
                    yield return Until(() => Scene() == Hk1Lobby.Workshop && HubReady(), 60f);
                    Check(Scene() == Hk1Lobby.Workshop && HubReady(), $"走到手办屋 / walked into the workshop: scene={Scene()} state={mc.State} ready={Lobby.Ready} busy={Hk1Hall.Busy}");
                }
            }
            if (Scene() == Hk1Lobby.Workshop)
            {
                List<Hk1Hall.Slot> all = Hk1Hall.Slots.OrderBy(s => s.Statue.transform.position.y).ThenBy(s => s.Statue.transform.position.x).ToList();
                Log("手办屋 / hall: " + Hk1Hall.Note);
                Check(all.Count == 36, $"雕像 / statues: {all.Count}");
                int painted = all.Count(s => s.MainArt != null && s.MainArt.sprite != null);
                Check(painted == all.Count, $"换成本作石像 / painted with our statues: {painted}/{all.Count}");
                Log("雕像 / statues: " + string.Join("，", all.Select(s => $"{s.Name.Replace("GG_Statue_", "")}={s.Shown.Id}{(s.Def.Variants.Count > 1 ? $"({s.Def.Variants.Count})" : "")}")));
                yield return Shot("workshop-arrive");
                // 门口的总览板：每个本作 Boss 一行，名字是我们的。
                {
                    int want = all.Sum(s => s.Def.Variants.Count);
                    Check(SummaryBoardPatch.Rows == want, $"总览板的行数 / summary board rows: {SummaryBoardPatch.Rows}/{want}");
                    BossSummaryUI? sui = Object.FindObjectsByType<BossSummaryUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
                    if (sui != null && sui.listItemTemplate != null)
                    {
                        Transform list = sui.listItemTemplate.transform.parent;
                        IEnumerable<string> rows = Enumerable.Range(0, list.childCount).Select(i => list.GetChild(i))
                            .Where(c => c.gameObject != sui.listItemTemplate)
                            .Select(c => c.GetComponentInChildren<Text>(true)?.text ?? "-");
                        Log("总览板 / summary board: " + string.Join("，", rows.Take(10)));
                    }
                    BossSummaryBoard? board = Object.FindObjectsByType<BossSummaryBoard>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
                    Transform? inspect = board != null ? board.transform.Find("Inspect") : null;
                    if (board != null && inspect != null)
                    {
                        // 照玩家的按法：站到板前按上（前作的 Inspect FSM：镜头锁过去、打开总览，取消键关上）。
                        yield return Go(board.transform.position.x + 1.5f, board.transform.position.y);
                        yield return PressUp(inspect.gameObject, "总览板 / summary board");
                        yield return new WaitForSecondsRealtime(2.5f);
                        BossSummaryUI? open = Object.FindObjectsByType<BossSummaryUI>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
                        if (open != null)
                        {
                            Canvas? cv = open.GetComponentInParent<Canvas>(true);
                            CanvasGroup? cg = open.GetComponent<CanvasGroup>();
                            Log($"总览板界面 / summary ui: active {open.gameObject.activeInHierarchy}，canvas {(cv != null ? $"{cv.renderMode} cam={(cv.worldCamera != null ? cv.worldCamera.name : "-")} sort={cv.sortingLayerName}/{cv.sortingOrder}" : "-")}，alpha {(cg != null ? cg.alpha : -1f):0.00}，pos {open.transform.position}");
                            Check(open.gameObject.activeInHierarchy, "总览板打开了 / summary board opened");
                        }
                        // 总览板画在界面镜头上（前作的世界空间画布，排序层 HUD）：连界面一起截。
                        yield return Shot("summary-board", ui: true);
                        PlayMakerFSM? uiFsm = inspect.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "UI");
                        Log($"总览板的 UI FSM / summary ui fsm: {uiFsm?.ActiveStateName ?? "-"}");
                        Try("关上总览板 / close summary", () => uiFsm?.SendEvent("CANCEL"));
                        yield return Until(() => HubReady(), 8f);
                        Check(HubReady(), $"关上总览板后能动 / control back after closing the board（UI FSM {uiFsm?.ActiveStateName ?? "-"}）");
                    }
                }
                // 沿着两层每隔几尊截一张。
                for (int i = 0; i < all.Count; i += 5)
                {
                    Hk1Hall.Slot s = all[i];
                    yield return Go(s.Statue.transform.position.x - 2f, s.Statue.transform.position.y);
                    yield return Shot("statue-" + s.Name.Replace("GG_Statue_", ""));
                }

                // 4b. 前作的雕像怎么做的：几尊对比、导出原图（雕刻参考）。
                foreach (Hk1Hall.Slot s in all.Where(s => s.Name is "GG_Statue_Gruz" or "GG_Statue_MantisLords" or "GG_Statue_HollowKnight" or "GG_Statue_Uumuu" or "GG_Statue_Zote").ToList())
                {
                    yield return CompareStatue(s);
                }

                // 5. 拉杆。
                Hk1Hall.Slot? multi = all.FirstOrDefault(s => s.Def.Variants.Count > 1 && s.Statue.GetComponentInChildren<BossStatueLever>(false) != null);
                Check(multi != null, $"有拉杆的多版本雕像 / a statue with a lever: {multi?.Name ?? "-"}");
                if (multi != null && Same())
                {
                    BossStatueLever lever = multi.Statue.GetComponentInChildren<BossStatueLever>(false);
                    yield return Go(lever.transform.position.x - 1.5f, multi.Statue.transform.position.y);
                    string before = multi.Shown.Id;
                    var strike = new GameObject("Godhome Tour Strike");
                    BoxCollider2D col = strike.AddComponent<BoxCollider2D>();
                    col.isTrigger = true;
                    try
                    {
                        strike.tag = "Nail Attack";
                        LeverHit.Invoke(lever, new object[] { col });
                    }
                    catch (Exception e)
                    {
                        Check(false, "敲拉杆出错 / lever strike failed: " + e.GetBaseException().Message);
                    }
                    Object.Destroy(strike);
                    yield return new WaitForSecondsRealtime(4f);
                    Check(multi.Shown.Id != before && GodhomePlugin.Instance.Records.StatueVariant.TryGetValue(multi.Def.Id, out string? recorded) && recorded == multi.Shown.Id,
                        $"拉杆换了版本 / the lever switched the version: {before} → {multi.Shown.Id}");
                    yield return Shot("lever-" + multi.Name.Replace("GG_Statue_", ""));
                }

                // 6. 雕像开战（照玩家的按法打开前作的挑战面板，选重织）→ 秒杀 → 回到雕像前。
                Hk1Hall.Slot? target = all.FirstOrDefault(s => s.Shown.Id == "moss_mother") ?? all.FirstOrDefault(s => s.Shown.ArenaReady);
                if (target != null && Same())
                {
                    Transform? inspect = target.Statue.transform.Find("Inspect");
                    yield return Go(target.Statue.transform.position.x - 1f, target.Statue.transform.position.y);
                    if (inspect == null)
                    {
                        Check(false, "雕像的 Inspect 找不到 / statue Inspect not found");
                    }
                    else
                    {
                        yield return PressUp(inspect.gameObject, "雕像 / statue " + target.Name);
                        BossChallengeUI? ui = null;
                        yield return Until(() => (ui = Object.FindObjectsByType<BossChallengeUI>(FindObjectsSortMode.None).FirstOrDefault(u => u.isActiveAndEnabled)) != null, 6f);
                        Check(ui != null, "前作雕像的挑战面板打开了 / hk1 statue challenge panel opened");
                        if (ui != null)
                        {
                            yield return new WaitForSecondsRealtime(1.5f);
                            Log("面板上的字 / panel texts: " + Texts(ui));
                            Check(ui.bossNameText != null && ui.bossNameText.text == target.Shown.DisplayName, $"名字是本作的 / name is ours: {ui.bossNameText?.text}");
                            yield return Shot("statue-ui", ui: true);
                            BossDef boss = target.Shown;
                            // 回来手办屋重新加载过，雕像是新的物体：先记下名字和位置。
                            string statueName = target.Name;
                            float statueX = target.Statue.transform.position.x;
                            BossChallengeUI load = ui;
                            Try("选重织 / LoadBoss(1)", () => load.LoadBoss(1, true));
                            yield return Until(() => mc.State is LoomState.ToFight or LoomState.Fight, 10f);
                            mc.MarkAssisted("hk1 tour / 前作巡视");
                            yield return Until(() => mc.State == LoomState.Fight, 60f);
                            Check(mc.State == LoomState.Fight && mc.CurrentBoss == boss && mc.CurrentTier == 1, $"开战 / fight: {mc.CurrentBoss?.Id} 难度 / tier {mc.CurrentTier}（{Scene()}）");
                            yield return new WaitForSecondsRealtime(3f);
                            LogView("statue-fight");
                            yield return Shot("statue-fight");
                            yield return new WaitForSecondsRealtime(4f);
                            LogView("statue-fight-7s");
                            yield return Shot("statue-fight-7s", ui: true);
                            float until = Time.unscaledTime + 40f;
                            while (Time.unscaledTime < until && mc.State == LoomState.Fight)
                            {
                                DebugTools.KillBoss(mc, false, out _);
                                yield return new WaitForSecondsRealtime(1f);
                            }
                            Check(mc.LastResult == FightResult.Won || mc.State == LoomState.Won, $"赢了 / won: state={mc.State} result={mc.LastResult}");
                            yield return Until(HubReady, 90f);
                            string want = Hk1Lobby.ReturnGatePrefix + statueName;
                            string got = Hero().GetEntryGateName() ?? "";
                            float off = Hero().transform.position.x - statueX;
                            Check(HubReady() && Scene() == Hk1Lobby.Workshop && got == want && Mathf.Abs(off) < 4f && !PlayerData.instance.disablePause,
                                $"回到雕像前 / back at the statue: scene={Scene()} 进门 / entry {got}（预期 / expected {want}） 离雕像 / from statue {off:+0.0;-0.0} 暂停 / disablePause={PlayerData.instance.disablePause}");
                            Log($"回到雕像前的画面 / view back at the statue: {ViewInfo.HudOpaque()} {ViewInfo.GateFsms(want)}");
                            yield return new WaitForSecondsRealtime(2f);
                            yield return Shot("back-at-statue", ui: true);
                            yield return new WaitForSecondsRealtime(5f);
                            Log($"回到雕像前 7 秒 / view back at the statue +7s: {ViewInfo.HudOpaque()}");
                            yield return Shot("back-at-statue-7s", ui: true);
                            yield return new WaitForSecondsRealtime(6f);
                            Hk1Hall.Slot? again = Hk1Hall.SlotNamed(statueName);
                            Transform? inspect2 = again != null && again.Statue != null ? again.Statue.transform.Find("Inspect") : null;
                            Check(inspect2 != null && inspect2.gameObject.activeInHierarchy, $"雕像又能交互了 / the statue can be inspected again（{(again != null ? again.Shown.Id : "-")}）");
                        }
                    }
                }

                // 7. 走回前厅，再去屋顶。
                if (Same() && HubReady())
                {
                    TransitionPoint? back = Object.FindObjectsByType<TransitionPoint>(FindObjectsSortMode.None).FirstOrDefault(t => t.targetScene == Hk1Lobby.Atrium);
                    Check(back != null, $"手办屋回前厅的门 / workshop door to the atrium: {back?.name ?? "-"}");
                    if (back != null)
                    {
                        // 本作的左右出口只在朝着出口走时才切场景（TransitionPoint.TryDoTransition 看 localScale.x），
                        // 回来时大黄蜂朝右，先转向出口再站过去。
                        HeroController? hc = HeroController.instance;
                        if (hc != null)
                        {
                            if (back.GetGatePosition() == GlobalEnums.GatePosition.left) hc.FaceLeft();
                            else if (back.GetGatePosition() == GlobalEnums.GatePosition.right) hc.FaceRight();
                        }
                        Sanctum.StandAt(Hero(), back.transform.position.x, back.transform.position.y - 1f);
                        yield return Until(() => Scene() == Hk1Lobby.Atrium && HubReady(), 60f);
                        Check(Scene() == Hk1Lobby.Atrium && HubReady(), $"走回前厅 / walked back to the atrium: {Where()}");
                    }
                }
            }
            if (Same() && HubReady() && Scene() == Hk1Lobby.Atrium)
            {
                // 去屋顶的出口（top2）照前作要第五扇门开了才在：按我们的开门条件（p5 要先完成 p1～p4）。
                bool finalOpen = Hk1Doors.Unlocked(Hk1Doors.FinalDoor);
                TransitionPoint? up = Object.FindObjectsByType<TransitionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(t => t.name == "top2");
                Check(up != null && up.gameObject.activeInHierarchy == finalOpen,
                    $"去屋顶的出口 / roof exit top2: {(up != null ? (up.gameObject.activeInHierarchy ? "开着 / open" : "关着 / closed") : "-")}（第五扇门 / final door {(finalOpen ? "开 / unlocked" : "锁 / locked")}）");
                yield return new WaitForSecondsRealtime(1f);
                if (up != null && up.gameObject.activeInHierarchy)
                {
                    Sanctum.StandAt(Hero(), up.transform.position.x, up.transform.position.y - 0.5f);
                }
                else
                {
                    // 没开：直接转场过去看看屋顶（调试，玩家走不到）。
                    Log("第五扇门没开，直接转场去屋顶看 / final door locked; transitioning straight to the roof to look");
                    Try("去屋顶 / to the roof", () => GameManager.instance.BeginSceneTransition(new GameManager.SceneLoadInfo
                    {
                        SceneName = Hk1Lobby.Roof,
                        EntryGateName = "bot2",
                        PreventCameraFadeOut = false,
                        WaitForSceneTransitionCameraFade = true,
                        Visualization = GameManager.SceneLoadVisualizations.Default,
                    }));
                }
                yield return Until(() => Scene() == Hk1Lobby.Roof && HubReady(), 60f);
                Check(Scene() == Hk1Lobby.Roof && HubReady(), $"到了屋顶 / on the roof: {Where()} state={mc.State}");
                if (Scene() == Hk1Lobby.Roof)
                {
                    yield return Shot("roof-arrive");
                    BossSequenceDoor? fifth = Object.FindObjectsByType<BossSequenceDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(d => Hk1Doors.DefOf(d) != null);
                    Check(fifth != null && Hk1Doors.DefOf(fifth)!.Id == "p5", $"第五扇门 / fifth door: {fifth?.name ?? "-"}");
                    if (fifth != null)
                    {
                        bool lockShown = fifth.lockSet != null && fifth.lockSet.activeSelf;
                        Check(lockShown != finalOpen, $"第五扇门的样子 / fifth door state: 锁 / lock set {lockShown}");
                        yield return Go(fifth.transform.position.x - 2f, fifth.transform.position.y - 1f);
                        yield return Shot("door-p5");
                    }
                }
            }

            // 8. 战斗中死亡，照前作：雕像的战斗里死了回到雕像前，万神殿里死了回到门前。
            //    游戏按存档里的复活点（本作的长椅）预加载、发复活转场，我们改道去雕像、门前的复活点（Codex 审查：不能卡在加载里）。
            IEnumerator DieAndCheck(string label, string wantScene, string wantMarker)
            {
                yield return new WaitForSecondsRealtime(3f);
                // 等的这几秒里回了标题、读了别的档、关了调试、没在打这场：不打（Codex 审查：巡视不能碰别的存档）。
                if (!Same() || !Hk1Lobby.Active || mc.State is not (LoomState.Fight or LoomState.Won))
                {
                    Check(false, $"{label}：条件变了，不打死 / conditions changed, not killing: same={Same()} state={mc.State}");
                    yield break;
                }
                DebugTools.AllowHeroDamage = true;
                HeroController h = Hero();
                yield return Until(() => h.CanTakeDamage(), 8f);
                if (!Same() || mc.State is not (LoomState.Fight or LoomState.Won))
                {
                    DebugTools.AllowHeroDamage = false;
                    Check(false, $"{label}：条件变了，不打死 / conditions changed, not killing: same={Same()} state={mc.State}");
                    yield break;
                }
                var dummy = new GameObject("Godhome Tour Hit");
                dummy.transform.position = h.transform.position + Vector3.right;
                Log($"{label} 挨打前 / before the hit: canTakeDamage={h.CanTakeDamage()} health={PlayerData.instance.health}");
                Try("打死大黄蜂 / kill the hero", () => h.TakeDamage(dummy, CollisionSide.other, 99, HazardType.ENEMY));
                Object.Destroy(dummy);
                yield return new WaitForSecondsRealtime(0.5f);
                Log($"{label} 挨打后 / after the hit: dead={h.cState.dead} health={PlayerData.instance.health} state={mc.State}");
                float t0 = Time.unscaledTime;
                yield return Until(() => Scene() == wantScene && HubReady(), 90f);
                DebugTools.AllowHeroDamage = false;
                PlayerData pdd = PlayerData.instance;
                GameObject? m2 = Scene() == wantScene ? SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == wantMarker) : null;
                float dm = m2 != null ? Vector2.Distance(m2.transform.position, Hero().transform.position) : -1f;
                Check(Scene() == wantScene && HubReady() && dm >= 0f && dm < 3f,
                    $"{label}：死后回到 / woke at {wantMarker}: {Where()} state={mc.State} 离复活点 / from marker {dm:0.0}，用时 / took {Time.unscaledTime - t0:0.0}s，加载中 / loading={GameManager.instance.IsInSceneTransition}");
                Check(!Hk1Lobby.IsHub(pdd.respawnScene) && string.IsNullOrEmpty(pdd.tempRespawnScene) && !Hk1Lobby.IsHub(pdd.nonLethalRespawnScene ?? ""),
                    $"{label}：死后的复活点 / respawn after dying: {pdd.respawnScene}@{pdd.respawnMarkerName} temp={pdd.tempRespawnScene ?? "-"}@{pdd.tempRespawnMarker ?? "-"} non-lethal={pdd.nonLethalRespawnScene ?? "-"}@{pdd.nonLethalRespawnMarker ?? "-"}");
                yield return new WaitForSecondsRealtime(1f);
                Log($"死后的画面 / view after death ({label}): {ViewInfo.HudOpaque()}"); yield return Shot("after-death-" + label, ui: true);
            }
            BossDef? victim = BossCatalog.Find("moss_mother");
            string? victimStatue = victim != null ? Hk1Hall.StatueOf(victim) : null;
            if (Same() && HubReady() && victim != null && victimStatue != null)
            {
                Try("雕像开战 / StartFight(moss_mother, statue)", () => mc.StartFight(victim, 0, victimStatue));
                mc.MarkAssisted("hk1 tour / 前作巡视");
                yield return Until(() => mc.State == LoomState.Fight, 60f);
                Check(mc.State == LoomState.Fight && mc.CurrentBoss == victim, $"雕像开战（要死一次）/ statue fight to die in: {mc.CurrentBoss?.Id}（{Scene()}）");
                if (mc.State == LoomState.Fight)
                {
                    yield return DieAndCheck("statue", Hk1Lobby.Workshop, Hk1Lobby.DeathReturnPrefix + victimStatue);
                }
            }
            PantheonDef? pDie = PantheonCatalog.Find("p1");
            if (Same() && HubReady() && pDie != null)
            {
                Try("万神殿开战 / StartPantheon(p1)", () => mc.StartPantheon(pDie, Binding.None));
                mc.MarkAssisted("hk1 tour / 前作巡视");
                yield return Until(() => mc.State == LoomState.Fight, 60f);
                Check(mc.State == LoomState.Fight && mc.Run?.Def == pDie, $"万神殿开战（要死一次）/ pantheon fight to die in: {mc.CurrentBoss?.Id}（{Scene()}）");
                if (mc.State == LoomState.Fight)
                {
                    yield return DieAndCheck("pantheon", Hk1Lobby.Atrium, Hk1Lobby.DeathReturnPrefix + ModeController.DoorSpot("p1"));
                }
            }
            completed = true;
        }
        finally
        {
            Running = false;
            DebugTools.AllowHeroDamage = false;
            string result = !completed ? $"没跑完 / incomplete（{fails} 项没通过 / failed）" : fails == 0 ? "全部通过 / all passed" : fails + " 项没通过 / failed";
            lines.Insert(3, $"- 结果 / result: {result}");
            File.WriteAllText(report, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作巡视结束 / hk1 tour finished ({result}): {report}");
        }
        if (GodhomeConfig.DebugQuitAfterSelfTest.Value && Same())
        {
            yield return new WaitForSecondsRealtime(5f);
            // 等的 5 秒里关了调试、取消了退出、换了档：不退（Codex 审计 0.10.3 D04）。
            if (GodhomeConfig.DebugQuitAfterSelfTest.Value && Same())
            {
                GodhomePlugin.Log.LogInfo("[Godhome] 前作巡视后退出游戏 / quitting after the hk1 tour");
                Application.Quit();
            }
        }
    }
}
