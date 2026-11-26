using System;
using UnityEngine;

namespace Godhome;

/// <summary>
/// Boss 的结局可以改常驻 HeroController；SaveGuard 只还原存档，不能还原这些运行时状态。
/// 只在离开这一场、以及大厅里首次站稳时收尾，不在战斗 Update 中维持重力。
/// </summary>
internal sealed class HeroRecovery
{
    private PlayerData? ownerData;
    private object? ownerSceneData;
    private HeroController? ownerHero;
    private int ownerSlot;
    private int ownerSerial;
    private bool fightPending;
    private bool lobbyPending;
    private string lobbyScene = "";

    /// <summary>
    /// 神居没有需要无限空跳的剧情。接管旧档、开战拍快照之前清掉这个标记，
    /// 否则旧档已经带着 true 时，RestoreKeep 会把 true 当作正确的大厅基线反复拷回。
    /// 只改神居的实时数据；快照存在时必须先由 SaveGuard 核对并还原。
    /// </summary>
    internal static bool NormalizeLoadedData(GameManager gm)
    {
        if (!GodhomeSave.IsGodhome(gm) || SaveGuard.Active || SaveGuard.BlockAll || !gm.playerData.infiniteAirJump)
        {
            return false;
        }
        gm.playerData.infiniteAirJump = false;
        GodhomePlugin.Log.LogWarning("[Godhome] 清除神居存档的无限空跳剧情标记 / cleared the godhome save's infinite-air-jump cutscene flag");
        return true;
    }

    internal void BeginFight(GameManager gm, int serial)
    {
        Clear();
        if (SaveGuard.Active && !SaveGuard.Stale(gm) && Capture(gm, serial))
        {
            fightPending = true;
        }
    }

    /// <summary>
    /// RestoreKeep 成功以后、旧场景还没有开始卸载的时候调用。此时战斗已结束/放弃，
    /// 新场景的开场尚未接管角色。死亡、暂停、已经开始的转场由游戏自己处理。
    /// </summary>
    internal void LeaveFight(GameManager gm, int serial, string why)
    {
        if (!fightPending || !Matches(gm, serial) || !SafeLiveHero(gm) || UiOwnsHero())
        {
            return;
        }
        HeroController hero = ownerHero!;
        // 这一场只做一次；异常也不在每帧重试。到达大厅后还有受限的静止检查。
        fightPending = false;
        try
        {
            string before = GravityState(hero);
            hero.ResetMotion(resetNailCharge: true);
            hero.ResetInputQueues();
            hero.ResetStartWithJumps();
            hero.ResetShuttlecock();
            hero.ResetUpdraft();
            hero.cState.floating = false;
            hero.cState.isInCutsceneMovement = false;
            hero.cState.isTriggerEventsPaused = false;
            hero.SetBlockFsmMove(false);
            // 原版结局演出用 SetIsKinematic2d 把刚体设成运动学、SetMeshRenderer 藏起主角；游戏只在死亡、危险复活时改回来，
            // 普通转场不管（反编译 HeroController.Respawn / HazardRespawn）。不改回来，人到了大厅照样浮着。
            string body = RestoreBody(hero);
            bool gravityRestored = RestoreGravity(hero);
            // 不接受旧场景里还按着的跳跃；新场景的进入流程负责重新接受输入。
            // 不清 inputBlockers，不替别的组件删除它们持有的输入锁。
            hero.RegainControl(allowInput: false);
            hero.StartAnimationControl();
            string message = $"[Godhome] 离场调用角色运动恢复 / requested native hero motion recovery before leaving ({why}): {before} → {GravityState(hero)}"
                + (body.Length > 0 ? $"；改回 / restored {body}" : "");
            if (gravityRestored)
            {
                GodhomePlugin.Log.LogInfo(message);
            }
            else
            {
                GodhomePlugin.Log.LogWarning(message + "（原生调用后重力仍异常，请保留日志 / gravity is still abnormal after the native calls; keep this log）");
            }
        }
        catch (Exception e)
        {
            // 不能让一个运动状态恢复失败阻止快照还原、回大厅。
            GodhomePlugin.Log.LogWarning($"[Godhome] 离场恢复角色运动失败 / hero motion recovery failed ({why}): {e.Message}");
        }
    }

