using System;
using System.Collections;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Godhome;

/// <summary>
/// [Godhome] 存档保护（第二版：每场战斗一份快照）。
/// 开战前，用游戏自己的序列化器（SaveDataUtility）把大厅里的 PlayerData + SceneData 存成快照。
/// 战斗期间游戏照常存档，但写进存档文件的是快照（见 SavePatches）；DataManager 照常保存模式进度。
/// 回大厅时把快照按游戏的 JSON 合约逐字段拷回内存里的同一个对象（HeroController 等缓存了引用，不能换对象），再丢掉快照。
/// </summary>
internal static class SaveGuard
{
    private static string? snapshotJson;
    private static int snapshotSlot = -1;
    // 建快照时内存里的那两个对象。游戏只在读档（SetLoadedGameData）、新游戏（StartNewGame）、
    // 回标题（QuitToMenu）时换掉 gm.playerData / sceneData；对象变了 = 内存里已经不是模式数据了。
    private static PlayerData? takenPd;
    private static SceneData? takenSd;
    private static RespawnState? entryRespawn;

    // 正在进行的存档替换：换进去的快照副本，以及要换回来的实时对象。
    private static int swapDepth;
    private static PlayerData? livePd;
    private static SceneData? liveSd;
    private static PlayerData? installedPd;
    private static SceneData? installedSd;

    internal static bool Active => snapshotJson != null;

    /// <summary>快照属于哪个存档槽（没有快照时是 -1）。</summary>
    internal static int Slot => snapshotJson != null ? snapshotSlot : -1;

    /// <summary>
    /// 为 true 时跳过 GameManager.SaveLevelState。回大厅时从还原快照开始，一直到进入大厅为止都是 true：
    /// 这期间（包括转场途中的存档、重试）都不能把战场的场景状态写进还原后的数据（Codex 复审）。
    /// 由 ModeController 在到达后清掉，Clear() 也会清。
    /// </summary>
    internal static bool SuppressLevelState { get; set; }

    /// <summary>
    /// 为 true 时拦下所有存档（战斗中快照意外丢失：内存里是战斗数据，不能写进神居存档）。回到标题画面时清掉。
    /// </summary>
    internal static bool BlockAll { get; set; }

