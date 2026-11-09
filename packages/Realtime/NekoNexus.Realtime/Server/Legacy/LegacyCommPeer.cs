using NekoNexus.Realtime.Server.Comm;
using Photon.SocketServer;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// UberStrike 4.3.10 comm/lobby peer. The legacy client probes the comm
	/// server through application method 2, exactly as it probes game servers.
	/// Replying with a correctly encoded ServerLoadData prevents the probe from
	/// timing out and wrongly marking a reachable comm server as unavailable.
	/// Lobby and chat RPCs still require their legacy method/type mappings.
	/// </summary>
	public class LegacyCommPeer : LegacyPeer {
		public LegacyCommPeer(InitRequest initRequest) : base(initRequest) { }

		protected override string Name => "LegacyComm";

		protected override bool OnApplicationMethod(byte methodId, object[] args, out object[] reply) {
			reply = null;
			switch (methodId) {
				case LegacyApplicationMethod.Handshake:
					HandleHandshake(args);
					return true;

				case LegacyApplicationMethod.ServerLoad:
					var app = CommServerApplication.Instance;
					var load = new LegacyServerLoadData {
						PlayersConnected = app.Peers,
						PeersConnected = app.Peers,
						RoomsCreated = 1, // The comm application hosts a single global lobby.
						MaxPlayerCount = app.Configuration.CommApplicationSettings.MaxPlayerCount
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
