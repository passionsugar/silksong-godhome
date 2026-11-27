using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// 调试：仇恨专项（0.10.6，玩家报二见兽蝇“飞到一边停着不打”；第九轮只照 FSM 改了两处 GO HOME，没人手打过）。
/// 只对 AggroCheckBosses 里的 Boss 跑：开战后先记下场地（Boss 看人在不在的范围、上升气流、可站的地方），
/// 再把人放到这个范围外面、场地里站得住的几处，每处看 Window 秒 Boss 的 FSM 有没有进出招的状态；最后放回落点接着打。
/// 人放过去是瞬移（自检做不了平台跳跃），这几处玩家能不能走到要看日志里的位置和截图。
/// </summary>
internal sealed partial class DebugTools
{
    private sealed class AggroSpec
    {
        internal string Path = "";
        internal string Fsm = "Control";
        /// <summary>Boss 看人在不在的范围（AlertRange 物体名）。</summary>
        internal string Range = "";
        /// <summary>冲着人出招才会进的状态（| 分隔）：进过任何一个、而且到过人附近，才算还在打人。</summary>
        internal string Attacks = "";
        /// <summary>不打人的状态（| 分隔）：进过就不算（例如兽蝇的撞墙循环、飞回家）。</summary>
        internal string Idle = "";
    }

    private static readonly Dictionary<string, AggroSpec> AggroCheckBosses = new()
    {
        // 重打版每次选招（Choice）、踩地前（Stomp Pos）看人在不在 Arena Range（Rematch Ready 时从它身上摘下来），不在就不打（第九轮改接 Charge Pos）；
        // Charge Pos 要等人进它自己的 Charge Range 才冲。Charge Pos / Stomp Pos / Roar Pos 只从 Choice 冲着人的分支进；
        // Door Slam（Final Slam 没设时 Choice 每次都走它：原高度撞墙、墙边停）和 Buzz To Home 不打人（0.10.6 第一轮自检里两种都算成了出招，判错）。
        ["savage_beastfly_2"] = new AggroSpec
        {
            Path = "Boss Scene Beastfly/Beastfly States/Active/Bone Flyer Giant",
            Range = "Arena Range",
            Attacks = "Charge Pos|Stomp Pos|Roar Pos|Stomp Antic|Stomp Antic R|Roar Antic",
            Idle = "Door Slam Pause|Door Slam Antic|Idly Fly Audio?|Buzz To Home",
        },
    };

    private const float AggroWindow = 8f;

    /// <summary>Boss 到过人多近才算冲着人来（格）。</summary>
    private const float AggroNear = 8f;

    private static bool AggroChecked(BossDef b) => AggroCheckBosses.ContainsKey(b.Id);

