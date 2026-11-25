using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace Godhome;

/// <summary>
/// [Godhome] 调试：替玩家按键（宣传片用，Debug.Promo）。在 InControl 每帧更新完一个动作之后，
/// 把我们要按的值再提交一次（OneAxisInputControl.CommitWithValue），游戏读到的就和真的按了一样（WasPressed、IsPressed 都对）。
/// Active 为 false 时什么都不做；只有宣传片导演会打开它。
/// </summary>
internal static class Puppet
{
    internal static bool Active { get; set; }

    private static readonly Dictionary<PlayerAction, float> held = new();
    private static readonly Dictionary<PlayerAction, int> tapUntil = new();

    internal static HeroActions? Actions => InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;

    internal static void Hold(PlayerAction? a, bool on)
    {
        if (a == null)
        {
            return;
        }
        if (on)
        {
            held[a] = 1f;
        }
        else
        {
            held.Remove(a);
        }
    }

    internal static bool IsHeld(PlayerAction? a) => a != null && held.ContainsKey(a);

    /// <summary>按一下：按住 frames 帧再松开。</summary>
    internal static void Tap(PlayerAction? a, int frames = 3)
    {
        if (a != null)
        {
            tapUntil[a] = Time.frameCount + frames;
        }
    }

    internal static void Clear()
    {
        held.Clear();
        tapUntil.Clear();
    }

    internal static float Value(PlayerAction a)
    {
        if (!Active)
        {
            return 0f;
        }
        if (held.TryGetValue(a, out float v))
        {
            return v;
        }
        return tapUntil.TryGetValue(a, out int f) && Time.frameCount <= f ? 1f : 0f;
    }
}

[HarmonyPatch(typeof(PlayerAction), "Update", new[] { typeof(ulong), typeof(float), typeof(InputDevice) })]
internal static class PuppetPatch
{
    private static void Postfix(PlayerAction __instance, ulong updateTick, float deltaTime)
    {
        if (!Puppet.Active)
        {
            return;
        }
        float v = Puppet.Value(__instance);
        if (v > 0f)
        {
            __instance.CommitWithValue(v, updateTick, deltaTime);
        }
    }
}

/// <summary>
/// [Godhome] 调试：宣传片里替玩家打 Boss（On 为 true、在战斗里、Boss 开战以后）。大黄蜂是调试无敌的，这里只管“看起来在打”：
/// 朝最近的 Boss 走到够得着的距离，转向它，连续挥针；Boss 在上方时跳起来上挑，在下方时下劈；隔一会儿跳一下、冲刺一下、放一次技能。
/// </summary>
internal sealed class PromoBot : MonoBehaviour
{
    internal static bool On { get; set; }

    private float nextAttack;
    private float nextJump;
    private float jumpRelease;
    private float nextDash;
    private float nextSkill;
    private float upUntil;
    private bool active;
    private readonly System.Random rng = new(1030300);

    private float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

    private void Release(HeroActions a)
    {
        Puppet.Hold(a.Left, false);
        Puppet.Hold(a.Right, false);
        Puppet.Hold(a.Up, false);
        Puppet.Hold(a.Down, false);
        Puppet.Hold(a.Jump, false);
    }

    private static Vector2 Center(HealthManager hm)
    {
        Collider2D? c = hm.GetComponent<Collider2D>();
        return c != null && c.enabled ? (Vector2)c.bounds.center : (Vector2)hm.transform.position;
    }

    private void Update()
    {
        ModeController? mc = ModeController.Instance;
        HeroController? h = HeroController.SilentInstance;
        HeroActions? a = Puppet.Actions;
        if (a == null)
        {
            return;
        }
        bool want = On && Puppet.Active && mc != null && h != null && mc.State == LoomState.Fight && Arena.Started && h.acceptingInput && !h.cState.dead;
        if (!want)
        {
            if (active)
            {
                Release(a);
                active = false;
            }
            return;
        }
        active = true;
        float now = Time.time;
        Vector2 hp = h!.transform.position;
        HealthManager? target = null;
        Vector2 tp = Vector2.zero;
        float best = float.MaxValue;
        foreach (HealthManager hm in mc!.BossHealth)
        {
            if (hm == null || !hm.gameObject.activeInHierarchy || hm.isDead)
            {
                continue;
            }
            Vector2 c = Center(hm);
            float d = (c - hp).sqrMagnitude * (DebugTools.OnScreen(c) ? 1f : 4f);
            if (d < best)
            {
                best = d;
                target = hm;
                tp = c;
            }
        }
        if (target == null)
        {
            Release(a);
            return;
        }
        float dx = tp.x - hp.x;
        float dy = tp.y - hp.y;
        float adx = Mathf.Abs(dx);
        bool toRight = dx > 0f;
        bool grounded = h.cState.onGround;

        // 走：远了就靠近，近了站住（每次挥针前转向它）。
        bool approach = adx > 3.0f;
        Puppet.Hold(a.Right, approach && toRight);
        Puppet.Hold(a.Left, approach && !toRight);
        if (!approach && h.cState.facingRight != toRight)
        {
            Puppet.Tap(toRight ? a.Right : a.Left, 1);
        }

        // 冲刺：离得很远时冲过去。
        if (adx > 7f && now >= nextDash)
        {
            Puppet.Tap(a.Dash, 3);
            nextDash = now + R(1.4f, 2.6f);
        }

        // 跳：Boss 在上方、或者隔一会儿。
        if (grounded && now >= nextJump && (dy > 2.2f || adx < 4f))
        {
            Puppet.Hold(a.Jump, true);
            jumpRelease = now + (dy > 2.2f ? R(0.32f, 0.45f) : R(0.15f, 0.35f));
            nextJump = now + (dy > 2.2f ? R(0.8f, 1.4f) : R(1.3f, 2.6f));
        }
        if (Puppet.IsHeld(a.Jump) && now >= jumpRelease)
        {
            Puppet.Hold(a.Jump, false);
        }

        // 上挑 / 下劈。
        bool up = dy > 2.0f && adx < 2.6f;
        bool down = !grounded && dy < -1.4f && adx < 1.8f;
        if (up)
        {
            upUntil = now + 0.2f;
        }
        Puppet.Hold(a.Up, now < upUntil);
        Puppet.Hold(a.Down, down);

        // 挥针：够得着就连打。
        if (now >= nextAttack && adx < 3.8f && Mathf.Abs(dy) < 3.2f)
        {
            Puppet.Tap(a.Attack, 2);
            nextAttack = now + R(0.2f, 0.3f);
        }

        // 技能：隔几秒放一次。
        if (now >= nextSkill && adx < 6f)
        {
            Puppet.Tap(a.QuickCast, 3);
            nextSkill = now + R(5f, 8f);
        }
    }
}
