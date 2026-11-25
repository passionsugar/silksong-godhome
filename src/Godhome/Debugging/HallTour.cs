using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using GlobalEnums;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：圣所和忆像长廊巡视（配置 Debug.HallTour）。自动读档进大厅后：
/// 1. 记下圣所、长廊、雕像、图鉴立绘、借来的图集；在圣所里走几个点截图；
/// 2. 用丝门传送到长廊，下层截图（右端没有台阶缺口）；丝线升降到上层截图；降回下层；
/// 3. 扳一次拉杆（只预览不记存档，看完换回原版本）截图；
/// 4. 核对回程门：每尊雕像、每扇万神殿的门前各一个门型 TransitionPoint，名字、位置对；
/// 5. 在第一尊雕像前打开挑战面板截图，选重织开战；进场后核对 BossLevel，并打自己 1 点伤害核对“伤害翻倍”（应该少 2 血）；
///    放弃回来，核对英雄是从这尊雕像的回程门（door_dreamReturn → bossReturnEntryGate）进来的、站在雕像前。
/// 结果写进日志和 BepInEx\Godhome\halltour-*.md，截图在 shots\hall-*。不替玩家打 Boss。
/// </summary>
internal static class HallTour
{
    internal static bool Running { get; private set; }

    internal static IEnumerator Run(DebugTools host)
    {
        Running = true;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string dir = Path.Combine(Paths.BepInExRootPath, "Godhome", "shots", "hall-" + stamp);
        Directory.CreateDirectory(dir);
        var lines = new List<string> { $"# 圣所巡视 / sanctum tour {stamp}", "", $"- 版本 / version: {GodhomePlugin.Version}" };
        int fails = 0;
        // 必测步骤全部跑完才算通过（Codex 审查 v0.6.0 #16）。
        bool completed = false;
        // 巡视固定在开始时的存档槽和数据对象上；有副作用的操作前都核对（Codex 审查 v0.6.0 #6）。
        GameManager gm0 = GameManager.instance;
        int slot = gm0 != null ? gm0.profileID : -1;
        PlayerData? pd0 = PlayerData.instance;
        bool Same() => GameManager.instance != null && GameManager.instance.profileID == slot && ReferenceEquals(PlayerData.instance, pd0)
            && GodhomeConfig.DebugMode.Value && GodhomeConfig.IsSlotEnabled(slot) && ModeController.Instance != null;
        void Log(string s)
        {
            lines.Add("- " + s);
            GodhomePlugin.Log.LogInfo("[Godhome] 巡视 / tour: " + s);
        }
        void Check(bool ok, string what)
        {
            if (!ok)
            {
                fails++;
            }
            Log((ok ? "✓ " : "✗ ") + what);
        }
        ModeController? mc = ModeController.Instance;
        int k = 0;
        HeroController hero = HeroController.instance;
        CameraController? cc = GameCameras.instance != null ? GameCameras.instance.cameraController : null;

        string Where()
        {
            Vector3 p = hero.transform.position;
            string zones = cc == null || cc.lockZoneList == null ? "-" : string.Join(",", cc.lockZoneList.Where(z => z != null).Select(z => z.name));
            return $"大黄蜂 / hero ({p.x:0.0}, {p.y:0.0})，镜头 / cam ({(cc != null ? cc.transform.position.x : 0f):0.0}, {(cc != null ? cc.transform.position.y : 0f):0.0}) locks=[{zones}]";
        }
        IEnumerator Shot(string name, bool ui = false)
        {
            Log($"{name}: {Where()}");
            // 看画面用：主镜头单独画一张 1440×810（屏幕是竖屏、很小也一样）；要看界面（面板）的才截屏。
            string file = Path.Combine(dir, $"{++k:00}-{name}.jpg");
            yield return ui ? Shots.CaptureWithUi(file) : Shots.CaptureWorld(file);
        }
        IEnumerator Stand(float x, float floor, string name, bool shot = true)
        {
            Sanctum.StandAt(hero, x, floor);
            yield return new WaitForSecondsRealtime(1.2f);
            float dy = hero.transform.position.y - floor;
            Check(Mathf.Abs(hero.transform.position.x - x) < 1.5f && dy > -0.3f && dy < 2.5f, $"{name} 站稳 / standing (离地 / above floor {dy:0.00})");
            if (shot)
            {
                yield return Shot(name);
            }
        }
        IEnumerator Travel(string gateName, Vector2 expect, string name)
        {
            SilkGate? gate = UnityEngine.Object.FindObjectsByType<SilkGate>(FindObjectsSortMode.None).FirstOrDefault(g => g != null && g.name == gateName);
            if (gate == null)
            {
                Check(false, $"找不到丝门 / gate not found: {gateName}");
                yield break;
            }
            Vector3 gp = gate.transform.position;
            Sanctum.StandAt(hero, gp.x, gp.y - 1.6f);
            yield return new WaitForSecondsRealtime(0.8f);
            gate.Interact();
            yield return new WaitForSecondsRealtime(0.25f);
            float mid = LoomUI.Instance != null ? LoomUI.Instance.CoverAlpha : -1f;
            yield return new WaitForSecondsRealtime(1.6f);
            Vector3 p = hero.transform.position;
            Check(Mathf.Abs(p.x - expect.x) < 1.5f && p.y - expect.y > -0.3f && p.y - expect.y < 2.5f && !Sanctum.Busy && !hero.controlReqlinquished,
                $"{gateName} 传送 / travel → ({p.x:0.0}, {p.y:0.0})，预期 / expected ({expect.x:0.0}, {expect.y:0.0})；途中黑幕 / cover mid-way {mid:0.00}；能操作 / control {(!hero.controlReqlinquished)}");
            yield return Shot(name);
        }

        try
        {
            if (mc == null || mc.State != LoomState.Lobby)
            {
                Log($"不在大厅，不巡视 / not in the lobby (state={mc?.State})");
                yield break;
            }
            // 坐在长椅上先起身：等能操作。
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (PlayerData.instance.atBench || !hero.acceptingInput))
            {
                if (PlayerData.instance.atBench)
                {
                    PlayMakerFSM? bench = UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)
                        .FirstOrDefault(f => f != null && f.FsmName == "Bench Control");
                    // 长椅 FSM 的 Resting 状态等 GET UP（跳跃或攻击键发的，Weave_07 场景数据）。
                    bench?.SendEvent("GET UP");
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            Log($"起身 / standing: atBench={PlayerData.instance.atBench} acceptingInput={hero.acceptingInput}");
            // 等画面搭好、黑幕揭开。
            until = Time.unscaledTime + 30f;
            while (Time.unscaledTime < until && (!Sanctum.Built || (LoomUI.Instance != null && LoomUI.Instance.CoverAlpha > 0.02f)))
            {
                yield return null;
            }
            Check(Sanctum.Built && Sanctum.BuildOk, "圣所画面搭好 / sanctum built: " + (Sanctum.Built ? Sanctum.Note : "-"));
            Log(SanctumArt.Note);
            Log(Tk2dArt.Note);
            Check(!Tk2dArt.Holding, "动作帧包雕完已还 / tk2d bundles released after carving");
            int stones = Sanctum.Statues.Count(s => s.IsStone);
            Check(stones == Sanctum.Statues.Count(s => s.HasSprite), $"石像 / stone statues: {stones}/{Sanctum.Statues.Count}（{StatueCarver.Note}）");
            // 长椅：直接读当前大厅的长椅本体和子物体（包括 Lit），都是没有青苔的织者长椅。
            string benchNow = Sanctum.BenchSprite;
            int cleanBench = benchNow.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries).Count(e => e.EndsWith("=weaver_bench", StringComparison.Ordinal));
            Check(cleanBench >= 2 && benchNow.IndexOf("moss", StringComparison.OrdinalIgnoreCase) < 0, $"长椅换成没有青苔的织者长椅（本体和 Lit）/ bench and Lit without moss: {benchNow}");
            // 音效和图集同时借（不挡黑幕）：最多再等 10 秒。
            until = Time.unscaledTime + 10f;
            while (Time.unscaledTime < until && SanctumAudio.LeverClip == null && SanctumAudio.Holding)
            {
                yield return null;
            }
            Check(SanctumAudio.LeverClip != null, $"拉杆原版音效借到了 / lever sound loaded: {SanctumAudio.Note}");
            foreach (SilkGate g in UnityEngine.Object.FindObjectsByType<SilkGate>(FindObjectsSortMode.None).Where(g => g != null && g.name.StartsWith("Gate ", StringComparison.Ordinal)))
            {
                Check(g.InteractLabel == InteractableBase.PromptLabels.Enter, $"丝门提示是“进入” / gate prompt is Enter: {g.name} = {g.InteractLabel}");
            }
            Log("界面结构 / UI dump: " + UiDump.Write(Path.Combine(dir, "ui-dump.txt")));
            if (!Sanctum.Built || Sanctum.Statues.Count == 0)
            {
                yield break;
            }
            foreach (Statue s in Sanctum.Statues)
            {
                lines.Add($"  - {s.Def.Id}: {string.Join(" / ", s.Def.Variants.Select(v => v.Id + (s.Def.IsBlack(v) ? "(黑线)" : "")))} 立绘 / art={(s.HasSprite ? (s.Figure != null && s.Figure.sprite != null ? s.Figure.sprite.name : "?") : "无 / none")} x={s.X:0.0} {(s.Upper ? "上 / upper" : "下 / lower")}");
            }
            // 全部图鉴条目（给缺立绘的雕像补 journal 名用）。
            var journal = new StringBuilder();
            foreach (EnemyJournalRecord r in EnemyJournalManager.GetAllEnemies())
            {
                if (r != null)
                {
                    journal.AppendLine($"{r.name}\t{r.DisplayName.ToString(false)}\t{(r.EnemySprite != null ? r.EnemySprite.name : "-")}");
                }
            }
            File.WriteAllText(Path.Combine(dir, "journal.tsv"), journal.ToString(), new UTF8Encoding(false));
            if (cc != null)
            {
                Check(cc.xLimit >= Sanctum.WantXLimit - 0.1f && cc.yLimit >= Sanctum.WantYLimit - 0.1f, $"镜头边界 / camera limits x={cc.xLimit:0.0}（要 / want {Sanctum.WantXLimit:0.0}）y={cc.yLimit:0.0}（要 / want {Sanctum.WantYLimit:0.0}）");
            }
            float f = Sanctum.FloorY;
            float u = Sanctum.UpperY;
            SceneColorManager? scm = GameCameras.instance != null ? GameCameras.instance.sceneColorManager : null;
            if (scm != null)
            {
                Log($"光照 / lighting: factor={scm.Factor:0.00} satA={scm.SaturationA:0.00} R/G/B(0.5)={scm.RedA.Evaluate(0.5f):0.00}/{scm.GreenA.Evaluate(0.5f):0.00}/{scm.BlueA.Evaluate(0.5f):0.00} ambient={RenderSettings.ambientLight}");
            }

            // 1. 圣所
            yield return Shot("hub-bench");
            yield return Shot("hub-bench-ui", ui: true);
            yield return Stand(14f, f, "hub-west");
            yield return Stand(Sanctum.AltarX - 2.5f, f, "hub-altar");
            yield return Stand(Sanctum.HubGateX - 4f, f, "hub-gate");

            // 2. 丝门 → 长廊下层
            yield return Travel("Gate Hub→Hall", new Vector2(Sanctum.HallLeft + Sanctum.HallGateOffset + 2.5f, f), "hall-entrance");
            int n = Sanctum.Statues.Count;
            List<Statue> lower = Sanctum.Statues.Where(s => !s.Upper).ToList();
            List<Statue> upper = Sanctum.Statues.Where(s => s.Upper).ToList();
            foreach (int i in new[] { 0, lower.Count / 2, lower.Count - 1 }.Distinct())
            {
                if (i >= 0 && i < lower.Count)
                {
                    yield return Stand(lower[i].X - 2f, f, $"lower-{i:00}-{lower[i].Def.Id}");
                }
            }
            // 右端：台阶删了，楼板一直铺到右墙。下层右端站一次；从下层往上打射线，先碰到的应该是楼板底（不再有缺口）。
            yield return Stand(Sanctum.HallRight - 2f, f, "lower-right-end");
            RaycastHit2D gapHit = Physics2D.Raycast(new Vector2(Sanctum.HallRight - 3f, f + 1f), Vector2.up, 30f, 1 << 8);
            Check(gapHit.collider != null && Mathf.Abs(gapHit.point.y - (u - Sanctum.SlabThick)) < 0.25f,
                $"右端没有台阶缺口 / no stair gap at the right end: 往上碰到 / hit {(gapHit.collider != null ? gapHit.collider.name : "-")} y={gapHit.point.y:0.0}（楼板底 / slab bottom {u - Sanctum.SlabThick:0.0}）");
            // 3. 升降：下层 → 上层
            yield return Travel("Lift Up", new Vector2(Sanctum.HallLeft + Sanctum.LiftOffset + Sanctum.LiftLanding, u), "lift-up");
            foreach (int i in new[] { 0, upper.Count / 2, upper.Count - 1 }.Distinct())
            {
                if (i >= 0 && i < upper.Count)
                {
                    yield return Stand(upper[i].X - 2f, u, $"upper-{i:00}-{upper[i].Def.Id}");
                }
            }
            yield return Stand(Sanctum.HallRight - 2f, u, "upper-right-end");

            // 4. 拉杆：只预览（不记存档），看完换回原来的版本（Codex 审查 v0.5.0 #8）。
            List<Statue> multi = Sanctum.Statues.Where(s => s.Def.Variants.Count > 1).ToList();
            Check(multi.All(s => s.Lever != null), $"拉杆的图都借到了 / every lever has its art ({multi.Count(s => s.Lever != null)}/{multi.Count})");
            Statue? lever = multi.FirstOrDefault(s => s.Def.Variants.Count > 2 && s.Lever != null) ?? multi.FirstOrDefault(s => s.Lever != null);
            if (lever != null)
            {
                GodhomeSaveData rec = GodhomePlugin.Instance.Records;
                string? savedBefore = rec.StatueVariant.TryGetValue(lever.Def.Id, out string? sv) ? sv : null;
                BossDef orig = lever.Current;
                // 站在拉杆右边（Codex 审查 v0.6.3 低 #4），像从右边敲一样：拉杆往左倒（0001..0006）。倒到最低（约 0.13 秒）截一张。
                yield return Stand(lever.X + SanctumLayout.LeverOffset + 1.6f, lever.Floor, $"lever-before-{lever.Def.Id}");
                int plays = SanctumAudio.LeverPlays;
                try
                {
                    lever.Cycle(save: false, towardRight: false);
                    Check(SanctumAudio.LeverPlays > plays, $"拉杆响了原版音效 / lever played its sound ({SanctumAudio.LeverPlays - plays})");
                    yield return new WaitForSecondsRealtime(0.13f);
                    string fl = SanctumLayout.LeverFrame(lever);
                    Check(IsLeverFrame(fl, 1, 6), $"拉杆往左倒 / lever falls left: {fl}");
                    yield return Shot($"lever-swing-left-{lever.Def.Id}");
                    // 换像动画：抖 0.6 秒、沉 0.7 秒、升 0.85 秒。沉到一半截一张，升完再截一张。
                    yield return new WaitForSecondsRealtime(0.8f);
                    yield return Shot($"lever-sinking-{lever.Def.Id}");
                    until = Time.unscaledTime + 6f;
                    while (Time.unscaledTime < until && lever.Swapping)
                    {
                        yield return null;
                    }
                    Check(!lever.Swapping, $"拉杆换像动画播完 / lever swap animation finished (stone={lever.IsStone})");
                    yield return new WaitForSecondsRealtime(0.3f);
                    Check(SanctumLayout.LeverFrame(lever) == "ant_lever_animated0000", $"拉杆立回去 / lever back upright: {SanctumLayout.LeverFrame(lever)}");
                    Log($"拉杆 / lever {lever.Def.Id}: {orig.Id} → {lever.Current.Id}");
                    yield return Shot($"lever-after-{lever.Def.Id}");
                    // 另一个方向（从左边敲往右倒，0007..0012）：只播拉杆，不换像。
                    SanctumLayout.PlayLever(lever, towardRight: true);
                    yield return new WaitForSecondsRealtime(0.13f);
                    string fr = SanctumLayout.LeverFrame(lever);
                    Check(IsLeverFrame(fr, 7, 12), $"拉杆往右倒 / lever falls right: {fr}");
                    yield return Shot($"lever-swing-right-{lever.Def.Id}");
                    yield return new WaitForSecondsRealtime(0.6f);
                    Check(SanctumLayout.LeverFrame(lever) == "ant_lever_animated0000", $"拉杆立回去 / lever back upright: {SanctumLayout.LeverFrame(lever)}");
                }
                finally
                {
                    // 中途停下也换回原来的版本（Codex 审查 v0.6.3 #7）。
                    if (lever != null)
                    {
                        lever.SetCurrent(orig, save: false);
                    }
                }
                string? savedAfter = rec.StatueVariant.TryGetValue(lever.Def.Id, out string? sa) ? sa : null;
                Check(savedBefore == savedAfter && lever.Current == orig, $"拉杆预览没写存档、已换回 / lever preview not saved and restored ({savedBefore ?? "-"} / {savedAfter ?? "-"})");
            }
            else
            {
                Log("没有带拉杆的雕像 / no statue with a lever");
            }

            // 4b. 有几种形态的雕像：每种形态的石像都得不一样，各拍一张（直接换，不播动画、不记存档），看完换回来。
            foreach (Statue st in multi)
            {
                List<Sprite?> carved = st.Def.Variants.Select(v => Sanctum.CarvedStatue(v)).ToList();
                bool distinct = carved.All(c => c != null) && carved.Select(c => c!.name).Distinct().Count() == carved.Count;
                Check(distinct, $"每种形态各有石像 / every form has its own statue: {st.Def.Id}: " +
                    string.Join(" / ", st.Def.Variants.Select((v, i) => v.Id + "=" + (carved[i] != null ? carved[i]!.name : "无 / none"))));
                GodhomeSaveData rec = GodhomePlugin.Instance.Records;
                string? savedBefore = rec.StatueVariant.TryGetValue(st.Def.Id, out string? sv) ? sv : null;
                // 先等正在播的换像播完，不把它截断（Codex 审查 v0.6.3 #7）。
                until = Time.unscaledTime + 6f;
                while (Time.unscaledTime < until && st.Swapping)
                {
                    yield return null;
                }
                BossDef orig = st.Current;
                yield return Stand(st.X + 1.6f, st.Floor, $"form-{st.Def.Id}-{st.Current.Id}");
                try
                {
                    foreach (BossDef v in st.Def.Variants.Where(v => v != orig).ToList())
                    {
                        st.SetCurrent(v, save: false);
                        Sprite? want = Sanctum.CarvedStatue(v);
                        Check(st.IsStone && st.Figure != null && want != null && st.Figure.sprite == want, $"显示的是这个形态的石像 / showing this form's statue: {st.Def.Id}/{v.Id} = {(st.Figure != null && st.Figure.sprite != null ? st.Figure.sprite.name : "-")}");
                        yield return new WaitForSecondsRealtime(0.25f);
                        yield return Shot($"form-{st.Def.Id}-{v.Id}");
                    }
                }
                finally
                {
                    st.SetCurrent(orig, save: false);
                }
                string? savedAfter = rec.StatueVariant.TryGetValue(st.Def.Id, out string? sa) ? sa : null;
                Check(savedBefore == savedAfter && st.Current == orig, $"形态预览没写存档、已换回 / form preview not saved and restored: {st.Def.Id}");
            }
            // 5. 升降：上层 → 下层
            yield return Stand(Sanctum.HallLeft + Sanctum.LiftOffset + 4f, u, "upper-near-lift");
            yield return Travel("Lift Down", new Vector2(Sanctum.HallLeft + Sanctum.LiftOffset + Sanctum.LiftLanding, f), "lift-down");

            // 6. 回程门：每尊雕像、每扇万神殿的门前各一个（打完从 door_dreamReturn 回来，游戏按 bossReturnEntryGate 找它）。
            //    门名能被游戏找到（登记在 TransitionPoint.TransitionPoints）、是门型、不能交互、在雕像左边 2 格 / 门左边。
            {
                int ok = 0, bad = 0;
                foreach (Statue s in Sanctum.Statues)
                {
                    string name = Sanctum.ReturnGateName(s.Def.Id)!;
                    TransitionPoint? tp = TransitionPoint.TransitionPoints.FirstOrDefault(p => p != null && p.name == name);
                    bool good = tp != null && tp.isADoor && tp.isInactive && tp.GetGatePosition() == GatePosition.door
                        && Mathf.Abs(tp.transform.position.x - (s.X - 2f)) < 0.1f && Mathf.Abs(tp.transform.position.y - (s.Floor + 1f)) < 0.1f;
                    if (good)
                    {
                        ok++;
                    }
                    else
                    {
                        bad++;
                        lines.Add($"- 回程门不对 / bad return gate {name}: {(tp == null ? "没有 / missing" : $"({tp.transform.position.x:0.0}, {tp.transform.position.y:0.0}) door={tp.isADoor} inactive={tp.isInactive} side={tp.GetGatePosition()}")}，雕像 / statue x={s.X:0.0} floor={s.Floor:0.0}");
                    }
                }
                foreach (PantheonDoor d in Sanctum.Doors)
                {
                    string name = Sanctum.ReturnGateName(ModeController.DoorSpot(d.Def.Id))!;
                    TransitionPoint? tp = TransitionPoint.TransitionPoints.FirstOrDefault(p => p != null && p.name == name);
                    bool good = tp != null && tp.isADoor && tp.isInactive && tp.GetGatePosition() == GatePosition.door && Mathf.Abs(tp.transform.position.x - (d.X - 3.6f * d.Size)) < 0.1f
                        && Mathf.Abs(tp.transform.position.y - (d.Floor + 1f)) < 0.1f;
                    if (good)
                    {
                        ok++;
                    }
                    else
                    {
                        bad++;
                        lines.Add($"- 回程门不对 / bad return gate {name}: {(tp == null ? "没有 / missing" : $"({tp.transform.position.x:0.0}, {tp.transform.position.y:0.0})")}，门 / door x={d.X:0.0} floor={d.Floor:0.0}");
                    }
                }
                Check(bad == 0 && ok == Sanctum.Statues.Count + Sanctum.Doors.Count, $"回程门 / return gates: {ok} 个对 / ok, {bad} 个不对 / bad（雕像 / statues {Sanctum.Statues.Count} + 门 / doors {Sanctum.Doors.Count}）");
            }

            // 7. 挑战：第一尊雕像选重织。
            Statue target = Sanctum.Statues[0];
            yield return Stand(target.X - 1f, target.Floor, "before-challenge");
            if (target.Interact == null || LoomUI.Instance == null)
            {
                Check(false, "雕像没有交互组件 / statue has no interact component");
                yield break;
            }
            // 打开前的界面输入（三位）和暂停开关：关掉以后要原样还回来。
            string UiBits()
            {
                UnityEngine.EventSystems.EventSystem? es = UnityEngine.EventSystems.EventSystem.current;
                bool acc = InputHandler.Instance != null && InputHandler.Instance.acceptingInput;
                bool nav = es != null && es.sendNavigationEvents;
                bool mouse = UIManager.instance != null && UIManager.instance.inputModule != null && UIManager.instance.inputModule.allowMouseInput;
                return $"accept={acc} nav={nav} mouse={mouse} pauseOff={PlayerData.instance.disablePause}";
            }
            string bitsBefore = UiBits();
            target.Interact.Interact();
            yield return new WaitForSecondsRealtime(0.8f);
            // 这次打开的编号：之后每次动手前都核对还是同一个槽、同一份数据、同一尊雕像、同一次打开（Codex 审查 v0.7.0 #8）。
            int menuId = LoomUI.Instance.ChallengeSession;
            bool SameMenu() => Same() && mc.State == LoomState.Lobby && LoomUI.Instance != null && LoomUI.Instance.Challenge == target
                && menuId > 0 && LoomUI.Instance.ChallengeSession == menuId && ChallengeMenu.Session == menuId;
            Check(LoomUI.Instance.Challenge == target, $"挑战面板 / challenge panel open, control relinquished={hero.controlReqlinquished}");
            // 挑战面板是游戏自己的界面（复制“模式选择”）：Boss 名字、三档难度、默认选第一档。
            Check(LoomUI.Instance.ChallengeNative, $"挑战面板是游戏自己的界面 / challenge menu is the game's own UI: {ChallengeMenu.Note}");
            Check(ChallengeMenu.TitleText == target.Current.DisplayName && ChallengeMenu.TierText(0) == L.Get("TIER_0") && ChallengeMenu.TierText(1) == L.Get("TIER_1") && ChallengeMenu.TierText(2) == L.Get("TIER_2"),
                $"面板文字 / menu text: {ChallengeMenu.TitleText} | {ChallengeMenu.TierText(0)} | {ChallengeMenu.TierText(1)} | {ChallengeMenu.TierText(2)}");
            Check(ChallengeMenu.Selected == 0, $"默认选中第一档 / first tier selected: {ChallengeMenu.Selected}");
            yield return Shot("challenge", ui: true);
            if (!SameMenu())
            {
                Check(false, "挑战面板变了，停止巡视 / the challenge menu changed, tour stopped");
                yield break;
            }
            // 方向键：照输入模块发移动事件，走按钮自己的导航（第一档下 → 第二档；第二档上、再上 → 返回，循环）。
            ChallengeMenu.MoveForTest(UnityEngine.EventSystems.MoveDirection.Down, menuId);
            yield return null;
            int afterDown = ChallengeMenu.Selected;
            ChallengeMenu.MoveForTest(UnityEngine.EventSystems.MoveDirection.Up, menuId);
            yield return null;
            ChallengeMenu.MoveForTest(UnityEngine.EventSystems.MoveDirection.Up, menuId);
            yield return null;
            int afterUpUp = ChallengeMenu.Selected;
            Check(afterDown == 1 && afterUpUp == 3, $"方向键导航 / navigation: down→{afterDown}, up up→{afterUpUp}（预期 / expected 1, 3）");
            // 焦点丢了（比如鼠标点了空白处）：下一帧选回上次选中的按钮，手柄、键盘还能接着用。
            if (UnityEngine.EventSystems.EventSystem.current != null)
            {
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            }
            yield return null;
            yield return null;
            Check(ChallengeMenu.Selected == 3, $"焦点丢了以后选回上次的按钮 / focus restored after losing it: {ChallengeMenu.Selected}");
            ChallengeMenu.Highlight(2, menuId);
            yield return new WaitForSecondsRealtime(0.5f);
            Check(ChallengeMenu.Selected == 2, $"选中第三档 / third tier selected: {ChallengeMenu.Selected}");
            yield return Shot("challenge-tier3", ui: true);
            if (!SameMenu())
            {
                Check(false, "挑战面板变了，停止巡视 / the challenge menu changed, tour stopped");
                yield break;
            }
            // 取消键：走游戏界面的取消（按钮上的取消触发器）。淡出期间还扣着交互、暂停和操作；淡出完面板关掉、交还操作、界面输入原样还回。
            Check(ChallengeMenu.PressForTest(2, menuId, cancel: true), "按取消键 / pressed cancel");
            yield return null;
            Check(LoomUI.Instance.ChallengeBusy && ChallengeMenu.Busy && Sanctum.InteractionBlocked && PlayerData.instance.disablePause && hero.controlReqlinquished,
                $"淡出期间还扣着交互、暂停、操作 / interaction, pause and control held while fading out (busy={LoomUI.Instance.ChallengeBusy})");
            yield return new WaitForSecondsRealtime(1.2f);
            string bitsAfter = UiBits();
            UnityEngine.EventSystems.EventSystem? esNow = UnityEngine.EventSystems.EventSystem.current;
            Check(!LoomUI.Instance.ChallengeBusy && !ChallengeMenu.Busy && !Sanctum.InteractionBlocked && !hero.controlReqlinquished && bitsAfter == bitsBefore
                && (esNow == null || esNow.currentSelectedGameObject == null),
                $"取消后面板关掉、能操作、界面输入和暂停原样还回 / cancel closes the menu (control={!hero.controlReqlinquished}; {bitsBefore} → {bitsAfter})");
            yield return Shot("challenge-closed", ui: true);
            // 再打开，用确认键选“重织”开战（和玩家按确认走同一条路）。
            target.Interact.Interact();
            yield return new WaitForSecondsRealtime(0.8f);
            menuId = LoomUI.Instance.ChallengeSession;
            Check(LoomUI.Instance.ChallengeNative && menuId > 0, $"再次打开挑战面板 / challenge menu reopened (#{menuId})");
            if (!SameMenu())
            {
                Check(false, "挑战面板变了，停止巡视 / the challenge menu changed, tour stopped");
                yield break;
            }
            ChallengeMenu.Highlight(1, menuId);
            yield return new WaitForSecondsRealtime(0.3f);
            if (!SameMenu())
            {
                Check(false, "挑战面板变了，停止巡视 / the challenge menu changed, tour stopped");
                yield break;
            }
            Check(ChallengeMenu.PressForTest(1, menuId), "按确认键选重织 / pressed submit on tier 2");
            // 面板淡出完才开战。
            until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && mc.State == LoomState.Lobby && LoomUI.Instance != null && LoomUI.Instance.ChallengeBusy)
            {
                yield return null;
            }
            yield return null;
            // 没能开战（快照失败、正在存档）就停在这里，不当成战斗处理（Codex 审查 v0.5.0 #5）。
            if (mc.State != LoomState.ToFight)
            {
                Check(false, $"没能开战 / fight did not start (state={mc.State})");
                yield break;
            }
            mc.MarkAssisted("hall tour / 长廊巡视");
            until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until && mc.State == LoomState.ToFight)
            {
                yield return null;
            }
            if (mc.State != LoomState.Fight)
            {
                Check(false, $"没进战场 / did not reach the arena (state={mc.State})");
                yield break;
            }
            // 固定这一场：等待以后还是同一个槽、同一份数据、同一场战斗才继续动手。
            int serial = mc.FightSerial;
            BossDef? fightBoss = mc.CurrentBoss;
            bool SameFight() => Same() && mc.State == LoomState.Fight && mc.FightSerial == serial && mc.CurrentBoss == fightBoss;
            yield return new WaitForSecondsRealtime(2f);
            if (!SameFight())
            {
                Check(false, $"等待期间战斗变了，停止巡视 / the fight changed while waiting, tour stopped (state={mc.State})");
                yield break;
            }
            hero = HeroController.instance;
            bool boss = BossSceneController.IsBossScene;
            int level = boss ? BossSceneController.Instance.BossLevel : -1;
            Check(boss && level == 1, $"进场 / arena: tier={mc.CurrentTier} IsBossScene={boss} BossLevel={level}");
            Check(SanctumArt.LoadedCount == 0, $"进战场后图集已释放 / atlases released in the arena ({SanctumArt.LoadedCount} left)");
            yield return Shot("arena-tier1");

            // 伤害翻倍：打自己 1 点敌人伤害，应该少 2 血（HeroController.TakeDamage：BossLevel 1 时伤害 ×2）。
            until = Time.unscaledTime + 8f;
            while (Time.unscaledTime < until && !hero.CanTakeDamage())
            {
                yield return null;
            }
            PlayerData pd = PlayerData.instance;
            bool can = hero.CanTakeDamage();
            if (!SameFight() || !can)
            {
                // 前提不满足就不扣血（不能受伤时打了也不算数）。
                Check(false, $"重织伤害翻倍：前提不满足，没有测 / double damage not tested (same fight={SameFight()}, canTakeDamage={can})");
            }
            else
            {
                int before = pd.health + pd.healthBlue;
                string mods = $"barbedWire={Equipped(GlobalSettings.Gameplay.BarbedWireTool)} luckyDice={Equipped(GlobalSettings.Gameplay.LuckyDiceTool)} bellBind={hero.WillDoBellBindHit()}";
                var dummy = new GameObject("Godhome Tour Hit");
                dummy.transform.position = hero.transform.position + Vector3.right;
                hero.TakeDamage(dummy, CollisionSide.other, 1, HazardType.ENEMY);
                int after = pd.health + pd.healthBlue;
                UnityEngine.Object.Destroy(dummy);
                Check(before - after == 2, $"重织伤害翻倍 / double damage: 打 1 点 / hit for 1 → 血 / health {before} → {after}（少了 / lost {before - after}，预期 / expected 2）canTakeDamage={can} {mods}");
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot("arena-after-hit");

            if (!SameFight())
            {
                Check(false, $"回大厅前战斗变了，不再请求回大厅 / the fight changed, not requesting the return (state={mc.State})");
                yield break;
            }
            mc.ReturnToLobby("hall tour / 长廊巡视");
            until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until && mc.State != LoomState.Lobby)
            {
                yield return null;
            }
            // 等画面重新搭好、黑幕揭开。
            until = Time.unscaledTime + 30f;
            while (Time.unscaledTime < until && (!Sanctum.Built || (LoomUI.Instance != null && LoomUI.Instance.CoverAlpha > 0.02f)))
            {
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
            hero = HeroController.instance;
            // 回大厅时场景重新读过，长廊和雕像都是新搭的：按 id 重新找。
            // 回程走游戏自己的 door_dreamReturn：英雄应该是从这尊雕像的回程门进来的，站在门那里（雕像左边 2 格）。
            Statue? again = Sanctum.Find(target.Def.Id);
            string wantGate = Sanctum.ReturnGateName(target.Def.Id)!;
            string gotGate = hero.GetEntryGateName() ?? "";
            float off = again != null ? hero.transform.position.x - again.X : float.NaN;
            Check(mc.State == LoomState.Lobby && !BossSceneController.IsBossScene && again != null && gotGate == wantGate && Mathf.Abs(off + 2f) < 1.5f && !PlayerData.instance.atBench,
                $"回来 / back: state={mc.State} IsBossScene={BossSceneController.IsBossScene} 进门 / entry gate {gotGate}（预期 / expected {wantGate}） 离雕像 / from statue {off:+0.0;-0.0} atBench={PlayerData.instance.atBench} 血 / health {PlayerData.instance.health}/{PlayerData.instance.maxHealth}");
            yield return Shot("back-at-statue");
            completed = true;
        }
        finally
        {
            Running = false;
            string result = !completed ? $"没跑完 / incomplete（{fails} 项没通过 / failed）" : fails == 0 ? "全部通过 / all passed" : fails + " 项没通过 / failed";
            lines.Insert(3, $"- 结果 / result: {result}");
            string report = Path.Combine(Paths.BepInExRootPath, "Godhome", $"halltour-{stamp}.md");
            File.WriteAllText(report, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            GodhomePlugin.Log.LogInfo($"[Godhome] 巡视结束 / tour finished ({result}): {report}");
        }
        // 调试关了、换了档就不替人退出游戏。
        if (GodhomeConfig.DebugQuitAfterSelfTest.Value && Same())
        {
            yield return new WaitForSecondsRealtime(5f);
            GodhomePlugin.Log.LogInfo("[Godhome] 巡视后退出游戏 / quitting after the hall tour");
            Application.Quit();
        }
    }

    // 拉杆当前帧是不是 ant_lever_animated{from..to}（4 位编号）。
    private static bool IsLeverFrame(string? name, int from, int to)
    {
        const string prefix = "ant_lever_animated";
        if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        return int.TryParse(name.Substring(prefix.Length), out int n) && n >= from && n <= to;
    }

    private static bool Equipped(ToolItem? t)
    {
        try
        {
            return t != null && t.IsEquipped;
        }
        catch
        {
            return false;
        }
    }
}
