using System;
using System.Text;
using NekoNexus.Realtime.Server.Comm;
using Photon.SocketServer;
using static NekoNexus.WebSocket;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// UberStrike 4.3.10 comm/lobby peer.
	/// The Android ClientCommCenter.SendLobbyChatMessage method (Assembly-CSharp.dll)
	/// uses instance ID 25, method ID 2, with [player name, message] strings.
	/// The name is UNTRUSTED and never forwarded as a PC identity.
	/// </summary>
	public class LegacyCommPeer : LegacyPeer {
		private const short LobbyChatInstanceId = 25;
		private const byte LobbyChatMethodId = 2;
		private DateTime lastChatUtc = DateTime.MinValue;
		private static readonly ProfanityFilter.ProfanityFilter ProfanityFilter = new ProfanityFilter.ProfanityFilter();

		public LegacyCommPeer(InitRequest initRequest) : base(initRequest) { }

		protected override string Name => "LegacyComm";

		protected override bool OnLegacyOperation(OperationRequest request) {
			if (request.OperationCode != LegacyOperationCode.MessageToServer) return false;
			if (!request.Parameters.TryGetValue(LegacyParameterKey.InstanceId, out var instanceObj)
				|| !(instanceObj is short instanceId) || instanceId != LobbyChatInstanceId) return false;
			if (!request.Parameters.TryGetValue(LegacyParameterKey.MethodId, out var methodObj)
				|| !(methodObj is byte methodId) || methodId != LobbyChatMethodId) return false;

			// Client 4.3.10 sends a numeric CMID in the transport application id.
			// This value is spoofable: there is no authenticated user session here.
			// Keep guest chat disabled by default and never impersonate the supplied name.
			if (!CommServerApplication.Instance.Configuration.EnableLegacyGuestChat) {
				Log.Warn($"[{Name}] cmid {Cmid}: legacy guest chat rejected (EnableLegacyGuestChat is false)");
				return true;
			}

			if (Cmid <= 0 || !request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var data)
				|| !(data is byte[] payload) || payload.Length > 4096) return true;

			object[] args;
			try {
				args = LegacySerialization.ToObjects(payload);
			} catch (LegacyProtocolException ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: invalid lobby chat payload: {ex.Message}");
				return true;
			}

			if (args.Length != 2 || !(args[0] is string) || !(args[1] is string rawMessage)
				|| string.IsNullOrWhiteSpace(rawMessage)) return true;

			var now = DateTime.UtcNow;
			if ((now - lastChatUtc).TotalMilliseconds < 750) return true;
			lastChatUtc = now;

			var cleaned = new StringBuilder();
			foreach (char ch in rawMessage) {
				if (!char.IsControl(ch)) cleaned.Append(ch);
				if (cleaned.Length >= 140) break;
			}
			if (cleaned.Length == 0) return true;
			string message = ProfanityFilter.CensorString(cleaned.ToString());
			// This guest identity is deliberately not the unverified player name/CMID.
			// Real account identity and mute/ban enforcement require a signed login ticket.
			CommServerApplication.Instance.SocketClient?.SendSync(
				PacketType.ChatMessage,
				new SocketChatMessage { Cmid = 0, Name = "Android Guest", Message = message },
				serverType: ServerType.Comm);
			return true;
		}

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
						RoomsCreated = 1,
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
