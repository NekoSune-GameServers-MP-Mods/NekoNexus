using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cmune.DataCenter.Common.Entities;
using NekoNexus.Realtime.Server.Comm;
using Photon.SocketServer;
using PhotonHostRuntimeInterfaces;
using UberStrike.Core.Models;
using static NekoNexus.WebSocket;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// UberStrike 4.3.10 (Android) client on the comm / lobby server.
	///
	/// Flow recovered from the 4.3.10 client (CommConnectionManager, PhotonPeerListener,
	/// ClientCommCenter):
	/// <list type="number">
	/// <item>connect + MessageToApplication 1 (handshake), 2 (server load) - handled by <see cref="LegacyPeer"/></item>
	/// <item>PhotonGameJoin 0x58 for RoomMetaData(88, "The CommServer", server). The reply must carry
	///   {9: actor id, 4: CmuneRoomID bytes, 200: build level (false), 201: server ticks}; only then is
	///   the client "in the room" and allowed to call ClientCommCenter methods.</item>
	/// <item>MessageToServer 0x52 to network id 4 (ClientCommCenter, a global class with a fixed id):
	///   method 1 JoinCommServer(CommActorInfo), 25 SendLobbyChatMessage(actorId, message), ...</item>
	/// <item>server -> client calls are events with code 0 and {101: network id, 100: method, 103: args}.</item>
	/// </list>
	///
	/// Identity: 4.3.10 sends its CMID as the Photon application id and its name inside
	/// CommActorInfo, with no verifiable session ticket. Both can be forged by a modified client, so
	/// Android players are shown as "Name [Android]", chat is relayed with CMID 0 (PC moderation tools
	/// never target an account because of a forged id) and relaying Android chat to PC is opt-in
	/// through EnableLegacyGuestChat.
	/// </summary>
	public class LegacyCommPeer : LegacyPeer {
		/// <summary>[NetworkClass(4)] ClientCommCenter.</summary>
		public const short CommCenterNetworkId = 4;

		/// <summary>ClientCommCenter method ids (NetworkMethod attributes / SendMethodToServer ids).</summary>
		public static class Method {
			// client -> server
			public const byte JoinCommServer = 1;          // (CommActorInfo me)
			public const byte SendUpdatedActorInfo = 3;    // (CommActorInfo me)
			public const byte SendUpdateResetLobby = 4;    // ()
			public const byte UpdatePlayerRoom = 21;       // (CmuneRoomID room)
			public const byte ResetPlayerRoom = 22;        // ()
			public const byte SendContactList = 43;        // (List<int> cmids)
			public const byte UpdateContacts = 44;         // (...)
			public const byte SendUpdateAllPlayers = 47;   // ()
			// both directions
			public const byte LobbyChat = 25;              // up: (int actorId, string msg)  down: (int cmid, int actorId, string name, string msg)
			// server -> client
			public const byte OnPlayerLeft = 2;            // (int cmid)
			public const byte OnPlayerUpdate = 3;          // (SyncObject actor)
			public const byte OnFullPlayerListUpdate = 4;  // (List<SyncObject> actors)
		}

		private const string AndroidSuffix = " [Android]";
		private const int MaxMessageLength = 140;

		private static readonly object RegistryLock = new object();
		private static readonly List<LegacyCommPeer> ConnectedPeers = new List<LegacyCommPeer>();
		private static readonly ProfanityFilter.ProfanityFilter ProfanityFilter = new ProfanityFilter.ProfanityFilter();

		private readonly object stateLock = new object();
		private bool hasJoinedRoom;
		private bool hasJoinedLobby;
		private DateTime lastChatUtc = DateTime.MinValue;
		private bool toldChatDisabled;

		public string PlayerName { get; private set; } = "";
		public byte ChannelValue { get; private set; } = (byte)ChannelType.Android;
		public int ActorId => Cmid;
		public bool InLobby { get { lock (stateLock) return hasJoinedLobby; } }

		/// <summary>Name as shown to everyone else.</summary>
		/// <summary>Name for relayed chat: tagged, because Android chat reaches PC with CMID 0.</summary>
		public string DisplayName => RosterName + AndroidSuffix;
		/// <summary>Name for player lists: both clients already show an Android channel icon.</summary>
		public string RosterName => string.IsNullOrWhiteSpace(PlayerName) ? $"Player {Cmid}" : PlayerName;

		/// <summary>The comm lobby room (88). 4.3.10 shows a player as in game when CurrentRoom.Number != 88.</summary>
		private const int CommLobbyRoomNumber = 88;
		private static readonly LegacyRoomId LobbyRoom = new LegacyRoomId { Number = CommLobbyRoomNumber };

		public LegacyCommPeer(InitRequest initRequest) : base(initRequest) {
			lock (RegistryLock) ConnectedPeers.Add(this);
		}

		protected override string Name => "LegacyComm";

		private static ApplicationConfiguration Config => CommServerApplication.Instance.Configuration;

		// ------------------------------------------------------------------ operations

		protected override bool OnLegacyOperation(OperationRequest request) {
			switch (request.OperationCode) {
				case LegacyOperationCode.PhotonGameJoin:
					HandleJoinRoom(request);
					return true;
				case LegacyOperationCode.PhotonGameLeave:
					HandleLeaveRoom();
					return true;
				case LegacyOperationCode.MessageToServer:
					return HandleMessageToServer(request);
				default:
					return false;
			}
		}

		private void HandleJoinRoom(OperationRequest request) {
			var roomId = new LegacyRoomId { Number = 88 };
			if (request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var raw) && raw is byte[] bytes) {
				try {
					var args = LegacySerialization.ToObjects(bytes);
					if (args.Length > 0 && args[0] is LegacyRoomMetaData room) {
						roomId = room.RoomId;
						Log.Info($"[{Name}] cmid {Cmid}: joining comm room {room}");
					}
				} catch (LegacyProtocolException ex) {
					Log.Warn($"[{Name}] cmid {Cmid}: unreadable join request ({ex.Message}), using default comm room");
				}
			}

			if (Cmid <= 0) {
				SendOperationResponse(new OperationResponse(LegacyOperationCode.PhotonGameJoin) {
					ReturnCode = -1,
					DebugMessage = "invalid cmid"
				}, new SendParameters());
				return;
			}

			lock (stateLock) hasJoinedRoom = true;

			var response = new OperationResponse(LegacyOperationCode.PhotonGameJoin) { ReturnCode = 0 };
			response.Parameters[LegacyParameterKey.ActorNr] = ActorId;
			response.Parameters[LegacyParameterKey.GameId] = roomId.ToBytes();
			response.Parameters[LegacyParameterKey.InitRoom] = false;
			response.Parameters[LegacyParameterKey.ServerTicks] = (long)Environment.TickCount;
			SendOperationResponse(response, new SendParameters());
		}

		private void HandleLeaveRoom() {
			LeaveLobby();
			lock (stateLock) hasJoinedRoom = false;
			SendOperationResponse(new OperationResponse(LegacyOperationCode.PhotonGameLeave) { ReturnCode = 0 }, new SendParameters());
		}

		private bool HandleMessageToServer(OperationRequest request) {
			if (!request.Parameters.TryGetValue(LegacyParameterKey.InstanceId, out var instanceObj) || !(instanceObj is short instanceId)
				|| !request.Parameters.TryGetValue(LegacyParameterKey.MethodId, out var methodObj) || !(methodObj is byte methodId)) {
				return false;
			}

			if (instanceId != CommCenterNetworkId) {
				Log.Debug($"[{Name}] cmid {Cmid}: call to network id {instanceId} method {methodId} ignored");
				return true;
			}

			object[] args;
			try {
				request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var raw);
				var payload = raw as byte[];
				if (payload != null && payload.Length > 8192) return true;
				args = LegacySerialization.ToObjects(payload);
			} catch (LegacyProtocolException ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: could not decode ClientCommCenter method {methodId}: {ex.Message}");
				return true;
			}

			switch (methodId) {
				case Method.JoinCommServer:
					HandleJoinCommServer(args);
					return true;
				case Method.SendUpdatedActorInfo:
					if (args.Length > 0 && args[0] is LegacySyncObject info) ReadActorInfo(info);
					return true;
				case Method.SendUpdateResetLobby:
				case Method.SendUpdateAllPlayers:
					SendFullPlayerList();
					return true;
				case Method.LobbyChat:
					HandleLobbyChat(args);
					return true;
				case Method.UpdatePlayerRoom:
				case Method.ResetPlayerRoom:
				case Method.SendContactList:
				case Method.UpdateContacts:
					return true; // nothing to do server side yet
				default:
					return false; // LegacyPeer logs it once
			}
		}

		// ------------------------------------------------------------------ lobby / roster

		private void ReadActorInfo(LegacySyncObject info) {
			if (info[LegacyActorField.PlayerName] is string name) PlayerName = CleanName(name);
			if (info[LegacyActorField.Channel] is byte channel) ChannelValue = channel;
			// The client also sends its access level; it is ignored on purpose (it is not verifiable).
		}

		private void HandleJoinCommServer(object[] args) {
			if (args.Length > 0 && args[0] is LegacySyncObject info) ReadActorInfo(info);

			bool firstJoin;
			lock (stateLock) {
				if (!hasJoinedRoom) return;
				firstJoin = !hasJoinedLobby;
				hasJoinedLobby = true;
			}

			Log.Info($"[{Name}] cmid {Cmid}: joined the lobby as '{DisplayName}'");
			if (!firstJoin) {
				SendFullPlayerList();
				return;
			}

			// Refreshes the roster of every PC player and (through BroadcastFullPlayerList) of every
			// Android player, this one included.
			try {
				LobbyManager.Instance.UpdatePlayerList();
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: failed to refresh player lists", ex);
				SendFullPlayerList();
			}
		}

		private void LeaveLobby() {
			bool wasInLobby;
			lock (stateLock) {
				wasInLobby = hasJoinedLobby;
				hasJoinedLobby = false;
			}
			if (!wasInLobby) return;

			foreach (var other in LobbyPeers().Where(p => p != this)) {
				other.SendCall(Method.OnPlayerLeft, Cmid);
			}
			try {
				foreach (var pc in LobbyManager.Instance.Peers.ToList()) {
					pc.LobbyEventSender.SendPlayerLeft(Cmid, true);
				}
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: failed to notify PC lobby of leave", ex);
			}
		}

		public LegacySyncObject ToSyncObject() {
			var sync = new LegacySyncObject { Id = ActorId };
			sync[LegacyActorField.Cmid] = Cmid;
			sync[LegacyActorField.PlayerName] = RosterName;
			sync[LegacyActorField.Channel] = ChannelValue;
			sync[LegacyActorField.CurrentRoom] = LobbyRoom; // Android players can't be in a match yet
			sync[LegacyActorField.AccessLevel] = (byte)MemberAccessLevel.Default;
			return sync;
		}

		private static LegacySyncObject ToSyncObject(CommActorInfo pc) {
			var sync = new LegacySyncObject { Id = pc.Cmid };
			sync[LegacyActorField.Cmid] = pc.Cmid;
			sync[LegacyActorField.PlayerName] = pc.PlayerName ?? "";
			if (!string.IsNullOrEmpty(pc.ClanTag)) sync[LegacyActorField.ClanTag] = pc.ClanTag;
			sync[LegacyActorField.Channel] = (byte)pc.Channel;
			sync[LegacyActorField.CurrentRoom] = ToLegacyRoom(pc.CurrentRoom);
			sync[LegacyActorField.AccessLevel] = (byte)pc.AccessLevel;
			return sync;
		}

		/// <summary>
		/// PC CurrentRoom as a 4.3.10 CmuneRoomID. Without it the client's default (room 0) made every
		/// player show as "in game" (CommActorInfo.IsInGame is CurrentRoom.Number != 88).
		/// </summary>
		private static LegacyRoomId ToLegacyRoom(GameRoom room) {
			if (room == null || room.Number <= 0) return LobbyRoom;
			var address = new byte[6];
			var ip = room.Server?.IpAddress?.Split('.');
			if (ip != null && ip.Length == 4) {
				for (var k = 0; k < 4; k++) byte.TryParse(ip[k], out address[k]);
			}
			var port = room.Server?.Port ?? 0;
			address[4] = (byte)(port & 0xFF);
			address[5] = (byte)(port >> 8);
			// Never report a real match as room 88, or it would read as "in the lobby".
			return new LegacyRoomId { Number = room.Number == CommLobbyRoomNumber ? -room.Number : room.Number, Address = address };
		}

		private void SendFullPlayerList() {
			var list = new List<LegacySyncObject>();
			var seen = new HashSet<int>();
			try {
				foreach (var pc in LobbyManager.Instance.Peers.ToList()) {
					if (pc?.Actor?.ActorInfo == null || !seen.Add(pc.Actor.Cmid)) continue;
					list.Add(ToSyncObject(pc.Actor.ActorInfo));
				}
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: could not read PC lobby", ex);
			}
			foreach (var android in LobbyPeers()) {
				if (seen.Add(android.Cmid)) list.Add(android.ToSyncObject());
			}
			SendCall(Method.OnFullPlayerListUpdate, list);
		}

		// ------------------------------------------------------------------ chat

		private void HandleLobbyChat(object[] args) {
			// SendLobbyChatMessage sends (ActorIdSecure, message) - the first value is the actor id the
			// server assigned in the join reply, not the CMID. Identity always comes from this peer.
			if (args.Length != 2 || !(args[1] is string rawMessage) || !InLobby) return;

			var now = DateTime.UtcNow;
			if ((now - lastChatUtc).TotalMilliseconds < 750) return;
			lastChatUtc = now;

			var message = CleanMessage(rawMessage);
			if (message.Length == 0) return;

			if (!Config.EnableLegacyGuestChat) {
				if (!toldChatDisabled) {
					toldChatDisabled = true;
					SendCall(Method.LobbyChat, 0, 0, "System", "Chat from Android is disabled on this server (EnableLegacyGuestChat).");
				}
				Log.Debug($"[{Name}] cmid {Cmid}: lobby chat not relayed (EnableLegacyGuestChat is false)");
				return;
			}

			var name = DisplayName;
			if (Config.EnableChatLog) Log.Info($"[Lobby] {name} (cmid {Cmid}): {message}");

			// Other comm servers, through the master server (it does not echo back to us).
			CommServerApplication.Instance.SocketClient?.SendSync(PacketType.ChatMessage,
				new SocketChatMessage { Cmid = 0, Name = name, Message = message },
				serverType: ServerType.Comm);

			// PC players on this comm server.
			try {
				foreach (var pc in LobbyManager.Instance.GlobalLobby.Peers.ToList()) {
					pc.LobbyEventSender.SendLobbyChatMessage(0, name, message);
				}
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: failed to deliver chat to PC peers", ex);
			}

			// Other Android players (the sender already shows its own message).
			BroadcastLobbyChat(0, name, message, this);
		}

		/// <summary>
		/// Deliver a lobby chat line (from PC or another Android player) to every Android player in
		/// the lobby, as ClientCommCenter.OnLobbyChatMessage(cmid, actorId, name, message).
		/// </summary>
		public static void BroadcastLobbyChat(int cmid, string name, string message, LegacyCommPeer except = null) {
			if (string.IsNullOrWhiteSpace(message)) return;
			foreach (var peer in LobbyPeers()) {
				if (peer == except) continue;
				peer.SendCall(Method.LobbyChat, cmid, cmid, name ?? "", message);
			}
		}

		/// <summary>A PC player left the lobby.</summary>
		public static void BroadcastPlayerLeft(int cmid) {
			foreach (var peer in LobbyPeers()) peer.SendCall(Method.OnPlayerLeft, cmid);
		}

		/// <summary>The PC lobby changed (someone joined): resend the roster to Android players.</summary>
		public static void BroadcastFullPlayerList() {
			foreach (var peer in LobbyPeers()) peer.SendFullPlayerList();
		}

		// ClientCommCenter methods that make the 4.3.10 client re-fetch from the web services.
		private const byte OnUpdateInboxRequests = 37;   // ()
		private const byte OnUpdateFriendsList = 38;     // ()
		private const byte OnUpdateInboxMessages = 39;   // (int messageId)

		/// <summary>New mail for an Android player: refresh their inbox now (same as PC).</summary>
		public static void NotifyInboxMessage(long cmid, int messageId) {
			foreach (var peer in PeersWithCmid(cmid)) peer.SendCall(OnUpdateInboxMessages, messageId);
		}

		/// <summary>A friend/clan request for an Android player: refresh requests and friends now.</summary>
		public static void NotifyInboxRequests(long cmid) {
			foreach (var peer in PeersWithCmid(cmid)) {
				peer.SendCall(OnUpdateInboxRequests);
				peer.SendCall(OnUpdateFriendsList);
			}
		}

		private static List<LegacyCommPeer> PeersWithCmid(long cmid) {
			lock (RegistryLock) return ConnectedPeers.Where(p => p.Cmid == cmid).ToList();
		}

		/// <summary>Android players as PC CommActorInfo entries, for the PC lobby roster.</summary>
		public static List<CommActorInfo> GetLobbyActorInfos(ICollection<int> excludeCmids = null) {
			var result = new List<CommActorInfo>();
			foreach (var peer in LobbyPeers()) {
				if (excludeCmids != null && excludeCmids.Contains(peer.Cmid)) continue;
				result.Add(new CommActorInfo {
					Cmid = peer.Cmid,
					PlayerName = peer.RosterName,
					Channel = ChannelType.Android,
					AccessLevel = MemberAccessLevel.Default,
				});
			}
			return result;
		}

		private static List<LegacyCommPeer> LobbyPeers() {
			lock (RegistryLock) return ConnectedPeers.Where(p => p.InLobby).ToList();
		}

		// ------------------------------------------------------------------ helpers

		/// <summary>Call a ClientCommCenter method on this client (event code 0).</summary>
		private void SendCall(byte methodId, params object[] args) {
			try {
				var data = new EventData(LegacyEventCode.NetworkMessage, new Dictionary<byte, object> {
					{ LegacyParameterKey.InstanceId, CommCenterNetworkId },
					{ LegacyParameterKey.MethodId, methodId },
					{ LegacyParameterKey.Bytes, LegacySerialization.ToBytes(args) }
				});
				SendEvent(data, new SendParameters());
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: failed to send ClientCommCenter method {methodId}", ex);
			}
		}

		private static string CleanName(string name) {
			var sb = new StringBuilder();
			foreach (var ch in name ?? "") {
				if (!char.IsControl(ch) && ch != '[' && ch != ']') sb.Append(ch);
				if (sb.Length >= 24) break;
			}
			return sb.ToString().Trim();
		}

		private static string CleanMessage(string raw) {
			var sb = new StringBuilder();
			foreach (var ch in raw ?? "") {
				if (!char.IsControl(ch)) sb.Append(ch);
				if (sb.Length >= MaxMessageLength) break;
			}
			var text = sb.ToString().Trim();
			return text.Length == 0 ? text : ProfanityFilter.CensorString(text);
		}

		protected override bool OnApplicationMethod(byte methodId, object[] args, out object[] reply) {
			reply = null;
			switch (methodId) {
				case LegacyApplicationMethod.Handshake:
					HandleHandshake(args);
					return true;
				case LegacyApplicationMethod.ServerLoad:
					var app = CommServerApplication.Instance;
					int androidPlayers;
					lock (RegistryLock) androidPlayers = ConnectedPeers.Count;
					var load = new LegacyServerLoadData {
						PlayersConnected = app.Peers + androidPlayers,
						PeersConnected = app.Peers + androidPlayers,
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

		protected override void OnDisconnect(DisconnectReason reasonCode, string reasonDetail) {
			LeaveLobby();
			lock (RegistryLock) ConnectedPeers.Remove(this);
			base.OnDisconnect(reasonCode, reasonDetail);
		}
	}
}
