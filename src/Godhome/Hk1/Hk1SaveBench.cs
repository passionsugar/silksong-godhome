using HutongGames.PlayMaker;

namespace Godhome;

internal sealed class Hk1SaveBench : FsmStateAction
{
    public override void OnEnter()
    {
        GameManager gm = GameManager.UnsafeInstance;
        string room = Owner.scene.name;
        // A pantheon spa bench is a rest stop, never a permanent checkpoint.
        if (GodhomeSave.IsGodhome(gm) && Hk1Lobby.IsHub(room) && Owner.name == Hk1Lobby.BenchMarker(room)
            && !SaveGuard.Active && ModeController.Instance?.State == LoomState.Lobby)
        {
            GodhomePlugin.Instance.Records.HubBenchScene = room;
            GodhomeSave.SetLobbyCheckpoint(gm.playerData);
            ModeController.Instance.SaveNow($"hub bench {room}@{Owner.name}");
        }
        Finish();
    }
}
