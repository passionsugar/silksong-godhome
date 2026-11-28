using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Godhome;

/// <summary>[Godhome] data/bosses.json 里的一条。代码不硬编码任何 Boss。</summary>
internal sealed class BossDef
{
    public string Id = "";
    public string NameZh = "";
    public string NameEn = "";
    public string? VariantZh;
    public string? VariantEn;
    public string Tier = "boss";
    public int Act;
    public string RegionZh = "";
    public string RegionEn = "";
    public string Scene = "";
    public bool Additive;
    public string? HostScene;
    /// <summary>要加载的场景（entry.scene）：一般就是 Boss 场景；附加场景 Boss 是宿主场景。入口一律是场地的 door_dreamEnter。</summary>
    public string EntryScene = "";
    public string[] BossObjects = Array.Empty<string>();
    public string[] DefeatedFlags = Array.Empty<string>();

    /// <summary>
    /// “见过这个 Boss”的标记（flags.encountered）。开战前在这一场的临时数据里设成 true（arena.encountered，默认开），
    /// 让 Boss 走“重打”分支：没有初见的对话、开场演出（例如末代裁决者初见要先演奏针琴或和朝圣者对话，重打直接开战）。
    /// 回圣所时快照还原。
    /// </summary>
    public string[] EncounteredFlags = Array.Empty<string>();
    public Dictionary<string, JToken> PlayerDataSet = new();

    /// <summary>神居场地（arena 段，见 docs/arena.md）：入场门、开战判定、加载时补丁、特殊结局。</summary>
    public ArenaDef Arena = new();

    /// <summary>表里有场地数据（至少有入场门），能开战。</summary>
    public bool ArenaReady => Arena.Door != null && Arena.Broken.Length == 0;
    public HashSet<string> AllowScenes = new();
    public int Order;

    /// <summary>图鉴记录名（EnemyJournalRecord.name）。只给雕像找立绘用（见 StatueArt），不参与判胜。</summary>
    public string[] Journal = Array.Empty<string>();

    /// <summary>
    /// 雕像用什么画（statueArt，只管长廊里的样子，不参与判胜），按顺序试：
    /// "tk2d:组/集合/帧"（游戏的动作帧，同一个 Boss 的不同形态用不同姿势）、"pair:图鉴名"（雕成一前一后两尊）、"图鉴名"。
    /// 都不行就用 journal，再按名字找。见 Sanctum.ArtCandidates。
    /// </summary>
    public string[] StatueArt = Array.Empty<string>();

    /// <summary>只在调试自检里用：对没有血量组件的 Boss 执行“击杀”。</summary>
    public List<FsmStep> DebugKill = new();

    /// <summary>自检这个 Boss 最多等多少秒（0 = 用配置 Debug.SelfTestStepTimeout）。阶段多、演出长的 Boss 要更久。</summary>
    public float DebugTimeout;

    /// <summary>自检秒杀：一击真正造成伤害后，等多少秒再打下一击（让倒地 / 换阶段的演出走完）。</summary>
    public float DebugKillInterval = 5f;

    /// <summary>自检时顺便打掉战斗场景里不是 Boss 的敌人（例如失缚怨魂要先清完尸壳，场景才放 Boss 出来）。</summary>
    public bool DebugKillAdds;

    /// <summary>killAdds 写成路径数组时：只打这些路径（及其下面）的敌人；空 = 场景里全部。</summary>
    public string[] DebugAddRoots = Array.Empty<string>();

    /// <summary>自检时的“条件反应”：某个 FSM 停在 when 状态时就执行这一步（例如替玩家在格挡提示时按招架）。</summary>
    public List<FsmStep> DebugReact = new();

    /// <summary>自检时暂停秒杀的条件：任何一个 FSM 停在 when 状态时不打 Boss（例如失缚怨魂休眠、等场景清小怪的时候）。</summary>
    public List<FsmStep> DebugHoldKill = new();

    public string DisplayName
    {
        get
        {
            bool zh = L.IsChinese;
            string name = zh ? NameZh : NameEn;
            string? variant = zh ? VariantZh : VariantEn;
            return string.IsNullOrEmpty(variant) ? name : $"{name}（{variant}）";
        }
    }

    public string Region => L.IsChinese ? RegionZh : RegionEn;
}

/// <summary>
/// 对场景里某个 FSM 或全局事件的一步操作。
/// type: "event"（给 path 物体上名为 fsm 的 FSM 发 event）、"state"（把它切到 state）、"register"（EventRegister 全局广播 event）、
/// "activate" / "deactivate"（打开 / 关闭 path 物体）。
/// </summary>
internal sealed class FsmStep
{
    public string Type = "event";
    public string Path = "";
    public string Fsm = "";
    public string Event = "";
    public string State = "";
    public float Delay;

