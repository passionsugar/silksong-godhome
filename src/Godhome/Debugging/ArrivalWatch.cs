using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GlobalEnums;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Godhome;

/// <summary>
/// [Godhome] 调试用：进场落点检查（玩家反馈“进下一场有时撞在 Boss 身上掉血，或者直接生成在岩浆里”）。
/// 人进场（Arena.OnHeroEntered）后 Window 秒内，HeroController.TakeDamage 每被调一次都记下：时间、来源、伤害、危险类型、人的位置。
/// 自检开着无敌，伤害被挡掉，调用照样有（HeroBox 碰到 DamageHero 就调，无敌在 TakeDamage 里面才判，反编译 HeroBox.cs、HeroController.cs:5369）。
/// 进场时另记离落点最近的 Boss 碰撞体有多远。窗口结束写一行“进场检查”，自检结束时汇总有问题的场次。
/// 只在调试打开、存档槽允许时记；补丁只在调试打开时打（GodhomePlugin.DebugOnlyPatches）。
/// </summary>
internal static class ArrivalWatch
{
    /// <summary>
    /// 进场后看多少秒（不算暂停）。第八轮是 4 秒：苍白苜鹿落在它身上，吼叫定住人、吼完才开伤害盒，4 秒内没碰到，漏了；改成 8 秒。
    /// </summary>
    internal const float Window = 8f;

    private static string boss = "";
    private static int serial;
    private static float watched = -1f;
    private static Vector2 landed;
    private static readonly List<string> hits = new();

    /// <summary>这次自检里进场挨过打的场次（自检结束时汇总后清掉）。</summary>
    private static readonly List<string> flagged = new();

    internal static bool Watching => watched >= 0f && watched <= Window;

    private static readonly Regex CloneSuffix = new(@"(\(Clone\)|\s\(\d+\))+$", RegexOptions.Compiled);

    internal static void Begin(MonoBehaviour host, BossDef b, Vector2 at)
    {
        if (!DebugTools.DebugAllowed)
        {
            return;
        }
        boss = b.Id;
        landed = at;
        hits.Clear();
        watched = 0f;
        int mine = ++serial;
        string near = NearestBoss(b, at);
        GodhomePlugin.Log.LogInfo($"[Godhome] 落点勘察 / landing survey {b.Id}：{Survey(b, at)}");
        host.StartCoroutine(Run(mine, near));
    }

    private const int TerrainLayer = 8;

