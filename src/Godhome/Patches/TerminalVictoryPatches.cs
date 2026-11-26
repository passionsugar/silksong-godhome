using System;
using System.Linq;
using HarmonyLib;

namespace Godhome;

/// <summary>
/// 截断原版结局时也可能略过原来的伤害清理。终点已经判胜、尚在等回程的短窗口内，
/// 不让残留攻击再伤害玩家；不写入任何无敌存档标记，离开 Won 状态立即失效。
/// </summary>
internal static class TerminalVictoryProtection
{
    internal static bool ShouldProtect(HeroController hero)
    {
        ModeController? mode = ModeController.Instance;
        GameManager gm = GameManager.UnsafeInstance;
        if (mode == null || mode.State != LoomState.Won || mode.CurrentBoss == null
            || !mode.CurrentBoss.Arena.End.Any(e => e.Terminal)
            || gm == null || !GodhomeSave.IsGodhome(gm) || !SaveGuard.Active || SaveGuard.BlockAll
            || gm.profileID != SaveGuard.Slot || SaveGuard.Stale(gm) || !PlayerData.HasInstance
            || !ReferenceEquals(gm.playerData, PlayerData.instance)
            || hero == null || !ReferenceEquals(hero, HeroController.SilentInstance))
        {
            return false;
        }
        // 不逆转已经发生的死亡、危险复活或最后一击前的伤害。
        return gm.playerData.health > 0 && !hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning;
    }
}

[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class TerminalVictoryDamagePatch
{
    private static bool Prefix(HeroController __instance) => !TerminalVictoryProtection.ShouldProtect(__instance);
}
