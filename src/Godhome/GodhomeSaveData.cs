using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Godhome;

/// <summary>[Godhome] 每个存档槽一份的模式进度，由 DataManager 存成 BepInEx 目录下的独立文件，不进游戏存档。</summary>
public sealed class GodhomeSaveData
{
    public int Version { get; set; } = 1;

    /// <summary>Boss id → 记录。</summary>
    public Dictionary<string, BossRecord> Bosses { get; set; } = new();

    /// <summary>雕像 id → 拉杆选中的 Boss id（第二版·3：同一尊雕像上的变体、黑线版）。</summary>
    public Dictionary<string, string> StatueVariant { get; set; } = new();

    /// <summary>万神殿 id → 完成情况（照前作 BossSequenceDoor.Completion，存在插件自己的记录里，不写游戏存档字段）。</summary>
    public Dictionary<string, PantheonRecord> Pantheons { get; set; } = new();

    /// <summary>
    /// 读档以后字段可能是显式的 null（手改过的文件、旧版本），统一补成空集合（Codex 审查 v0.5.0 #9）。
    /// 每次取 Records 时调用，开销只是几次判空。
    /// </summary>
    internal GodhomeSaveData Normalized()
    {
        Bosses ??= new Dictionary<string, BossRecord>();
        StatueVariant ??= new Dictionary<string, string>();
        Pantheons ??= new Dictionary<string, PantheonRecord>();
        if (Bosses.Values.Any(r => r == null))
        {
            foreach (string k in Bosses.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
            {
                Bosses.Remove(k);
            }
        }
        if (Pantheons.Values.Any(r => r == null))
        {
            foreach (string k in Pantheons.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
            {
                Pantheons.Remove(k);
            }
        }
        return this;
    }

    internal PantheonRecord Pantheon(string id)
    {
        if (!Pantheons.TryGetValue(id, out PantheonRecord? r) || r == null)
        {
            r = new PantheonRecord();
            Pantheons[id] = r;
        }
        return r;
    }

    /// <summary>这个万神殿完成过吗（不建空记录）。</summary>
    internal bool PantheonDone(string id) => Pantheons.TryGetValue(id, out PantheonRecord? r) && r != null && r.Completed;

    internal BossRecord Get(string id)
    {
        if (!Bosses.TryGetValue(id, out BossRecord? r) || r == null)
        {
            r = new BossRecord();
            Bosses[id] = r;
        }
        r.Normalize();
        return r;
    }
}

/// <summary>
/// 一个万神殿的完成情况（照前作 BossSequenceDoor.Completion：completed、四种束缚各自、全束缚、无伤），
/// 另外记尝试次数、完成次数、最快用时。
/// </summary>
public sealed class PantheonRecord
{
    public int Attempts { get; set; }

    public int Clears { get; set; }

    public bool Completed { get; set; }

    public bool BoundNeedle { get; set; }

    public bool BoundMask { get; set; }

    public bool BoundTools { get; set; }

    public bool BoundSilk { get; set; }

    public bool AllBindings { get; set; }

    public bool NoHits { get; set; }

    /// <summary>最快用时（秒），没完成过是 -1。</summary>
    public float BestTime { get; set; } = -1f;

    public string? FirstClearUtc { get; set; }

    /// <summary>这种束缚完成过吗（结算、门上的图标用）。</summary>
    internal bool Bound(Binding b) => b switch
    {
        Binding.Needle => BoundNeedle,
        Binding.Mask => BoundMask,
        Binding.Tools => BoundTools,
        Binding.Silk => BoundSilk,
        _ => false,
    };

    /// <summary>记一次完成（照前作 FinishLastBossScene：完成、用过的每种束缚、四种都用了记全束缚、没受伤记无伤）。</summary>
    internal void RecordClear(Binding bindings, bool damaged, float seconds)
    {
        Clears++;
        Completed = true;
        BoundNeedle |= (bindings & Binding.Needle) != 0;
        BoundMask |= (bindings & Binding.Mask) != 0;
        BoundTools |= (bindings & Binding.Tools) != 0;
        BoundSilk |= (bindings & Binding.Silk) != 0;
        AllBindings |= bindings == Binding.All;
        NoHits |= !damaged;
        if (BestTime < 0 || seconds < BestTime)
        {
            BestTime = seconds;
        }
        FirstClearUtc ??= System.DateTime.UtcNow.ToString("o");
    }
}

public sealed class BossRecord
{
    /// <summary>进入战斗的次数。</summary>
    public int Attempts { get; set; }

    /// <summary>胜利次数（所有难度合计）。</summary>
    public int Clears { get; set; }

    /// <summary>最快用时（秒，所有难度合计），没赢过是 -1。</summary>
    public float BestTime { get; set; } = -1f;

    /// <summary>第一次胜利的时间（UTC，ISO 8601）。</summary>
    public string? FirstClearUtc { get; set; }

    /// <summary>三档难度（素织、重织、断丝）各自的胜利次数。第二版·3 之前的胜利都记在素织。</summary>
    public int[] TierClears { get; set; } = new int[3];

    /// <summary>三档难度各自的最快用时（秒），没赢过是 -1。</summary>
    public float[] TierBest { get; set; } = { -1f, -1f, -1f };

    /// <summary>
    /// 分档以前的记录迁移到素织：只有 Clears / BestTime、没有分档数据的胜利都是素织打的。
    /// 必须在改 Clears、BestTime 之前做（Codex 审查 v0.5.0 #3：先改总数再迁移，重织的时间会串进素织）。
    /// 数组长度不够时扩容，已有的元素保留。幂等：迁移后 Clears 等于各档之和，再调用不会变。
    /// </summary>
    internal void Normalize()
    {
        TierClears = Resize(TierClears, 0);
        TierBest = Resize(TierBest, -1f);
        int legacy = Clears - TierClears.Sum();
        if (legacy > 0)
        {
            TierClears[0] += legacy;
            if (TierBest[0] < 0 && BestTime >= 0)
            {
                TierBest[0] = BestTime;
            }
        }
    }

    private static T[] Resize<T>(T[]? a, T fill)
    {
        if (a != null && a.Length >= 3)
        {
            return a;
        }
        var r = new T[3];
        for (int i = 0; i < 3; i++)
        {
            r[i] = a != null && i < a.Length ? a[i] : fill;
        }
        return r;
    }

    internal int ClearsAt(int tier)
    {
        Normalize();
        return tier >= 0 && tier < 3 ? TierClears[tier] : 0;
    }

    internal float BestAt(int tier)
    {
        Normalize();
        return tier >= 0 && tier < 3 ? TierBest[tier] : -1f;
    }

    /// <summary>记一次胜利：总数、总最快和分档数据一起改（调用前 Get 已经迁移过）。</summary>
    internal void RecordWin(int tier, float seconds)
    {
        Normalize();
        tier = Mathf.Clamp(tier, 0, 2);
        Clears++;
        if (BestTime < 0 || seconds < BestTime)
        {
            BestTime = seconds;
        }
        TierClears[tier]++;
        if (TierBest[tier] < 0 || seconds < TierBest[tier])
        {
            TierBest[tier] = seconds;
        }
    }
}