    private static readonly Regex BattleTrigger = new("Battle|Start|Alert|Wake|Range|Arena", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 落点勘察（调表用）：原落点左右 16 格内每格往下找地面，排除：门的位置在墙里、站的地方有伤害（岩浆、尖刺、敌人身体）、
    /// 站的地方出了原落点所在的触发区（开战要人进的区域，出去了可能不开战）。剩下的按离 Boss 碰撞体的距离（封顶 12 格）减一点挪动距离排，
    /// 给出最好的几个门的位置（y 按原门比原落点地面高多少算）。只记日志，不改落点。
    /// </summary>
    private static string Survey(BossDef b, Vector2 landedAt)
    {
        try
        {
            int terrain = 1 << TerrainLayer;
            HeroController hero = HeroController.SilentInstance;
            Vector2 door = b.Arena.Door ?? landedAt;
            RaycastHit2D g0 = Physics2D.Raycast(new Vector2(landedAt.x, landedAt.y + 0.5f), Vector2.down, 12f, terrain);
            if (!g0)
            {
                return "原落点下面 12 格内没有地面 / no ground under the landing";
            }
            float doorAbove = door.y - g0.point.y;
            List<Collider2D> bossCols = BossColliders(b);
            // 离 Boss 多远：只算实体碰撞体和伤害区（Boss 身上的警戒、开战范围之类的触发区不算身体）。
            List<Collider2D> body = bossCols.Where(c => !c.isTrigger || c.GetComponent<DamageHero>() != null).ToList();
            bool Mine(Collider2D c) => hero != null && c.transform.IsChildOf(hero.transform);
            // 要留在里面的触发区：名字像开战用的（Battle、Start、Alert、Wake、Range、Arena），镜头、音效、环境区域不算；Boss 身上的也算（开战范围）。
            List<Collider2D> triggers = Physics2D.OverlapPointAll(landedAt)
                .Where(c => c != null && c.isTrigger && !Mine(c) && c.GetComponent<DamageHero>() == null
                    && BattleTrigger.IsMatch(c.name) && !c.name.StartsWith("Needolin", StringComparison.Ordinal))
                .ToList();
            float Dist(Vector2 p) => body.Count == 0 ? 0f : body.Min(c => Vector2.Distance(p, c.bounds.ClosestPoint(p)));
            string where = body.Count == 0 ? "-" : string.Join(",", body.Where(c => !c.isTrigger).Take(3)
                .Select(c => $"{c.name}({c.bounds.center.x:0.0},{c.bounds.center.y:0.0} 宽 / w {c.bounds.size.x:0.0})"));
            var cands = new List<(float X, float Y, float D, float Dx)>();
            for (float dx = -16f; dx <= 16f; dx += 1f)
            {
                float x = landedAt.x + dx;
                var top = new Vector2(x, door.y);
                if (Physics2D.OverlapPoint(top, terrain) != null)
                {
                    continue;
                }
                RaycastHit2D g = Physics2D.Raycast(top, Vector2.down, 12f, terrain);
                if (!g)
                {
                    continue;
                }
                var stand = new Vector2(x, g.point.y + 0.8f);
                bool hurts = Physics2D.OverlapPointAll(stand).Concat(Physics2D.OverlapPointAll(new Vector2(x, g.point.y + 0.1f)))
                    .Any(c => c != null && !Mine(c) && c.GetComponent<DamageHero>() is DamageHero d && d.enabled && d.damageDealt > 0);
                if (hurts || triggers.Any(t => !t.OverlapPoint(stand)))
                {
                    continue;
                }
                cands.Add((x, g.point.y + doorAbove, Dist(stand), Mathf.Abs(dx)));
            }
            string inside = triggers.Count == 0 ? "-" : string.Join(",", triggers.Select(t => t.name).Distinct().Take(6));
            string head = $"原落点离 Boss / boss {Dist(landedAt):0.0} 格；Boss 身体 / body {where}；要留在的触发区 / keep inside {inside}";
            if (cands.Count == 0)
            {
                return $"{head}；没有别的可站的点 / no other spot";
            }
            var best = cands.OrderByDescending(c => Mathf.Min(c.D, 12f) - 0.1f * c.Dx).Take(3)
                .Select(c => $"({c.X:0.0}, {c.Y:0.0}) 离 Boss / boss {c.D:0.0}，挪 / moved {c.Dx:0}");
            string xs = string.Join(" ", cands.Select(c => $"{c.X:0}:{c.D:0}"));
            return $"{head}；可站 / standable {cands.Count} 处（x:离 Boss / x:dist {xs}）；建议门 / suggested door：{string.Join("；", best)}";
        }
        catch (Exception e)
        {
            return "? " + e.Message;
        }
    }

    /// <summary>表里 bossObjects 的碰撞体（物体关着的也算：进场时还没出来的 Boss 一会儿也会在那）。</summary>
    private static List<Collider2D> BossColliders(BossDef b)
    {
        var list = new List<Collider2D>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (!s.isLoaded)
            {
                continue;
            }
            foreach (GameObject r in s.GetRootGameObjects())
            {
                foreach (HealthManager hm in r.GetComponentsInChildren<HealthManager>(true))
                {
                    if (hm != null && b.BossObjects.Contains(CloneSuffix.Replace(hm.gameObject.name, "")))
                    {
                        list.AddRange(hm.GetComponentsInChildren<Collider2D>(true).Where(c => c != null));
                    }
                }
            }
        }
        // 开着的碰撞体有就只用开着的（关着的碰撞体的范围不可靠）。
        List<Collider2D> on = list.Where(c => c.enabled && c.gameObject.activeInHierarchy).ToList();
        return on.Count > 0 ? on : list;
    }

