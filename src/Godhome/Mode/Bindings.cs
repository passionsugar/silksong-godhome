using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Godhome;

/// <summary>四种束缚（照前作神居的 BossSequenceController.ChallengeBindings：骨钉、外壳、护符、灵魂）。</summary>
[Flags]
internal enum Binding
{
    None = 0,

    /// <summary>织针束缚：连战期间织针最多二级。</summary>
    Needle = 1,

    /// <summary>面具束缚（前作外壳）：最多几格血。</summary>
    Mask = 2,

    /// <summary>工具束缚（前作护符）：所有工具（红、蓝、黄）都不算装备；丝技（白色）照前作护符束缚不管法术，照常能放。</summary>
    Tools = 4,

    /// <summary>灵丝束缚：压低丝上限，每次缚丝少回一格。</summary>
    Silk = 8,

    All = Needle | Mask | Tools | Silk,
}

/// <summary>
/// [Godhome] 万神殿的束缚。只在一次万神殿连战期间生效（ModeController 开始时 Apply、结束或作废时 Clear），
/// 开战前的快照负责存档，束缚只作用于连战中的实时数据。
///
/// - 织针：临时限制 nailUpgrades，由游戏按该等级计算基础伤害、敌人受伤系数和依赖织针的丝技。
///   前作留下的 GetNailDamage 动作也能通过 BoundNailDamage 取得实时针伤。
/// - 面具：PlayerData.CurrentMaxHealth 本来就是“BoundShell 时取 Min(maxHealth, BoundMaxHealth)”（前作的逻辑还在），
///   但 BoundShell / BoundMaxHealth 读的是前作的连战数据（丝之歌里没人设，BoundMaxHealth 直接读会空引用）。
///   补丁这两个：束缚时 BoundShell = true、BoundMaxHealth = 上限。HUD 面具上前作的 GGCheckBoundHeart 也看这两个。
/// - 工具：ToolItemManager.IsToolEquipped（两个重载，ToolItem.IsEquipped / IsEquippedHud 都走它）束缚时返回 false，
///   和游戏自己的 ToolsActiveStates.Disabled 一样的效果，但不会被场景里的 FSM 改回来。开关时发一次装备变化事件刷新缓存和 HUD。
///   丝技（ToolItemType.Skill，白色）不算：它和红色工具一样经 GetBoundAttackTool 放出来（HeroController.GetWillThrowTool / ThrowTool），
///   以前一并挡掉，套着工具束缚就放不了法术（0.10.6 实测清单第 8 条）。前作的护符束缚也不管法术。
/// - 灵丝：PlayerData.CurrentSilkMaxBasic（HUD 的丝轴按它画）和 CurrentSilkMax（回丝、缚丝判断用）都压到上限。
///   丝轴的长度只在 SilkSpool.DrawSpool 里按 CurrentSilkMaxBasic 画，游戏只在丝上限变的时候调它：套上、解开时自己调一次，
///   不然丝轴还是原来的长度，只填到 9，看不出上限（实测清单第 9 条）。
/// </summary>
internal static class Bindings
{
    private static PlayerData? needleOwner;
    private static int originalNeedleLevel;

    internal static Binding Active { get; private set; }

    internal static bool NeedleBound => needleOwner != null && ReferenceEquals(needleOwner, PlayerData.instance);

    internal static bool Has(Binding b) => (Active & b) != 0;

