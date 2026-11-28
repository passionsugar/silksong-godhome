using System.Collections;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Godhome;

/// <summary>Receives the original roof lever's Cut message on its original platform.</summary>
internal sealed class Hk1RoofPlatform : MonoBehaviour
{
    private PlayMakerFSM lever = null!;
    private bool opened;

    internal static void Prepare(GameObject[] roots)
    {
        GameObject? button = roots.FirstOrDefault(r => r.name == "gg_roof_lever");
        PlayMakerFSM? control = button?.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Switch Control");
        PlayMakerFSM? cut = button?.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Cut Vine Plat");
        GameObject? platform = cut?.FsmVariables.FindFsmGameObject("Vine Plat Cut")?.Value;
        if (control == null || platform == null || platform.name != "GG Fall Platform")
        {
            GodhomePlugin.Log.LogWarning("[Godhome] Roof shortcut: could not bind the lever to its platform");
            return;
        }
        if (platform.GetComponent<Hk1RoofPlatform>() != null)
        {
            return;
        }
        platform.AddComponent<Hk1RoofPlatform>().lever = control;
        // Silksong identifies needle hits via IHitResponder. The old Idle action
        // tries to read attackType from the Hollow Knight weapon's damages_enemy FSM.
        foreach (var action in control.Fsm.GetState("Idle").Actions.OfType<ReceivedDamage>())
        {
            action.Enabled = false;
        }
        control.gameObject.AddComponent<Hk1RoofLever>().Control = control;
    }

    private IEnumerator Start()
    {
        // PersistentBoolItem restores the old lever in Start. This also recovers a
        // save where the lever was already hit but the old Cut message did nothing.
        yield return null;
        var activated = lever.FsmVariables.FindFsmBool("Activated");
        if (!opened && (GodhomePlugin.Instance.Records.RoofShortcutOpened || activated?.Value == true))
        {
            RestoreLanded();
            opened = true;
            GodhomePlugin.Instance.Records.RoofShortcutOpened = true;
            if (activated != null)
            {
                activated.Value = true;
            }
            lever.SetState("Activated");
        }
    }

    // Invoked by SendMessageV2 in the imported Cut Vine Plat FSM.
    public void Cut()
    {
        if (opened)
        {
            return;
        }
        opened = true;
        VinePlatform vine = GetComponent<VinePlatform>();
        if (vine != null)
        {
            vine.CutDown();
        }
        else
        {
            RestoreLanded();
        }
        GodhomePlugin.Instance.Records.RoofShortcutOpened = true;
        ModeController.Instance?.SaveNow("roof shortcut");
    }

    private void RestoreLanded()
    {
        // The scene contains a second platform with its own solid collider at the
        // exact landing position. This is the same layout as VinePlatform.OnStartActivated.
        transform.Find("Plat Sprite").gameObject.SetActive(false);
        transform.Find("Activated Plat").gameObject.SetActive(true);
        GetComponent<Collider2D>().enabled = false;
        transform.Find("Landing Box").gameObject.SetActive(false);
    }
}

internal sealed class Hk1RoofLever : MonoBehaviour, IHitResponder
{
    internal PlayMakerFSM Control = null!;

    public IHitResponder.HitResponse Hit(HitInstance hit)
    {
        if (!hit.IsFirstHit || !hit.IsNailDamage || Control.ActiveStateName != "Idle"
            || ModeController.Instance?.State != LoomState.Lobby)
        {
            return IHitResponder.Response.None;
        }
        Control.SetState("Player Data?");
        return IHitResponder.Response.GenericHit;
    }
}
