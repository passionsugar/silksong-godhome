using System;
using HarmonyLib;
using UnityEngine;

namespace Godhome;

/// <summary>四种束缚（照前作神居的 BossSequenceController.ChallengeBindings：骨钉、外壳、护符、灵魂）。</summary>
[Flags]
internal enum Binding
{
    None = 0,

    /// <summary>织针束缚（前作骨钉）：伤害封顶，已经不超过封顶就打八折。</summary>
    Needle = 1,

    /// <summary>面具束缚（前作外壳）：最多几格血。</summary>
    Mask = 2,

    /// <summary>工具束缚（前作护符）：所有工具（红、蓝、黄）都不算装备。</summary>
    Tools = 4,

    /// <summary>灵丝束缚（前作灵魂）：丝上限压低。</summary>
    Silk = 8,

    All = Needle | Mask | Tools | Silk,
}

/// <summary>
/// [Godhome] 万神殿的束缚。只在一次万神殿连战期间生效（ModeController 开始时 Apply、结束或作废时 Clear），
/// 不改存档数据，全部用补丁在读取的地方改结果（存档里的升级、装备都原样留着）：
///
/// - 织针：PlayerData.nailDamage 是按升级算的属性（5/9/13/17/21），丝之歌的织针伤害（DamageEnemies、HealthManager、
///   SetNailDamage）都直接读它，所以补丁它的 getter。前作留下的 GetNailDamage 动作看 BossSequenceController.BoundNail /
///   BoundNailDamage，也一起补上。
/// - 面具：PlayerData.CurrentMaxHealth 本来就是“BoundShell 时取 Min(maxHealth, BoundMaxHealth)”（前作的逻辑还在），
///   但 BoundShell / BoundMaxHealth 读的是前作的连战数据（丝之歌里没人设，BoundMaxHealth 直接读会空引用）。
///   补丁这两个：束缚时 BoundShell = true、BoundMaxHealth = 上限。HUD 面具上前作的 GGCheckBoundHeart 也看这两个。
/// - 工具：ToolItemManager.IsToolEquipped（两个重载，ToolItem.IsEquipped / IsEquippedHud 都走它）束缚时一律返回 false，
///   和游戏自己的 ToolsActiveStates.Disabled 一样的效果，但不会被场景里的 FSM 改回来。开关时发一次装备变化事件刷新缓存和 HUD。
/// - 灵丝：PlayerData.CurrentSilkMaxBasic（HUD 的丝轴按它画）和 CurrentSilkMax（回丝、缚丝判断用）都压到上限。
/// </summary>
internal static class Bindings
{
    internal static Binding Active { get; private set; }

    internal static bool Has(Binding b) => (Active & b) != 0;

    /// <summary>开始连战时（快照拍好以后）套上束缚；血和丝超出新上限的压下来。</summary>
    internal static void Apply(Binding b, string why)
    {
        Active = b & Binding.All;
        GodhomePlugin.Log.LogInfo($"[Godhome] 束缚 / bindings: {Describe(Active)}（{why}）");
        Refresh();
    }

    /// <summary>连战结束、作废、回标题时解开。没套着就什么都不做。</summary>
    internal static void Clear(string why)
    {
        if (Active == Binding.None)
        {
            return;
        }
        Active = Binding.None;
        GodhomePlugin.Log.LogInfo($"[Godhome] 解开束缚 / bindings cleared（{why}）");
        Refresh();
    }

    internal static string Describe(Binding b)
    {
        if (b == Binding.None)
        {
            return "无 / none";
        }
        var parts = new System.Collections.Generic.List<string>();
        foreach (Binding x in new[] { Binding.Needle, Binding.Mask, Binding.Tools, Binding.Silk })
        {
            if ((b & x) != 0)
            {
                parts.Add(L.Get("BIND_" + x.ToString().ToUpperInvariant()));
            }
        }
        return string.Join("、", parts);
    }

    /// <summary>织针束缚下的伤害（照前作 BossSequenceController.BoundNailDamage）。</summary>
    internal static int NeedleDamage(int raw)
    {
        BindingParams p = PantheonCatalog.Bindings;
        return raw <= p.NeedleCap ? Mathf.Max(1, Mathf.RoundToInt(raw * p.NeedleLowerPercent)) : p.NeedleCap;
    }

