using NekoNexus.Realtime.Server.Game;
using Photon.SocketServer;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// 4.3.10 client on a game server. Server load is reported from the same room manager that
	/// serves PC players, so Android and PC see identical player/room counts for each server.
	/// </summary>
	public class LegacyGamePeer : LegacyPeer {
		public LegacyGamePeer(InitRequest initRequest) : base(initRequest) { }

		protected override string Name => "LegacyGame";

		protected override bool OnApplicationMethod(byte methodId, object[] args, out object[] reply) {
			reply = null;
			switch (methodId) {
				case LegacyApplicationMethod.Handshake:
					HandleHandshake(args);
					return true;

				case LegacyApplicationMethod.ServerLoad:
					var app = GameServerApplication.Instance;
					var load = new LegacyServerLoadData {
						PlayersConnected = app.Players,
						PeersConnected = app.Peers,
						RoomsCreated = app.RoomManager.Rooms.Count,
						MaxPlayerCount = app.Configuration.GameApplicationSettings.MaxPlayerCount,
					};
					Log.Debug($"[{Name}] cmid {Cmid}: server load -> {load}");
					reply = new object[] { load };
					return true;

				default:
					return false;
			}
		}
	}
}
