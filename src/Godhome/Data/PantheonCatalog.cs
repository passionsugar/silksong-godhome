using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Godhome;

/// <summary>[Godhome] data/pantheons.json 里的一个万神殿（照前作神居的 BossSequence：一串 Boss、休息点、解锁条件）。</summary>
internal sealed class PantheonDef
{
    public string Id = "";
    public string NameZh = "";
    public string NameEn = "";
    public string DescZh = "";
    public string DescEn = "";

    /// <summary>门徽：成就图标（achievement_icons_hornet 图集）里的精灵名。</summary>
    public string Emblem = "";

    /// <summary>按出场顺序的 Boss（表里找不到的 id 已经去掉并记了警告）。</summary>
    public List<BossDef> Bosses = new();

    /// <summary>打完第几个 Boss（从 1 数）以后去休息点。</summary>
    public HashSet<int> Rests = new();

    /// <summary>要先完成的万神殿 id（前作第五扇门要前四扇都完成）。</summary>
    public string[] Requires = Array.Empty<string>();

    public int Order;

    public string DisplayName => L.IsChinese ? NameZh : NameEn;

    public string Description => L.IsChinese ? DescZh : DescEn;

    /// <summary>打完第 index 个（从 0 数）以后要不要休息。</summary>
    public bool RestAfter(int index) => Rests.Contains(index + 1) && index + 1 < Bosses.Count;
}

/// <summary>束缚的参数（照前作 BossSequence 的 nailDamage / lowerNailDamagePercentage / maxHealth；丝上限是丝之歌的灵魂束缚）。</summary>
internal sealed class BindingParams
{
    public int NeedleCap = 13;
    public float NeedleLowerPercent = 0.8f;
    public int MaxMasks = 4;
    public int MaxSilk = 9;
}

internal static class PantheonCatalog
{
    internal static List<PantheonDef> All { get; private set; } = new();

    internal static BindingParams Bindings { get; private set; } = new();

    /// <summary>休息点的场景和醒来的位置（真实的长椅房间）。</summary>
    internal static string RestScene { get; private set; } = "Weave_07";

    internal static string RestMarker { get; private set; } = "RestBench";

    internal static PantheonDef? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// 读万神殿表。要在 Boss 表之后读（按 id 找 Boss）。整个文件读不了时表为空；
    /// 单个万神殿出错只跳过那一个；找不到的 Boss id 跳过并记警告（不让一个错字毁掉整个殿）。
    /// </summary>
    internal static void Load(string path)
    {
        var list = new List<PantheonDef>();
        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception e)
        {
            All = list;
            GodhomePlugin.Log.LogError($"[Godhome] 读取万神殿表失败 / failed to load pantheon table {path}: {e}");
            return;
        }
        if (root["bindings"] is JObject bj)
        {
            Bindings = new BindingParams
            {
                NeedleCap = Mathf.Clamp(Int(bj["needleCap"]) ?? 13, 1, 999),
                NeedleLowerPercent = Mathf.Clamp(Float(bj["needleLowerPercent"]) ?? 0.8f, 0.05f, 1f),
                MaxMasks = Mathf.Clamp(Int(bj["maxMasks"]) ?? 4, 1, 20),
                MaxSilk = Mathf.Clamp(Int(bj["maxSilk"]) ?? 9, 1, 99),
            };
        }
        if (root["rest"] is JObject rj)
        {
            RestScene = Str(rj, "scene") ?? RestScene;
            RestMarker = Str(rj, "marker") ?? RestMarker;
        }
        var ids = new HashSet<string>();
        JArray rows = root["pantheons"] as JArray ?? new JArray();
        for (int i = 0; i < rows.Count; i++)
        {
            try
            {
                if (rows[i] is not JObject o)
                {
                    throw new InvalidDataException("not an object");
                }
                string id = Str(o, "id") ?? throw new InvalidDataException("missing \"id\"");
                if (!ids.Add(id))
                {
                    throw new InvalidDataException($"duplicate id \"{id}\"");
                }
                var p = new PantheonDef
                {
                    Id = id,
                    NameZh = Str(o["name"], "zh") ?? id,
                    NameEn = Str(o["name"], "en") ?? id,
                    DescZh = Str(o["desc"], "zh") ?? "",
                    DescEn = Str(o["desc"], "en") ?? "",
                    Emblem = Str(o, "emblem") ?? "",
                    Order = list.Count,
                };
                foreach (JToken t in o["bosses"] as JArray ?? new JArray())
                {
                    string? bid = t.Type == JTokenType.String ? (string?)t : null;
                    BossDef? b = bid != null ? BossCatalog.Find(bid) : null;
                    if (b == null)
                    {
                        // 缺一个就整座不开（Codex 审计 0.10.3）：以前跳过这一个照样开，路线变短、休息点位置还按原来的数，
                        // 完成记录却照常给（数据表混装、只更新了一半时会这样）。
                        throw new InvalidDataException($"Boss 表里没有 / not in the boss table: {t}（整座万神殿不开 / the whole pantheon is disabled）");
                    }
                    p.Bosses.Add(b);
                }
                foreach (JToken t in o["rests"] as JArray ?? new JArray())
                {
                    if (Int(t) is int r && r > 0)
                    {
                        p.Rests.Add(r);
                    }
                }
                p.Requires = (o["requires"] as JArray ?? new JArray()).Where(t => t.Type == JTokenType.String).Select(t => (string)t!).ToArray();
                if (p.Bosses.Count == 0)
                {
                    throw new InvalidDataException($"{id}: no bosses");
                }
                list.Add(p);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 万神殿表第 {i + 1} 条跳过 / pantheon row {i + 1} skipped: {e.Message}");
            }
        }
        All = list;
        GodhomePlugin.Log.LogInfo($"[Godhome] 万神殿 / pantheons: {string.Join("，", list.Select(p => $"{p.Id} {p.Bosses.Count}"))}；束缚 / bindings: 织针封顶 {Bindings.NeedleCap}（以下 ×{Bindings.NeedleLowerPercent}），面具 {Bindings.MaxMasks}，丝 {Bindings.MaxSilk}；休息点 / rest {RestScene}@{RestMarker}");
    }

    private static string? Str(JToken? o, string key) => o is JObject j && j[key] is JValue v && v.Type == JTokenType.String ? (string?)v : null;

    private static int? Int(JToken? t) => t is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? (int)Math.Round(Convert.ToDouble(v.Value)) : null;

    private static float? Float(JToken? t)
    {
        if (t is JValue v && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float))
        {
            double d = Convert.ToDouble(v.Value);
            return double.IsNaN(d) || double.IsInfinity(d) ? null : (float)d;
        }
        return null;
    }
}