    private static IEnumerator Run(int mine, string near)
    {
        while (mine == serial && watched >= 0f && watched <= Window)
        {
            yield return null;
            if (!(GameManager.UnsafeInstance is GameManager gm && gm.IsGamePaused()))
            {
                watched += Time.unscaledDeltaTime;
            }
        }
        if (mine != serial)
        {
            yield break;
        }
        watched = -1f;
        string summary = hits.Count == 0
            ? "没碰到伤害 / no damage"
            : $"碰到伤害 {hits.Count} 次 / {hits.Count} damage calls：" + string.Join("；", hits.GroupBy(h => h).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).Take(8));
        string line = $"[Godhome] 进场检查 / arrival check {boss}：落点 / landed ({landed.x:0.0}, {landed.y:0.0})，最近的 Boss / nearest boss {near}；{Window:0} 秒内 / within {Window:0} s {summary}";
        if (hits.Count == 0)
        {
            GodhomePlugin.Log.LogInfo(line);
        }
        else
        {
            GodhomePlugin.Log.LogWarning(line);
            flagged.Add($"{boss}（{hits.Count}：{hits[0]}）");
        }
    }

    /// <summary>HeroController.TakeDamage 前缀里调：窗口里就记一笔。</summary>
    internal static void OnDamage(HeroController hero, GameObject? go, int damage, HazardType hazard)
    {
        if (!Watching)
        {
            return;
        }
        try
        {
            Vector2 p = hero.transform.position;
            string src = go != null ? SceneTools.PathOf(go.transform) : "-";
            hits.Add($"{watched:0.0}s {src} 伤害 / dmg {damage} {hazard} 人在 / hero ({p.x:0.0}, {p.y:0.0})");
        }
        catch (Exception e)
        {
            hits.Add($"{watched:0.0}s ? ({e.Message})");
        }
    }

    /// <summary>自检结束时：这次有哪些场次进场挨过打。</summary>
    internal static void Summary()
    {
        if (!DebugTools.DebugAllowed)
        {
            return;
        }
        GodhomePlugin.Log.LogInfo(flagged.Count == 0
            ? "[Godhome] 进场检查汇总 / arrival summary：没有场次进场就碰到伤害 / no fight took damage on arrival"
            : $"[Godhome] 进场检查汇总 / arrival summary：{flagged.Count} 场进场就碰到伤害 / fights hit on arrival：{string.Join("，", flagged)}");
        flagged.Clear();
    }

    /// <summary>离落点最近的 Boss 物体（表里的 bossObjects）：碰撞体最近点的距离，物体关着的也列（标“关”）。</summary>
    private static string NearestBoss(BossDef b, Vector2 at)
    {
        try
        {
            var found = new List<(string Name, float Dist, bool On)>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded)
                {
                    continue;
                }
                foreach (GameObject r in s.GetRootGameObjects())
                {
                    foreach (HealthManager hm in r.GetComponentsInChildren<HealthManager>(true))
                    {
                        if (hm == null || !b.BossObjects.Contains(CloneSuffix.Replace(hm.gameObject.name, "")))
                        {
                            continue;
                        }
                        float d = float.MaxValue;
                        foreach (Collider2D c in hm.GetComponentsInChildren<Collider2D>(true))
                        {
                            if (c != null && !c.isTrigger)
                            {
                                d = Mathf.Min(d, Vector2.Distance(at, c.bounds.ClosestPoint(at)));
                            }
                        }
                        if (d == float.MaxValue)
                        {
                            d = Vector2.Distance(at, hm.transform.position);
                        }
                        found.Add((hm.gameObject.name, d, hm.gameObject.activeInHierarchy));
                    }
                }
            }
            if (found.Count == 0)
            {
                return "-";
            }
            var n = found.OrderBy(f => f.Dist).First();
            return $"{n.Name} {n.Dist:0.0} 格 / units{(n.On ? "" : "（关 / off）")}";
        }
        catch (Exception e)
        {
            return "? " + e.Message;
        }
    }
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class ArrivalDamagePatch
{
    private static void Prefix(HeroController __instance, GameObject go, int damageAmount, HazardType hazardType)
    {
        ArrivalWatch.OnDamage(__instance, go, damageAmount, hazardType);
    }
}
