using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Godhome;

/// <summary>
/// 调试：Boss 的演出有没有把主角改坏（0.10.5：打完崇高圣母灵丝后，原版结局演出把刚体设成运动学，回大厅后浮在空中）。
/// State/Problems 只读；Fall 把人往上抬一点，看真实物理下能不能落回地面（不调用模组或游戏的恢复方法）。
/// </summary>
internal static class HeroCheck
{
    private const int TerrainMask = 1 << 8;

    internal static string State(HeroController hero)
    {
        Rigidbody2D rb = hero.rb2d;
        PlayerData pd = PlayerData.instance;
        MeshRenderer? mr = hero.GetComponent<MeshRenderer>();
        return $"刚体 / body {rb.bodyType}{(rb.simulated ? "" : "（不模拟 / not simulated）")}，"
            + $"重力 / gravity {rb.gravityScale:0.##}（{(hero.IsGravityApplied ? "开 / on" : "关 / off")}），"
            + $"显示 / shown {mr != null && mr.enabled}，操作 / control {!hero.controlReqlinquished && hero.acceptingInput}，"
            + $"无限空跳 / air jump {pd.infiniteAirJump}，无敌 / invincible {pd.isInvincible}，"
            + $"物品栏 / inventory {(pd.disableInventory ? "关 / off" : "开 / on")}，暂停 / pause {(pd.disablePause ? "关 / off" : "开 / on")}";
    }

    /// <param name="full">
    /// 大厅里站稳以后：重力、操作、暂停也要正常。进场时 Boss 的开场可能合法地收着这些，只看不该从上一场带过来的。
    /// </param>
    internal static List<string> Problems(HeroController hero, bool full)
    {
        var bad = new List<string>();
        Rigidbody2D rb = hero.rb2d;
        PlayerData pd = PlayerData.instance;
        if (rb.bodyType != RigidbodyType2D.Dynamic)
        {
            bad.Add($"刚体 / body {rb.bodyType}");
        }
        if (!rb.simulated)
        {
            bad.Add("刚体不模拟 / not simulated");
        }
        MeshRenderer? mr = hero.GetComponent<MeshRenderer>();
        if (mr == null || !mr.enabled)
        {
            bad.Add("主角不显示 / hero hidden");
        }
        if (pd.infiniteAirJump)
        {
            bad.Add("无限空跳 / infinite air jump");
        }
        if (pd.isInvincible)
        {
            bad.Add("无敌 / invincible");
        }
        if (pd.disableInventory)
        {
            bad.Add("物品栏关着 / inventory disabled");
        }
        if (full)
        {
            if (!hero.IsGravityApplied || !(rb.gravityScale > 0f))
            {
                bad.Add($"重力 / gravity {rb.gravityScale:0.##}{(hero.IsGravityApplied ? "" : " off")}");
            }
            if (hero.controlReqlinquished || !hero.acceptingInput)
            {
                bad.Add("没有操作 / no control");
            }
            if (pd.disablePause)
            {
                bad.Add("不能暂停 / pause disabled");
            }
        }
        return bad;
    }

    internal sealed class Result
    {
        public bool Ok;
        public string Detail = "";
    }

    /// <summary>
    /// 等人能动、站在地上（最多 8 秒），记下状态；然后抬高（头顶有地形就少抬），2.5 秒内要落回原来的高度、着地。
    /// 浮空（运动学刚体、没有重力）时人停在抬高的位置，判 FAIL。
    /// </summary>
    internal static IEnumerator Fall(Result r)
    {
        float until = Time.unscaledTime + 8f;
        HeroController? hero = HeroController.SilentInstance;
        while (Time.unscaledTime < until
            && (hero == null || hero.controlReqlinquished || !hero.acceptingInput || hero.cState.transitioning || !hero.cState.onGround))
        {
            yield return null;
            hero = HeroController.SilentInstance;
        }
        if (hero == null)
        {
            r.Ok = false;
            r.Detail = "落地检查 / fall check: 没有主角 / no hero";
            yield break;
        }
        string before = State(hero);
        List<string> bad = Problems(hero, full: true);
        bool settled = hero.cState.onGround && !hero.controlReqlinquished && hero.acceptingInput;
        Vector3 start = hero.transform.position;
        float lift = 4f;
        RaycastHit2D up = Physics2D.Raycast((Vector2)start + Vector2.up * 1.6f, Vector2.up, 6f, TerrainMask);
        if (up.collider != null)
        {
            lift = Mathf.Clamp(up.distance - 0.5f, 1f, 4f);
        }
        hero.transform.position = start + Vector3.up * lift;
        if (hero.rb2d.bodyType == RigidbodyType2D.Dynamic)
        {
            hero.rb2d.linearVelocity = Vector2.zero;
        }
        Physics2D.SyncTransforms();
        float end = Time.unscaledTime + 2.5f;
        bool landed = false;
        yield return null;
        while (Time.unscaledTime < end && hero != null)
        {
            yield return null;
            if (hero.transform.position.y <= start.y + 0.6f && hero.cState.onGround)
            {
                landed = true;
                break;
            }
        }
        float dy = hero != null ? hero.transform.position.y - start.y : float.NaN;
        r.Ok = landed && settled && bad.Count == 0;
        r.Detail = $"落地检查 / fall check: {(landed ? "落回地面 / landed" : "没落下来 / did not come down")}"
            + $"（抬高 / lifted {lift:0.0}，之后 / then Δy {dy:+0.00;-0.00}）"
            + (settled ? "" : "，开始前没站稳或不能操作 / not settled before the lift")
            + (bad.Count > 0 ? $"，异常 / bad: {string.Join("、", bad)}" : "")
            + $"；{before}";
    }
}