    /// <summary>束缚变了：工具缓存、HUD 刷新；血、丝超出上限的压下来。</summary>
    private static void Refresh()
    {
        try
        {
            ToolItemManager.SendEquippedChangedEvent(force: true);
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 刷新工具装备出错 / refreshing tool equips failed: {e.Message}");
        }
        try
        {
            PlayerData pd = PlayerData.instance;
            if (pd != null)
            {
                int maxHp = pd.CurrentMaxHealth;
                if (pd.health > maxHp)
                {
                    pd.health = maxHp;
                }
                int maxSilk = pd.CurrentSilkMax;
                if (pd.silk > maxSilk)
                {
                    pd.silk = maxSilk;
                }
            }
            HeroController hero = HeroController.SilentInstance;
            if (hero != null)
            {
                hero.RefreshSilk();
            }
            // HUD 的面具（前作的 GGCheckBoundHeart 在面具的 FSM 里）：前作套束缚时广播这个事件让它们重新检查。
            // 不广播 HUD IN（Boss 开场演出藏着 HUD 时会被叫出来）；血量的显示由调用方随后的回满血刷新。
            PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 刷新血量、丝出错 / refreshing health/silk failed: {e.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.nailDamage), MethodType.Getter)]
internal static class BindNeedleDamagePatch
{
    private static void Postfix(ref int __result)
    {
        if (Bindings.Has(Binding.Needle))
        {
            __result = Bindings.NeedleDamage(__result);
        }
    }
}

[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundNail), MethodType.Getter)]
internal static class BindNeedleFlagPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Bindings.Has(Binding.Needle))
        {
            __result = true;
            return false;
        }
        return true;
    }
}

/// <summary>GetNailDamage 动作取 Min(nailDamage, BoundNailDamage)：nailDamage 已经补丁过，这里直接给同一个值（不能再算一次八折）。</summary>
[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundNailDamage), MethodType.Getter)]
internal static class BindNeedleValuePatch
{
    private static bool Prefix(ref int __result)
    {
        if (Bindings.Has(Binding.Needle))
        {
            __result = PlayerData.instance != null ? PlayerData.instance.nailDamage : Bindings.NeedleDamage(5);
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundShell), MethodType.Getter)]
internal static class BindMaskFlagPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Bindings.Has(Binding.Mask))
        {
            __result = true;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundMaxHealth), MethodType.Getter)]
internal static class BindMaskValuePatch
{
    private static bool Prefix(ref int __result)
    {
        if (Bindings.Has(Binding.Mask))
        {
            __result = PantheonCatalog.Bindings.MaxMasks;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ToolItemManager), nameof(ToolItemManager.IsToolEquipped), new[] { typeof(ToolItem), typeof(ToolEquippedReadSource) })]
internal static class BindToolsPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Bindings.Has(Binding.Tools))
        {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ToolItemManager), nameof(ToolItemManager.IsToolEquipped), new[] { typeof(string) })]
internal static class BindToolsByNamePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Bindings.Has(Binding.Tools))
        {
            __result = false;
            return false;
        }
        return true;
    }
}

/// <summary>
/// 红色攻击工具不走 IsToolEquipped：扔工具（HeroController 的 GetBoundAttackTool(..., Active, out ...)）和 HUD 的工具图标
/// （ToolHudIcon，GetBoundAttackTool(..., Hud)）直接读纹章槽位和缓存（反编译 ToolItemManager.cs:768）。
/// 两个参数的重载也是调这个，所以补这一个就够了。用后缀：原方法照常跑（out 参数、缓存都更新），只把结果换成“没有”（Codex 审查 0.8.0）。
/// </summary>
[HarmonyPatch(typeof(ToolItemManager), nameof(ToolItemManager.GetBoundAttackTool),
    new[] { typeof(AttackToolBinding), typeof(ToolEquippedReadSource), typeof(AttackToolBinding) },
    new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
internal static class BindAttackToolPatch
{
    private static void Postfix(ref ToolItem __result)
    {
        if (Bindings.Has(Binding.Tools))
        {
            __result = null!;
        }
    }
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.CurrentSilkMaxBasic), MethodType.Getter)]
internal static class BindSilkBasicPatch
{
    private static void Postfix(ref int __result)
    {
        if (Bindings.Has(Binding.Silk))
        {
            __result = Mathf.Min(__result, PantheonCatalog.Bindings.MaxSilk);
        }
    }
}

[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.CurrentSilkMax), MethodType.Getter)]
internal static class BindSilkPatch
{
    private static void Postfix(ref int __result)
    {
        if (Bindings.Has(Binding.Silk))
        {
            __result = Mathf.Min(__result, PantheonCatalog.Bindings.MaxSilk);
        }
    }
}