    /// <summary>读档接管/回到大厅：只登记一次检查，不抢回程门、长椅或结算界面的控制。</summary>
    internal void ArmLobby(GameManager gm, int serial)
    {
        Clear();
        if (SaveGuard.Active || SaveGuard.BlockAll || !Lobby.Is(gm.GetSceneNameString()) || !Capture(gm, serial))
        {
            return;
        }
        NormalizeLoadedData(gm);
        lobbyScene = gm.GetSceneNameString();
        lobbyPending = true;
        CheckLobby(gm, serial);
    }

    /// <summary>
    /// 只在 ModeController 的 Lobby 状态调用。等游戏已归还控制、人在地面静止且没有动作时，
    /// 检查一次后撤销登记；正常跳跃、滑翔、冲刺及新的 Boss 开场都不会被这里改重力。
    /// </summary>
    internal void CheckLobby(GameManager gm, int serial)
    {
        if (!lobbyPending)
        {
            return;
        }
        if (!Matches(gm, serial) || gm.GetSceneNameString() != lobbyScene || !Lobby.Is(lobbyScene))
        {
            Clear();
            return;
        }
        if (!SafeLiveHero(gm) || UiOwnsHero() || Sanctum.Busy || Hk1Lobby.InteractionBlocked)
        {
            return;
        }
        HeroController hero = ownerHero!;
        HeroControllerStates c = hero.cState;
        // 人已经能操作，刚体却是运动学 / 不模拟：大厅里没有合法的这种状态（长椅、界面上面已经排除），
        // 就是演出留下来的，马上改。不能等下面“站在地上、静止”：浮在空中的人永远等不到（审查 PR #1）。
        if (!hero.controlReqlinquished && hero.acceptingInput && BodyBroken(hero))
        {
            try
            {
                string before = GravityState(hero);
                string body = RestoreBody(hero);
                bool restored = RestoreGravity(hero);
                GodhomePlugin.Log.LogWarning($"[Godhome] 大厅里主角刚体异常，已改回 / hero body was broken in the lobby, restored {body} (gravity ok={restored}): {before} → {GravityState(hero)}");
            }
            catch (Exception e)
            {
                GodhomePlugin.Log.LogWarning($"[Godhome] 大厅刚体恢复失败 / lobby body recovery failed: {e.Message}");
            }
        }
        if (hero.controlReqlinquished || !hero.acceptingInput || !hero.HasAnimationControl || hero.IsInputBlocked()
            || !hero.rb2d.simulated || hero.rb2d.bodyType != RigidbodyType2D.Dynamic
            || !c.onGround || hero.rb2d.linearVelocity.sqrMagnitude > 0.01f || HasMotion(hero))
        {
            return;
        }
        lobbyPending = false;
        // 不把当前 rb/currentGravity 拍成“正常重力”：它们可能正是旧结局遗留的 0。
        // 正常值由游戏的 ResetGravity 决定，不假设 DEFAULT_GRAVITY 和刚体值的单位相同。
        // 只看刚体重力：HeroController.currentGravity 在游戏里声明了却从来不赋值（反编译 HeroController.cs:840，只有声明），
        // 恒为 0，拿它判断每次都会误报“重力异常”（0.10.5 第一轮实机日志）。
        bool badGravity = !hero.IsGravityApplied || !PositiveFinite(hero.rb2d.gravityScale);
        if (!badGravity)
        {
            return;
        }
        try
        {
            string before = GravityState(hero);
            bool restored = RestoreGravity(hero);
            GodhomePlugin.Log.LogWarning($"[Godhome] 大厅静止时调用原生重力恢复 / requested native gravity recovery while idle in the lobby (restored={restored}): {before} → {GravityState(hero)}");
        }
        catch (Exception e)
        {
            GodhomePlugin.Log.LogWarning($"[Godhome] 大厅重力恢复失败 / lobby gravity recovery failed: {e.Message}");
        }
    }

    internal void CancelLobby() => lobbyPending = false;

    internal void Clear()
    {
        ownerData = null;
        ownerSceneData = null;
        ownerHero = null;
        fightPending = false;
        lobbyPending = false;
        lobbyScene = "";
    }