    /// <summary>
    /// debug.react / holdKill：FSM 当前状态是它时才执行（多个状态用 | 分隔）。
    /// arena.enter：先等 path 上的 FSM 进到这个状态（最多 15 秒）再执行，例如等沙克拉飞回来停在 Idle 再开切磋。
    /// </summary>
    public string When = "";

    // The native trigger may have fired before the arrival steps run.
    public string SkipWhen = "";

    /// <summary>只用于 debug.react：在 when 状态里停了至少这么多秒才执行（0 = 一进去就执行）。</summary>
    public float After;

    /// <summary>
    /// 只用于 arena.enter：开战离不开这一步（"required": true）。没等到 when、执行失败，这一场按场地出错回大厅（不记尝试），
    /// 不留在一场永远开不了的战斗里（Codex 审计 0.10.3）。没写的照旧：失败只记警告。
    /// </summary>
    public bool Required;

    public override string ToString() => Type switch
    {
        "register" => $"register '{Event}'",
        "activate" => $"activate {Path}",
        "deactivate" => $"deactivate {Path}",
        "state" => $"{Path}::{Fsm} → state '{State}'",
        _ => $"{Path}::{Fsm} ← event '{Event}'",
    };
}

/// <summary>[Godhome] 一个 Boss 的神居场地（bosses.json 的 arena 段，见 docs/arena.md）。</summary>
internal sealed class ArenaDef
{
    // Explicit encounter overrides, keyed by a name from bossObjects. Top-level hp is the vanilla inventory.
    public readonly Dictionary<string, int> StartingHealth = new();
    // World-space combat limits for the hero (left, bottom, right, top). The floor stays native.
    public Rect? PlayerBounds;
    /// <summary>入场门 door_dreamEnter 的位置（世界坐标）。门型入口只用 x 和往下找地面，y 写在地面上方一点。</summary>
    public Vector2? Door;

    /// <summary>进场朝向：1 朝右、-1 朝左、0 不改。</summary>
    public int Face;

    /// <summary>开战判定（只给日志和自检用，不影响游戏）。</summary>
    public WakeDef? Wake;

    /// <summary>开战前把 flags.encountered 设成 true，走重打分支（默认开）。</summary>
    public bool Encountered = true;

    /// <summary>加载时（FSM 还没开始跑）对场景的改动，相当于前作 Boss FSM 里的神居分支。</summary>
    public List<ArenaPatch> Patch = new();

    /// <summary>特殊结局：在这些状态最前面插 EndGGBossScene；terminal 为真时截断原版结局。写了就不用 bosses 判胜。</summary>
    public List<FsmRef> End = new();

    /// <summary>进场后（人站好、能操作时）按顺序执行的步骤，相当于前作 Dream Entry 的最后几步。</summary>
    public List<FsmStep> Enter = new();

    /// <summary>加载时不关的交互物体（路径前缀）。</summary>
    public string[] Keep = Array.Empty<string>();

    /// <summary>跳过的小怪波次（BattleWave 物体的路径）：加载时从所属 BattleScene 的波次表里拿掉，进场直接打 Boss 那一波。</summary>
    public string[] SkipWaves = Array.Empty<string>();

    /// <summary>挑战位：放进来以后不自己开战，玩家按“挑战”才开打（自检会替玩家按 debug.react 里的 INTERACT）。</summary>
    public bool Challenge;

    /// <summary>
    /// 这一场不加载的附加场景（场景名）：宿主场景按原条件还会加载别的 Boss 场景时写在这里（例如沙克拉那里的荒沼翼主备用地点
    /// Greymoor_08_boss）。在 SceneAdditiveLoadConditional.TryTestLoad 里拦下（它在 OnEnable / Start 里判，比 sceneLoaded 早）。
    /// </summary>
    public string[] NoAdditive = Array.Empty<string>();

    /// <summary>
    /// 开战后 HUD 收着、人能操作，过多少秒叫回来（ModeController.CheckFightHud，默认 2 秒）。
    /// 开场演出本来就收着 HUD、演完自己会叫回来、又没收人控制的写长一点（崇高圣母灵丝：挑战以后到 Begin 约 20 秒）。
    /// </summary>
    public float HudGrace = 2f;