    /// <summary>
    /// 建立快照。已经有快照时拒绝（不能覆盖）。建好后马上试着反序列化一次：
    /// 读不回来的快照不能发布，否则之后每次存档都会出错（Codex 审查 #5）。出错时什么都不留下，异常交给调用方。
    /// </summary>
    internal static void Take(GameManager gm)
    {
        if (snapshotJson != null && !SameData(gm))
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 丢弃旧快照（内存里已是别的存档数据）/ dropping a stale snapshot of slot {snapshotSlot}");
            Clear();
        }
        if (snapshotJson != null)
        {
            throw new InvalidOperationException($"[Godhome] snapshot already taken for slot {snapshotSlot}");
        }
        gm.SaveLevelState();
        string json = SaveDataUtility.SerializeSaveData(new SaveGameData(gm.playerData, gm.sceneData));
        SaveGameData check = SaveDataUtility.DeserializeSaveData<SaveGameData>(json)
            ?? throw new InvalidOperationException("[Godhome] snapshot does not deserialize");
        if (check.playerData == null || check.sceneData == null)
        {
            throw new InvalidOperationException("[Godhome] snapshot deserialized without playerData/sceneData");
        }
        snapshotJson = json;
        snapshotSlot = gm.profileID;
        takenPd = gm.playerData;
        takenSd = gm.sceneData;
        entryRespawn = RespawnState.Capture(gm.playerData);
        swapDepth = 0;
        GodhomePlugin.Log.LogInfo($"[Godhome] 存档快照已建立 / snapshot taken ({json.Length} chars, slot {gm.profileID})");
    }

    /// <summary>内存里还是建快照时的那两个对象吗（换了说明读了别的档、开了新游戏或回了标题）。</summary>
    private static bool SameData(GameManager gm) => ReferenceEquals(gm.playerData, takenPd) && ReferenceEquals(gm.sceneData, takenSd);

    /// <summary>有快照，但内存里的数据对象已经不是建快照时的那两个了（不在存档替换途中时才判断）。</summary>
    internal static bool Stale(GameManager gm) => snapshotJson != null && swapDepth == 0 && !SameData(gm);

    /// <summary>
    /// 有快照，两个数据对象只换了一个（游戏自己的代码里没有这种路径，读档两个一起换、新游戏只在标题画面换 playerData）：
    /// 说不清内存里是谁的数据，丢快照之前要先拦下所有存档（Codex 审查 0.8.0）。
    /// </summary>
    internal static bool Mixed(GameManager gm) =>
        snapshotJson != null && swapDepth == 0 && ReferenceEquals(gm.playerData, takenPd) != ReferenceEquals(gm.sceneData, takenSd);

    /// <summary>当前槽和要写的槽是不是都等于快照的槽。</summary>
    private static bool SlotMatches(GameManager gm, int targetSlot, string what)
    {
        if (gm.profileID == snapshotSlot && targetSlot == snapshotSlot)
        {
            return true;
        }
        GodhomePlugin.Log.LogError($"[Godhome] {what}：存档槽对不上 / slot mismatch (snapshot {snapshotSlot}, current {gm.profileID}, target {targetSlot})");
        return false;
    }

    /// <summary>还原前的检查：数据对象和存档槽都必须是建快照时的那个，否则抛异常，什么都不改。</summary>
    private static void RequireOwner(GameManager gm, string what)
    {
        if (!SameData(gm))
        {
            throw new InvalidOperationException($"[Godhome] {what}: the save data in memory is no longer the one the snapshot was taken from");
        }
        if (!SlotMatches(gm, gm.profileID, what))
        {
            throw new InvalidOperationException($"[Godhome] {what}: refusing to apply slot {snapshotSlot}'s snapshot to slot {gm.profileID}");
        }
    }

    /// <summary>从快照反序列化出一份全新的数据（每次调用都是新对象）。</summary>
    internal static SaveGameData Fresh()
    {
        if (snapshotJson == null)
        {
            throw new InvalidOperationException("[Godhome] no snapshot");
        }
        return SaveDataUtility.DeserializeSaveData<SaveGameData>(snapshotJson);
    }

    /// <summary>
    /// 存档函数开始前：把 GameManager 上的 playerData/sceneData 临时换成快照副本。返回是否换了（换了才需要 SwapOut）。
    /// 先把快照副本读出来，再改计数和字段：读失败时什么都没变，异常让这次存档失败，绝不退回去写实时数据（Codex 审查 #1）。
    /// targetSlot：存档函数要写的槽（Codex 审查 #2、复审）：
    ///   两个数据对象都换了 = 读了别的档 / 新游戏，内存里是那份存档自己的数据，快照作废，照常存；
    ///   只换了一个 = 说不清内存里是谁的数据，拦下这次存档；
    ///   对象没换但槽对不上 = 模式数据不能写到任何地方，拦下这次存档；
    ///   嵌套调用时 GameManager 上必须还是我们换进去的那两个对象。
    /// </summary>
    internal static bool SwapIn(GameManager gm, int targetSlot)
    {
        if (!Active)
        {
            return false;
        }
        if (swapDepth > 0)
        {
            if (!ReferenceEquals(gm.playerData, installedPd) || !ReferenceEquals(gm.sceneData, installedSd))
            {
                throw new InvalidOperationException("[Godhome] save data was replaced during a protected save");
            }
            if (!SlotMatches(gm, targetSlot, "存档 / save"))
            {
                throw new InvalidOperationException($"[Godhome] refusing to save while slot {snapshotSlot}'s mode data is in memory (target {targetSlot})");
            }
            swapDepth++;
            return true;
        }
        bool pdSame = ReferenceEquals(gm.playerData, takenPd);
        bool sdSame = ReferenceEquals(gm.sceneData, takenSd);
        if (!pdSame && !sdSame)
        {
            GodhomePlugin.Log.LogError($"[Godhome] 内存里已经换成别的存档数据，作废快照 / different save data is loaded (slot {gm.profileID}), dropping slot {snapshotSlot}'s snapshot");
            Clear();
            return false;
        }
        if (!pdSame || !sdSame)
        {
            throw new InvalidOperationException($"[Godhome] only {(pdSame ? "sceneData" : "playerData")} was replaced; refusing to save mixed data");
        }
        if (!SlotMatches(gm, targetSlot, "存档 / save"))
        {
            throw new InvalidOperationException($"[Godhome] refusing to save while slot {snapshotSlot}'s mode data is in memory (current {gm.profileID}, target {targetSlot})");
        }
        SaveGameData fresh = Fresh();
        if (fresh.playerData == null || fresh.sceneData == null)
        {
            throw new InvalidOperationException("[Godhome] snapshot copy has no playerData/sceneData");
        }
        livePd = gm.playerData;
        liveSd = gm.sceneData;
        installedPd = fresh.playerData;
        installedSd = fresh.sceneData;
        gm.playerData = fresh.playerData;
        gm.sceneData = fresh.sceneData;
        swapDepth = 1;
        return true;
    }

    /// <summary>
    /// 存档函数结束后（含异常）：换回内存里的真实数据。
    /// 如果存档途中 GameManager 上的对象被换掉了（不是我们换进去的副本），就不换回旧对象，作废快照。
    /// </summary>
    internal static void SwapOut(GameManager gm)
    {
        if (swapDepth == 0)
        {
            return;
        }
        if (--swapDepth > 0)
        {
            return;
        }
        if (ReferenceEquals(gm.playerData, installedPd) && ReferenceEquals(gm.sceneData, installedSd))
        {
            gm.playerData = livePd!;
            gm.sceneData = liveSd!;
        }
        else
        {
            GodhomePlugin.Log.LogError("[Godhome] 存档途中数据对象被换掉了，不换回旧对象，作废快照 / save data was replaced during a protected save; not reinstalling, dropping the snapshot");
            Clear();
        }
        livePd = null;
        liveSd = null;
        installedPd = null;
        installedSd = null;
    }

    /// <summary>
    /// 把快照拷回内存里的 PlayerData / SceneData，核对一致后丢弃快照，返回拍快照时的运行时复活点（调用方到达后再用一次）。
    /// 核对不一致或核对出错就抛异常、保留快照（Codex 复审）：存档照样只写快照，回标题重新读档即可。
    /// </summary>
    internal static RespawnState? RestoreAndClear(GameManager gm)
    {
        RespawnState? respawn = RestoreKeep(gm, "回大厅 / back to lobby");
        snapshotJson = null;
        snapshotSlot = -1;
        takenPd = null;
        takenSd = null;
        entryRespawn = null;
        GodhomePlugin.Log.LogInfo("[Godhome] 快照已恢复并丢弃 / snapshot restored and released");
        return respawn;
    }

    /// <summary>
    /// 把快照拷回内存里的 PlayerData / SceneData 并核对，但快照留着（Codex 审查 v0.6.0 #5）：
    /// 回程一开始就还原（人还在战场，战场脚本这段时间还可能直接改实时数据），转场途中的存档照样只写快照，
    /// 到了大厅再用 RestoreAndClear 拷一次、丢掉快照。核对不一致或出错就抛异常。返回拍快照时的运行时复活点。
    /// </summary>
    internal static RespawnState? RestoreKeep(GameManager gm, string when)
    {
        RequireOwner(gm, "还原 / restore (" + when + ")");
        SaveGameData fresh = Fresh();
        JsonSerializer ser = Traverse.Create(typeof(SaveDataUtility)).Field("_serializer").GetValue<JsonSerializer>()
            ?? throw new InvalidOperationException("[Godhome] SaveDataUtility._serializer is null");
        int n1 = CopyContract(ser, fresh.playerData, gm.playerData);
        int n2 = CopyContract(ser, fresh.sceneData, gm.sceneData);
        if (!VerifyRestored(gm, when))
        {
            throw new InvalidOperationException("[Godhome] restored data does not match the snapshot; keeping the snapshot");
        }
        // 这几个运行时字段不进存档（NonSerialized），战斗里可能被改：恢复成拍快照时的值，不一律清空（Codex 复审）。
        entryRespawn?.Apply(gm.playerData);
        // 记忆区域的工具弹药覆盖（PlayerData.toolAmountsOverride，NonSerialized）：进记忆区域时游戏抓一份，
        // 离开时（EnteredNewMapZone 里 HasStoredMemoryState 为真的分支）清掉。快照把 HasStoredMemoryState 拷回成 false，
        // 那个分支就不会走，覆盖会一直留着（读弹药读到覆盖值、改弹药只改覆盖）。快照里不在记忆状态，就照游戏的做法清掉（Codex 审查 0.8.0）。
        if (!gm.playerData.HasStoredMemoryState)
        {
            gm.playerData.ClearToolAmountsOverride();
        }
        GodhomePlugin.Log.LogInfo($"[Godhome] 快照已拷回 / snapshot copied back ({n1} PlayerData + {n2} SceneData members, {when})");
        return entryRespawn;
    }

    /// <summary>
    /// 还原后的核对（Codex 审查 #16）：把内存里的数据用游戏的序列化器重新打包，和快照逐字比较。
    /// 逐字段拷贝会跳过只读成员、换掉嵌套对象；有没有漏掉什么，这里能看出来。返回是否一致（出错算不一致）。
    /// </summary>
    private static bool VerifyRestored(GameManager gm, string when)
    {
        try
        {
            string now = SaveDataUtility.SerializeSaveData(new SaveGameData(gm.playerData, gm.sceneData));
            if (now == snapshotJson)
            {
                GodhomePlugin.Log.LogInfo($"[Godhome] 还原核对 / restore check ({when}): 与快照一致 / identical to the snapshot ({now.Length} chars)");
                return true;
            }
            string snap = snapshotJson ?? "";
            int i = 0;
            while (i < now.Length && i < snap.Length && now[i] == snap[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 60);
            string Around(string s) => s.Substring(Math.Min(from, s.Length), Math.Min(160, Math.Max(0, s.Length - from))).Replace('\n', ' ').Replace('\r', ' ');
            var log = GodhomePlugin.Log;
            log.LogWarning($"[Godhome] 还原核对 / restore check ({when}): 与快照不一致 / differs from the snapshot at char {i} (snapshot {snap.Length}, now {now.Length} chars)");
            log.LogWarning($"[Godhome]   快照 / snapshot: …{Around(snap)}…");
            log.LogWarning($"[Godhome]   现在 / now:      …{Around(now)}…");
            return false;
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 还原核对出错 / restore check failed ({when}): {e.Message}");
            return false;
        }
    }

    /// <summary>不恢复，直接丢弃（回到标题画面、或发现已经换了存档时用：此时存档文件里已经是快照）。</summary>
    internal static void Clear()
    {
        if (snapshotJson != null)
        {
            GodhomePlugin.Log.LogInfo("[Godhome] 快照已丢弃 / snapshot discarded");
        }
        snapshotJson = null;
        snapshotSlot = -1;
        takenPd = null;
        takenSd = null;
        entryRespawn = null;
        SuppressLevelState = false;
    }

    /// <summary>
    /// 给存档补丁用：试着换入快照。换入时抛异常（快照读不回来、存档槽对不上、数据对象只换了一个）就记错误日志、返回 false，
    /// 调用方据此跳过这次存档，绝不让实时数据写盘。
    /// </summary>
    internal static bool TrySwapIn(GameManager gm, int targetSlot, string caller, out bool swapped)
    {
        try
        {
            swapped = SwapIn(gm, targetSlot);
            return true;
        }
        catch (Exception e)
        {
            swapped = false;
            GodhomePlugin.Log.LogError($"[Godhome] 模式期间存档被拦下 / save blocked during the mode ({caller}, slot {targetSlot}): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 游戏的存档队列里还有没写完的存档吗（SaveDataUtility 在后台线程序列化捕获的 SaveGameData，
    /// 那个对象引用的是存档开始时 GameManager 上的数据）。开战前改数据之前要等它空（Codex 复审）。
    /// 读不到这两个私有字段时当作空闲，只记一次警告。
    /// </summary>
    internal static bool SaveQueueBusy()
    {
        try
        {
            object? running = AccessTools.Field(typeof(SaveDataUtility), "_runningSaveTask")?.GetValue(null);
            if (running is Task t && !t.IsCompleted)
            {
                return true;
            }
            return AccessTools.Field(typeof(SaveDataUtility), "_saveTaskQueue")?.GetValue(null) is ICollection q && q.Count > 0;
        }
        catch (Exception e)
        {
            if (!warnedQueue)
            {
                warnedQueue = true;
                GodhomePlugin.Log.LogWarning($"[Godhome] 读不到存档队列状态，当作空闲 / cannot read the save queue, assuming idle: {e.Message}");
            }
            return false;
        }
    }

    private static bool warnedQueue;

    private static int CopyContract(JsonSerializer ser, object src, object dst)
    {
        if (ser.ContractResolver.ResolveContract(src.GetType()) is not JsonObjectContract contract)
        {
            throw new InvalidOperationException($"[Godhome] no object contract for {src.GetType()}");
        }
        int n = 0;
        foreach (JsonProperty p in contract.Properties)
        {
            if (p.Ignored || !p.Readable || !p.Writable || p.ValueProvider == null)
            {
                continue;
            }
            p.ValueProvider.SetValue(dst, p.ValueProvider.GetValue(src));
            n++;
        }
        return n;
    }
}

/// <summary>
/// PlayerData 里不进存档（NonSerialized）的复活点字段：临时复活点、非致命死亡复活点。
/// 拍快照时记下，回大厅时恢复成原来的值（一律清空的话，原来的值会丢）。
/// </summary>
internal sealed class RespawnState
{
    private string? tempScene;
    private string? tempMarker;
    private int tempType;
    private string? nonLethalScene;
    private string? nonLethalMarker;
    private int nonLethalType;

    internal static RespawnState Capture(PlayerData pd) => new()
    {
        tempScene = pd.tempRespawnScene,
        tempMarker = pd.tempRespawnMarker,
        tempType = pd.tempRespawnType,
        nonLethalScene = pd.nonLethalRespawnScene,
        nonLethalMarker = pd.nonLethalRespawnMarker,
        nonLethalType = pd.nonLethalRespawnType,
    };

    internal void Apply(PlayerData pd)
    {
        pd.tempRespawnScene = tempScene;
        pd.tempRespawnMarker = tempMarker;
        pd.tempRespawnType = tempType;
        pd.nonLethalRespawnScene = nonLethalScene;
        pd.nonLethalRespawnMarker = nonLethalMarker;
        pd.nonLethalRespawnType = nonLethalType;
    }

    public override string ToString() => $"temp={tempScene ?? "-"}@{tempMarker ?? "-"}/{tempType}, nonLethal={nonLethalScene ?? "-"}@{nonLethalMarker ?? "-"}/{nonLethalType}";
}
