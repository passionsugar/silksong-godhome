using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 前作的手办屋（GG_Workshop）：36 尊前作雕像用前作自己的代码（BossStatue 的拉杆换版、雕像沉下升起的动画、
/// 奖牌、BossChallengeUI 的三档挑战面板），放的是本作的 Boss（data/hk1.json）。
/// 改动都在补丁里（Hk1Patches）：雕像的完成情况读我们的记录，挑战面板显示本作的名字，选好难度走我们的开战流程；
/// 雕像的画换成本作 Boss 雕成的石像（和织忆圣所同一套 StatueCarver）。
/// </summary>
internal static class Hk1Hall
{
    /// <summary>
    /// 前作手办屋下层的雕像，从左到右（试验时从场景里读出来的位置）。Godhome_ 开头的是复制出来的（Extra）。
    /// （x 249.9 的 GG_Statue_Knight 只是摆设，没有 BossStatue，用不上。）
    /// </summary>
    private static readonly string[] Lower =
    {
        "Gruz", "Vengefly", "Mawlek", "FalseKnight", "Hornet", "MegaMossCharger", "Flukemarm", "MantisLords", "Oblobbles",
        "HiveKnight", "Vessel", "Nosk", "Collector", "GodTamer", "CrystalGuardian", "Uumuu", "TraitorLord", "GreyPrince",
        "Godhome_L205", "Godhome_L217", "Godhome_L226", "Godhome_L234",
    };

    /// <summary>上层，从左到右。</summary>
    private static readonly string[] Upper =
    {
        "Mage_Knight", "SoulMaster", "Defender", "Godhome_U65", "WatcherKnights", "NoEyes", "Marmu", "Xero", "Markoth", "Galien", "Gorb",
        "ElderHu", "Nailmasters", "Godhome_U155", "Paintmaster", "Sly", "HollowKnight", "Grimm", "Godhome_U202",
    };

    /// <summary>
    /// 复制出来的雕像（0.10.6 实测清单：推杆只切同一个 Boss 的形态，拆开 7 对合并以后前作的雕像不够）：名字、照哪尊复制、放在哪（x）。
    /// 位置是 0.10.7 巡视量的地面（从两层地面上方往下打射线）和截图：下层灰色王子右边（x 205，平地 21–207）、装饰门柱右边（217.5，平地 213–222；
    /// 门柱 208–212 高出一截）、装饰墙前面（226、234）；上层守卫骑士和守望骑士之间（65.2）、针师和画师之间（155.5）、格林右边（202）。
    /// 高度按复制源的底座到地面的距离，地面照射线现找；找不到地面就不放、记警告。
    /// </summary>
    private static readonly (string Name, string From, float X)[] Extra =
    {
        ("Godhome_L205", "Gruz", 205f), ("Godhome_L217", "Gruz", 217.5f), ("Godhome_L226", "Gruz", 226f), ("Godhome_L234", "Gruz", 234f),
        ("Godhome_U65", "Gorb", 65.2f), ("Godhome_U155", "Gorb", 155.5f), ("Godhome_U202", "Gorb", 202f),
    };

    private const string Prefix = "GG_Statue_";

    /// <summary>一尊前作雕像现在的样子：放的是哪组 Boss、两个展示物体上各是哪个版本、现在露出来的是哪个。</summary>
    internal sealed class Slot
    {
        public BossStatue Statue = null!;
        public string Name = "";
        public StatueDef Def = null!;
        public int Main;
        public int Alt;
        public bool UsingAlt;
        public SpriteRenderer? MainArt;
        public SpriteRenderer? AltArt;
        public string Pd = "";

        /// <summary>前作梦境版的那圈红光（BossStatueDreamToggle.litPieces）：露出来的不是第一个形态时亮。</summary>
        public GameObject? Glow;
        public ColorFader[] GlowFaders = Array.Empty<ColorFader>();

        public int ShownIndex => UsingAlt ? Alt : Main;

        public BossDef Shown => Def.Variants[ShownIndex];
    }

    /// <summary>石像最高多高（世界单位；前作最高的螳螂领主的画约 11.7）。按 Boss 真实大小摆，太高的压到这个高度。</summary>
    private const float StatueMaxHeight = 13f;

    private static Dictionary<string, List<string>> merge = new();
    private static Dictionary<string, string> special = new();
    private static Dictionary<string, StatueDef>? assignment;
    private static readonly Dictionary<int, Slot> slots = new();
    private static int token;

