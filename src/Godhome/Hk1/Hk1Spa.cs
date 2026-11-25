using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Godhome;

/// <summary>
/// [Godhome] 用前作神居时，万神殿的休息点照前作是温泉 GG_Spa（重新打包的前作场景，从前作 Boss 场景都有的 door_dreamEnter 进来）。
/// 到了回满血和丝（ModeController 照旧做），从出口走出去就进下一场（FilterTransition 改道）。
///
/// 温泉里有前作的场景控制器（BossSceneController，丝之歌里还有这个类）：
/// - 照前作 BossSequenceController 进下一场前设好 SetupEvent，不然 Awake 去读存档里的前作连战（currentBossSequence）、报错；
/// - 不让它自己放转场（转场由 Hk1Battle 放，带看门狗），不让它销毁时“还原束缚”（RestoreBindings 会回满血、清丝，我们的束缚自己管）；
/// - 它在 Setup 里挂到 HeroController.OnTakenDamage 上记“受过伤”（前作的无伤判定），这里马上摘掉（无伤由我们自己记，见 HookDamage）。
/// 前作的复活触发器、调试画线、FSM 里丝之歌接不上的地方照前作神居的房间处理（Hk1Lobby.Sanitize）。
/// </summary>
internal static class Hk1Spa
{
    private static readonly BossSceneController.SetupEventDelegate OnSetup = Setup;
    private static readonly AccessTools.FieldRef<BossSceneController, bool> RestoreField = AccessTools.FieldRefAccess<BossSceneController, bool>("restoreBindingsOnDestroy");
    private static readonly AccessTools.FieldRef<BossSceneController, bool> DoTransitionField = AccessTools.FieldRefAccess<BossSceneController, bool>("doTransition");
    private static readonly AccessTools.FieldRef<BossSceneController, bool> SubscribedField = AccessTools.FieldRefAccess<BossSceneController, bool>("knightDamagedSubscribed");

    /// <summary>温泉能用：在用前作神居、温泉打包好了。</summary>
    internal static bool Usable
    {
        get
        {
            if (!Hk1Lobby.Active || !Hk1Assets.Has(Hk1Lobby.Spa))
            {
                return false;
            }
            try
            {
                return Hk1Assets.Locate(Hk1Lobby.Spa) != null;
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 温泉用不了，休息点用本作的长椅房间 / GG_Spa unavailable, using the Silksong bench room: {e.GetBaseException().Message}");
                return false;
            }
        }
    }

    /// <summary>去休息点之前（马上要切场景）：去温泉就设好前作场景控制器的 SetupEvent，不是就清掉我们设的。</summary>
    internal static void BeforeLoad(string scene)
    {
        if (scene == Hk1Lobby.Spa)
        {
            BossSceneController.SetupEvent = OnSetup;
        }
        else
        {
            ClearSetup();
        }
    }

    /// <summary>到了（或者放弃了）：没被用掉的 SetupEvent 清掉，不留给别的场景。</summary>
    internal static void Arrived() => ClearSetup();

    internal static void ClearSetup()
    {
        if (BossSceneController.SetupEvent == OnSetup)
        {
            BossSceneController.SetupEvent = null;
        }
    }

    /// <summary>前作场景控制器 Awake 里调（前作 BossSequenceController.SetupBossScene 的那份委托，这里只做休息点要的）。</summary>
    private static void Setup(BossSceneController self)
    {
        if (self.gameObject.scene.name != Hk1Lobby.Spa)
        {
            // 不该发生（去温泉的路上别的场景先醒了）：这个控制器照它自己的来，不改。
            GodhomePlugin.Log.LogWarning($"[Godhome] 温泉的场景控制器设置被 {self.gameObject.scene.name} 用掉了 / the GG_Spa setup was consumed by {self.gameObject.scene.name}");
            return;
        }
        self.DreamReturnEvent = "DREAM EXIT";
        RestoreField(self) = false;
        DoTransitionField(self) = false;
    }

