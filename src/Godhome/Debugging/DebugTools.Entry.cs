using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// 调试：入场点专项（0.10.5，双苔母有一次被放在地图外面进不来）。只对 EntryCheckBosses 里的 Boss 跑：
/// 1. 落点：人在开战触发区（BattleScene）里、站在地上；
/// 2. 危险复活：关掉无敌，用游戏自己的 TakeDamage 吃一下尖刺类伤害，走原版的危险死亡、危险复活，看复活到哪；
/// 3. 死亡重试（这一场打完回大厅以后）：再进一次、被打死、回大厅，再进第三次，落点要和第一次一样，最后放弃回大厅。
/// 全程走游戏和模组自己的流程，自检只发伤害、开战和放弃。
/// </summary>
internal sealed partial class DebugTools
{
    private static readonly HashSet<string> EntryCheckBosses = new() { "moss_mother_duo" };

    private static bool EntryChecked(BossDef b) => EntryCheckBosses.Contains(b.Id);

    private void EntryNote(Row row, bool ok, string what, string detail)
    {
        row.EntryCheck = (row.EntryCheck ?? true) && ok;
        row.Note += (row.Note.Length > 0 ? "; " : "") + $"{(ok ? "PASS" : "FAIL")} {what}: {detail}";
        GodhomePlugin.Log.LogInfo($"[Godhome]   入场检查 / entry check {row.Boss.Id}: {(ok ? "PASS" : "FAIL")} {what}: {detail}");
    }