    /// <summary>
    /// 判胜相关的配置写错了（arena.end 不是数组、有一项缺 path / state）：不为空就不能开战（Codex 审查 0.9.0 #12）。
    /// 跳过写错的那项会悄悄改成按 bosses 判胜，特殊死亡的 Boss 就赢不了。
    /// </summary>
    public string Broken = "";
}

/// <summary>开战判定：path 上名为 fsm 的 FSM 在 state（多个用 | 分隔）里等过、又离开了；或者 battle 路径上的 BattleScene 开战了。</summary>
internal sealed class WakeDef
{
    public string Path = "";
    public string Fsm = "";
    public string State = "";
    public string Battle = "";

    /// <summary>
    /// 可选：离开等待状态的跳转事件要是这个（多个用 | 分隔）才算开战，例如沙克拉的 BATTLE HERO（和玩家切磋）；
    /// 同一个状态还能经别的事件离开（她的 ENEMY 是去打别的敌人）时写。不写就是离开等待状态就算。
    /// </summary>
    public string Via = "";

    public override string ToString() => Battle.Length > 0 ? $"BattleScene {Battle}" : $"{Path}::{Fsm} [{State}]{(Via.Length > 0 ? " via " + Via : "")}";
}

/// <summary>FSM 里的一个状态（arena.end）。</summary>
internal sealed class FsmRef
{
    public string Path = "";
    public string Fsm = "";
    public string State = "";
    /// <summary>在此判胜并截断后续跳转。默认保留此状态的攻击清理动作。</summary>
    public bool Terminal;

    public override string ToString() => $"{Path}::{Fsm} [{State}]{(Terminal ? " (terminal)" : "")}";
}

/// <summary>
/// 加载时的一条补丁。type："start"（改起始状态 state）、"goto"（把 from 状态里 event 的跳转改到 to，from 写 * 是全局跳转；
/// 原来没有这个事件的跳转就加一条）、"var"（设 FSM 变量 name，值写在 bool / int / float / string 里）、
/// "activate" / "deactivate"（开关 path 物体）。
/// </summary>
internal sealed class ArenaPatch
{
    public int HpThreshold;
    public int? HealAmount;
    public string Type = "";
    public string Path = "";
    public string Fsm = "";
    public string State = "";
    public string From = "";
    public string Event = "";
    public string To = "";
    public string Name = "";
    public string ValueType = "";
    public JToken? Value;

    /// <summary>
    /// 开战离不开这条补丁（"required": true）：这一场的场景都准备好以后还没打上，按场地出错回大厅（不记尝试）。
    /// 没写的照旧：打不上只记警告（有的补丁只是跳过前置演出，打不上退回原版流程也能打）。
    /// </summary>
    public bool Required;

    public override string ToString() => Type switch
    {
        "start" => $"{Path}::{Fsm} start → '{State}'",
        "goto" => $"{Path}::{Fsm} [{From}] {Event} → '{To}'",
        "var" => $"{Path}::{Fsm} {ValueType} {Name} = {Value}",
        "hpCheck" => $"{Path}::{Fsm} [{State}] HP threshold {HpThreshold}",
        "actionLast" => $"{Path}::{Fsm} [{State}] {Name} → 最后 / last",
        "actionFirst" => $"{Path}::{Fsm} [{State}] {Name} → first",
        "holdInput" => $"{Path}::{Fsm} [{State}] block hero input",
        _ => $"{Type} {Path}",
    };
}

internal static class BossCatalog
{
    internal static List<BossDef> All { get; private set; } = new();

