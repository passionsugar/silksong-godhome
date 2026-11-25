using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Godhome;

/// <summary>
/// [Godhome] 神居存档。
/// 新建存档时在“选择模式”里选“织忆圣所”，走游戏自己留下的前作神居分支：
/// GameManager.StartNewGame(bossRushMode: true) 新建 PlayerData → AddGGPlayerDataOverrides()（丝之歌里是空函数）→
/// 不播开场，直接按“继续游戏”的流程在 respawnScene / respawnMarkerName 出生（GameManager.cs:5184-5298）。
/// PlayerData.bossRushMode 会存进存档（PlayerData.cs:2721），存档列表对这种存档有专门的显示（SaveSlotButton.cs:1544）。
/// 插件只在 bossRushMode = true 的存档里做事。
/// 满配的内容在 data/kit.json。
/// </summary>
internal static class GodhomeSave
{
    private static readonly Dictionary<string, JToken> kitFields = new();
    private static int kitMaxHealthBase = -1;
    private static bool kitTools;
    private static bool kitCrests;

    /// <summary>刚新建、还没存过档的神居存档数据（进游戏后要马上存一次，否则关游戏时存档槽还是空的）。</summary>
    private static PlayerData? justCreated;

    internal static bool IsGodhome(PlayerData? pd) => pd != null && pd.bossRushMode;

    /// <summary>这份数据是不是刚新建的（取一次就清掉）。</summary>
    internal static bool TakeJustCreated(PlayerData pd)
    {
        bool yes = ReferenceEquals(justCreated, pd);
        justCreated = null;
        return yes;
    }

    internal static bool IsGodhome(GameManager? gm) => gm != null && IsGodhome(gm.playerData);