    /// <summary>开始连战时（快照拍好以后）套上束缚；血和丝超出新上限的压下来。</summary>
    internal static void Apply(Binding b, PlayerData pd, string why)
    {
        RestoreNeedleLevel();
        Active = b & Binding.All;
        if (Has(Binding.Needle) && GodhomeSave.IsGodhome(pd))
        {
            needleOwner = pd;
            originalNeedleLevel = pd.nailUpgrades;
            ReapplyNeedleLevel(pd);
        }
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
        RestoreNeedleLevel();
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

    /// <summary>丝技（白色）：工具束缚不管它。</summary>
    internal static bool IsSkill(ToolItem? tool) => tool != null && tool.Type == ToolItemType.Skill;

    /// <summary>换场时快照会拷回原等级。只给本次连战的实时 PlayerData 重新套用束缚。</summary>
    internal static void ReapplyNeedleLevel(PlayerData pd)
    {
        if (ReferenceEquals(pd, needleOwner))
        {
            pd.nailUpgrades = Math.Min(originalNeedleLevel, PantheonCatalog.Bindings.NeedleMaxLevel);
        }
    }

    private static void RestoreNeedleLevel()
    {
        if (needleOwner != null)
        {
            needleOwner.nailUpgrades = originalNeedleLevel;
            needleOwner = null;
        }
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
            SilkSpool? spool = SilkSpool.Instance;
            if (spool != null)
            {
                spool.DrawSpool();
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

[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundNail), MethodType.Getter)]
internal static class BindNeedleFlagPatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Bindings.NeedleBound)
        {
            __result = true;
            return false;
        }
        return true;
    }
}

/// <summary>旧版 GetNailDamage 动作也使用当前织针等级对应的原生伤害。</summary>
[HarmonyPatch(typeof(BossSequenceController), nameof(BossSequenceController.BoundNailDamage), MethodType.Getter)]
internal static class BindNeedleValuePatch
{
    private static bool Prefix(ref int __result)
    {
        if (Bindings.NeedleBound)
        {
            __result = PlayerData.instance.nailDamage;
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
    private static bool Prefix(ToolItem tool, ref bool __result)
    {
        if (Bindings.Has(Binding.Tools) && !Bindings.IsSkill(tool))
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
    private static bool Prefix(string name, ref bool __result)
    {
        if (Bindings.Has(Binding.Tools) && !Bindings.IsSkill(ToolItemManager.GetToolByName(name)))
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
        if (Bindings.Has(Binding.Tools) && !Bindings.IsSkill(__result))
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

// hornet_bind calls AddHealth with the same "Heal Amount" variable for both
// Multibinder pulses. Is First Loop is set anew by the game for every bind.
[HarmonyPatch(typeof(CallMethodProper), "DoMethodCall")]
internal static class BindSilkHealPatch
{
    private static bool Prefix(CallMethodProper __instance)
    {
        if (!Bindings.Has(Binding.Silk) || __instance.methodName?.Value != nameof(HeroController.AddHealth)
            || __instance.behaviour?.Value != nameof(HeroController)
            || __instance.State.Name is not ("Heal" or "Leave Scene Bind?")
            || __instance.parameters?.Length != 1
            || __instance.parameters[0] is not { useVariable: true, variableName: "Heal Amount" })
        {
            return true;
        }
        Fsm fsm = __instance.Fsm;
        HeroController hero = HeroController.SilentInstance;
        if (hero == null || fsm.GetOwnerDefaultTarget(__instance.gameObject) != hero.gameObject
            || fsm.Variables.FindFsmBool("Is First Loop")?.Value != true)
        {
            return true;
        }
        int? amount = fsm.Variables.FindFsmInt("Heal Amount")?.Value;
        if (amount is not (2 or 3))
        {
            return true;
        }
        hero.AddHealth(amount.Value - 1);
        return false;
    }
}

// Beast and Witch crests heal one mask per successful hit during a bind.
// Reduce the number of eligible hits without changing when those hits pay out.
[HarmonyPatch]
internal static class BindSilkCrestHealCapPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HeroController), nameof(HeroController.GetRageModeHealCap));
        yield return AccessTools.Method(typeof(HeroController), nameof(HeroController.GetWitchHealCap));
    }

    private static void Postfix(ref int __result)
    {
        if (Bindings.Has(Binding.Silk) && __result is 3 or 4)
        {
            __result--;
        }
    }
}