    internal static BossDef? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    /// <summary>
    /// 读 Boss 表。整个文件读不了时表为空；单条出错只跳过那一条（Codex 审查 #19）：
    /// 缺 id 或 scene、id 重复、字段类型不对抛异常的，都记警告后跳过，其余照常可用。
    /// </summary>
    internal static void Load(string path)
    {
        var list = new List<BossDef>();
        JArray rows;
        try
        {
            JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            rows = root["bosses"] as JArray ?? throw new InvalidDataException("no \"bosses\" array");
        }
        catch (Exception e)
        {
            All = list;
            GodhomePlugin.Log.LogError($"[Godhome] 读取 Boss 表失败 / failed to load boss table {path}: {e}");
            return;
        }
        var ids = new HashSet<string>();
        int skipped = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            try
            {
                if (rows[i] is not JObject o)
                {
                    throw new InvalidDataException("not an object");
                }
                string? id = Str(o, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidDataException("missing \"id\"");
                }
                if (string.IsNullOrWhiteSpace(Str(o, "scene")))
                {
                    throw new InvalidDataException($"{id}: missing \"scene\"");
                }
                if (ids.Contains(id!))
                {
                    throw new InvalidDataException($"duplicate id \"{id}\"");
                }
                // 先解析成功再占用 id（Codex 复审）：解析失败跳过的那条不该让后面同 id 的正确条目也被当成重复。
                BossDef b = Parse(o, id!);
                ids.Add(id!);
                b.Order = list.Count;
                list.Add(b);
            }
            catch (Exception e)
            {
                skipped++;
                GodhomePlugin.Log.LogWarning($"[Godhome] Boss 表第 {i + 1} 条跳过 / boss table row {i + 1} skipped: {e.Message}");
            }
        }
        All = list;
        Validate(skipped);
    }

    private static BossDef Parse(JObject o, string id)
    {
        string scene = Str(o, "scene")!;
        JObject? entry = o["entry"] as JObject;
        var b = new BossDef
        {
            Id = id,
            NameZh = Str(o["name"], "zh") ?? id,
            NameEn = Str(o["name"], "en") ?? id,
            VariantZh = Str(o["variant"], "zh"),
            VariantEn = Str(o["variant"], "en"),
            Tier = Str(o, "tier") ?? "boss",
            // 超出 int 的整数强转会抛异常（Codex 复审），只认 0..99。
            Act = o["act"] is JValue av && av.Type == JTokenType.Integer && av.Value is long al && al is >= 0 and <= 99 ? (int)al : 0,
            RegionZh = Str(o["region"], "zh") ?? "",
            RegionEn = Str(o["region"], "en") ?? "",
            Scene = scene,
            Additive = Bool(o, "additive"),
            HostScene = Str(o, "hostScene"),
            // entry.scene 写成空字符串也当没写（Codex 复审），否则会以空场景名切场景。
            EntryScene = string.IsNullOrWhiteSpace(Str(entry, "scene")) ? scene : Str(entry, "scene")!,
            BossObjects = Strings(o["bossObjects"]),
            DefeatedFlags = Strings((o["flags"] as JObject)?["defeated"]),
            EncounteredFlags = Strings((o["flags"] as JObject)?["encountered"]),
        };
        // 不认识的字段记警告（写错名字、或者是表里还留着不再有的字段），不读。
        Unknown(o, TopKeys, id, "");
        Unknown(entry, EntryKeys, id, "entry.");
        Unknown(o["arena"] as JObject, ArenaKeys, id, "arena.");
        Unknown(o["debug"] as JObject, DebugKeys, id, "debug.");
        if (o["arena"] is JObject arena)
        {
            b.Arena = ParseArena(arena, id);
            if (arena["health"] is JToken health)
            {
                if (health is not JObject targets)
                {
                    b.Arena.Broken = "arena.health must map bossObjects to positive HP";
                }
                else
                {
                    foreach (JProperty target in targets.Properties())
                    {
                        if (!b.BossObjects.Contains(target.Name) || PositiveInt(target.Value) is not int hp)
                        {
                            b.Arena.Broken = $"invalid arena.health target or HP: {target.Name}";
                            break;
                        }
                        b.Arena.StartingHealth.Add(target.Name, hp);
                    }
                }
            }
        }
        if ((o["prerequisites"] as JObject)?["playerData"] is JObject pd)
        {
            foreach (JProperty p in pd.Properties())
            {
                b.PlayerDataSet[p.Name] = p.Value;
            }
        }
        foreach (string s in Strings(o["allowScenes"]))
        {
            b.AllowScenes.Add(s);
        }
        b.Journal = Strings(o["journal"]);
        b.StatueArt = Strings(o["statueArt"]);
        if (o["debug"] is JObject debug)
        {
            b.DebugKill = Steps(debug["kill"], id, "debug.kill");
            // 数值一律过 Num（不认 NaN / 无穷大）再限定范围（Codex 复审）：写成负数或极大值不会让流程卡住或立刻超时。
            b.DebugTimeout = Clamp(Num(debug["timeout"]), 0f, 900f, 0f);
            b.DebugKillInterval = Clamp(Num(debug["killInterval"]), 0.5f, 60f, 5f);
            b.DebugAddRoots = Strings(debug["killAdds"]);
            b.DebugKillAdds = Bool(debug, "killAdds") || b.DebugAddRoots.Length > 0;
            b.DebugReact = Steps(debug["react"], id, "debug.react");
            b.DebugHoldKill = Steps(debug["holdKill"], id, "debug.holdKill");
        }
        return b;
    }

    // 表里认识的字段（字段说明见 docs/boss-inventory.md、docs/arena.md）。titleKey、hp、notes 这类只是给人看的备注。
    private static readonly HashSet<string> TopKeys = new()
    {
        "$comment", "id", "name", "variant", "titleKey", "tier", "act", "region", "scene", "additive", "hostScene", "additiveLoader",
        "entry", "bossObjects", "hp", "flags", "prerequisites", "allowScenes", "journal", "statueArt", "arena", "debug", "notes",
    };
    private static readonly HashSet<string> EntryKeys = new() { "scene" };
    private static readonly HashSet<string> ArenaKeys = new() { "$comment", "door", "face", "wake", "encountered", "patch", "skipWaves", "end", "enter", "keep", "challenge", "noAdditive", "hudGrace", "playerBounds", "health" };
    private static readonly HashSet<string> DebugKeys = new() { "$comment", "kill", "timeout", "killInterval", "killAdds", "react", "holdKill" };

    private static void Unknown(JObject? o, HashSet<string> known, string id, string prefix)
    {
        foreach (JProperty p in o?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            if (!known.Contains(p.Name))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: 不认识的字段 {prefix}{p.Name}，不读 / unknown field, ignored");
            }
        }
    }

    private static readonly HashSet<string> StepTypes = new() { "event", "state", "register", "activate", "deactivate" };

    /// <summary>读一组步骤。不认识的 type、不是对象的项记警告后跳过（不让一条写错的步骤变成别的操作）。</summary>
    private static List<FsmStep> Steps(JToken? t, string id, string section)
    {
        var list = new List<FsmStep>();
        if (t != null && t.Type != JTokenType.Array)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 不是数组，忽略 / is not an array, ignored");
            return list;
        }
        foreach (JToken x in t as JArray ?? new JArray())
        {
            if (x is not JObject s)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 里有一项不是对象，跳过 / an item is not an object, skipped");
                continue;
            }
            // 写了 type 但不是字符串（数字、对象）就跳过，不默认当成 event（Codex 复审）。
            if (s["type"] != null && Str(s, "type") == null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 里有一项 type 不是字符串，跳过 / a step's \"type\" is not a string, skipped");
                continue;
            }
            string type = Str(s, "type") ?? "event";
            if (!StepTypes.Contains(type))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 里有不认识的 type '{type}'，跳过 / unknown step type, skipped (valid: {string.Join(", ", StepTypes)})");
                continue;
            }
            var step = new FsmStep
            {
                Type = type,
                Path = Str(s, "path") ?? "",
                Fsm = Str(s, "fsm") ?? "",
                Event = Str(s, "event") ?? "",
                State = Str(s, "state") ?? "",
                Delay = Clamp(Num(s["delay"]), 0f, 60f, 0f),
                When = Str(s, "when") ?? "",
                SkipWhen = Str(s, "skipWhen") ?? "",
                After = Clamp(Num(s["after"]), 0f, 60f, 0f),
                Required = Bool(s, "required"),
            };
            if (s["required"] is JToken rq && rq.Type != JTokenType.Boolean)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 的 required 要写 true / false，当作 false / \"required\" must be a bool; treated as false: {rq.ToString(Newtonsoft.Json.Formatting.None)}");
            }
            if (step.Required && section != "arena.enter")
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: required 只用于 arena.enter，{section} 里不认 / \"required\" only applies to arena.enter; ignored in {section}");
                step.Required = false;
            }
            if ((section == "debug.react" || section == "debug.holdKill") && step.When.Length == 0)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 有一步没写 when，跳过 / a step has no \"when\", skipped");
                continue;
            }
            list.Add(step);
        }
        return list;
    }

    /// <summary>读 arena 段。写错的项记警告后跳过（不让一条写错的补丁变成别的操作）。</summary>
    private static ArenaDef ParseArena(JObject arena, string id)
    {
        var a = new ArenaDef();
        if (arena["playerBounds"] is JToken bounds)
        {
            if (bounds is JArray box && box.Count == 4 && Num(box[0]) is float left
                && Num(box[1]) is float bottom && Num(box[2]) is float right && Num(box[3]) is float top
                && left < right && bottom < top)
            {
                a.PlayerBounds = Rect.MinMaxRect(left, bottom, right, top);
            }
            else
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.playerBounds must be [left, bottom, right, top]");
            }
        }
        if (arena["door"] is JArray d && d.Count >= 2 && Num(d[0]) is float x && Num(d[1]) is float y)
        {
            a.Door = new Vector2(x, y);
        }
        else if (arena["door"] != null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.door 要写成 [x, y] / arena.door must be [x, y]");
        }
        string? face = Str(arena, "face");
        a.Face = face switch { "right" => 1, "left" => -1, _ => 0 };
        if (face != null && a.Face == 0)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.face 只能是 right / left / arena.face must be right or left");
        }
        if (arena["wake"] is JObject w)
        {
            a.Wake = new WakeDef
            {
                Path = Str(w, "path") ?? "",
                Fsm = Str(w, "fsm") ?? "",
                State = Str(w, "state") ?? "",
                Battle = Str(w, "battle") ?? "",
                Via = Str(w, "via") ?? "",
            };
            if (a.Wake.Battle.Length == 0 && (a.Wake.Path.Length == 0 || a.Wake.State.Length == 0))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.wake 要写 path + state，或者 battle / arena.wake needs path + state, or battle");
                a.Wake = null;
            }
            else if (a.Wake.Battle.Length > 0 && a.Wake.Via.Length > 0)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.wake.via 只用于 FSM 等待状态，battle 不认 / arena.wake.via only applies to an FSM wait state; ignored with battle");
                a.Wake.Via = "";
            }
        }
        if (arena["encountered"] is JToken et)
        {
            if (et.Type == JTokenType.Boolean)
            {
                a.Encountered = (bool)et;
            }
            else
            {
                // 写成 "false" 之类不当真（Codex 审查 0.9.0 #12）：报出来，按默认（true）走。
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.encountered 要写 true / false，按默认 true / arena.encountered must be a bool; using the default (true): {et.ToString(Newtonsoft.Json.Formatting.None)}");
            }
        }
        if (arena["patch"] is JToken pt && pt is not JArray)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.patch 要写成数组，不打补丁 / arena.patch must be an array; no patches");
        }
        a.Patch = Patches(arena["patch"], id);
        JToken? endTok = arena["end"];
        if (endTok != null && endTok is not JArray)
        {
            a.Broken = "arena.end 要写成数组 / arena.end must be an array";
        }
        a.End = Refs(endTok, id, "arena.end", out int badEnds);
        if (badEnds > 0)
        {
            a.Broken = $"arena.end 有 {badEnds} 项写错 / {badEnds} malformed arena.end item(s)";
        }
        a.Enter = Steps(arena["enter"], id, "arena.enter");
        a.Keep = Strings(arena["keep"]);
        a.SkipWaves = Strings(arena["skipWaves"]);
        a.Challenge = Bool(arena, "challenge");
        a.NoAdditive = Strings(arena["noAdditive"]);
        if (arena["hudGrace"] is JToken hg && Num(hg) == null)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.hudGrace 要写秒数，按默认 2 秒 / arena.hudGrace must be a number of seconds; using the default (2): {hg.ToString(Newtonsoft.Json.Formatting.None)}");
        }
        a.HudGrace = Clamp(Num(arena["hudGrace"]), 0.5f, 120f, 2f);
        return a;
    }

    private static int? PositiveInt(JToken? t) =>
        t is JValue { Type: JTokenType.Integer, Value: long n } && n is > 0 and <= int.MaxValue ? (int)n : null;

    private static readonly HashSet<string> PatchTypes = new() { "start", "goto", "var", "activate", "deactivate", "actionLast", "actionFirst", "holdInput", "hpCheck" };

    private static List<ArenaPatch> Patches(JToken? t, string id)
    {
        var list = new List<ArenaPatch>();
        foreach (JToken x in t as JArray ?? new JArray())
        {
            if (x is not JObject s || Str(s, "type") is not string type || !PatchTypes.Contains(type) || string.IsNullOrEmpty(Str(s, "path")))
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.patch 里有一项写错了（要有 type 和 path），跳过 / a patch is malformed, skipped: {x.ToString(Newtonsoft.Json.Formatting.None)}");
                continue;
            }
            var p = new ArenaPatch
            {
                Type = type,
                Path = Str(s, "path")!,
                Fsm = Str(s, "fsm") ?? "",
                State = Str(s, "state") ?? "",
                From = Str(s, "from") ?? "",
                Event = Str(s, "event") ?? "",
                To = Str(s, "to") ?? "",
                Name = Str(s, "name") ?? "",
                HpThreshold = PositiveInt(s["threshold"]) ?? 0,
                HealAmount = PositiveInt(s["heal"]),
                Required = Bool(s, "required"),
            };
            if (s["required"] is JToken rq && rq.Type != JTokenType.Boolean)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.patch 的 required 要写 true / false，当作 false / \"required\" must be a bool; treated as false: {rq.ToString(Newtonsoft.Json.Formatting.None)}");
            }
            // var 只能写一个值，类型要和键对上（Codex 审查 0.9.0 #12）：写两个时不让后一个悄悄盖掉前一个，类型不对不留到 FSM 醒来才抛异常。
            string[] valueKeys = new[] { "bool", "int", "float", "string" }.Where(vt => s[vt] != null).ToArray();
            string? valueBad = null;
            if (valueKeys.Length > 1)
            {
                valueBad = "var 只能写 bool / int / float / string 中的一个";
            }
            else if (valueKeys.Length == 1)
            {
                string vt = valueKeys[0];
                JToken v = s[vt]!;
                bool typeOk = vt switch
                {
                    "bool" => v.Type == JTokenType.Boolean,
                    "int" => v.Type == JTokenType.Integer && v is JValue { Value: long n } && n is >= int.MinValue and <= int.MaxValue,
                    "float" => (v.Type is JTokenType.Integer or JTokenType.Float) && Num(v) != null,
                    _ => v.Type == JTokenType.String,
                };
                if (typeOk)
                {
                    p.ValueType = vt;
                    p.Value = (JValue)v;
                }
                else
                {
                    valueBad = $"{vt} 的值类型不对";
                }
            }
            string? bad = type switch
            {
                "start" when p.State.Length == 0 => "start 要写 state",
                "goto" when p.From.Length == 0 || p.Event.Length == 0 || p.To.Length == 0 => "goto 要写 from、event、to",
                "var" when valueBad != null => valueBad,
                "var" when p.Name.Length == 0 || p.ValueType.Length == 0 => "var 要写 name 和 bool / int / float / string 之一",
                "actionLast" or "actionFirst" when p.State.Length == 0 || p.Name.Length == 0 => "动作排序要写 state 和 name",
                "holdInput" when p.State.Length == 0 => "holdInput 要写 state",
                "hpCheck" when p.State.Length == 0 || p.HpThreshold == 0 || (s["heal"] != null && p.HealAmount == null)
                    => "hpCheck needs a state, positive threshold and optional positive heal",
                _ => null,
            };
            if (bad != null)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: arena.patch 跳过 / patch skipped ({bad}): {p}");
                continue;
            }
            list.Add(p);
        }
        return list;
    }

    private static List<FsmRef> Refs(JToken? t, string id, string section, out int bad)
    {
        var list = new List<FsmRef>();
        bad = 0;
        foreach (JToken x in t as JArray ?? new JArray())
        {
            if (x is not JObject s || string.IsNullOrEmpty(Str(s, "path")) || string.IsNullOrEmpty(Str(s, "state")))
            {
                bad++;
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section} 里有一项要写 path 和 state / an item needs path and state: {x.ToString(Newtonsoft.Json.Formatting.None)}");
                continue;
            }
            JToken? terminal = s["terminal"];
            if (terminal != null && terminal.Type != JTokenType.Boolean)
            {
                // 截断原版结局是安全要求，不能将拼错的 true 当作 false 后继续开战。
                bad++;
                GodhomePlugin.Log.LogWarning($"[Godhome] {id}: {section}.terminal 必须是布尔值 / terminal must be a boolean: {terminal.ToString(Newtonsoft.Json.Formatting.None)}");
                continue;
            }
            list.Add(new FsmRef
            {
                Path = Str(s, "path")!, Fsm = Str(s, "fsm") ?? "", State = Str(s, "state")!,
                Terminal = terminal?.Value<bool>() ?? false,
            });
        }
        return list;
    }

    /// <summary>
    /// 检查表里的 PlayerData 字段名是否真的存在、击败标记是不是 bool。写错的在日志里报出来，
    /// 并且从这条里去掉（Codex 复审）：只报不去，后面读击败标记、写前置条件时还会拿着错的名字去用。
    /// </summary>
    private static void Validate(int skipped)
    {
        int bad = 0;
        foreach (BossDef b in All)
        {
            // 存档身份字段（神居存档的标记）不许 Boss 表改（Codex 审查 0.9.0 #2）：开战后改掉它，下一帧模式重置会丢掉快照，
            // 战斗里的实时数据就会写进存档。击败标记要设成 false、见过标记要设成 true、前置条件随便设，三处都拦。
            foreach (string f in SaveIdentityFields)
            {
                bool hit = false;
                if (b.DefeatedFlags.Contains(f))
                {
                    b.DefeatedFlags = b.DefeatedFlags.Where(x => x != f).ToArray();
                    hit = true;
                }
                if (b.EncounteredFlags.Contains(f))
                {
                    b.EncounteredFlags = b.EncounteredFlags.Where(x => x != f).ToArray();
                    hit = true;
                }
                hit |= b.PlayerDataSet.Remove(f);
                if (hit)
                {
                    bad++;
                    GodhomePlugin.Log.LogError($"[Godhome] {b.Id}: Boss 表不能改存档身份字段，去掉 / the boss table must not change the save identity field '{f}', dropped");
                }
            }
            var flags = new List<string>();
            foreach (string f in b.DefeatedFlags)
            {
                FieldInfo? fi = PlayerDataField(f);
                if (fi == null)
                {
                    bad++;
                    GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: PlayerData 没有字段，去掉这个击败标记 / no PlayerData field '{f}', defeated flag dropped");
                }
                else if (fi.FieldType != typeof(bool))
                {
                    bad++;
                    GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: 击败标记 '{f}' 不是 bool，去掉 / defeated flag is {fi.FieldType.Name}, not bool, dropped");
                }
                else
                {
                    flags.Add(f);
                }
            }
            b.DefeatedFlags = flags.ToArray();
            var enc = new List<string>();
            foreach (string f in b.EncounteredFlags)
            {
                FieldInfo? fi = PlayerDataField(f);
                if (fi == null || fi.FieldType != typeof(bool))
                {
                    bad++;
                    GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: 见过标记 '{f}' 不存在或不是 bool，去掉 / encountered flag missing or not bool, dropped");
                }
                else
                {
                    enc.Add(f);
                }
            }
            b.EncounteredFlags = enc.ToArray();
            foreach (string f in b.PlayerDataSet.Keys.ToList())
            {
                if (PlayerDataField(f) == null)
                {
                    bad++;
                    b.PlayerDataSet.Remove(f);
                    GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id}: PlayerData 没有字段，去掉这条前置条件 / no PlayerData field '{f}', prerequisite dropped");
                }
            }
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] Boss 表校验完成 / boss table checked: {All.Count} 条, 跳过 {skipped} 条 / rows skipped, {bad} 个无效字段 / invalid fields");
        // 判胜配置写错的不能开战（Codex 审查 0.9.0 #12）。
        foreach (BossDef b in All.Where(x => x.Arena.Broken.Length > 0))
        {
            GodhomePlugin.Log.LogError($"[Godhome] {b.Id}: 判胜配置写错了，不能开战 / broken win setup, cannot be fought: {b.Arena.Broken}");
        }
        // 没有场地数据的 Boss 不能开战（运行时不猜）。
        List<string> noArena = All.Where(b => !b.ArenaReady).Select(b => b.Id).ToList();
        List<string> noWake = All.Where(b => b.ArenaReady && b.Arena.Wake == null).Select(b => b.Id).ToList();
        if (noArena.Count > 0)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {noArena.Count} 个 Boss 没有场地数据（arena.door），不能开战 / bosses without arena data cannot be fought: {string.Join(", ", noArena)}");
        }
        if (noWake.Count > 0)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] {noWake.Count} 个 Boss 没写开战判定（arena.wake），自检认不出开战 / bosses without arena.wake: {string.Join(", ", noWake)}");
        }
    }

    /// <summary>认存档是不是神居存档的字段（GodhomeSave.IsGodhome 看 bossRushMode）。</summary>
    private static readonly string[] SaveIdentityFields = { "bossRushMode" };

    internal static FieldInfo? PlayerDataField(string name) =>
        typeof(PlayerData).GetField(name, BindingFlags.Public | BindingFlags.Instance);

    private static string? Str(JToken? t, string key) =>
        t is JObject o && o[key] is JValue v && v.Type == JTokenType.String ? (string?)v : null;

    /// <summary>读数字；不是数字、NaN、无穷大都当作没写。</summary>
    private static float? Num(JToken? t)
    {
        if (t is not JValue v || (v.Type != JTokenType.Float && v.Type != JTokenType.Integer))
        {
            return null;
        }
        float f = (float)v;
        return float.IsNaN(f) || float.IsInfinity(f) ? null : f;
    }

    /// <summary>没写就用默认值，写了就限定在 [min, max]。</summary>
    private static float Clamp(float? v, float min, float max, float fallback) =>
        v is float f ? Mathf.Clamp(f, min, max) : fallback;

    private static bool Bool(JToken? t, string key) =>
        t is JObject o && o[key] is JValue v && v.Type == JTokenType.Boolean && (bool)v;

    private static string[] Strings(JToken? t) =>
        t is JArray a ? a.Where(x => x.Type == JTokenType.String).Select(x => (string)x!).ToArray() : Array.Empty<string>();
}
