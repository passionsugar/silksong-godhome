using HutongGames.PlayMaker;
using UnityEngine;

namespace Godhome;

internal sealed class PantheonRestBench : MonoBehaviour
{
    private PlayMakerFSM bench = null!;
    private SylphsongEffects? effects;
    private bool seated;
    private float nextSilk;

    // The fallback room also serves as a normal story bench. Change only the live
    // instance loaded for a pantheon rest, after PlayMaker has copied its template.
    internal static void Configure(PlayMakerFSM pm)
    {
        var mode = ModeController.Instance;
        if (mode?.Run == null || mode.State is not (LoomState.ToRest or LoomState.Rest)
            || pm.FsmName != "Bench Control" || !GodhomeSave.IsGodhome(GameManager.UnsafeInstance))
        {
            return;
        }
        string room = pm.gameObject.scene.name;
        bool spa = room == Hk1Lobby.Spa;
        if (!spa && room != PantheonCatalog.RestScene)
        {
            return;
        }
        // Set the imported bench's serialized flag even if its FSM is still dormant.
        if (!spa && !pm.Fsm.Initialized)
        {
            return;
        }
        var respawn = pm.FsmVariables.FindFsmBool("Set Respawn");
        if (respawn != null)
        {
            respawn.Value = false;
        }
        if (!spa)
        {
            // The native bench already has Eva Heal. Skip only its checkpoint/save actions.
            var save = pm.Fsm.GetState("Save Game");
            if (save != null)
            {
                foreach (FsmStateAction action in save.Actions)
                {
                    if (action is global::SaveGame)
                    {
                        action.Enabled = false;
                    }
                }
            }
        }
        if (pm.GetComponent<PantheonRestBench>() == null)
        {
            var rest = pm.gameObject.AddComponent<PantheonRestBench>();
            rest.bench = pm;
            if (spa)
            {
                rest.effects = pm.gameObject.AddComponent<SylphsongEffects>();
            }
        }
    }

    private void Update()
    {
        var mode = ModeController.Instance;
        PlayerData pd = PlayerData.instance;
        if (mode?.State != LoomState.Rest || mode.Run == null || pd == null || !pd.atBench || !bench.isActiveAndEnabled
            || !GodhomeSave.IsGodhome(pd) || bench.ActiveStateName == "Get Off")
        {
            if (seated)
            {
                effects?.End(false);
                seated = false;
            }
            return;
        }
        if (!seated)
        {
            if (bench.ActiveStateName != "Resting")
            {
                return;
            }
            seated = true;
            var hero = HeroController.SilentInstance;
            if (hero != null && hero.cState.isMaggoted)
            {
                hero.SetIsMaggoted(false);
            }
            nextSilk = Time.time + 1.26f;
        }
        // The imported HK1 seat has no Eva Heal FSM. Match its warm-up, tick and cap.
        if (effects != null && pd.HasBoundCrestUpgrader && Time.time >= nextSilk && pd.silk < pd.CurrentSilkMax
            && HeroController.SilentInstance is { } hornet)
        {
            nextSilk = Time.time + 0.26f;
            hornet.AddSilk(1, false);
            effects.Pulse(hornet.transform);
            if (pd.silk >= pd.CurrentSilkMax)
            {
                effects.End(true);
            }
        }
    }
}
