using System;

namespace Godhome;

/// <summary>
/// [Godhome] 一次万神殿连战（照前作 BossSequenceController.BossSequenceData：束缚、计时、受过伤没有、打到第几个）。
/// 整串用开始时拍的一份快照：每场之间把快照拷回实时数据（上一场改过的剧情标记、场景状态都撤掉），
/// 只把血和丝带到下一场（前作 FinishBossScene 也是带着血和灵魂直接进下一个场景）。
/// </summary>
internal sealed class PantheonRun
{
    internal PantheonRun(PantheonDef def, Binding bindings)
    {
        Def = def;
        Bindings = bindings;
    }

    internal PantheonDef Def { get; }

    internal Binding Bindings { get; }

    /// <summary>正在打（或者刚打完）的是第几个（从 0 数）。</summary>
    internal int Index { get; set; }

    /// <summary>用时：只数战斗中（进场、战斗、判赢后那一小段）没暂停的时间，休息点不算。</summary>
    internal float Elapsed { get; set; }

    /// <summary>受过伤（前作 KnightDamaged）：HeroController.OnTakenDamage。</summary>
    internal bool Damaged { get; set; }

    /// <summary>用过调试辅助（自检、秒杀键、调试无敌）：不记录完成情况。</summary>
    internal bool Assisted { get; set; }

    /// <summary>挂着受伤事件的那个角色（结束时从它身上摘掉）。</summary>
    internal HeroController? DamageHook { get; set; }

    /// <summary>
    /// 带到下一场的血和丝（不经过休息点时）。进场后再设一次：进入游戏自己的记忆区域时，
    /// GameManager.EnteredNewMapZone 会 MaxHealthKeepBlue 回满血（Codex 审查 0.8.0），场景加载完才发生，比我们换场时设的值晚。
    /// </summary>
    internal (int Hp, int Silk)? Carry { get; set; }

    internal BossDef Current => Def.Bosses[Math.Max(0, Math.Min(Index, Def.Bosses.Count - 1))];

    internal bool IsLast => Index >= Def.Bosses.Count - 1;

    public override string ToString() => $"{Def.Id} {Index + 1}/{Def.Bosses.Count}（{Godhome.Bindings.Describe(Bindings)}）";
}

/// <summary>一次万神殿完成后的结算（回到门前时弹出）。</summary>
internal sealed class PantheonResult
{
    internal PantheonDef Def = null!;
    internal Binding Bindings;
    internal bool Damaged;
    internal float Seconds;
    internal bool Assisted;

    /// <summary>这次之前这扇门已经有的完成情况（结算里已经有的图标直接亮，新的逐个闪出来，照前作）。</summary>
    internal bool WasCompleted;
    internal bool WasAllBindings;
    internal bool WasNoHits;
    internal Binding WasBound;
}
