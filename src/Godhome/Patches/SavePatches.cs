using System;
using HarmonyLib;

namespace Godhome;

// [Godhome] 战斗期间（有快照时）所有“把内存数据打包成存档”的入口都临时换成快照副本。
// 这些函数在构造 SaveGameData 之前是同步的，异步写盘用的是已经捕获的快照副本。
// 正常情况下不跳过原函数：DataManager 挂在 SaveGame 上保存模式进度，跳过会让它不存。
// 换入失败（快照读不回来、存档槽对不上）时跳过这次存档并按失败回调，绝不写实时数据（Codex 审查 #1、#2）。
// SaveGame / CreateRestorePoint 里先调 SaveLevelState：场景里的持久物件写的是 SceneData.instance（实时对象），
// 不是换进来的快照副本，所以战斗里的场景状态不会进存档文件（见 PersistentBoolItem / GeoRock 反编译代码）。
// 大厅里没有快照，这些补丁什么都不做，存档照常写实时数据。
//
// 恢复点（Codex 复审）：游戏每个槽只保留约 19 个恢复点，多了删最旧的（DesktopSaveRestoreHandler: TrimAll(19, …)，
// 只有 ACT_1/2/3 不删）。Boss 场景会触发自动恢复点，内容虽然是快照，却会挤掉旧恢复点。
// 所以有快照期间不生成恢复点：CreateRestorePoint 直接按成功回调（和游戏“不允许存档”时一样），
// SaveGame 的 withAutoSave 改成 false（只写存档、不附带恢复点）。
//
// SaveGuard.BlockAll（战斗中快照意外丢失）时一律拦下，回到标题画面才放开。
//
// 分析器 Harmonize 1.0.3 只认 Prefix / Postfix / Transpiler，不认 [HarmonyFinalizer]，这几个 Finalizer 会报 HARMONIZE004。
// HarmonyX 是认的（异常路径要靠它把实时数据换回来），不能为了消告警改成 Postfix（Codex 审计 0.10.3 第 6.2 节）。
#pragma warning disable HARMONIZE004

[HarmonyPatch(typeof(GameManager), "SaveGame", new[] { typeof(int), typeof(Action<bool>), typeof(bool), typeof(AutoSaveName) })]
internal static class SaveGamePatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(GameManager __instance, int saveSlot, Action<bool>? ogCallback, ref bool withAutoSave, AutoSaveName autoSaveName, out bool __state)
    {
        __state = false;
        if (!SaveGuard.BlockAll)
        {
            if (SaveGuard.TrySwapIn(__instance, saveSlot, "SaveGame", out __state))
            {
                // 换入成功、写的是快照时才去掉恢复点：换入失败（例如快照不是这个存档的）时不该动别的存档的恢复点（审查）。
                if (__state && withAutoSave)
                {
                    GodhomePlugin.Log.LogInfo($"[Godhome] 战斗中存档不附带恢复点 / no restore point with this save during a fight ({autoSaveName})");
                    withAutoSave = false;
                }
                return true;
            }
        }
        else
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 存档被拦下（等回标题画面）/ save blocked until the title screen (SaveGame, slot {saveSlot})");
        }
        SavePatchUtil.NotifyBlocked();
        if (ogCallback != null)
        {
            CoreLoop.InvokeNext(() => ogCallback(false));
        }
        return false;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(GameManager __instance, bool __state, Exception? __exception)
    {
        if (__state)
        {
            SaveGuard.SwapOut(__instance);
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(GameManager), "CreateRestorePoint", new[] { typeof(int), typeof(AutoSaveName), typeof(Action<bool>) })]
internal static class CreateRestorePointPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Prefix(GameManager __instance, int saveSlot, AutoSaveName autoSaveName, Action<bool>? callback, out bool __state)
    {
        __state = false;
        if (SaveGuard.BlockAll)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 恢复点被拦下（等回标题画面）/ restore point blocked until the title screen ({autoSaveName}, slot {saveSlot})");
            SavePatchUtil.NotifyBlocked();
            if (callback != null)
            {
                CoreLoop.InvokeNext(() => callback(false));
            }
            return false;
        }
        // 快照还是内存里这份数据的才跳过（Codex 复查）：两个数据对象都换了（读了别的档）就照常生成那份存档自己的恢复点，
        // 只换了一个的交给下面的 TrySwapIn 按失败处理。
        if (SaveGuard.Active && !SaveGuard.Stale(__instance))
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 战斗中不生成恢复点 / restore point skipped during a fight ({autoSaveName}, slot {saveSlot})");
            callback?.Invoke(true);
            return false;
        }
        if (SaveGuard.TrySwapIn(__instance, saveSlot, "CreateRestorePoint", out __state))
        {
            return true;
        }
        SavePatchUtil.NotifyBlocked();
        if (callback != null)
        {
            CoreLoop.InvokeNext(() => callback(false));
        }
        return false;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(GameManager __instance, bool __state, Exception? __exception)
    {
        if (__state)
        {
            SaveGuard.SwapOut(__instance);
        }
        return __exception;
    }
}