    /// <summary>温泉刚加载完（Awake 已跑、Start 没跑，人还没进来）：照前作神居的房间处理，盖上前作的转场特效。</summary>
    internal static void Prepare(Scene scene)
    {
        ClearSetup();
        GameObject[] roots = scene.GetRootGameObjects();
        (int triggers, int debug, int fsms) = Hk1Lobby.Sanitize(roots);
        Hk1Lobby.LowerBlurPlanes(roots);
        // 入口门 door_dreamEnter 是门型转场点：照前作不能交互（不然站在入口有“交谈”，按上从入口“进门”）。
        Hk1Lobby.FixDoors(scene.name, roots);
        var sb = new StringBuilder();
        // 前作的长椅（Bench Control）坐下时如果 Set Respawn 开着，会把复活点名写成 RestBench (1)、复活类型写成长椅：
        // 场景名有 Hk1SetStringPatch 拦着，复活点名却会照写，存档里的圣所长椅就对不上了。休息点的长椅只用来坐、回血，不当复活点。
        foreach (PlayMakerFSM bench in roots.SelectMany(r => r.GetComponentsInChildren<PlayMakerFSM>(true)).Where(p => p.FsmName == "Bench Control"))
        {
            var setRespawn = bench.FsmVariables.FindFsmBool("Set Respawn");
            sb.Append($" 长椅 / bench {FsmDump.PathOf(bench.transform)}（Set Respawn {(setRespawn != null ? setRespawn.Value.ToString() : "-")}{(setRespawn != null && setRespawn.Value ? " → False" : "")}）；");
            if (setRespawn != null)
            {
                setRespawn.Value = false;
            }
        }
        foreach (BossSceneController c in roots.SelectMany(r => r.GetComponentsInChildren<BossSceneController>(true)))
        {
            // SetupEvent 没用上（控制器关着、Awake 还没跑）也不能让它销毁时还原束缚、自己放转场。
            RestoreField(c) = false;
            DoTransitionField(c) = false;
            if (SubscribedField(c) && HeroController.instance != null)
            {
                try
                {
                    HeroController.instance.OnTakenDamage -= (Action)Delegate.CreateDelegate(typeof(Action), c, AccessTools.Method(typeof(BossSceneController), "SetKnightDamaged"));
                    SubscribedField(c) = false;
                }
                catch (Exception e)
                {
                    GodhomePlugin.Log.LogWarning($"[Godhome] 温泉：摘不掉场景控制器的受伤记录 / GG_Spa: could not unhook SetKnightDamaged: {e.Message}");
                }
            }
            sb.Append($" 控制器 / controller {FsmDump.PathOf(c.transform)}（transitionPrefab {(c.transitionPrefab != null ? c.transitionPrefab.name : "-")}，"
                + $"in {c.doTransitionIn}/{c.transitionInHoldTime:0.##}s，out {c.doTransitionOut}，exit {(c.customExitPoint != null ? c.customExitPoint.name + "→" + c.customExitPoint.targetScene : "-")}，"
                + $"bosses {(c.bosses != null ? c.bosses.Length : 0)}）；");
        }
        string gates = string.Join("，", roots.SelectMany(r => r.GetComponentsInChildren<TransitionPoint>(true))
            .Select(tp => $"{tp.name}→{(string.IsNullOrEmpty(tp.targetScene) ? "-" : tp.targetScene)}{(tp.isADoor ? "(door)" : "")}"));
        string benches = string.Join("，", roots.SelectMany(r => r.GetComponentsInChildren<PlayMakerFSM>(true))
            .Where(p => p.FsmName.IndexOf("Bench", StringComparison.OrdinalIgnoreCase) >= 0 || p.FsmName.IndexOf("Spring", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(p => $"{FsmDump.PathOf(p.transform)}|{p.FsmName}"));
        GodhomePlugin.Log.LogInfo($"[Godhome] 温泉休息点准备好 / GG_Spa rest stop prepared（复活触发器 / respawn triggers {triggers}，调试画线 / debug draw {debug}，改过的 FSM / fixed fsms {fsms}）"
            + $"{sb} 门 / gates: {gates}；长椅、温泉 / benches, springs: {(benches.Length == 0 ? "-" : benches)}");
        if (GodhomeConfig.DebugMode.Value)
        {
            DumpFsms(roots);
        }
        Hk1Battle.Cover(scene);
    }

    /// <summary>调试开着：温泉里所有 FSM 写到 BepInEx\Godhome\hk1-spa-fsm.txt（核对长椅、温泉、出口用）。</summary>
    private static void DumpFsms(GameObject[] roots)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (PlayMakerFSM pm in roots.SelectMany(r => r.GetComponentsInChildren<PlayMakerFSM>(true)))
            {
                FsmDump.Write(pm, sb);
                sb.AppendLine();
            }
            string dir = Path.Combine(Paths.BepInExRootPath, "Godhome");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "hk1-spa-fsm.txt"), sb.ToString());
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 温泉 FSM 导出失败 / dumping the GG_Spa fsms failed: {e.Message}");
        }
    }
}