    /// <summary>正在雕石像（黑幕挡着）。</summary>
    internal static bool Busy { get; private set; }

    internal static string Note { get; private set; } = "";

    internal static IEnumerable<Slot> Slots => slots.Values;

    /// <summary>读 data/hk1.json 的雕像部分（合并、特殊雕像）；门的部分给 Hk1Doors。</summary>
    internal static void Load(string path)
    {
        try
        {
            JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            merge = (root["merge"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.Values<string>().Where(s => s != null).Select(s => s!).ToList())
                ?? new Dictionary<string, List<string>>();
            special = (root["special"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString()) ?? new Dictionary<string, string>();
            Hk1Doors.Load(root["doors"] as JObject);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 读 hk1.json 失败 / failed to read hk1.json: {e.Message}");
        }
        assignment = null;
    }

    /// <summary>前作雕像名 → 放在上面的那组 Boss（第一次用到时按雕像表、合并、特殊雕像排好）。</summary>
    private static Dictionary<string, StatueDef> Assignment()
    {
        if (assignment != null)
        {
            return assignment;
        }
        var groups = StatueCatalog.All.Select(s => new StatueDef { Id = s.Id, Variants = new List<BossDef>(s.Variants), Black = new HashSet<string>(s.Black) }).ToList();
        foreach (KeyValuePair<string, List<string>> m in merge)
        {
            StatueDef? into = groups.FirstOrDefault(g => g.Id == m.Key);
            if (into == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] hk1.json：合并的雕像不在雕像表里 / merge target not found: {m.Key}");
                continue;
            }
            foreach (string id in m.Value)
            {
                StatueDef? from = groups.FirstOrDefault(g => g.Id == id);
                if (from == null || from == into)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] hk1.json：要并进 {m.Key} 的雕像不在雕像表里 / merge source not found: {id}");
                    continue;
                }
                into.Variants.AddRange(from.Variants);
                into.Black.UnionWith(from.Black);
                groups.Remove(from);
            }
        }
        var map = new Dictionary<string, StatueDef>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> sp in special)
        {
            StatueDef? g = groups.FirstOrDefault(x => x.Id == sp.Value || x.Variants.Any(v => v.Id == sp.Value));
            if (g == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] hk1.json：特殊雕像 {sp.Key} 的 Boss 不在雕像表里 / special statue boss not found: {sp.Value}");
                continue;
            }
            map[sp.Key] = g;
            groups.Remove(g);
        }
        string[] rows = Lower.Concat(Upper).Select(n => Prefix + n).Where(n => !map.ContainsKey(n)).ToArray();
        for (int i = 0; i < rows.Length && i < groups.Count; i++)
        {
            map[rows[i]] = groups[i];
        }
        if (groups.Count > rows.Length)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 前作雕像不够，这些 Boss 没地方放 / not enough hk1 statues for: {string.Join(", ", groups.Skip(rows.Length).Select(g => g.Id))}");
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作手办屋 / hk1 hall: {map.Count} 尊雕像放了 {map.Values.Sum(g => g.Variants.Count)} 个 Boss / statues hold bosses（带拉杆 / with a lever {map.Values.Count(g => g.Variants.Count > 1)}）");
        assignment = map;
        return map;
    }

    internal static bool IsStatue(string name) => Assignment().ContainsKey(name);

    internal static Slot? SlotOf(BossStatue? s) => s != null && slots.TryGetValue(s.GetInstanceID(), out Slot? x) ? x : null;

    internal static Slot? SlotNamed(string name) => slots.Values.FirstOrDefault(s => s.Name == name);

    /// <summary>这个 Boss 在哪尊前作雕像上（自检、日志用）。</summary>
    internal static string? StatueOf(BossDef b) => Assignment().FirstOrDefault(kv => kv.Value.Variants.Contains(b)).Key;

    internal static void Forget()
    {
        slots.Clear();
        Busy = false;
        token++;
    }

    /// <summary>这个版本的完成情况（照前作 BossStatue.Completion）：神居存档全部解锁；三档按我们的记录；奖牌一直显示。</summary>
    internal static BossStatue.Completion CompletionOf(BossDef b, bool usingAlt)
    {
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        rec.Bosses.TryGetValue(b.Id, out BossRecord? r);
        return new BossStatue.Completion
        {
            hasBeenSeen = true,
            isUnlocked = true,
            completedTier1 = r != null && r.ClearsAt(0) > 0,
            completedTier2 = r != null && r.ClearsAt(1) > 0,
            completedTier3 = r != null && r.ClearsAt(2) > 0,
            // 前作第三档要先打过第二档才开、第一次开时有个特效：照前作的规矩走（seenTier3Unlock 记在运行时，特效只放一次）。
            seenTier3Unlock = true,
            usingAltVersion = usingAlt,
        };
    }

    // ───────────────────────────── 加载时 ─────────────────────────────

    private static readonly AccessTools.FieldRef<BossStatue, IBossStatueToggle> ToggleField = AccessTools.FieldRefAccess<BossStatue, IBossStatueToggle>("dreamToggle");

    /// <summary>
    /// 手办屋刚加载完（雕像的 Awake 已跑、Start 没跑）：每尊雕像接上本作的 Boss，换上石像；有几个版本的打开拉杆（前作的拉杆用织针敲），
    /// 梦之钉切换的那种关掉（丝之歌没有梦之钉）；藏起来的雕像（墙后的、笼子里的）直接打开。
    /// </summary>
    internal static void Prepare(Scene scene)
    {
        Forget();
        Dictionary<string, StatueDef> map = Assignment();
        AddExtra(scene);
        GameObject[] roots = scene.GetRootGameObjects();
        OpenHidden(roots);
        GodhomeSaveData rec = GodhomePlugin.Instance.Records;
        int n = 0, levers = 0, art = 0;
        var noArt = new List<string>();
        foreach (BossStatue st in roots.SelectMany(r => r.GetComponentsInChildren<BossStatue>(true)))
        {
            if (!map.TryGetValue(st.name, out StatueDef? def))
            {
                continue;
            }
            int k = 0;
            if (rec.StatueVariant.TryGetValue(def.Id, out string? chosen))
            {
                k = Math.Max(0, def.Variants.FindIndex(v => v.Id == chosen));
            }
            var slot = new Slot
            {
                Statue = st,
                Name = st.name,
                Def = def,
                Main = k,
                Alt = (k + 1) % def.Variants.Count,
                UsingAlt = false,
                Pd = st.statueStatePD ?? "",
            };
            slots[st.GetInstanceID()] = slot;
            // 雕像的战斗里死了照前作回到雕像前：回程门那里放一个复活点。
            Hk1Lobby.AddDeathMarker(scene, st.name, st.dreamReturnGate, st.transform);
            n++;
            try
            {
                WireStatue(slot, ref levers);
                slot.MainArt = Dress(st.statueDisplay, "main");
                slot.AltArt = Dress(st.statueDisplayAlt, "alt");
                float maxW = WidthFor(st, roots);
                foreach (SpriteRenderer? shown in new[] { slot.MainArt, slot.AltArt })
                {
                    if (shown != null && shown.GetComponent<Hk1StatueFit>() is Hk1StatueFit f)
                    {
                        f.MaxWidth = maxW;
                    }
                }
                if (Paint(slot))
                {
                    art++;
                }
                else
                {
                    noArt.Add(def.Id);
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogError($"[Godhome] 前作雕像 {st.name}（{def.Id}）布置出错 / hk1 statue setup failed: {e}");
            }
        }
        Note = $"前作手办屋 / hk1 hall: 雕像 / statues {n}，拉杆 / levers {levers}，已有石像 / carved art {art}";
        GodhomePlugin.Log.LogInfo($"[Godhome] {Note}{(noArt.Count > 0 ? "；还要雕 / to carve: " + string.Join(", ", noArt) : "")}");
        if (noArt.Count > 0)
        {
            // 还有没雕的：黑幕挡着，到场后雕（雕好有缓存，下次进来就不用等）。
            Busy = true;
            LoomUI.Instance?.Cover(1f, () => Busy);
        }
    }

    /// <summary>
    /// 照前作的雕像复制几尊放到空地上（Extra）。复制时雕像已经 Awake 过（回程门名字接过雕像名），复制品自己的 Awake 又接一次：
    /// 改回 door_dreamReturnGG_ + 新名字（ReturnTo、死后复活点都按这个名字找）。镜头锁的高度在 Awake 里按位置算，同一层不用改。
    /// </summary>
    private static void AddExtra(Scene scene)
    {
        var made = new List<string>();
        foreach ((string name, string from, float x) in Extra)
        {
            GameObject? src = scene.GetRootGameObjects().FirstOrDefault(r => r.name == Prefix + from);
            if (src == null || scene.GetRootGameObjects().Any(r => r.name == Prefix + name))
            {
                if (src == null)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 前作手办屋：复制源不在 / hk1 hall: copy source missing {Prefix + from}");
                }
                continue;
            }
            Vector3 sp = src.transform.position;
            float? srcGround = Ground(sp.x, sp.y + 3f);
            float? ground = Ground(x, sp.y + 3f);
            if (srcGround == null || (ground != null && Mathf.Abs(ground.Value - srcGround.Value) > 1.5f))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作手办屋：{name} 那里的地面和复制源不一样高，不放 / hk1 hall: floor for {name} at x {x:0.0} differs（复制源地面 / source floor {srcGround?.ToString("0.0") ?? "-"}，这里 / here {ground?.ToString("0.0") ?? "-"}）");
                continue;
            }
            if (ground == null)
            {
                // 第一次加载手办屋时右边那段的地形碰撞还没建好（0.10.7 巡视：第一次 L205、L217 打不到地面，之后再进就有，量出来都是 5.0），
                // 按复制源的地面高度放（Extra 的位置都是量过和复制源同一层平地的）。
                GodhomePlugin.Log.LogInfo($"[Godhome] 前作手办屋：{name} 那里暂时打不到地面，按复制源的地面 {srcGround.Value:0.0} 放 / hk1 hall: no floor hit for {name} yet; using the source floor");
                ground = srcGround;
            }
            GameObject copy = Object.Instantiate(src);
            SceneManager.MoveGameObjectToScene(copy, scene);
            copy.name = Prefix + name;
            copy.transform.position = new Vector3(x, ground.Value + (sp.y - srcGround.Value), sp.z);
            BossStatue? st = copy.GetComponent<BossStatue>();
            if (st != null && st.dreamReturnGate != null)
            {
                st.dreamReturnGate.name = Hk1Lobby.ReturnGatePrefix + copy.name;
            }
            made.Add($"{name}({x:0.0},{copy.transform.position.y:0.0})");
        }
        if (made.Count > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作手办屋：复制的雕像 / hk1 hall: copied statues {string.Join("，", made)}");
        }
    }

    /// <summary>x 处从 fromY 往下 12 格内的地面高度（地形层）；起点在地形里或者找不到返回 null。</summary>
    private static float? Ground(float x, float fromY)
    {
        var o = new Vector2(x, fromY);
        if (Physics2D.OverlapPoint(o, 1 << 8) != null)
        {
            return null;
        }
        RaycastHit2D g = Physics2D.Raycast(o, Vector2.down, 12f, 1 << 8);
        return g.collider != null ? g.point.y : null;
    }

    /// <summary>石像的宽度上限：同一层（高度差 3 以内）最近的邻居雕像离多远，减掉 2（两边各留 1），至少 5，最多 12。</summary>
    private static float WidthFor(BossStatue st, GameObject[] roots)
    {
        Vector3 p = st.transform.position;
        float gap = roots.SelectMany(r => r.GetComponentsInChildren<BossStatue>(true))
            .Where(o => o != st && Mathf.Abs(o.transform.position.y - p.y) < 3f)
            .Select(o => Mathf.Abs(o.transform.position.x - p.x))
            .DefaultIfEmpty(14f).Min();
        return Mathf.Clamp(gap - 2f, 5f, 12f);
    }

    /// <summary>
    /// 红光（前作梦境版底座旁的那圈）：露出来的不是第一个形态时亮（实测清单第 4 条：前作切到第二形态时才有）。
    /// fade = true 时照前作淡入淡出（ColorFader），否则直接开关。
    /// </summary>
    internal static void SetGlow(Slot s, bool fade)
    {
        if (s.Glow == null)
        {
            return;
        }
        bool on = s.ShownIndex != 0;
        if (on)
        {
            s.Glow.SetActive(true);
            foreach (ColorFader f in s.GlowFaders)
            {
                if (f != null)
                {
                    f.Fade(up: true);
                }
            }
        }
        else if (fade && s.Glow.activeInHierarchy && s.GlowFaders.Length > 0)
        {
            foreach (ColorFader f in s.GlowFaders)
            {
                if (f != null)
                {
                    f.Fade(up: false);
                }
            }
        }
        else
        {
            s.Glow.SetActive(false);
        }
    }

    /// <summary>藏起来的雕像：墙后的（前作皮鬼的雕像，要先打破墙）、笼子里的（辐光，要先打完万神殿）。神居存档里直接打开。</summary>
    private static void OpenHidden(GameObject[] roots)
    {
        foreach (GameObject r in roots)
        {
            switch (r.name)
            {
                case "Zote_Appear_bits":
                    foreach (DeactivateIfPlayerdataFalse d in r.GetComponents<DeactivateIfPlayerdataFalse>())
                    {
                        Object.Destroy(d);
                    }
                    r.SetActive(true);
                    Transform? seq = r.transform.Find("first_appear_sequence");
                    if (seq != null)
                    {
                        seq.gameObject.SetActive(false);
                    }
                    break;
                case "Zote_Break_wall":
                case "Radiance Statue Gate":
                    r.SetActive(false);
                    break;
            }
        }
    }

    /// <summary>
    /// 接线：完成情况、名字走补丁（Hk1Patches）；这里只管拉杆。前作每尊雕像都带一个拉杆（BossStatueLever）和一个梦之钉开关
    /// （BossStatueDreamToggle），哪个开着由前作的数据定。几个版本的统一用拉杆，梦之钉开关一律关掉。
    /// BossStatue.Awake 已经按“开着的那个”记下了 dreamToggle，这里改掉它（Start 里会调 SetOwner/SetState）。
    /// </summary>
    private static void WireStatue(Slot slot, ref int levers)
    {
        BossStatue st = slot.Statue;
        st.isAlwaysUnlocked = true;
        st.isAlwaysUnlockedDream = true;
        st.hasNoTiers = false;
        st.dontCountCompletion = false;
        st.isHidden = false;
        // 前作的数据里没有梦境版的雕像，StatueState / HasDreamVersion 都看这两个：补上占位的。
        if (st.bossScene == null)
        {
            st.bossScene = ScriptableObject.CreateInstance<BossScene>();
        }
        if (string.IsNullOrEmpty(st.statueStatePD))
        {
            st.statueStatePD = "godhome_" + slot.Def.Id;
        }
        bool multi = slot.Def.Variants.Count > 1;
        if (multi)
        {
            if (st.dreamBossScene == null)
            {
                st.dreamBossScene = ScriptableObject.CreateInstance<BossScene>();
            }
            if (string.IsNullOrEmpty(st.dreamStatueStatePD))
            {
                st.dreamStatueStatePD = "godhome_alt_" + slot.Def.Id;
            }
        }
        else
        {
            // 前作有梦境版的雕像（失败的冠军之类）上放了只有一个形态的 Boss：底座照前作会摆左右两块牌子（BossStatue.SetPlaquesVisible
            // 看 dreamBossScene），像是还有第二个形态（实测清单第 3 条）。去掉梦境版，只摆中间一块。
            st.dreamBossScene = null!;
            st.dreamStatueStatePD = "";
        }
        foreach (BossStatueDreamToggle d in st.GetComponentsInChildren<BossStatueDreamToggle>(true))
        {
            // 梦之钉开关本身不用（丝之歌没有梦之钉）。它亮的那圈红光（litPieces）留给有几个形态的雕像：露出来的不是第一个形态时亮（SetGlow）。
            // 开关所在的那组物体里，红光以外的（梦境球的底座、粒子）都关掉；组件关掉，它的 Start 不会再按梦境版去开关红光。
            d.enabled = false;
            GameObject? lit = d.litPieces;
            Transform? group = lit != null ? lit.transform.parent : null;
            if (multi && slot.Glow == null && lit != null && group != null)
            {
                slot.Glow = lit;
                slot.GlowFaders = lit.GetComponentsInChildren<ColorFader>(true);
                foreach (Transform c in group)
                {
                    c.gameObject.SetActive(false);
                }
                group.gameObject.SetActive(true);
                if (d.transform != group)
                {
                    d.gameObject.SetActive(false);
                }
            }
            else
            {
                d.gameObject.SetActive(false);
            }
        }
        BossStatueLever? lever = st.GetComponentsInChildren<BossStatueLever>(true).FirstOrDefault();
        // 拉杆在 alt_lever 下面（架子 GG_statue_switch_bracket、拉杆 GG_statue_switch_lever）：前作只有变体雕像的 alt_lever 是开着的，
        // 以前只开了拉杆本身，父物体关着，大多数有几个形态的雕像看不到拉杆（实测清单第 2 条）。
        Transform? rack = lever != null ? lever.transform : null;
        while (rack != null && rack.parent != null && rack.parent != st.transform)
        {
            rack = rack.parent;
        }
        if (multi && lever != null)
        {
            for (Transform? t = lever.transform; t != null && t != st.transform; t = t.parent)
            {
                t.gameObject.SetActive(true);
            }
            ToggleField(st) = lever;
            levers++;
        }
        else
        {
            if (rack != null)
            {
                rack.gameObject.SetActive(false);
            }
            ToggleField(st) = null!;
            if (multi)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 前作雕像 {slot.Name} 没有拉杆，只能打第一个版本 / no lever on this statue, only the first variant ({slot.Def.Id})");
            }
        }
        // 换版本的动画放完：奖牌按新露出来的版本重新显示（三个版本的雕像两边的奖牌会换）。
        st.OnStatueSwapFinished += () =>
        {
            if (st != null)
            {
                st.SetPlaquesVisible(true);
            }
        };
    }

    /// <summary>
    /// 展示物体（statueDisplay / statueDisplayAlt）里前作的画藏起来，在最大的那张图的物体下面挂一张我们的石像
    /// （跟着前作的沉下升起动画走）。返回挂上的渲染器（石像还没雕好时是空图）。
    /// </summary>
    private static SpriteRenderer? Dress(GameObject? display, string label)
    {
        if (display == null)
        {
            return null;
        }
        List<SpriteRenderer> srs = display.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r != null && r.sprite != null && r.name != "Godhome Statue").ToList();
        if (srs.Count == 0)
        {
            return null;
        }
        SpriteRenderer big = srs.OrderByDescending(r => WorldBox(r).size.x * WorldBox(r).size.y).First();
        // 所有图合起来的范围（Codex 审计 0.10.3 F09：以前 big 来自排序后的序列，Skip(1) 却跳过原列表的第一张，
        // 第一张不是最大的时候它的范围漏掉了）。big 再包一次无害。
        Bounds box = WorldBox(big);
        foreach (SpriteRenderer r in srs)
        {
            box.Encapsulate(WorldBox(r));
        }
        if (srs[0] != big)
        {
            // 记下以前的算法会得到什么，看哪尊受影响（审计说没有实际资产证据，这里留证据）。
            Bounds old = WorldBox(big);
            foreach (SpriteRenderer r in srs.Skip(1))
            {
                old.Encapsulate(WorldBox(r));
            }
            bool same = (old.min - box.min).sqrMagnitude < 1e-6f && (old.max - box.max).sqrMagnitude < 1e-6f;
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像 {label}：最大的图不是第一张（{big.name}，第一张 {srs[0].name}），范围 / bounds {box.min.x:0.00},{box.min.y:0.00} .. {box.max.x:0.00},{box.max.y:0.00}"
                + (same ? "，和以前一样 / same as before" : $"，以前 / was {old.min.x:0.00},{old.min.y:0.00} .. {old.max.x:0.00},{old.max.y:0.00}"));
        }
        foreach (SpriteRenderer r in srs)
        {
            r.enabled = false;
        }
        var go = new GameObject("Godhome Statue");
        go.transform.SetParent(big.transform, false);
        SpriteRenderer mine = go.AddComponent<SpriteRenderer>();
        // 前作雕像用会受光的材质（实测 Sprites-Diffuse，靠光柱和点光照亮）；石像也用它（深色石料就是按这个调的）。
        // 有的雕像上 SpriteFadeMaterial 会在 Start 里把子精灵的材质换掉：贴上以后由 Hk1StatueFit 盯着换回来。
        Material? lit = srs.Select(r => r.sharedMaterial).FirstOrDefault(m => m != null && m.shader != null && m.shader.name.Contains("Diffuse"));
        mine.sharedMaterial = lit != null ? lit : big.sharedMaterial;
        mine.sortingLayerID = big.sortingLayerID;
        mine.sortingOrder = big.sortingOrder;
        mine.color = Color.white;
        var fit = go.AddComponent<Hk1StatueFit>();
        fit.Mat = mine.sharedMaterial;
        // 底边中点记在父物体（前作那张画）的本地坐标里：前作的沉下升起动画移动的是父物体，
        // 以前记世界坐标，在雕像沉在地下时换图（拉杆换版本时两个展示物体都重新摆）就会偏出底座，下次换图又瞬移回来（实测清单第 6 条）。
        fit.LocalBottom = big.transform.InverseTransformPoint(new Vector3(box.center.x, box.min.y, big.transform.position.z));
        // 大小按 Boss 真实大小（雕出来的石像就是 Boss 的世界尺寸），所有雕像同一个比例；太高、太宽的压下来（实测清单第 5 条）。
        // 宽度上限 Prepare 里按同一层左右邻居的间距再定。
        fit.MaxHeight = StatueMaxHeight;
        fit.MaxWidth = 8f;
        return mine;
    }

    /// <summary>精灵在世界里的包围盒（物体关着也能算：按精灵自己的包围盒和变换算，不用 Renderer.bounds）。</summary>
    private static Bounds WorldBox(SpriteRenderer r)
    {
        Bounds b = r.sprite.bounds;
        Transform t = r.transform;
        Vector3 a = t.TransformPoint(b.min), c = t.TransformPoint(b.max);
        var w = new Bounds(a, Vector3.zero);
        w.Encapsulate(c);
        w.Encapsulate(t.TransformPoint(new Vector3(b.min.x, b.max.y, 0f)));
        w.Encapsulate(t.TransformPoint(new Vector3(b.max.x, b.min.y, 0f)));
        return w;
    }

    /// <summary>两个展示物体按各自的版本换上石像。全都有了返回 true。</summary>
    internal static bool Paint(Slot s)
    {
        bool ok = Set(s.MainArt, s.Def.Variants[s.Main]);
        if (s.Def.Variants.Count > 1)
        {
            ok &= Set(s.AltArt, s.Def.Variants[s.Alt]);
        }
        return ok;
    }

    private static bool Set(SpriteRenderer? r, BossDef b)
    {
        if (r == null)
        {
            return true;
        }
        Sprite? sp = Sanctum.CarvedStatue(b);
        r.sprite = sp;
        r.GetComponent<Hk1StatueFit>()?.Apply();
        return sp != null;
    }

    /// <summary>
    /// 拉杆换版本（BossStatue.SetDreamVersion 前缀，doAnim = true 时是拉杆敲的）：要露出来的那个展示物体换成下一个版本，
    /// 记下选中的版本。前作只有两个版本，来回切；我们的雕像可能有三个，按顺序轮。
    /// </summary>
    internal static void OnToggle(Slot s, bool toAlt)
    {
        int cur = s.UsingAlt ? s.Alt : s.Main;
        int next = (cur + 1) % s.Def.Variants.Count;
        if (toAlt)
        {
            s.Alt = next;
        }
        else
        {
            s.Main = next;
        }
        s.UsingAlt = toAlt;
        Paint(s);
        SetGlow(s, fade: true);
        GodhomePlugin.Instance.Records.StatueVariant[s.Def.Id] = s.Def.Variants[next].Id;
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像换版本 / hk1 statue variant: {s.Name} → {s.Def.Variants[next].Id}");
    }

    // ───────────────────────────── 到场以后 ─────────────────────────────

    internal static void OnArrive()
    {
        ModeController? host = ModeController.Instance;
        if (host == null)
        {
            return;
        }
        int t = token;
        if (Busy)
        {
            host.StartCoroutine(CarveRoutine(t));
        }
        host.StartCoroutine(InspectWatch(t));
        host.StartCoroutine(GlowAfterStart(t));
    }

    /// <summary>雕像、渐变组件的 Start 跑过以后（下一帧）按当前露出来的形态开关红光。</summary>
    private static IEnumerator GlowAfterStart(int t)
    {
        yield return null;
        if (!Alive(t))
        {
            yield break;
        }
        int on = 0;
        foreach (Slot s in slots.Values)
        {
            SetGlow(s, fade: false);
            on += s.Glow != null && s.Glow.activeSelf ? 1 : 0;
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像的红光 / hk1 statue glows: {slots.Values.Count(s => s.Glow != null)} 尊有 / statues have one，亮着 / lit {on}");
    }

    private static bool Alive(int t) => t == token && Hk1Lobby.Active && ModeController.Instance != null && ModeController.Instance.State == LoomState.Lobby
        && SceneManager.GetActiveScene().name == Hk1Lobby.Workshop;

    private static IEnumerator CarveRoutine(int t)
    {
        try
        {
            yield return Sanctum.CarveAllRoutine(() => Alive(t));
            if (!Alive(t))
            {
                yield break;
            }
            int ok = slots.Values.Count(Paint);
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作手办屋：石像雕好 / hk1 hall: statues carved, {ok}/{slots.Count} 尊有画 / with art");
            foreach (Slot s in slots.Values.Where(s => !Paint(s)))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome]   前作雕像没有石像 / hk1 statue without carved art: {s.Name}（{s.Def.Id}）");
            }
        }
        finally
        {
            if (t == token)
            {
                Busy = false;
                LoomUI.Instance?.FadeCover(0f, 0.8f);
            }
        }
    }

    /// <summary>
    /// 从雕像回来时前作的“Return from boss”FSM 先关掉雕像的交互（Inspect），等前作的转场特效发“DREAM WAKE”再打开；
    /// 丝之歌里那个事件可能不来：回来 6 秒后还关着就打开。
    /// </summary>
    private static IEnumerator InspectWatch(int t)
    {
        yield return new WaitForSecondsRealtime(6f);
        if (!Alive(t))
        {
            yield break;
        }
        int fixedN = 0;
        foreach (Slot s in slots.Values)
        {
            Transform? ins = s.Statue != null ? s.Statue.transform.Find("Inspect") : null;
            if (ins != null && !ins.gameObject.activeSelf)
            {
                ins.gameObject.SetActive(true);
                fixedN++;
            }
        }
        if (fixedN > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 前作雕像的交互没有自己打开，补开了 {fixedN} 个 / reopened {fixedN} statue inspect region(s)");
        }
    }
}

