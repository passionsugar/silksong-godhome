using System;
using System.Collections;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 神居存档里不弹“获得”提示（玩家反馈：打完幽影弹“获得十字绣”，原初罪者也会）。
/// 满配已经什么都有了，Boss 的收尾演出照原版发技能、能力、纹章、物品时还是会弹整屏的提示，要按键才关，关之前人不能动。
/// 游戏里这几种提示的入口（反编译）：
/// - 灵丝技能 SkillGetMsg.Spawn（FSM 动作 SpawnSkillGetMsg：幽影的十字绣、原初罪者神龛的技能）。提示关掉时先后发
///   SKILL GET MSG FADED OUT、SKILL GET MSG ENDED，FSM 等这两个事件才往下走（幽影：UI Msg → Get Control → End Pause）。
/// - 能力 PowerUpGetMsg.Spawn（SpawnPowerUpGetMsg：黑寡妇之后的针琴、原初罪者神龛的能力）。
/// - 纹章 ToolCrestUIMsg.Spawn（ShowToolCrestUIMsg）。
/// - 物品 CreateUIMsgGetItem（提示自己的 FSM 关掉时发 GET ITEM MSG END，原初罪者神龛的 Item Msg 等这个事件）。
/// 神居存档接管着（ModeController.InMode）时都不生成提示，下一帧照提示关掉时那样收尾：调回调（动作结束）、补发事件。
/// 晚一帧：同一个状态里登记事件的动作（AddEventRegister）排在生成提示的动作后面，当帧发出去 FSM 还没登记，收不到。
/// </summary>
internal static class RewardMsgs
{
    internal static bool Suppress => ModeController.Instance != null && ModeController.Instance.InMode;

    /// <summary>不弹提示：记一行日志，下一帧依次发 before 里的事件，再调 after，再发 afterEvents 里的事件。</summary>
    internal static void Skip(string what, Action? after, string[]? before = null, string[]? afterEvents = null)
    {
        GodhomePlugin.Log.LogInfo($"[Godhome] 神居存档里不弹获得提示 / skipped an item-get message: {what}");
        ModeController.Instance!.StartCoroutine(Finish(what, after, before, afterEvents));
    }

    private static IEnumerator Finish(string what, Action? after, string[]? before, string[]? afterEvents)
    {
        yield return null;
        Send(what, before);
        if (before != null && before.Length > 0)
        {
            // 原版“淡出了”在提示的隐藏动画里发，“结束了”在隐藏完以后：中间隔一帧，FSM 先走完“淡出了”那一步。
            yield return null;
        }
        try
        {
            after?.Invoke();
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 跳过获得提示后收尾出错 / finishing a skipped item-get message failed ({what}): {e.Message}");
        }
        Send(what, afterEvents);
    }

    private static void Send(string what, string[]? events)
    {
        if (events == null)
        {
            return;
        }
        foreach (string ev in events)
        {
            try
            {
                EventRegister.SendEvent(ev);
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 跳过获得提示后发事件出错 / sending {ev} after a skipped item-get message failed ({what}): {e.Message}");
            }
        }
    }
}

[HarmonyPatch(typeof(SkillGetMsg), nameof(SkillGetMsg.Spawn))]
internal static class SkillGetMsgPatch
{
    private static bool Prefix(ToolItemSkill skill, Action afterMsg)
    {
        if (!RewardMsgs.Suppress)
        {
            return true;
        }
        RewardMsgs.Skip($"技能 / skill {(skill != null ? skill.name : "-")}", afterMsg,
            before: new[] { "SKILL GET MSG FADED OUT" }, afterEvents: new[] { "SKILL GET MSG ENDED" });
        return false;
    }
}

[HarmonyPatch(typeof(PowerUpGetMsg), nameof(PowerUpGetMsg.Spawn))]
internal static class PowerUpGetMsgPatch
{
    private static bool Prefix(PowerUpGetMsg.PowerUps skill, Action afterMsg)
    {
        if (!RewardMsgs.Suppress)
        {
            return true;
        }
        RewardMsgs.Skip($"能力 / power-up {skill}", afterMsg);
        return false;
    }
}

[HarmonyPatch(typeof(ToolCrestUIMsg), nameof(ToolCrestUIMsg.Spawn))]
internal static class ToolCrestUIMsgPatch
{
    private static bool Prefix(ToolCrest crest, Action afterMsg)
    {
        if (!RewardMsgs.Suppress)
        {
            return true;
        }
        RewardMsgs.Skip($"纹章 / crest {(crest != null ? crest.name : "-")}", afterMsg);
        return false;
    }
}

/// <summary>物品提示：照原版登记“提示结束”事件、动作结束，提示不显示，下一帧发 GET ITEM MSG END（原版由提示自己的 FSM 关掉时发）。</summary>
[HarmonyPatch(typeof(CreateUIMsgGetItem), nameof(CreateUIMsgGetItem.OnEnter))]
internal static class CreateUIMsgGetItemPatch
{
    private const string EndEvent = "GET ITEM MSG END";

    private static bool Prefix(CreateUIMsgGetItem __instance)
    {
        if (!RewardMsgs.Suppress)
        {
            return true;
        }
        GameObject? owner = __instance.Owner;
        if (owner != null)
        {
            EventRegister.GetRegisterGuaranteed(owner, EndEvent);
        }
        __instance.Finish();
        RewardMsgs.Skip($"物品 / item（{(owner != null ? owner.name : "-")}）", null, afterEvents: new[] { EndEvent });
        return false;
    }
}