// 下面两个返回 SaveGameData，没法“跳过”：换入失败时让异常抛出去（CreateRestorePoint 里有 try/catch，会按失败回调），
// 不返回实时数据。异常发生在 SwapIn 改任何东西之前，__state 是 false，Finalizer 不会多做 SwapOut。

[HarmonyPatch(typeof(GameManager), nameof(GameManager.GetSaveGameData))]
internal static class GetSaveGameDataPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(GameManager __instance, int saveSlot, out bool __state)
    {
        SavePatchUtil.ThrowIfBlocked("GetSaveGameData");
        __state = SaveGuard.SwapIn(__instance, saveSlot);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(GameManager __instance, bool __state, Exception? __exception)
    {
        if (__state)
        {
            SaveGuard.SwapOut(__instance);
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.CreateSaveGameData))]
internal static class CreateSaveGameDataPatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(GameManager __instance, int saveSlot, out bool __state)
    {
        SavePatchUtil.ThrowIfBlocked("CreateSaveGameData");
        __state = SaveGuard.SwapIn(__instance, saveSlot);
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(GameManager __instance, bool __state, Exception? __exception)
    {
        if (__state)
        {
            SaveGuard.SwapOut(__instance);
        }
        return __exception;
    }
}

/// <summary>
/// 前作神居（分支 hk1-godhome）：存档里的复活点不能是前作的场景（没有前作或插件时读档进不去），
/// 写盘前（PreparePlayerDataForSave，SaveGame / CreateSaveGameData 经过它；GetSaveGameData 把同样的代码抄了一份、不经过它，
/// 游戏自己不调 GetSaveGameData，只有别的模组会用。战斗中写的是换进来的快照）
/// 把前作神居的复活点换回织忆圣所的长椅；读档后落在长椅，有前作就马上转去前厅（ModeController）。
/// </summary>
[HarmonyPatch(typeof(GameManager), "PreparePlayerDataForSave")]
internal static class SaveSanitizePatch
{
    private static void Postfix(GameManager __instance)
    {
        PlayerData pd = __instance.playerData;
        if (pd == null || !pd.bossRushMode)
        {
            return;
        }
        string lobby = GodhomeConfig.LobbyScene.Value;
        string marker = GodhomeConfig.LobbyMarker.Value;
        string was = "";
        if (Hk1Lobby.IsHk1Scene(pd.respawnScene))
        {
            was += $" respawn {pd.respawnScene}@{pd.respawnMarkerName}";
            pd.respawnScene = lobby;
            pd.respawnMarkerName = marker;
            pd.respawnType = 1;
        }
        if (Hk1Lobby.IsHk1Scene(pd.tempRespawnScene))
        {
            was += $" temp {pd.tempRespawnScene}@{pd.tempRespawnMarker}";
            pd.tempRespawnScene = "";
            pd.tempRespawnMarker = "";
        }
        if (Hk1Lobby.IsHk1Scene(pd.nonLethalRespawnScene))
        {
            was += $" non-lethal {pd.nonLethalRespawnScene}@{pd.nonLethalRespawnMarker}";
            pd.nonLethalRespawnScene = lobby;
            pd.nonLethalRespawnMarker = marker;
        }
        if (was.Length > 0)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 存档里的复活点换回织忆圣所 / save respawn moved back to the sanctum ({lobby}@{marker}):{was}");
        }
    }
}

/// <summary>回大厅途中不保存场景状态（快照已经还原，不能再把战场的状态写进去）。</summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.SaveLevelState))]
internal static class SaveLevelStatePatch
{
    private static bool Prefix() => !SaveGuard.SuppressLevelState;
}

internal static class SavePatchUtil
{
    /// <summary>存档被拦下时告诉玩家（原函数被跳过，游戏自己的“存档失败”提示也就没了，Codex 复审）。</summary>
    internal static void NotifyBlocked() => ModeController.Instance?.ShowNotice(L.Get("ERR_SAVE_BLOCKED"), 8f);

    internal static void ThrowIfBlocked(string what)
    {
        if (SaveGuard.BlockAll)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 存档数据打包被拦下（等回标题画面）/ save data blocked until the title screen ({what})");
            throw new InvalidOperationException($"[Godhome] saves are blocked until the title screen ({what})");
        }
    }
}
