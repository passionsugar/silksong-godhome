using System;

namespace Godhome;

/// <summary>
/// [Godhome] 万神殿连战里身上的配装：纹章和装着的工具（PlayerData 的 CurrentCrestID / PreviousCrestID / IsCurrentCrestTemp、
/// ToolEquips、ExtraToolEquips）。
///
/// 整串连战用开殿前的一份快照，每场之间把快照拷回实时数据（上一场改过的剧情标记、场景状态都撤掉）。以前配装也跟着拷回去：
/// 休息点长椅上换的纹章、工具只管紧接着的那一场，再下一场又变回开殿时的（Codex 审计 0.10.3 F03）。
/// 现在换场前先记下身上的配装，拷回快照以后放回去；和快照里的不一样时刷新工具和纹章招式。
///
/// 只带这几个字段，不拿整份实时数据去盖快照：快照里照样只有开殿前的状态，战斗期间存档也只写它。
/// 连战结束回大厅时配装回到开殿前（和战斗里别的改动一样）。工具的剩余次数不带：每场回到开殿时的数量（和以前一样）。
/// </summary>
internal sealed class RunGear
{
    private string? crest;
    private string? prevCrest;
    private bool crestTemp;
    private ToolCrestsData? equips;
    private FloatingCrestSlotsData? extra;

    /// <summary>记下身上的配装（拿的是对象本身：拷回快照时游戏数据里换成新对象，这几个旧对象不会被改）。</summary>
    internal static RunGear Take(PlayerData pd) => new()
    {
        crest = pd.CurrentCrestID,
        prevCrest = pd.PreviousCrestID,
        crestTemp = pd.IsCurrentCrestTemp,
        equips = pd.ToolEquips,
        extra = pd.ExtraToolEquips,
    };

    /// <summary>和 pd 里现在的配装（刚拷回的快照）比：纹章或者任何一个槽位不一样就是 true。比不出来（序列化出错）也当作不一样。</summary>
    internal bool Differs(PlayerData pd)
        => crest != pd.CurrentCrestID || prevCrest != pd.PreviousCrestID
            || !SaveGuard.SameJson(equips, pd.ToolEquips) || !SaveGuard.SameJson(extra, pd.ExtraToolEquips);

    /// <summary>
    /// 拷回快照以后放回记下的配装。返回和快照里的是否不一样（不一样时调用方要 Refresh）。
    /// 一样也放回原来的对象：游戏里别处可能还拿着它们。
    /// </summary>
    internal bool PutBack(PlayerData pd, string why)
    {
        bool changed = Differs(pd);
        pd.CurrentCrestID = crest;
        pd.PreviousCrestID = prevCrest;
        pd.IsCurrentCrestTemp = crestTemp;
        if (equips != null)
        {
            pd.ToolEquips = equips;
        }
        if (extra != null)
        {
            pd.ExtraToolEquips = extra;
        }
        if (changed)
        {
            GodhomePlugin.Log.LogInfo($"[Godhome] 连战换过配装，带到下一场 / the run's loadout differs from the snapshot, carrying it on（纹章 / crest {crest}，{why}）");
        }
        return changed;
    }

    /// <summary>
    /// 配装变了：工具缓存、工具栏、纹章招式都按现在的数据来。游戏在物品栏里换纹章、工具后也是发这个事件
    /// （ToolItemManager.SendEquippedChangedEvent；角色收到 TOOL EQUIPS CHANGED 会 ResetAllCrestState 换招式配置）。
    /// </summary>
    internal static void Refresh(string why)
    {
        try
        {
            ToolItemManager.SendEquippedChangedEvent(force: true);
            GodhomePlugin.Log.LogInfo($"[Godhome] 刷新配装 / loadout refreshed（{why}）");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 刷新配装出错 / refreshing the loadout failed（{why}）: {e.Message}");
        }
    }
}