    /// <summary>读满配表。读不了就不补满配（神居存档照样能玩，只是要自己去拿能力），并记错误日志。</summary>
    internal static void LoadKit(string path)
    {
        kitFields.Clear();
        kitMaxHealthBase = -1;
        kitTools = kitCrests = false;
        try
        {
            JObject root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (root["playerData"] is JObject fields)
            {
                foreach (JProperty p in fields.Properties())
                {
                    FieldInfo? f = BossCatalog.PlayerDataField(p.Name);
                    if (f == null)
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 满配表：PlayerData 没有字段，跳过 / kit: no PlayerData field '{p.Name}', skipped");
                        continue;
                    }
                    try
                    {
                        _ = p.Value.ToObject(f.FieldType);
                        kitFields[p.Name] = p.Value;
                    }
                    catch (Exception e)
                    {
                        GodhomePlugin.Log.LogWarning($"[Godhome] 满配表：'{p.Name}' 的值不是 {f.FieldType.Name}，跳过 / kit: bad value for '{p.Name}': {e.Message}");
                    }
                }
            }
            if (root["maxHealthBase"] is JValue mh && mh.Type == JTokenType.Integer && (long)mh is >= 1 and <= 99)
            {
                kitMaxHealthBase = (int)(long)mh;
            }
            kitTools = root["unlockAllTools"] is JValue t && t.Type == JTokenType.Boolean && (bool)t;
            kitCrests = root["unlockAllCrests"] is JValue c && c.Type == JTokenType.Boolean && (bool)c;
            GodhomePlugin.Log.LogInfo($"[Godhome] 满配表 / kit: {kitFields.Count} 个字段, 面具 / masks {kitMaxHealthBase}, 工具 / tools {kitTools}, 纹章 / crests {kitCrests}");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 读取满配表失败 / failed to load the kit {path}: {e.Message}");
        }
    }

    /// <summary>
    /// 新建神居存档（AddGGPlayerDataOverrides 之后）：打上神居标记、出生点设在大厅长椅、写入满配的数值。
    /// 这时还在标题画面，只改数据；工具和纹章要用到游戏里的管理器，进游戏后（ApplyKit）再解锁。
    /// </summary>
    internal static void SetupNew(PlayerData pd)
    {
        pd.bossRushMode = true;
        pd.permadeathMode = GlobalEnums.PermadeathModes.Off;
        pd.respawnScene = GodhomeConfig.LobbyScene.Value;
        pd.respawnMarkerName = GodhomeConfig.LobbyMarker.Value;
        // 1 = 在长椅上醒来（HeroController.cs:7215）。
        pd.respawnType = 1;
        int n = ApplyFields(pd);
        pd.health = pd.maxHealth;
        justCreated = pd;
        GodhomePlugin.Log.LogInfo($"[Godhome] 新建神居存档 / new godhome save: respawn {pd.respawnScene} @ {pd.respawnMarkerName}, {n} 个满配字段 / kit fields set, masks {pd.maxHealthBase}, silk {pd.silkMax}");
    }

    /// <summary>
    /// 每次进入神居存档补一遍满配：数值字段、全部工具、全部纹章。已经有的不变，不弹获得提示。
    /// 解锁时游戏会排成就，神居存档里都被拦下（AchievementGate）。
    /// </summary>
    /// <returns>改了多少项（0 = 本来就是满配）。</returns>
    internal static int ApplyKit(PlayerData pd)
    {
        int fields = ApplyFields(pd);
        int tools = kitTools ? UnlockTools() : 0;
        int crests = kitCrests ? UnlockCrests() : 0;
        GodhomePlugin.Log.LogInfo($"[Godhome] 满配 / kit applied: {fields} 个字段, 新解锁工具 / tools {tools}, 新解锁纹章 / crests {crests}");
        return fields + tools + crests;
    }

    private static int ApplyFields(PlayerData pd)
    {
        int changed = 0;
        foreach (KeyValuePair<string, JToken> kv in kitFields)
        {
            FieldInfo? f = BossCatalog.PlayerDataField(kv.Key);
            if (f == null)
            {
                continue;
            }
            try
            {
                object? v = kv.Value.ToObject(f.FieldType);
                if (!Equals(f.GetValue(pd), v))
                {
                    f.SetValue(pd, v);
                    changed++;
                }
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 满配字段设置失败 / failed to set kit field {kv.Key}: {e.Message}");
            }
        }
        // 面具用游戏自己的函数加，maxHealthBase 和 maxHealth 一起变（PlayerData.AddToMaxHealth）。
        if (kitMaxHealthBase > 0 && pd.maxHealthBase < kitMaxHealthBase)
        {
            pd.AddToMaxHealth(kitMaxHealthBase - pd.maxHealthBase);
            changed++;
        }
        return changed;
    }

    /// <summary>解锁全部工具（含灵丝技能），不弹提示。后来的版本会替换掉的旧版本照游戏的规则锁上（ToolItem.getReplaces）。</summary>
    private static int UnlockTools()
    {
        int n = 0;
        var unlocked = new List<string>();
        try
        {
            PlayerData pd = PlayerData.instance;
            pd.SeenToolGetPrompt = true;
            pd.SeenToolWeaponGetPrompt = true;
            List<ToolItem> all = ToolItemManager.GetAllTools().Where(t => t != null).ToList();
            foreach (ToolItem t in all)
            {
                if (t.IsUnlocked)
                {
                    continue;
                }
                t.SetUnlockedTestsComplete();
                t.Unlock(null, ToolItem.PopupFlags.None);
                n++;
                unlocked.Add(t.name);
            }
            // 被替换的旧版本：新版本解锁时游戏会把旧版本锁上；如果旧版本排在后面又被解锁了，这里再锁回去。
            foreach (ToolItem t in all)
            {
                ToolItem? old = Traverse.Create(t).Field("getReplaces").GetValue<ToolItem>();
                if (old != null && t.IsUnlocked && old.IsUnlocked)
                {
                    ToolItemManager.ReplaceToolEquips(old, t);
                    old.Lock();
                    unlocked.Remove(old.name);
                    GodhomePlugin.Log.LogInfo($"[Godhome]   {old.name} 被 {t.name} 替换，锁上 / replaced by {t.name}, locked");
                }
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 解锁工具出错 / unlocking tools failed: {e}");
        }
        if (unlocked.Count > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome]   新解锁工具 / tools unlocked: {string.Join(", ", unlocked)}");
        }
        return n;
    }

    /// <summary>解锁全部不隐藏的纹章（升级版会连带解锁前一版，ToolCrest.Unlock），加上额外的蓝、黄槽（kit.json）。</summary>
    private static int UnlockCrests()
    {
        int n = 0;
        var unlocked = new List<string>();
        try
        {
            foreach (ToolCrest c in ToolItemManager.GetAllCrests().Where(c => c != null))
            {
                if (c.IsHidden || c.IsUnlocked)
                {
                    continue;
                }
                c.Unlock();
                n++;
                unlocked.Add(c.name);
            }
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 解锁纹章出错 / unlocking crests failed: {e}");
        }
        if (unlocked.Count > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome]   新解锁纹章 / crests unlocked: {string.Join(", ", unlocked)}");
        }
        return n;
    }
}
