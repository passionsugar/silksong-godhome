using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;

namespace Godhome;

/// <summary>Block player input while this scene state owns the intro animation.</summary>
internal sealed class ArenaIntroInput : FsmStateAction
{
    private static readonly HashSet<ArenaIntroInput> held = new();
    private static int generation;
    private readonly int fight = generation;
    private HeroController? hero;

    public override void OnEnter()
    {
        // Old scenes can advance one last state while their replacement is loading.
        if (fight == generation && HeroController.SilentInstance is HeroController current && current != null)
        {
            hero = current;
            hero.AddInputBlocker(this);
            held.Add(this);
        }
        // FINISHED transitions still depend only on the original animation actions.
        Finish();
    }

    public override void OnExit()
    {
        if (hero != null)
        {
            hero.RemoveInputBlocker(this);
        }
        hero = null;
        held.Remove(this);
    }

    internal static void ReleaseAll()
    {
        // An abandoned fight can be cleared before its scene runs OnExit.
        generation++;
        foreach (var action in held.ToArray())
        {
            action.OnExit();
        }
    }
}
