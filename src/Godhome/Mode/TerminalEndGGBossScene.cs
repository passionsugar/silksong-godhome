using System;
using System.Linq;
using HutongGames.PlayMaker;

namespace Godhome;

/// <summary>
/// 表里标了 terminal 的结局状态（崇高圣母灵丝的 Phase Control / Death Hit）：神居在这里判胜，原版不能再往结局走。
/// 原版 Death Hit 0.2 秒后经 Death Type 发 DEATH START，结局演出（Death Sequence 的 Death Slashes Up）会收走操作、
/// 把主角刚体设成运动学（SetIsKinematic2d）、藏起主角——神居离场时不还原，回大厅后人浮在空中（0.10.5 报告）。
///
/// 做法：判胜动作插在最前面，这个状态原来的动作照样执行（停攻击 ATTACK STOP、停移动 MOVE STOP、清弹幕 ATTACK CLEAR、
/// Boss 无敌、死亡动画；ATTACK STOP 还会让 Attack Control 走 Uncatch Hornet? 放开被丝线抓住的主角），
/// 只去掉出口（FINISHED → Death Type），进入时再清掉这个 FSM 的全局跳转（MID STAGGER 之类），停在这里直到离场。
/// 全局跳转不能在打补丁时清：战斗中的阶段切换要用（审查 PR #1 时保留了这一点）。
/// </summary>
internal sealed class TerminalEndGGBossScene : EndGGBossScene
{
    internal static void Install(FsmState state)
    {
        FsmStateAction[] rest = state.Actions.Where(a => a is not EndGGBossScene).ToArray();
        state.Actions = new FsmStateAction[] { new TerminalEndGGBossScene() }.Concat(rest).ToArray();
        state.Transitions = Array.Empty<FsmTransition>();
    }

    public override void OnEnter()
    {
        Fsm.GlobalTransitions = Array.Empty<FsmTransition>();
        base.OnEnter();
    }
}
