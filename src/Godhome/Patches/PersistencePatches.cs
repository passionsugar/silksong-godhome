using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Godhome;

// [Godhome] 战斗场景里，Boss 本体和“装着 Boss 的那场战斗”不读存档里的持久记录。
//
// 存档里打赢过的战斗，BattleScene 挂的 PersistentBoolItem 记着 completed=true。
// 场景一读到就调 BattleCompleted()，把每一波敌人的物体都关掉（BattleScene.cs:587-608）；
// 之后再开战，StartWave 只开关波里的 BattleSceneEnemy 组件，不会把关掉的物体再打开，Boss 就一直不出现。
// 例：苔藓之母的 Wave 1 被关掉。Boss 本体自己的“已击杀”记录同理（ID 不一定等于物体名，按名字删不全）。
//
// 做法：PersistentBoolItem.TryGetValue 在战斗期间对这两类物体返回 false，
// 游戏就按“没有记录”处理，用场景里的默认值（PersistentItem.Start → UpdateValue）。
// 只影响内存里这一次加载；回大厅时快照还原，战斗中写进存档的也一直是快照。
// 范围：只认这场 Boss 的场景（bosses.json 的 scene / hostScene / entry.scene）里的物体，
// 时间是整场战斗而不只是加载那一刻：波次里的物体可能战斗中才激活、才走 Start。

[HarmonyPatch(typeof(PersistentBoolItem), "TryGetValue")]
internal static class PersistentBoolLoadPatch
{
    private static void Postfix(PersistentBoolItem __instance, ref bool __result)
    {
        if (!__result)
        {
            return;
        }
        ModeController? mc = ModeController.Instance;
        BossDef? b = mc?.CurrentBoss;
        if (mc == null || b == null || !mc.FightLoading || b.BossObjects.Length == 0)
        {
            return;
        }
        GameObject go = __instance.gameObject;
        if (!ModeController.ScenesOf(b).Contains(go.scene.name))
        {
            return;
        }
        bool isBoss = IsBossName(go.name);
        bool isBossBattle = !isBoss && go.GetComponent<BattleScene>() != null
            && go.GetComponentsInChildren<Transform>(true).Any(t => IsBossName(t.name));
        if (isBoss || isBossBattle)
        {
            __result = false;
            GodhomePlugin.Log.LogInfo($"[Godhome]   忽略持久记录 / ignored saved state: {SceneTools.PathOf(go.transform)} ({(isBoss ? "boss" : "battle")})");
        }

        bool IsBossName(string name) => b.BossObjects.Contains(name.Replace("(Clone)", "").Trim());
    }
}
