using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Godhome;

/// <summary>
/// [Godhome] 忆像长廊里的一尊雕像（第二版·3）。同一个 Boss 的几个版本放在同一尊雕像上，用拉杆切换，像前作那样。
/// </summary>
internal sealed class StatueDef
{
    public string Id = "";

    /// <summary>拉杆依次切换的版本，第一个是默认版本。</summary>
    public List<BossDef> Variants = new();

    /// <summary>黑线版（拉杆上标成黑线，默认不选）。</summary>
    public HashSet<string> Black = new();

    /// <summary>长廊里的顺序：按默认版本的幕、表里顺序排。</summary>
    public int Act => Variants[0].Act;

    public int Order => Variants[0].Order;

    public bool IsBlack(BossDef b) => Black.Contains(b.Id);

    /// <summary>雕像上显示的名字：默认版本的名字（不带变体说明）。</summary>
    public string Name => L.IsChinese ? Variants[0].NameZh : Variants[0].NameEn;
}

/// <summary>
/// 雕像表：bosses.json 的 "statues" 数组把几个 Boss 归到同一尊雕像，没列出来的 Boss 各自一尊。
/// 格式：{ "id": "lace", "variants": ["lace_1", "lace_2", "lost_lace"], "black": ["lost_lace"] }。
/// </summary>
internal static class StatueCatalog
{
    internal static List<StatueDef> All { get; private set; } = new();

    internal static StatueDef? Of(BossDef b) => All.FirstOrDefault(s => s.Variants.Contains(b));

    internal static void Load(string path)
    {
        var list = new List<StatueDef>();
        var grouped = new HashSet<string>();
        try
        {
            JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (root["statues"] is JArray rows)
            {
                foreach (JToken row in rows)
                {
                    if (row is not JObject o || o["id"]?.ToString() is not string id || o["variants"] is not JArray vs)
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 雕像表有一条格式不对，跳过 / statue row skipped: {row.ToString(Newtonsoft.Json.Formatting.None)}");
                        continue;
                    }
                    var s = new StatueDef { Id = id };
                    foreach (JToken v in vs)
                    {
                        BossDef? b = BossCatalog.Find(v.ToString());
                        if (b == null)
                        {
                            GodhomePlugin.Log.LogWarning($"[Godhome] 雕像 {id} 的版本不在 Boss 表里 / statue {id}: unknown boss {v}");
                            continue;
                        }
                        if (!grouped.Add(b.Id))
                        {
                            GodhomePlugin.Log.LogWarning($"[Godhome] {b.Id} 已经在别的雕像上 / {b.Id} is already on another statue");
                            continue;
                        }
                        s.Variants.Add(b);
                    }
                    if (o["black"] is JArray bl)
                    {
                        foreach (JToken x in bl)
                        {
                            s.Black.Add(x.ToString());
                        }
                    }
                    if (s.Variants.Count > 0)
                    {
                        list.Add(s);
                    }
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 读雕像表失败，每个 Boss 各一尊 / failed to read statues, one per boss: {e.Message}");
        }
        foreach (BossDef b in BossCatalog.All)
        {
            if (!grouped.Contains(b.Id))
            {
                list.Add(new StatueDef { Id = b.Id, Variants = { b } });
            }
        }
        All = list.OrderBy(s => s.Act == 0 ? 99 : s.Act).ThenBy(s => s.Order).ToList();
        GodhomePlugin.Log.LogInfo($"[Godhome] 雕像 / statues: {All.Count} 尊，{All.Count(s => s.Variants.Count > 1)} 尊带拉杆 / with a lever");
    }
}
