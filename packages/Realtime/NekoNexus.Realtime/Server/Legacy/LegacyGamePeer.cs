using System;
using Cmune.DataCenter.Common.Entities;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NekoNexus.Realtime.Server.Game;
using Photon.SocketServer;
using PhotonHostRuntimeInterfaces;
using UberStrike.Core.Models;
using UberStrike.Core.Types;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// 4.3.10 client on a game server. Server load is reported from the same room manager that
	/// serves PC players, so Android and PC see identical player/room counts for each server.
	///
	/// Game list: when a player picks a server, the 4.3.10 LobbyConnectionManager joins
	/// RoomMetaData(66, "The Lobby", server) on it with PhotonGameJoin. While joined, the server
	/// pushes room-list events (code 3 = clear and refill) with {42: Hashtable{122: List&lt;GameMetaData&gt;}}.
	/// The list is built from the same <see cref="GameRoomManager"/> PC players use, so Android
	/// players see the PC matches. Joining one of them is not translated yet: the join is refused
	/// with a message instead of leaving the client hanging.
	/// </summary>
	public class LegacyGamePeer : LegacyPeer {
		private const int RefreshMilliseconds = 5000;

		private readonly object stateLock = new object();
		private Timer refreshTimer;
		private LegacyRoomId lobbyRoomId;

		public LegacyGamePeer(InitRequest initRequest) : base(initRequest) { }

		protected override string Name => "LegacyGame";

		// ------------------------------------------------------------------ operations

		protected override bool OnLegacyOperation(OperationRequest request) {
			switch (request.OperationCode) {
				case LegacyOperationCode.PhotonGameJoin:
					HandleJoin(request);
					return true;
				case LegacyOperationCode.PhotonGameLeave:
					StopRoomList();
					LeaveMatch();
					SendOperationResponse(new OperationResponse(LegacyOperationCode.PhotonGameLeave) { ReturnCode = 0 }, new SendParameters());
					return true;
				case LegacyOperationCode.MessageToServer:
				case LegacyOperationCode.MessageToAll:
				case LegacyOperationCode.MessageToOthers:
				case LegacyOperationCode.MessageToPlayer:
					RouteGameModeCall(request);
					return true;
				default:
					return false;
			}
		}

		private void HandleJoin(OperationRequest request) {
			LegacyRoomMetaData room = null;
			if (request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var raw) && raw is byte[] bytes) {
				try {
					var args = LegacySerialization.ToObjects(bytes);
					room = args.Length > 0 ? args[0] as LegacyRoomMetaData : null;
				} catch (LegacyProtocolException ex) {
					Log.Warn($"[{Name}] cmid {Cmid}: unreadable join request: {ex.Message}");
				}
			}

			if (room == null || Cmid <= 0) {
				RefuseJoin("Invalid join request.");
				return;
			}

			if (room.RoomId.Number != LegacyGameMetaData.LobbyRoomNumber) {
				// Creating or joining a match needs a web-service round trip (session, profile); do it
				// off the network thread and answer the join when done.
				var requested = room;
				System.Threading.Tasks.Task.Run(() => JoinMatch(requested));
				return;
			}

			var response = new OperationResponse(LegacyOperationCode.PhotonGameJoin) { ReturnCode = 0 };
			response.Parameters[LegacyParameterKey.ActorNr] = Cmid;
			response.Parameters[LegacyParameterKey.GameId] = room.RoomId.ToBytes();
			response.Parameters[LegacyParameterKey.InitRoom] = false;
			response.Parameters[LegacyParameterKey.ServerTicks] = (long)Environment.TickCount;
			SendOperationResponse(response, new SendParameters());

			Log.Info($"[{Name}] cmid {Cmid}: joined the game lobby of {room.RoomId.Server}");
			StartRoomList(room.RoomId);
		}

		private void RefuseJoin(string message) {
			SendOperationResponse(new OperationResponse(LegacyOperationCode.PhotonGameJoin) {
				ReturnCode = 1,
				DebugMessage = message,
			}, new SendParameters());
		}

		// ------------------------------------------------------------------ matches

		private LegacyMatchBridge match;

		/// <summary>
		/// Create (room number 0, from the create-game screen) or join a PC room, and put a server-side
		/// stand-in for this player in it. The stand-in uses the player's own web-service session, so the
		/// room loads their real profile and loadout and records their stats like a PC player's.
		/// </summary>
		private void JoinMatch(LegacyRoomMetaData requested) {
			try {
				LeaveMatch();

				var token = LegacyMatchBridge.FetchSessionToken(Cmid);
				if (string.IsNullOrEmpty(token)) {
					RefuseJoin("Your session has expired. Please log in again.");
					return;
				}
				var member = UserWebServiceClient.Instance.GetMember(token);
				if (member?.CmuneMemberView?.PublicProfile == null || member.CmuneMemberView.PublicProfile.Cmid != Cmid) {
					RefuseJoin("Could not load your profile. Please log in again.");
					return;
				}

				var manager = GameServerApplication.Instance.RoomManager;
				BaseGameRoom pcRoom;
				if (requested.RoomId.Number == 0) {
					if (!(requested is LegacyGameMetaData meta)) {
						RefuseJoin("Invalid game settings.");
						return;
					}
					var mode = ToPcGameMode(meta.GameMode);
					if (mode == GameModeType.None) {
						RefuseJoin("This game mode is not available on this server.");
						return;
					}
					var address = requested.RoomId.Address ?? new byte[6];
					var data = new GameRoomData {
						Name = string.IsNullOrWhiteSpace(meta.Name) ? member.CmuneMemberView.PublicProfile.Name + " game" : meta.Name,
						Server = new ConnectionAddress($"{address[0]}.{address[1]}.{address[2]}.{address[3]}", (ushort)(address[4] | (address[5] << 8))),
						MapID = meta.MapId,
						GameMode = mode,
						PlayerLimit = Math.Max(2, (int)meta.MaxPlayers),
						TimeLimit = Math.Max(60, meta.TimeLimitSeconds),
						KillLimit = Math.Max(1, meta.SplatLimit),
						GameFlags = meta.GameFlags & 0x1F,
						LevelMin = meta.LevelMin,
						LevelMax = meta.LevelMax,
					};
					pcRoom = manager.CreateRoom(data, meta.Password);
					Log.Info($"[{Name}] cmid {Cmid}: created {pcRoom}({pcRoom.RoomId}) map {data.MapID} mode {mode}");
				} else if (!manager.TryGetRoom(requested.RoomId.Number, out pcRoom)) {
					RefuseJoin("The game doesn't exist anymore.");
					return;
				}

				if (pcRoom.MetaData.ConnectedPlayers >= pcRoom.MetaData.PlayerLimit) {
					RefuseJoin("The game is full.");
					return;
				}

				var proxy = LegacyMatchProxy.Create(token, member);
				var roomId = new LegacyRoomId { Number = pcRoom.RoomId, Address = requested.RoomId.Address ?? new byte[6] };
				var bridge = new LegacyMatchBridge(this, proxy, pcRoom, roomId);
				pcRoom.Join(proxy);
				proxy.Actor.ActorInfo.Channel = ChannelType.Android;
				lock (stateLock) match = bridge;

				var response = new OperationResponse(LegacyOperationCode.PhotonGameJoin) { ReturnCode = 0 };
				response.Parameters[LegacyParameterKey.ActorNr] = bridge.MyActorId;
				response.Parameters[LegacyParameterKey.GameId] = roomId.ToBytes();
				response.Parameters[LegacyParameterKey.InitRoom] = false;
				response.Parameters[LegacyParameterKey.ServerTicks] = (long)BaseGameRoom.GetWireTimestamp();
				SendOperationResponse(response, new SendParameters());
				Log.Info($"[{Name}] cmid {Cmid}: joined {pcRoom}({pcRoom.RoomId}) as actor {bridge.MyActorId}");
			} catch (Exception ex) {
				Log.Error($"[{Name}] cmid {Cmid}: joining a match failed", ex);
				RefuseJoin("Could not join the game.");
			}
		}

		private void LeaveMatch() {
			LegacyMatchBridge current;
			lock (stateLock) { current = match; match = null; }
			current?.Leave();
		}

		/// <summary>Game-mode RPCs from the phone (MessageToServer / All / Others / Player).</summary>
		private void RouteGameModeCall(OperationRequest request) {
			LegacyMatchBridge current;
			lock (stateLock) current = match;
			if (current == null) return;
			if (!request.Parameters.TryGetValue(LegacyParameterKey.InstanceId, out var idObj) || !(idObj is short networkId)) return;
			if (!request.Parameters.TryGetValue(LegacyParameterKey.MethodId, out var mObj) || !(mObj is byte methodId)) return;
			if (networkId != current.ModeNetworkId) return;

			object[] args;
			try {
				request.Parameters.TryGetValue(LegacyParameterKey.Bytes, out var raw);
				args = LegacySerialization.ToObjects(raw as byte[]);
			} catch (LegacyProtocolException ex) {
				Log.Debug($"[{Name}] cmid {Cmid}: unreadable game-mode call {methodId}: {ex.Message}");
				return;
			}

			switch (request.OperationCode) {
				case LegacyOperationCode.MessageToServer:
					current.OnServerCall(methodId, args);
					break;
				case LegacyOperationCode.MessageToPlayer:
					if (request.Parameters.TryGetValue(LegacyParameterKey.ActorId, out var target) && target is int targetActor) {
						current.OnPlayerCall(targetActor, methodId, args);
					}
					break;
				default:
					current.OnBroadcastCall(methodId, args);
					break;
			}
		}

		protected override void OnNetworkClassRegistered(short networkId) {
			if (networkId != LegacyGameMetaData.ModeTeamDeathMatch && networkId != LegacyGameMetaData.ModeDeathMatch
				&& networkId != LegacyGameMetaData.ModeTeamElimination) return;
			LegacyMatchBridge current;
			lock (stateLock) current = match;
			current?.OnModeRegistered(networkId);
		}

		/// <summary>Call a method of the phone's game-mode class.</summary>
		internal void SendGameModeCall(short networkId, byte methodId, object[] args) => SendNetworkCall(networkId, methodId, args);

		private static GameModeType ToPcGameMode(short legacyMode) {
			switch (legacyMode) {
				case LegacyGameMetaData.ModeDeathMatch: return GameModeType.DeathMatch;
				case LegacyGameMetaData.ModeTeamDeathMatch: return GameModeType.TeamDeathMatch;
				case LegacyGameMetaData.ModeTeamElimination: return GameModeType.EliminationMode;
				default: return GameModeType.None;
			}
		}

		// ------------------------------------------------------------------ room list

		private void StartRoomList(LegacyRoomId lobby) {
			lock (stateLock) {
				lobbyRoomId = lobby;
				refreshTimer?.Dispose();
				refreshTimer = new Timer(_ => SendRoomList(), null, 0, RefreshMilliseconds);
			}
		}

		private void StopRoomList() {
			lock (stateLock) {
				refreshTimer?.Dispose();
				refreshTimer = null;
				lobbyRoomId = null;
			}
		}

		private void SendRoomList() {
			LegacyRoomId lobby;
			lock (stateLock) lobby = lobbyRoomId;
			if (lobby == null) return;

			try {
				var rooms = BuildRoomList(GameServerApplication.Instance.RoomManager.Rooms.Values.Select(r => r.MetaData).ToList(), lobby.Address);
				var table = new Hashtable { { LegacyParameterKey.LobbyRoomUpdate, LegacySerialization.ToBytes(rooms) } };
				SendEvent(new EventData(LegacyEventCode.RoomListUpdate, new Dictionary<byte, object> {
					{ LegacyParameterKey.Data, table }
				}), new SendParameters());
			} catch (Exception ex) {
				Log.Warn($"[{Name}] cmid {Cmid}: failed to send room list", ex);
			}
		}

		/// <summary>PC rooms as 4.3.10 GameMetaData. Password rooms and unknown modes are left out.</summary>
		public static List<LegacyRoomMetaData> BuildRoomList(IEnumerable<GameRoomData> pcRooms, byte[] serverAddress) {
			var list = new List<LegacyRoomMetaData>();
			foreach (var pc in pcRooms) {
				if (pc == null || pc.IsPasswordProtected) continue;
				var mode = ToLegacyGameMode(pc.GameMode);
				if (mode == 0) continue;
				list.Add(new LegacyGameMetaData {
					Name = Truncate(pc.Name, 64),
					Password = "",
					MaxPlayers = ClampByte(pc.PlayerLimit),
					InPlayers = ClampByte(pc.ConnectedPlayers),
					Tag = 0,
					RoomId = new LegacyRoomId { Number = pc.Number, Address = serverAddress ?? new byte[6] },
					MapId = (short)pc.MapID,
					TimeLimitSeconds = pc.TimeLimit,
					SplatLimit = pc.KillLimit,
					GameMode = mode,
					GameFlags = pc.GameFlags & 0x1F, // LowGravity .. CannonArena exist in 4.3.10
					LevelMin = ClampByte(pc.LevelMin),
					LevelMax = ClampByte(pc.LevelMax),
					PlayersInGame = pc.ConnectedPlayers,
				});
			}
			return list;
		}

		public static short ToLegacyGameMode(GameModeType mode) {
			switch (mode) {
				case GameModeType.DeathMatch: return LegacyGameMetaData.ModeDeathMatch;
				case GameModeType.TeamDeathMatch: return LegacyGameMetaData.ModeTeamDeathMatch;
				case GameModeType.EliminationMode: return LegacyGameMetaData.ModeTeamElimination;
				default: return 0;
			}
		}

		private static byte ClampByte(int value) => (byte)Math.Max(0, Math.Min(255, value));

		private static string Truncate(string value, int max) =>
			string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value.Substring(0, max));

		// ------------------------------------------------------------------ application methods

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

		protected override void OnDisconnect(DisconnectReason reasonCode, string reasonDetail) {
			StopRoomList();
			LeaveMatch();
			base.OnDisconnect(reasonCode, reasonDetail);
		}
	}
}
