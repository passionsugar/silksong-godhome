using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GlobalSettings;
using HarmonyLib;
using UnityEngine;

namespace Godhome;

/// <summary>
/// 调试：神居存档的念珠、工具碎片无限（0.10.5，念珠炮、建筑师纹章）。走游戏自己的入口：
/// 主角花钱的方法、货币队列、念珠炮（ThrowTool，和按键扔工具同一条路）、建筑师的现场打造（TryReplenishTools QuickCraft）；
/// 再拿存档副本核对普通数据照常扣（不补、不免扣）。只在自检槽、大厅里跑，改的配装由测试槽还原。
/// </summary>
internal sealed partial class DebugTools
{
    private readonly List<string> resourceLines = new();
    private bool resourceFail;

    private void ResCheck(string what, bool ok, string detail)
    {
        resourceFail |= !ok;
        resourceLines.Add($"- {(ok ? "PASS" : "FAIL")} {what}：{detail}");
        GodhomePlugin.Log.LogInfo($"[Godhome]   资源自检 / resource check: {(ok ? "PASS" : "FAIL")} {what}：{detail}");
    }

    private static IEnumerator Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            yield return null;
        }
    }

    /// <summary>
    /// 跑子协程，每一步都心跳。Unity 嵌套 yield 子协程时外层不醒，长的子步骤（落地检查、念珠炮等 3–8 秒）
    /// 会被 600 帧的看门狗当成协程停了（0.10.5 第一轮实机：念珠炮那步被看门狗中止）。
    /// 子协程里再嵌套的只能是短等待（Frames）。
    /// </summary>
    private IEnumerator Beating(IEnumerator inner)
    {
        while (true)
        {
            Beat();
            if (!inner.MoveNext())
            {
                yield break;
            }
            yield return inner.Current;
        }
    }

    private IEnumerator ResourceSelfTest()
    {
        PlayerData pd = PlayerData.instance;
        GameManager gm = GameManager.instance;
        HeroController hero = HeroController.instance;
        int wantGeo = GodhomeResources.Target(CurrencyType.Money);
        int wantShard = GodhomeResources.Target(CurrencyType.Shard);
        string Have() => $"念珠 / rosaries {pd.geo}，碎片 / shards {pd.ShellShards}（要 / want {wantGeo}/{wantShard}；上限 / caps {Gameplay.GetCurrencyCap(CurrencyType.Money)}/{Gameplay.GetCurrencyCap(CurrencyType.Shard)}）";
        bool Full() => pd.geo == wantGeo && pd.ShellShards == wantShard;
        GodhomePlugin.Log.LogInfo($"[Godhome] 资源自检开始 / resource self-test: {Have()}");

        // 1. 旧神居档余额很低（以前打过念珠炮、或 0.10.4 以前的档）：读进来以后要补满。
        pd.geo = 3;
        pd.ShellShards = 2;
        yield return Frames(3);
        ResCheck("低余额补满 / low balance topped up", Full(), Have());

        // 2. 游戏自己的花钱入口：主角的 TakeCurrency（念珠炮、受伤掉钱走这条）、商店式 TakeCurrency、CurrencyManager.TakeGeo/TakeShards、PlayerData 直接扣。
        hero.TakeCurrency(150, CurrencyType.Money);
        hero.TakeCurrency(30, CurrencyType.Shard);
        CurrencyManager.TakeCurrency(20, CurrencyType.Money);
        CurrencyManager.TakeShards(5);
        pd.TakeGeo(7);
        pd.TakeShards(3);
        pd.IntAdd(nameof(PlayerData.geo), -11);
        yield return Frames(4);
        ResCheck("花钱不减少 / spending does not reduce", Full(), Have());

        // 3. 普通数据照常扣：存档副本（保存时写的就是这种副本）、普通存档（bossRushMode 关）。
        string iso;
        bool isoOk;
        try
        {
            PlayerData copy = SaveDataUtility.DeserializeSaveData<SaveGameData>(
                SaveDataUtility.SerializeSaveData(new SaveGameData(pd, gm.sceneData)))!.playerData;
            copy.geo = 500;
            copy.TakeGeo(5);
            copy.ShellShards = 100;
            copy.TakeShards(4);
            int geoCopy = copy.geo, shardCopy = copy.ShellShards;
            copy.bossRushMode = false;
            copy.TakeGeo(10);
            copy.IntAdd(nameof(PlayerData.geo), -20);
            int geoNormal = copy.GetInt(nameof(PlayerData.geo));
            isoOk = geoCopy == 495 && shardCopy == 96 && geoNormal == 465 && Full();
            iso = $"神居副本 / godhome copy 500-5={geoCopy}、100-4={shardCopy}；普通数据 / normal data 495-10-20={geoNormal}；当前档 / live {pd.geo}/{pd.ShellShards}";
        }
        catch (Exception e)
        {
            isoOk = false;
            iso = "出错 / error: " + e.Message;
        }
        ResCheck("不是当前神居档的数据照常扣 / other data is charged as usual", isoOk, iso);

        // 4. 建筑师（Toolmaster）的现场打造：清空一个用碎片补的工具，碎片压低，走 TryReplenishTools(QuickCraft)。
        string prevCrest = pd.CurrentCrestID;
        ToolCrest? toolmaster = ToolItemManager.GetCrestByName("Toolmaster");
        if (toolmaster == null)
        {
            ResCheck("建筑师现场打造 / Architect quick craft", false, "找不到纹章 Toolmaster / crest not found");
        }
        else
        {
            ToolItemManager.AutoEquip(toolmaster, false, false);
            ToolItemManager.SendEquippedChangedEvent(force: true);
            yield return Frames(3);
            ToolItem? shardTool = ToolItemManager.GetEquippedToolsForCrest(pd.CurrentCrestID)?
                .FirstOrDefault(t => t != null && t.ReplenishResource == ToolItem.ReplenishResources.Shard && t.IsAutoReplenished());
            if (shardTool == null)
            {
                shardTool = ToolItemManager.GetUnlockedTools().FirstOrDefault(t => t != null && t.Type == ToolItemType.Red
                    && t.ReplenishResource == ToolItem.ReplenishResources.Shard && t.IsAutoReplenished());
                if (shardTool != null)
                {
                    ToolItemManager.AutoEquip(shardTool);
                    ToolItemManager.SendEquippedChangedEvent(force: true);
                    yield return Frames(3);
                }
            }
            if (shardTool == null)
            {
                ResCheck("建筑师现场打造 / Architect quick craft", false, $"没有用碎片补给的工具 / no shard-replenished tool（crest {pd.CurrentCrestID}）");
            }
            else
            {
                ToolItemsData.Data data = pd.GetToolData(shardTool.name);
                int max = ToolItemManager.GetToolStorageAmount(shardTool);
                data.AmountLeft = 0;
                pd.SetToolData(shardTool.name, data);
                pd.ShellShards = 1;
                int craftWaivedBefore = GodhomeResources.Waived;
                bool crafted = ToolItemManager.TryReplenishTools(true, ToolItemManager.ReplenishMethod.QuickCraft);
                yield return Frames(4);
                int after = pd.GetToolData(shardTool.name).AmountLeft;
                int craftWaived = GodhomeResources.Waived - craftWaivedBefore;
                ResCheck("建筑师现场打造 / Architect quick craft", pd.CurrentCrestID == "Toolmaster" && crafted && after == max && craftWaived > 0 && Full(),
                    $"纹章 / crest {pd.CurrentCrestID}，{shardTool.name} 0→{after}/{max}，补给 / replenished {crafted}，免扣 / waived spends {craftWaived}，{Have()}");
            }
        }

        // 5. 念珠炮。主角 Tool Attacks FSM（fsm-hero-tool-attacks.txt）：每发一炮是 CustomToolUsage(UseAmount=1)，扣的是工具自己的
        //    弹药（AmountLeft），不直接扣念珠；按住工具键（ListenForToolThrow）才继续蓄力、连发。念珠花在补弹药上：念珠炮是
        //    OneForOne，TryReplenishTools（长椅、建筑师现场打造）跳过它；补弹药是在长椅边打开物品栏按住按键，InventoryItemToolBase
        //    每 0.025 秒调一次 ToolItem.ReloadSingle()（弹药 +1，CurrencyManager.TakeCurrency(1, Money)），直到 CanReload() 为假
        //    （弹药满，或念珠不到 1）。所以分两步测：
        //    a) 弹药打空、念珠压到 0，照物品栏那样一发一发补：能补（查余额先补齐）、补满、每发的扣款都被免、念珠不少；
        //    b) 真的开炮：走 ThrowTool 进蓄力，自检不能按住按键，就在蓄力状态里直接切到开炮（Shoot Start Ground），看弹药被用掉。
        ToolItem? cannon = ToolItemManager.GetAllTools().FirstOrDefault(t => t != null && t.name.IndexOf("Rosary", StringComparison.OrdinalIgnoreCase) >= 0
            && t.name.IndexOf("Cannon", StringComparison.OrdinalIgnoreCase) >= 0);
        if (cannon == null)
        {
            string names = string.Join(",", ToolItemManager.GetAllTools().Where(t => t != null).Select(t => t.name));
            ResCheck("念珠炮 / Rosary Cannon", false, "找不到工具 / tool not found；all: " + names);
        }
        else
        {
            ToolItemManager.AutoEquip(cannon);
            ToolItemManager.SendEquippedChangedEvent(force: true);
            ToolItemsData.Data cd = pd.GetToolData(cannon.name);
            cd.IsUnlocked = true;
            cd.AmountLeft = 0;
            pd.SetToolData(cannon.name, cd);
            yield return Frames(3);
            int max = ToolItemManager.GetToolStorageAmount(cannon);
            int waivedBefore = GodhomeResources.Waived;
            pd.geo = 0;
            bool canStart = cannon.CanReload();
            int loads = 0;
            while (cannon.CanReload() && loads < 1000)
            {
                cannon.ReloadSingle();
                loads++;
            }
            yield return Frames(4);
            int refilled = pd.GetToolData(cannon.name).AmountLeft;
            int waived = GodhomeResources.Waived - waivedBefore;
            ResCheck("念珠炮补弹药 / Rosary Cannon reload", cannon.ReplenishResource == ToolItem.ReplenishResources.Money
                    && cannon.ReplenishUsage == ToolItem.ReplenishUsages.OneForOne && canStart && max > 0 && refilled == max && loads == max && waived >= max && Full(),
                $"{cannon.name}（{cannon.ReplenishResource} / {cannon.ReplenishUsage}），念珠压到 0 后能补 / reload allowed at 0 rosaries {canStart}，"
                + $"弹药 / ammo 0→{refilled}/{max}，补了 / reloads {loads}，免扣 / waived spends {waived}，{Have()}");

            AttackToolBinding? binding = ToolItemManager.GetAttackToolBinding(cannon);
            PlayMakerFSM? toolFsm = AccessTools.Field(typeof(HeroController), "toolEventTarget").GetValue(hero) as PlayMakerFSM;
            float until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && (hero.controlReqlinquished || !hero.acceptingInput || !hero.cState.onGround))
            {
                yield return null;
            }
            int ammoBefore = pd.GetToolData(cannon.name).AmountLeft;
            var states = new List<string> { toolFsm != null ? toolFsm.ActiveStateName : "-" };
            string err = "";
            bool forced = false;
            int projectiles = -1;
            try
            {
                AccessTools.Field(typeof(HeroController), "willThrowTool").SetValue(hero, cannon);
                AccessTools.Method(typeof(HeroController), "ThrowTool", new[] { typeof(bool) }).Invoke(hero, new object[] { true });
            }
            catch (Exception e)
            {
                err = "；出错 / error: " + (e.InnerException ?? e).Message;
            }
            float end = Time.unscaledTime + 3f;
            while (Time.unscaledTime < end && toolFsm != null)
            {
                string st = toolFsm.ActiveStateName;
                if (st != states[states.Count - 1])
                {
                    states.Add(st);
                }
                if (!forced && st == "RC Charge Ground")
                {
                    // 相当于按住工具键蓄力到头。
                    toolFsm.SetState("Shoot Start Ground");
                    forced = true;
                    st = toolFsm.ActiveStateName;
                    if (st != states[states.Count - 1])
                    {
                        states.Add(st);
                    }
                    // Shoot 状态从对象池放出的念珠弹（Geo Small Projectile），只作旁证记下来。
                    projectiles = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                        .Count(t => t != null && t.gameObject.activeInHierarchy && t.name.StartsWith("Geo Small Projectile", StringComparison.Ordinal));
                }
                yield return null;
            }
            int ammoAfter = pd.GetToolData(cannon.name).AmountLeft;
            // 没按住键时 Shoot Start Ground → Shoot Start → Big Shoot → Shoot → Shoot Loop → Return Control 在同一帧里走完，
            // 逐帧看状态看不到 Shoot（0.10.5 第六轮实机：Idle → RC Charge Ground → Cooldown → Idle，弹药 80→79）。
            // 念珠炮是自定义用量（IsCustomUsage），ThrowTool 不扣弹药，只有 Shoot 状态的 CustomToolUsage 扣，所以弹药少 1 就是开了一炮。
            bool shot = states.Contains("Shoot") || (cannon.IsCustomUsage && ammoAfter == ammoBefore - 1);
            ResCheck("念珠炮开炮 / Rosary Cannon fires", binding != null && forced && shot && ammoAfter < ammoBefore && Full() && err.Length == 0,
                $"绑定 / binding {(binding?.ToString() ?? "-")}，切到开炮 / forced {forced}，自定义用量 / custom usage {cannon.IsCustomUsage}，弹药 / ammo {ammoBefore}→{ammoAfter}，念珠弹 / projectiles {projectiles}，"
                + $"工具 FSM / tool fsm {toolFsm?.FsmName ?? "-"}: {string.Join(" → ", states.Take(16))}，{Have()}{err}");
        }

        // 配装放回（测试槽，自检结束后整个还原）。
        ToolCrest? back = ToolItemManager.GetCrestByName(prevCrest);
        if (back != null && pd.CurrentCrestID != prevCrest)
        {
            ToolItemManager.AutoEquip(back, false, false);
            ToolItemManager.SendEquippedChangedEvent(force: true);
        }
        yield return Frames(2);
    }
}