    /// <summary>开战触发区（场景里启用的 BattleScene 物体上的触发碰撞体）用形状数据算的世界范围；触发区开战后会被关掉，不能用 bounds。</summary>
    private static List<Rect> BattleRegions(BossDef b)
    {
        var list = new List<Rect>();
        List<string> scenes = ModeController.ScenesOf(b);
        foreach (BattleScene bs in Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (bs == null || !scenes.Contains(bs.gameObject.scene.name))
            {
                continue;
            }
            foreach (Collider2D c in bs.GetComponents<Collider2D>())
            {
                if (c == null || !c.isTrigger)
                {
                    continue;
                }
                var pts = new List<Vector2>();
                if (c is BoxCollider2D box)
                {
                    Vector2 h = box.size * 0.5f;
                    pts.AddRange(new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) });
                }
                else if (c is PolygonCollider2D poly)
                {
                    for (int p = 0; p < poly.pathCount; p++)
                    {
                        pts.AddRange(poly.GetPath(p));
                    }
                }
                else
                {
                    continue;
                }
                Vector2[] world = pts.Select(p => (Vector2)c.transform.TransformPoint(p + c.offset)).ToArray();
                if (world.Length == 0)
                {
                    continue;
                }
                float x0 = world.Min(p => p.x), x1 = world.Max(p => p.x), y0 = world.Min(p => p.y), y1 = world.Max(p => p.y);
                list.Add(Rect.MinMaxRect(x0, y0, x1, y1));
            }
        }
        return list;
    }

    private static string Spot(Vector2 p, List<Rect> regions)
    {
        string inside = regions.Any(r => r.Contains(p)) ? "在开战触发区里 / inside the battle trigger" : "不在开战触发区里 / outside the battle trigger";
        string all = regions.Count == 0 ? "-" : string.Join(" ", regions.Select(r => $"[{r.xMin:0.0}..{r.xMax:0.0} × {r.yMin:0.0}..{r.yMax:0.0}]"));
        return $"({p.x:0.00}, {p.y:0.00}) {inside}，触发区 / triggers {all}";
    }

    /// <summary>等人就位、能动、站稳（入场修正、开场演出都做完）；返回站的位置。</summary>
    private IEnumerator Settle(float timeout, Vector2[] at)
    {
        float until = Time.unscaledTime + timeout;
        while (Time.unscaledTime < until)
        {
            Beat();
            HeroController h = HeroController.SilentInstance;
            if (h != null && h.isHeroInPosition && h.acceptingInput && !h.controlReqlinquished && h.cState.onGround
                && !h.cState.transitioning && !h.cState.hazardRespawning && h.rb2d.linearVelocity.sqrMagnitude < 0.01f)
            {
                break;
            }
            yield return null;
        }
        HeroController hero = HeroController.SilentInstance;
        at[0] = hero != null ? (Vector2)hero.transform.position : new Vector2(float.NaN, float.NaN);
    }

    /// <summary>进场以后、开战之前：落点、危险复活。</summary>
    private IEnumerator EntryChecks(ModeController mc, BossDef b, string shot, Row row)
    {
        var at = new Vector2[1];
        yield return Settle(6f, at);
        HeroController hero = HeroController.instance;
        List<Rect> regions = BattleRegions(b);
        bool inside = regions.Any(r => r.Contains(at[0]));
        EntryNote(row, inside && hero.cState.onGround, "落点 / landing", Spot(at[0], regions) + $"，着地 / grounded {hero.cState.onGround}");
        if (mc.State != LoomState.Fight || !Allowed())
        {
            yield break;
        }

        // 危险复活：游戏自己的路（TakeDamage → 危险死亡 → GameManager.HazardRespawn → HeroController.HazardRespawn）。
        AllowHeroDamage = true;
        float until = Time.unscaledTime + 8f;
        while (Time.unscaledTime < until && (InvincibleActive || !hero.CanTakeDamage()))
        {
            Beat();
            yield return null;
        }
        if (InvincibleActive || !hero.CanTakeDamage() || mc.State != LoomState.Fight || !Allowed())
        {
            AllowHeroDamage = false;
            EntryNote(row, false, "危险复活 / hazard respawn", $"没能挨打，没有测 / could not take damage (invincible={InvincibleActive}, can={hero.CanTakeDamage()}, state={mc.State})");
            yield break;
        }
        int hp = PlayerData.instance.health;
        Vector2 hazardAt = PlayerData.instance.hazardRespawnLocation;
        var dummy = new GameObject("Godhome Self-test Spikes");
        dummy.transform.position = hero.transform.position + Vector3.down;
        string err = "";
        try
        {
            hero.TakeDamage(dummy, CollisionSide.bottom, 1, HazardType.SPIKES);
        }
        catch (Exception e)
        {
            err = "；出错 / error: " + e.Message;
        }
        Object.Destroy(dummy);
        bool sawHazard = false;
        until = Time.unscaledTime + 12f;
        while (Time.unscaledTime < until)
        {
            Beat();
            sawHazard |= hero.cState.hazardDeath || hero.cState.hazardRespawning;
            if (sawHazard && !hero.cState.hazardDeath && !hero.cState.hazardRespawning && hero.acceptingInput && !hero.controlReqlinquished && hero.cState.onGround)
            {
                break;
            }
            yield return null;
        }
        AllowHeroDamage = false;
        yield return Wait(0.5f);
        Vector2 back = hero.transform.position;
        float off = Vector2.Distance(back, at[0]);
        bool ok = err.Length == 0 && sawHazard && mc.State == LoomState.Fight && regions.Any(r => r.Contains(back)) && hero.cState.onGround && off < 2f;
        EntryNote(row, ok, "危险复活 / hazard respawn",
            $"复活到 / respawned at {Spot(back, regions)}，离落点 / from landing {off:0.0}，复活点 / respawn point ({hazardAt.x:0.0}, {hazardAt.y:0.0})，"
            + $"走了危险死亡 / hazard death seen {sawHazard}，血 / hp {hp}→{PlayerData.instance.health}，状态 / state {mc.State}，着地 / grounded {hero.cState.onGround}{err}");
        yield return Shots.Capture(shot + "-1b-hazard.jpg", row.Shots);
    }

    /// <summary>这一场打完、回到大厅以后：再进、被打死、回大厅，再进一次看落点，放弃回大厅。</summary>
    private IEnumerator RetryCheck(ModeController mc, BossDef b, string shot, Row row, float timeout)
    {
        var first = new Vector2[1];
        var second = new Vector2[1];
        List<Rect> regions;

        // 再进一次，被打死。
        yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
        mc.StartFight(b);
        yield return WaitFor(() => mc.State != LoomState.ToFight, timeout);
        if (mc.State != LoomState.Fight || Arena.EnteredVia != Arena.EnterGate || !Allowed())
        {
            EntryNote(row, false, "死亡重试 / death retry", $"第二次没进场 / second entry failed (state={mc.State}, via={Arena.EnteredVia})");
            yield break;
        }
        yield return Settle(8f, first);
        regions = BattleRegions(b);
        HeroController hero = HeroController.instance;
        AllowHeroDamage = true;
        float until = Time.unscaledTime + 8f;
        while (Time.unscaledTime < until && (InvincibleActive || !hero.CanTakeDamage()))
        {
            Beat();
            yield return null;
        }
        if (InvincibleActive || !hero.CanTakeDamage() || mc.State != LoomState.Fight || !Allowed())
        {
            AllowHeroDamage = false;
            EntryNote(row, false, "死亡重试 / death retry", $"没能挨打 / could not take damage (state={mc.State})");
            if (mc.State == LoomState.Fight && Allowed())
            {
                mc.ReturnToLobby("self-test retry check");
                yield return WaitFor(() => mc.State == LoomState.Lobby, 60f);
            }
            yield break;
        }
        var dummy = new GameObject("Godhome Self-test Hit");
        dummy.transform.position = hero.transform.position + Vector3.right;
        string err = "";
        try
        {
            hero.TakeDamage(dummy, CollisionSide.other, 99, HazardType.ENEMY);
        }
        catch (Exception e)
        {
            err = "；出错 / error: " + e.Message;
        }
        Object.Destroy(dummy);
        yield return Wait(0.5f);
        bool died = hero.cState.dead || PlayerData.instance.health <= 0 || mc.LastResult == FightResult.Died;
        yield return WaitFor(() => mc.State == LoomState.Lobby, 90f);
        AllowHeroDamage = false;
        FightResult result = mc.LastResult;
        var fall = new HeroCheck.Result();
        if (mc.State == LoomState.Lobby)
        {
            yield return Wait(1.5f);
            yield return Shots.Capture(shot + "-5-died.jpg", row.Shots);
            yield return HeroCheck.Fall(fall);
        }
        EntryNote(row, err.Length == 0 && died && mc.State == LoomState.Lobby && result == FightResult.Died && fall.Ok, "死亡回大厅 / death back to the lobby",
            $"第二次落点 / second landing {Spot(first[0], regions)}，死了 / died {died}，结果 / result {result}，状态 / state {mc.State}；{(fall.Ok ? "PASS " : "FAIL ")}{fall.Detail}{err}");
        if (mc.State != LoomState.Lobby || !Allowed())
        {
            yield break;
        }

        // 第三次：重试的落点要和前面一样。
        yield return Wait(2f);
        yield return WaitFor(() => !SaveGuard.SaveQueueBusy(), 15f);
        mc.StartFight(b);
        yield return WaitFor(() => mc.State != LoomState.ToFight, timeout);
        bool entered = mc.State == LoomState.Fight && Arena.EnteredVia == Arena.EnterGate;
        if (entered)
        {
            yield return Settle(8f, second);
            regions = BattleRegions(b);
            yield return Shots.Capture(shot + "-6-retry.jpg", row.Shots);
        }
        float d = entered ? Vector2.Distance(first[0], second[0]) : float.NaN;
        EntryNote(row, entered && d < 1f && regions.Any(r => r.Contains(second[0])), "重试落点 / retry landing",
            entered ? $"{Spot(second[0], regions)}，和第二次差 / differs from the second entry by {d:0.00}" : $"没进场 / not entered (state={mc.State})");
        if (mc.State == LoomState.Fight && Allowed())
        {
            mc.ReturnToLobby("self-test retry check");
            yield return WaitFor(() => mc.State == LoomState.Lobby, 60f);
        }
    }
}
