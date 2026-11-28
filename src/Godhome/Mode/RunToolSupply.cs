using System.Collections.Generic;

namespace Godhome;

internal sealed class RunToolSupply
{
    private readonly Dictionary<string, int> amounts = new();
    private readonly ToolItemLiquidsData liquids;

    internal RunToolSupply(PlayerData pd)
    {
        liquids = pd.ToolLiquids;
        foreach (var entry in pd.Tools.Enumerate())
        {
            if (entry.Value.IsUnlocked)
            {
                // In a memory fight the current amount can differ from the serialized amount.
                amounts[entry.Key] = pd.GetToolData(entry.Key).AmountLeft;
            }
        }
    }

    internal void Restore(PlayerData pd)
    {
        // RestoreKeep replaced the live inventory. Keep its unlocks and quest flags.
        foreach (var entry in amounts)
        {
            var data = pd.GetToolData(entry.Key);
            if (data.IsUnlocked && data.AmountLeft != entry.Value)
            {
                data.AmountLeft = entry.Value;
                pd.SetToolData(entry.Key, data);
            }
        }
        pd.ToolLiquids = liquids;
        ToolItemManager.ReportAllBoundAttackToolsUpdated();
    }

    internal static void RefillAtRest(PlayerData pd)
    {
        foreach (ToolItem tool in ToolItemManager.GetAllTools())
        {
            if (tool == null || !tool.IsUnlocked || !tool.IsAutoReplenished())
            {
                continue;
            }
            var data = pd.GetToolData(tool.name);
            int capacity = ToolItemManager.GetToolStorageAmount(tool);
            if (data.AmountLeft < capacity)
            {
                data.AmountLeft = capacity;
                pd.SetToolData(tool.name, data);
            }
        }
        ToolItemManager.ReportAllBoundAttackToolsUpdated();
    }
}
