using Photon.SocketServer;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// 4.3.10 client on the comm (chat / lobby) server. Accepts the connection and the handshake so
	/// the client stays connected; lobby and chat methods are logged until they are implemented.
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

				default:
					return false;
			}
		}
	}
}
