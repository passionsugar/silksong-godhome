using System;
using HarmonyLib;

namespace Godhome;

/// <summary>
/// 神居的念珠、工具碎片：保留足够的真实字段值供工具/FSM的“能不能用”检查，
/// 消耗时将扣款改成 0，仍执行游戏原有流程。不是一次性发一笔钱，也不改普通工具弹药、灵丝或工具束缚。
/// </summary>
internal static class GodhomeResources
{
    // 数值供直接读取 PlayerData 字段的条件使用；消费入口免扣，通用写入和实时兜底补给维持储备。
    // 补到游戏自己的上限（Gameplay.GetCurrencyCap，随工具袋升级变大；HUD 显示的就是满的），最多 9999：
    // 上限没配置时游戏返回 9999999，不能照抄（审查 PR #1：原来固定 9999，可能超过上限，捡钱时 AddGeo 又会压回上限）。
    internal const int Reserve = 9999;

    /// <summary>神居存档里这种货币应保持的余额。</summary>
    internal static int Target(CurrencyType type)
    {
        int cap = Reserve;
        try
        {
            cap = GlobalSettings.Gameplay.GetCurrencyCap(type);
        }
        catch (Exception)
        {
            // 设置还没加载（极早期）：用 9999，下一次补给再按上限来。
        }
        return Math.Max(1, Math.Min(cap, Reserve));
    }

    /// <summary>新建/读档补满配时调用，在开战快照之前补齐；较大的现有余额不减少。</summary>
    internal static int Refill(PlayerData? pd)
    {
        if (!GodhomeSave.IsGodhome(pd))
        {
            return 0;
        }
        int changed = 0;
        int geo = Target(CurrencyType.Money);
        int shards = Target(CurrencyType.Shard);
        if (pd!.geo < geo)
        {
            pd.geo = geo;
            changed++;
        }
        if (pd.ShellShards < shards)
        {
            pd.ShellShards = shards;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// 只接受当前实时神居对象。保存时 GameManager 临时指向快照副本、PlayerData.instance 仍是实时对象，
    /// 此处要求两个入口引用相同，不补写保存副本，也不修改已经离开的旧档。
    /// </summary>
    internal static PlayerData? Current()
    {
        GameManager gm = GameManager.UnsafeInstance;
        if (gm == null || !PlayerData.HasInstance)
        {
            return null;
        }
        PlayerData pd = PlayerData.instance;
        return ReferenceEquals(pd, gm.playerData) && GodhomeSave.IsGodhome(pd) ? pd : null;
    }

    internal static bool IsSupplied(CurrencyType type) => type is CurrencyType.Money or CurrencyType.Shard;

    /// <summary>PlayMaker 等也会用按名字访问整数的入口，只照顾这两个字段和当前实时神居对象。</summary>
    internal static void RefillField(PlayerData? pd, string intName)
    {
        if (intName is not (nameof(PlayerData.geo) or nameof(PlayerData.ShellShards)) ||
            pd == null || !ReferenceEquals(pd, Current()))
        {
            return;
        }
        if (Refill(pd) > 0)
        {
            Restored++;
        }
    }

    /// <summary>ModeController 每帧调：游戏直接改字段（不走任何入口）的扣款在下一帧补回。</summary>
    internal static void Tick()
    {
        if (Refill(Current()) > 0)
        {
            Restored++;
        }
    }

    /// <summary>通用整数入口（IntAdd/SetInt/DecrementInt/GetInt）和每帧补给把被直接改少的余额补回去的次数（调试用）。</summary>
    internal static int Restored { get; private set; }

    /// <summary>免掉的扣款次数（调试核对念珠炮这类入口真的走到了这里）。</summary>
    internal static int Waived { get; private set; }

    /// <summary>只修改正向扣款。0/负数的原始语义不变；不跳过原方法的通知、排队等工作。</summary>
    internal static void ProtectSpend(PlayerData? pd, ref int amount)
    {
        if (amount <= 0 || pd == null || !ReferenceEquals(pd, Current()))
        {
            return;
        }
        Refill(pd);
        amount = 0;
        Waived++;
    }
}

/// <summary>念珠炮、建筑师等查询余额时也补齐，覆盖演出直接清零后首次使用的条件检查。</summary>
[HarmonyPatch(typeof(CurrencyManager), nameof(CurrencyManager.GetCurrencyAmount), new[] { typeof(CurrencyType) })]
internal static class GodhomeResourceQueryPatch
{
    private static void Prefix(CurrencyType type)
    {
        if (GodhomeResources.IsSupplied(type))
        {
            GodhomeResources.Refill(GodhomeResources.Current());
        }
    }
}

/// <summary>
/// 货币队列的总入口：CurrencyManager.TakeCurrency/TakeGeo/TakeShards、HeroController.TakeCurrency/TakeGeo/TakeShards
/// 都调 ChangeCurrency(-amount)（反编译 CurrencyManager.cs、HeroController.cs）。在这里把扣款改成 0，
/// HUD 计数器也就不会播“-N”再跳回去（审查 PR #1：原来只拦 CurrencyManager.TakeCurrency，主角那条路的计数器照样减）。
/// 加钱（正数）不动；原方法照常执行。
/// </summary>
[HarmonyPatch(typeof(CurrencyManager), nameof(CurrencyManager.ChangeCurrency),
    new[] { typeof(int), typeof(CurrencyType), typeof(bool) })]
internal static class GodhomeResourceSpendPatch
{
    private static void Prefix(CurrencyType type, ref int amount)
    {
        if (amount < 0 && GodhomeResources.IsSupplied(type))
        {
            int spend = -amount;
            GodhomeResources.ProtectSpend(GodhomeResources.Current(), ref spend);
            amount = -spend;
        }
    }
}

/// <summary>游戏动作也可能直接调用 PlayerData，仍只保护当前神居对象。</summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.TakeGeo), new[] { typeof(int) })]
internal static class GodhomeGeoSpendPatch
{
    private static void Prefix(PlayerData __instance, ref int amount) => GodhomeResources.ProtectSpend(__instance, ref amount);
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.TakeShards), new[] { typeof(int) })]
internal static class GodhomeShardSpendPatch
{
    private static void Prefix(PlayerData __instance, ref int amount) => GodhomeResources.ProtectSpend(__instance, ref amount);
}

/// <summary>通用字段读取先补齐，使 FSM 的余额门槛看到真实储备；其余 PlayerData 整数完全不动。</summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.GetInt), new[] { typeof(string) })]
internal static class GodhomeResourceIntQueryPatch
{
    private static void Prefix(PlayerData __instance, string intName) => GodhomeResources.RefillField(__instance, intName);
}

/// <summary>这些通用写入口仍先执行游戏逻辑，再恢复储备，避免漏掉直接改整数的工具/FSM。</summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetInt), new[] { typeof(string), typeof(int) })]
internal static class GodhomeResourceIntSetPatch
{
    private static void Postfix(PlayerData __instance, string intName) => GodhomeResources.RefillField(__instance, intName);
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.IntAdd), new[] { typeof(string), typeof(int) })]
internal static class GodhomeResourceIntAddPatch
{
    private static void Postfix(PlayerData __instance, string intName) => GodhomeResources.RefillField(__instance, intName);
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.DecrementInt), new[] { typeof(string) })]
internal static class GodhomeResourceIntDecrementPatch
{
    private static void Postfix(PlayerData __instance, string intName) => GodhomeResources.RefillField(__instance, intName);
}