/// <summary>
/// 石像摆放：底边中点对在前作雕像画的底边中点（记在父物体的本地坐标里，跟着前作的沉下升起动画走）；
/// 大小是 Boss 的真实大小（所有雕像同一个比例），超过高度、宽度上限的压下来。
/// </summary>
internal sealed class Hk1StatueFit : MonoBehaviour
{
    public Vector3 LocalBottom;
    public float MaxHeight = 13f;
    public float MaxWidth = 8f;
    public Material? Mat;

    /// <summary>同一个物体上的渲染器（第一次用时取，之后不再每帧找，Codex 审查）。</summary>
    private SpriteRenderer? rend;

    private void LateUpdate()
    {
        if (Mat == null)
        {
            return;
        }
        if (rend == null)
        {
            rend = GetComponent<SpriteRenderer>();
            if (rend == null)
            {
                enabled = false;
                return;
            }
        }
        if (rend.sharedMaterial != Mat)
        {
            rend.sharedMaterial = Mat;
        }
    }

    internal void Apply()
    {
        SpriteRenderer r = GetComponent<SpriteRenderer>();
        if (r == null || r.sprite == null)
        {
            return;
        }
        Transform t = transform;
        Transform? p = t.parent;
        Vector3 ps = p != null ? p.lossyScale : Vector3.one;
        Bounds b = r.sprite.bounds;
        float h = Mathf.Max(0.01f, b.size.y), w = Mathf.Max(0.01f, b.size.x);
        float s = Mathf.Min(1f, Mathf.Min(MaxHeight / h, MaxWidth / w));
        // 世界缩放 = s；本地缩放要除掉父物体的缩放（前作的画可能是翻过来的：保留符号）。
        t.localScale = new Vector3(s / Mathf.Max(0.0001f, Mathf.Abs(ps.x)) * Mathf.Sign(ps.x == 0 ? 1 : ps.x), s / Mathf.Max(0.0001f, Mathf.Abs(ps.y)), 1f);
        t.localRotation = Quaternion.identity;
        // 本地位置：精灵的底边中点放到 LocalBottom（父物体空间里，精灵上一点 = 位置 + 本地缩放 × 精灵里的坐标）。
        Vector3 ls = t.localScale;
        t.localPosition = new Vector3(LocalBottom.x - b.center.x * ls.x, LocalBottom.y - b.min.y * ls.y, LocalBottom.z - 0.01f);
    }
}
