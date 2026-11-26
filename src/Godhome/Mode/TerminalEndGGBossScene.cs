using System;
using HutongGames.PlayMaker;

namespace Godhome;

/// <summary>
/// A data-selected story-ending state is terminal in Godhome. Keep the game's win action,
/// but do not let FINISHED or a later global event enter the original ending sequence.
/// Global routes belong to the live FSM instance and are removed only on death: removing
/// them when installing the action would also break the boss's combat phase transitions.
/// </summary>
internal sealed class TerminalEndGGBossScene : EndGGBossScene
{
    internal static void Install(FsmState state)
    {
        var existing = state.Actions.Length > 0 ? state.Actions[0] as TerminalEndGGBossScene : null;
        state.Actions = new FsmStateAction[] { existing != null && existing.Enabled ? existing : new TerminalEndGGBossScene() };
        state.Transitions = Array.Empty<FsmTransition>();
    }

    public override void OnEnter()
    {
        Fsm.GlobalTransitions = Array.Empty<FsmTransition>();
        base.OnEnter();
    }
}