    private bool Capture(GameManager gm, int serial)
    {
        HeroController hero = HeroController.SilentInstance;
        if (!GodhomeSave.IsGodhome(gm) || hero == null)
        {
            return false;
        }
        ownerData = gm.playerData;
        ownerSceneData = gm.sceneData;
        ownerHero = hero;
        ownerSlot = gm.profileID;
        ownerSerial = serial;
        return true;
    }

    private bool Matches(GameManager gm, int serial) => GodhomeSave.IsGodhome(gm)
        && !SaveGuard.BlockAll && !SaveGuard.Stale(gm) && gm.profileID == ownerSlot && serial == ownerSerial
        && ReferenceEquals(ownerData, gm.playerData) && ReferenceEquals(ownerSceneData, gm.sceneData)
        && ownerHero != null && ReferenceEquals(ownerHero, HeroController.SilentInstance);

    private bool SafeLiveHero(GameManager gm)
    {
        HeroController hero = ownerHero!;
        HeroControllerStates c = hero.cState;
        return gm.GameState == GlobalEnums.GameState.PLAYING && gm.IsGameplayScene()
            && !gm.IsGamePaused() && !gm.IsInSceneTransition && !gm.RespawningHero
            && hero.isActiveAndEnabled && hero.rb2d != null && gm.playerData.health > 0 && !gm.playerData.atBench
            && !c.dead && !c.hazardDeath && !c.hazardRespawning && !c.transitioning && !c.isPaused;
    }

    private static bool UiOwnsHero() => LoomUI.Instance != null
        && (LoomUI.Instance.ChallengeBusy || LoomUI.Instance.ResultBusy);

    private static bool HasMotion(HeroController hero)
    {
        HeroControllerStates c = hero.cState;
        return c.jumping || c.wallJumping || c.doubleJumping || c.dashing || c.airDashing || c.superDashing || c.backDashing
            || c.floating || c.shuttleCock || hero.isUmbrellaActive.Value || c.inUpdraft || c.swimming
            || c.attacking || c.casting || c.isToolThrowing || c.isBinding || c.focusing
            || c.mantling || c.mantleRecovery || c.downSpiking || c.spellQuake;
    }

    private static bool PositiveFinite(float value) => value > 0f && !float.IsInfinity(value) && !float.IsNaN(value);

    private static bool RestoreGravity(HeroController hero)
    {
        // 先重新允许重力（AffectedByGravity(true) 在刚体重力为 0 时拷回 prevGravityScale），
        // 再 ResetGravity：rb2d.gravityScale = DEFAULT_GRAVITY（反编译 HeroController.cs:8290、8314）。
        hero.AffectedByGravity(true);
        hero.ResetGravity();
        return hero.IsGravityApplied && PositiveFinite(hero.rb2d.gravityScale);
    }

    /// <summary>只看刚体（大厅里有能操作时合法藏起主角的场合也不碰显示；显示在离场时恢复）。</summary>
    private static bool BodyBroken(HeroController hero) =>
        hero.rb2d.bodyType != RigidbodyType2D.Dynamic || !hero.rb2d.simulated;

    /// <summary>刚体改回 Dynamic、恢复模拟、显示主角；返回改了什么（没改是空串）。</summary>
    private static string RestoreBody(HeroController hero)
    {
        var done = new System.Collections.Generic.List<string>();
        Rigidbody2D rb = hero.rb2d;
        if (rb.bodyType != RigidbodyType2D.Dynamic)
        {
            done.Add($"body {rb.bodyType}→Dynamic");
            rb.bodyType = RigidbodyType2D.Dynamic;
        }
        if (!rb.simulated)
        {
            done.Add("simulated");
            rb.simulated = true;
        }
        MeshRenderer? mr = hero.GetComponent<MeshRenderer>();
        if (mr != null && !mr.enabled)
        {
            done.Add("shown");
            mr.enabled = true;
        }
        return string.Join(", ", done);
    }

    private static string GravityState(HeroController hero) =>
        $"rb={hero.rb2d.bodyType}, applied={hero.IsGravityApplied}, previous={hero.prevGravityScale}, body={hero.rb2d.gravityScale}, default={hero.DEFAULT_GRAVITY}";
}