    /// <summary>一个 BoxCollider2D 的世界范围（物体没激活时 bounds 是空的，用形状算）。</summary>
    private static Rect BoxRect(BoxCollider2D box)
    {
        Vector2 h = box.size * 0.5f;
        Vector2[] w = new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) }
            .Select(p => (Vector2)box.transform.TransformPoint(p + box.offset)).ToArray();
        return Rect.MinMaxRect(w.Min(p => p.x), w.Min(p => p.y), w.Max(p => p.x), w.Max(p => p.y));
    }

    private static string R(Rect r) => $"x {r.xMin:0.0}..{r.xMax:0.0} × y {r.yMin:0.0}..{r.yMax:0.0}";

    private IEnumerator AggroChecks(ModeController mc, BossDef b, List<string> scenes, string shot, Row row)
    {
        AggroSpec spec = AggroCheckBosses[b.Id];
        PlayMakerFSM? fsm = SceneTools.FindFsm(spec.Path, spec.Fsm, scenes);
        BoxCollider2D? range = Object.FindObjectsByType<AlertRange>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(a => a != null && a.name == spec.Range && scenes.Contains(a.gameObject.scene.name))
            .Select(a => a.GetComponent<BoxCollider2D>()).FirstOrDefault(c => c != null);
        HeroController hero = HeroController.instance;
        Collider2D? heroCol = hero != null ? hero.GetComponent<Collider2D>() : null;
        if (fsm == null || range == null || hero == null || heroCol == null)
        {
            EntryNote(row, false, "仇恨检查 / aggro check", $"找不到 / missing：FSM {(fsm != null)}，范围 / range {(range != null)}，人 / hero {(hero != null)}");
            yield break;
        }
        Rect inRange = BoxRect(range);
        Vector2 back = Arena.Landed ?? (Vector2)hero.transform.position;

        // 场地：上升气流（人能被吹上去的地方）、本场所有地面，找范围外面能站的点（地面朝上、头顶留得下人）。
        string drafts = string.Join("；", Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(c => c != null && c.isTrigger && c.name.StartsWith("Updraft Region", System.StringComparison.Ordinal) && scenes.Contains(c.gameObject.scene.name))
            .Select(c => $"{c.name} {R(Rect.MinMaxRect(c.bounds.min.x, c.bounds.min.y, c.bounds.max.x, c.bounds.max.y))}"));
        Vector2 size = heroCol.bounds.size;
        var spots = new List<(Vector2 P, float Out, string Side)>();
        for (float x = inRange.xMin - 30f; x <= inRange.xMax + 30f; x += 1.5f)
        {
            foreach (RaycastHit2D h in Physics2D.RaycastAll(new Vector2(x, inRange.yMax + 40f), Vector2.down, 90f, 1 << 8))
            {
                if (h.collider == null || h.collider.isTrigger || h.normal.y < 0.7f)
                {
                    continue;
                }
                Vector2 feet = h.point;
                Vector2 centre = feet + new Vector2(0f, size.y * 0.5f + 0.1f);
                if (Physics2D.OverlapBox(centre, size * 0.9f, 0f, 1 << 8) != null)
                {
                    continue;
                }
                if (inRange.Contains(centre))
                {
                    continue;
                }
                // 和落点之间有地形挡着的不要：场地天花板、墙外面的岩层顶上也“站得住”，玩家去不了
                // （0.10.6 第二轮：挑到了天花板上面 y 33.6 的两处，截图是岩层里一片黑、Boss 贴着天花板往上够）。
                if (Physics2D.Linecast(centre, back, 1 << 8).collider != null)
                {
                    continue;
                }
                float dx = Mathf.Max(inRange.xMin - centre.x, 0f, centre.x - inRange.xMax);
                float dy = Mathf.Max(inRange.yMin - centre.y, 0f, centre.y - inRange.yMax);
                string side = dy > dx ? (centre.y > inRange.yMax ? "上 / above" : "下 / below") : centre.x < inRange.xMin ? "左 / left" : "右 / right";
                spots.Add((feet, Mathf.Max(dx, dy), side));
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome]   仇恨检查 / aggro check {b.Id}：Boss 看人的范围 / {spec.Range} {R(inRange)}；落点 / landed ({back.x:0.0}, {back.y:0.0})；"
            + $"上升气流 / updrafts {(drafts.Length > 0 ? drafts : "-")}；范围外能站的点 / standable spots outside {spots.Count}："
            + string.Join(" ", spots.OrderBy(s => s.P.x).Select(s => $"({s.P.x:0.0},{s.P.y:0.0})")));
        // 先在落点（范围里，玩家平时站的地方）看一次作对照；再每个方向挑离范围最近的一处（玩家最容易走到的），最多 3 处。
        RaycastHit2D ground = Physics2D.Raycast(back, Vector2.down, 20f, 1 << 8);
        Vector2 home = new(back.x, ground.collider != null ? ground.point.y : back.y - 1f);
        var pick = new List<(Vector2 P, float Out, string Side)> { (home, 0f, "落点 / landing") };
        pick.AddRange(spots.GroupBy(s => s.Side).Select(g => g.OrderBy(s => s.Out).First()).Take(3));
        string[] attackNames = spec.Attacks.Split('|');
        string[] idleNames = spec.Idle.Split('|');
        int n = 0;
        foreach ((Vector2 p, float outBy, string side) in pick)
        {
            if (mc.State != LoomState.Fight || !Allowed())
            {
                yield break;
            }
            n++;
            Sanctum.StandAt(hero, p.x, p.y);
            var states = new List<string>();
            var attacks = new HashSet<string>();
            var idle = new HashSet<string>();
            float t0 = Time.unscaledTime;
            float closest = float.MaxValue;
            bool shotTaken = false;
            while (Time.unscaledTime - t0 < AggroWindow && mc.State == LoomState.Fight && Allowed())
            {
                string st = fsm.ActiveStateName;
                if (states.Count == 0 || states[states.Count - 1] != st)
                {
                    states.Add(st);
                }
                if (attackNames.Contains(st))
                {
                    attacks.Add(st);
                }
                // 刚换地方的头一秒不算：上一处没打完的招还在收尾。
                if (idleNames.Contains(st) && Time.unscaledTime - t0 > 1f)
                {
                    idle.Add(st);
                }
                closest = Mathf.Min(closest, Vector2.Distance(hero.transform.position, fsm.transform.position));
                if (!shotTaken && Time.unscaledTime - t0 > AggroWindow * 0.6f)
                {
                    shotTaken = true;
                    yield return Shots.Capture($"{shot}-aggro{n}.jpg", row.Shots);
                }
                yield return null;
            }
            Vector2 heroAt = hero.transform.position;
            Vector2 bossAt = fsm.transform.position;
            bool ok = attacks.Count > 0 && idle.Count == 0 && closest < AggroNear;
            EntryNote(row, ok, $"仇恨检查 / aggro check {n}（{side}{(outBy > 0f ? $"，出范围 / outside by {outBy:0.0}" : "")}）",
                $"人在 / hero ({heroAt.x:0.0}, {heroAt.y:0.0})，{AggroWindow:0} 秒内冲着人的招 / hero-aimed {(attacks.Count > 0 ? string.Join("、", attacks) : "无 / none")}，"
                + $"不打人的 / idle {(idle.Count > 0 ? string.Join("、", idle) : "无 / none")}，最近到过 / closest {closest:0.0} 格（要小于 / under {AggroNear:0}）；"
                + $"Boss 最后在 / boss ended at ({bossAt.x:0.0}, {bossAt.y:0.0})，状态 / states {string.Join(" → ", states.Take(14))}");
        }
        // 放回落点接着打（后面的击杀、判胜照常）。
        Sanctum.StandAt(hero, home.x, home.y);
        yield return Wait(1f);
    }
}
