namespace Godhome;

/// <summary>
/// [Godhome] 大厅在哪：有前作时是前作的神居（前厅、屋顶、手办屋三个房间都算大厅），没有时是织忆圣所（一个场景）。
/// 存档里的复活点始终是织忆圣所的长椅（本作的场景），见 SaveSanitizePatch。
/// </summary>
internal static class Lobby
{
    /// <summary>回大厅时去的场景（前作：前厅）。</summary>
    internal static string Scene => Hk1Lobby.Active ? Hk1Lobby.Atrium : GodhomeConfig.LobbyScene.Value;

    /// <summary>回大厅时醒来的地方（复活点的名字）。</summary>
    internal static string Marker => Hk1Lobby.Active ? Hk1Lobby.ArriveMarker : GodhomeConfig.LobbyMarker.Value;

    /// <summary>这个场景是不是大厅（前作的三个房间都是）。</summary>
    internal static bool Is(string scene) => Hk1Lobby.Active ? Hk1Lobby.IsHub(scene) : scene == GodhomeConfig.LobbyScene.Value;

    /// <summary>大厅准备好了：圣所搭好了，或者在前作神居里、雕像换好了。</summary>
    internal static bool Ready => Hk1Lobby.Active ? Hk1Lobby.Ready : Sanctum.Built && !Sanctum.Busy;
}
