using Cmune.DataCenter.Common.Entities;
using log4net;
using NekoNexus.Realtime.Server.Game;
using Photon.SocketServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UberStrike.Core.Models;
using UberStrike.Core.Serialization;
using UberStrike.Core.Types;
using UberStrike.Core.ViewModel;
using UberStrike.Realtime.Client;
using UberStrike.Realtime.UnitySdk;
using UnityEngine;
using static NekoNexus.WebSocket;
using BaseGameRoom = NekoNexus.Realtime.Server.Game.BaseGameRoom;
using GameActorInfo = UberStrike.Core.Models.GameActorInfo;

namespace NekoNexus.Realtime.Server.Legacy {
	/// <summary>
	/// Server-side stand-in for an Android (4.3.10) player inside a normal PC game room. It is a real
	/// <see cref="GamePeer"/> (like an AI bot), so the room runs every rule - spawning, damage, scoring,
	/// statistics - exactly as for a PC player, and PC players see it as an ordinary opponent. It has
	/// no network connection of its own: the room's events are handed to <see cref="LegacyMatchBridge"/>,
	/// which translates them into 4.3.10 calls for the phone.
	/// </summary>
	public sealed class LegacyMatchProxy : GamePeer {
		internal LegacyMatchBridge Bridge;

		private LegacyMatchProxy(InitRequest initRequest) : base(initRequest) { }

		public static LegacyMatchProxy Create(string authToken, UberstrikeUserViewModel member) {
			var initRequest = new InitRequest(ApiVersion.Current, new PeerConfiguration(
				heartbeatInterval: 30,
				heartbeatTimeout: 30,
				compositeHashes: new List<byte[]>(),
				junkHashes: new List<byte[]>()
			) { EnableHashVerification = false });

			var proxy = new LegacyMatchProxy(initRequest);
			proxy.AuthToken = authToken;
			proxy.Member = member;
			return proxy;
		}

		public override SendResult SendEvent(EventData eventData, SendParameters sendParameters) {
			if (eventData?.Parameters != null && eventData.Parameters.TryGetValue(0, out var payload) && payload is byte[] bytes) {
				Bridge?.OnRoomEvent(eventData.Code, bytes);
			}
			return SendResult.Ok;
		}
	}

	/// <summary>
	/// Two-way translation between one Android player's 4.3.10 game-mode RPCs and the PC room their
	/// <see cref="LegacyMatchProxy"/> plays in.
	///
	/// Ids: the 4.3.10 "actor id" and "player number" of a player are both PC PlayerId + 1 (they must be
	/// greater than 0 on 4.3.10). Positions: 4.3.10 ShortVector3 is x*10, y*100, z*10; PC handles real
	/// Vector3 values in memory, so conversions happen only at the 4.3.10 edge.
	/// </summary>
	public sealed class LegacyMatchBridge {
		private static readonly ILog Log = LogManager.GetLogger(nameof(LegacyMatchBridge));

		// ---- 4.3.10 RPC ids (ClientGameMode / FpsGameMode / mode classes) -----------------------
		private static class Rpc {
			public const byte Join = 1, Leave = 2, PlayerUpdate = 3, FullPlayerListUpdate = 4, GameFrameUpdate = 5, ResetPlayer = 6;
			public const byte Begin = 21, End = 22;
			public const byte TeamChange = 54, PowerUpPicked = 61, SetPowerUpCount = 62, PlayerHit = 68, SetSpawnPoints = 70;
			public const byte SetNextSpawnPoint = 71, RequestRespawn = 72, ReadyForNextRound = 74, EndOfRoundCountdown = 75;
			public const byte MatchStart = 76, MatchEnd = 77, DamageEvent = 78, UpdateSplatCount = 79, SplatGameEvent = 80;
			public const byte TeamBalanceUpdate = 81, PositionUpdate = 83, DoorOpen = 84, SetPlayerSpawnPosition = 85;
			public const byte EmitProjectile = 86, RemoveProjectile = 87, SingleBulletFire = 89;
			public const byte SetPowerupState = 97, IncreaseHealthAndArmor = 98, QuickItemEvent = 99, EmitQuickItem = 100;
			public const byte KickFromGame = 101, SetShotCounts = 102;
			// TeamEliminationGameMode (106)
			public const byte PlayerSpectator = 51, TeamEliminationRoundEnd = 58, GraceTimeCountDown = 90;
			public const byte UpdateRoundStats = 91, SyncRoundTime = 92, SetWaitingForPlayers = 93;
		}

		private readonly LegacyGamePeer owner;
		private readonly LegacyMatchProxy proxy;
		private readonly BaseGameRoom room;
		private readonly LegacyRoomId roomId;
		private readonly object sync = new object();
		private readonly System.Random random = new System.Random();

		/// <summary>Network id of the game-mode class the phone registered (100 TDM, 101 DM, 106 TE).</summary>
		public short ModeNetworkId { get; private set; }
		public BaseGameRoom Room => room;

		// Mirror of every player's PC state, by PC PlayerId, kept current from AllPlayers / deltas.
		private readonly Dictionary<byte, GameActorInfo> actors = new Dictionary<byte, GameActorInfo>();
		private readonly Dictionary<byte, PlayerMovement> movements = new Dictionary<byte, PlayerMovement>();
		// Rotation/keys/move state last sent to the phone, so 5 GameFrameUpdate only carries changes.
		private readonly Dictionary<byte, int> lastLookSent = new Dictionary<byte, int>();

		// What the phone told us about itself.
		private readonly Dictionary<TeamID, int> spawnPointCounts = new Dictionary<TeamID, int>();
		private Vector3 lastPosition;
		private DateTime lastPositionTime = DateTime.MinValue;
		private byte hrot, vrot, legacyKeys;
		private ushort legacyMoveState = (ushort)LegacyPlayerStates.Grounded;
		private bool gameBegun;

		// Bridges on this game server by Android CMID, for chat relayed from the comm server.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, LegacyMatchBridge> ByCmid =
			new System.Collections.Concurrent.ConcurrentDictionary<int, LegacyMatchBridge>();

		public LegacyMatchBridge(LegacyGamePeer owner, LegacyMatchProxy proxy, BaseGameRoom room, LegacyRoomId roomId) {
			this.owner = owner;
			this.proxy = proxy;
			this.room = room;
			this.roomId = roomId;
			proxy.Bridge = this;
			ByCmid[owner.Cmid] = this;
		}

		/// <summary>In-match chat from an Android player (relayed from the comm server): post it in their room.</summary>
		public static void RouteChat(int cmid, string message, byte context) {
			if (string.IsNullOrEmpty(message) || !ByCmid.TryGetValue(cmid, out var bridge)) return;
			bridge.Op(IGameRoomOperationsType.ChatMessage, s => {
				StringProxy.Serialize(s, message);
				ByteProxy.Serialize(s, context);
			});
		}

		private byte MyPlayerId => proxy.Actor?.ActorInfo?.PlayerId ?? 0;
		public int MyActorId => MyPlayerId + 1;
		private static int ActorIdOf(byte playerId) => playerId + 1;

		// ============================================================== phone -> room

		/// <summary>The phone's game mode registered: the match can now be driven.</summary>
		public void OnModeRegistered(short networkId) {
			ModeNetworkId = networkId;
			lock (sync) {
				if (gameBegun) return;
				gameBegun = true;
			}
			// ClientGameMode.OnStartGame (21): IsGameStarted gates position sending and interpolation.
			Log.Info($"cmid {owner.Cmid}: game mode {networkId} registered, sent Begin (room state {room.State.CurrentStateId})");
			Call(Rpc.Begin);
			// If a round is already running, start the client's match clock right away.
			if (room.RoundEndTime != int.MinValue && room.State.CurrentStateId == GameStateId.MatchRunning) {
				Call(Rpc.MatchStart, room.RoundNumber, room.RoundEndTime);
			}
		}

		/// <summary>MessageToServer to the game-mode class.</summary>
		public void OnServerCall(byte methodId, object[] args) {
			switch (methodId) {
				case Rpc.Join: HandleJoin(args); break;
				case Rpc.Leave: Leave(); break;
				case Rpc.PlayerUpdate: if (args.Length > 0 && args[0] is LegacySyncObject delta) HandlePlayerUpdate(delta); break;
				case Rpc.ResetPlayer: break; // sent after the phone spawned itself; nothing to do
				case Rpc.SetSpawnPoints:
					// (byte none, byte red, byte blue): how many spawn points this map has per team.
					if (args.Length >= 3) {
						spawnPointCounts[TeamID.NONE] = Convert.ToInt32(args[0]);
						spawnPointCounts[TeamID.RED] = Convert.ToInt32(args[1]);
						spawnPointCounts[TeamID.BLUE] = Convert.ToInt32(args[2]);
					}
					break;
				case Rpc.PositionUpdate:
					if (args.Length > 0 && args[0] is byte[] raw) HandlePosition(raw);
					break;
				case Rpc.SetPlayerSpawnPosition:
					if (args.Length > 1 && args[1] is LegacyVector3 spawn) {
						lastPosition = ToUnity(spawn);
						lastPositionTime = DateTime.UtcNow;
						SendMovement(Vector3.zero);
					}
					break;
				case Rpc.PlayerHit: HandleHit(args); break;
				case Rpc.RequestRespawn: Op(IGameRoomOperationsType.RespawnRequest, _ => { }); break;
				case Rpc.ReadyForNextRound: Op(IGameRoomOperationsType.IsReadyForNextMatch, s => BooleanProxy.Serialize(s, true)); break;
				case Rpc.TeamChange: Op(IGameRoomOperationsType.SwitchTeam, _ => { }); break;
				case Rpc.PowerUpPicked:
					// (int actorId, int pickupId, byte type, byte value)
					if (args.Length >= 4) Op(IGameRoomOperationsType.PowerUpPicked, s => {
						Int32Proxy.Serialize(s, Convert.ToInt32(args[1]));
						ByteProxy.Serialize(s, Convert.ToByte(args[2]));
						ByteProxy.Serialize(s, Convert.ToByte(args[3]));
					});
					break;
				case Rpc.IncreaseHealthAndArmor:
					// (int actorId, byte health, byte armor)
					if (args.Length >= 3) Op(IGameRoomOperationsType.IncreaseHealthAndArmor, s => {
						ByteProxy.Serialize(s, Convert.ToByte(args[1]));
						ByteProxy.Serialize(s, Convert.ToByte(args[2]));
					});
					break;
				case Rpc.DoorOpen:
					if (args.Length >= 1) Op(IGameRoomOperationsType.OpenDoor, s => Int32Proxy.Serialize(s, Convert.ToInt32(args[0])));
					break;
				case Rpc.SetPowerUpCount:
					// (int actorId, List<byte> respawn seconds per pickup) - no positions in 4.3.10.
					if (args.Length >= 2 && args[1] is byte[] durations) {
						room.LoadPowerUpsWithoutPositions(durations.Select(d => (ushort)d).ToList());
					}
					break;
				case Rpc.SetShotCounts:   // anti-cheat shot counters
					break;
				default:
					Log.Debug($"cmid {owner.Cmid}: game-mode method {methodId} not translated");
					break;
			}
		}

		/// <summary>MessageToAll / MessageToOthers from the phone: effects other players must see.</summary>
		public void OnBroadcastCall(byte methodId, object[] args) {
			switch (methodId) {
				case Rpc.SingleBulletFire:
					Op(IGameRoomOperationsType.SingleBulletFire, _ => { });
					break;
				case Rpc.EmitProjectile:
					// (int actorId, Vector3 origin, Vector3 direction, byte slot, int projectileId, bool explode)
					if (args.Length >= 6 && args[1] is LegacyVector3 origin && args[2] is LegacyVector3 direction) {
						Op(IGameRoomOperationsType.EmitProjectile, s => {
							Vector3Proxy.Serialize(s, ToUnity(origin));
							Vector3Proxy.Serialize(s, ToUnity(direction));
							ByteProxy.Serialize(s, Convert.ToByte(args[3]));
							Int32Proxy.Serialize(s, Convert.ToInt32(args[4]));
							BooleanProxy.Serialize(s, Convert.ToBoolean(args[5]));
						});
					}
					break;
				case Rpc.RemoveProjectile:
					// (int projectileId, bool explode)
					if (args.Length >= 2) Op(IGameRoomOperationsType.RemoveProjectile, s => {
						Int32Proxy.Serialize(s, Convert.ToInt32(args[0]));
						BooleanProxy.Serialize(s, Convert.ToBoolean(args[1]));
					});
					break;
				case Rpc.QuickItemEvent:
					// (int actorId, byte logic, int robotLifeTime, int scrapsLifeTime, bool isInstant)
					if (args.Length >= 5) Op(IGameRoomOperationsType.ActivateQuickItem, s => {
						EnumProxy<QuickItemLogic>.Serialize(s, (QuickItemLogic)Convert.ToInt32(args[1]));
						Int32Proxy.Serialize(s, Convert.ToInt32(args[2]));
						Int32Proxy.Serialize(s, Convert.ToInt32(args[3]));
						BooleanProxy.Serialize(s, Convert.ToBoolean(args[4]));
					});
					break;
				case Rpc.EmitQuickItem:
					// (Vector3 origin, Vector3 direction, int itemId, byte playerNumber, int projectileId)
					if (args.Length >= 5 && args[0] is LegacyVector3 qo && args[1] is LegacyVector3 qd) {
						Op(IGameRoomOperationsType.EmitQuickItem, s => {
							Vector3Proxy.Serialize(s, ToUnity(qo));
							Vector3Proxy.Serialize(s, ToUnity(qd));
							Int32Proxy.Serialize(s, Convert.ToInt32(args[2]));
							ByteProxy.Serialize(s, MyPlayerId);
							Int32Proxy.Serialize(s, Convert.ToInt32(args[4]));
						});
					}
					break;
				default:
					Log.Debug($"cmid {owner.Cmid}: broadcast method {methodId} not translated");
					break;
			}
		}

		/// <summary>MessageToPlayer from the phone (hit feedback: knockback force on the target).</summary>
		public void OnPlayerCall(int targetActorId, byte methodId, object[] args) {
			if (methodId == Rpc.PlayerHit && args.Length >= 1 && args[0] is LegacyVector3 force && TryGetCmid(targetActorId, out var targetCmid)) {
				Op(IGameRoomOperationsType.HitFeedback, s => {
					Int32Proxy.Serialize(s, targetCmid);
					Vector3Proxy.Serialize(s, ToUnity(force));
				});
			}
		}

		private void HandleJoin(object[] args) {
			// (CharacterInfo me): the team the player picked on the join screen.
			var team = TeamID.NONE;
			if (args.Length > 0 && args[0] is LegacyCharacterInfo info && info.Sync[LegacyCharField.TeamID] is byte t) {
				team = (TeamID)t;
			}
			room.EnsureSpawnPoints(team);
			Log.Info($"cmid {owner.Cmid}: joins the match in {room}({room.RoomId}) on team {team}");
			Op(IGameRoomOperationsType.JoinGame, s => EnumProxy<TeamID>.Serialize(s, team));
		}

		private void HandlePlayerUpdate(LegacySyncObject delta) {
			var moved = false;
			if (delta[LegacyCharField.HorizontalRotation] is byte h) { hrot = h; moved = true; }
			if (delta[LegacyCharField.VerticalRotation] is byte v) { vrot = v; moved = true; }
			if (delta[LegacyCharField.Keys] is byte k) { legacyKeys = k; moved = true; }
			if (delta[LegacyCharField.MoveState] is ushort ms) { legacyMoveState = ms; moved = true; }
			if (delta[LegacyCharField.CurrentWeaponSlot] is byte slot) {
				Op(IGameRoomOperationsType.SwitchWeapon, s => ByteProxy.Serialize(s, slot));
			}
			if (delta[LegacyCharField.IsFiring] is bool firing) {
				Op(IGameRoomOperationsType.IsFiring, s => BooleanProxy.Serialize(s, firing));
			}
			if (moved) SendMovement(Vector3.zero);
		}

		/// <summary>83: [int actorId][ShortVector3 position][int server time].</summary>
		private void HandlePosition(byte[] raw) {
			if (raw.Length < 14) return;
			var i = 4; // actor id
			var position = ToUnity(LegacyShortVector3.ReadRaw(raw, ref i).Value);
			var now = DateTime.UtcNow;
			var velocity = Vector3.zero;
			if (lastPositionTime != DateTime.MinValue) {
				var dt = (float)(now - lastPositionTime).TotalSeconds;
				if (dt > 0.001f && dt < 1f) velocity = (position - lastPosition) / dt;
			}
			lastPosition = position;
			lastPositionTime = now;
			SendMovement(velocity);
		}

		private void SendMovement(Vector3 velocity) {
			var moveState = ToPcMoveState(legacyMoveState);
			Op(IGameRoomOperationsType.UpdatePositionAndRotation, s => {
				ShortVector3Proxy.Serialize(s, lastPosition);
				ShortVector3Proxy.Serialize(s, velocity);
				ByteProxy.Serialize(s, hrot);
				ByteProxy.Serialize(s, vrot);
				ByteProxy.Serialize(s, moveState);
			});
		}

		/// <summary>
		/// 68: (int myActorId, int targetActorId, short damage, byte bodyPart, int shotCount, byte angle,
		/// int weaponId, byte weaponClass, int damageEffectFlag, float damageEffectValue). The damage the
		/// phone claims is ignored: the PC room computes it from the shooter's weapon, as for PC hits.
		/// </summary>
		private void HandleHit(object[] args) {
			if (args.Length < 4) return;
			var targetActor = Convert.ToInt32(args[1]);
			var bodyPart = Convert.ToByte(args[3]);
			if (targetActor == MyActorId) {
				// Self damage (falling, own explosions).
				var damage = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, (int)Convert.ToInt16(args[2])));
				Op(IGameRoomOperationsType.DirectDamage, s => UInt16Proxy.Serialize(s, damage));
				return;
			}
			if (!TryGetCmid(targetActor, out var targetCmid)) return;
			Op(IGameRoomOperationsType.DirectHitDamage, s => {
				Int32Proxy.Serialize(s, targetCmid);
				ByteProxy.Serialize(s, bodyPart);
				ByteProxy.Serialize(s, 1);
			});
		}

		public void Leave() {
			Log.Info($"cmid {owner.Cmid}: leaving {room}({room.RoomId})");
			if (ByCmid.TryGetValue(owner.Cmid, out var current) && current == this) ByCmid.TryRemove(owner.Cmid, out _);
			try {
				if (proxy.Room != null) proxy.Room.Leave(proxy);
			} catch (Exception ex) {
				Log.Warn($"cmid {owner.Cmid}: leaving room failed", ex);
			}
			proxy.Bridge = null;
		}

		private void Op(IGameRoomOperationsType op, Action<MemoryStream> write) {
			if (proxy.Room == null) return;
			try {
				using (var stream = new MemoryStream()) {
					write(stream);
					room.RunProxyOperation(proxy, (byte)op, stream.ToArray());
				}
			} catch (Exception ex) {
				Log.Warn($"cmid {owner.Cmid}: room operation {op} failed", ex);
			}
		}

		// ============================================================== room -> phone

		private bool IsElimination => ModeNetworkId == LegacyGameMetaData.ModeTeamElimination;

		public void OnRoomEvent(byte code, byte[] payload) {
			if (code < 12) return; // GamePeer events (room entered, game lists ...) are not for 4.3.10
			try {
				using (var s = new MemoryStream(payload)) {
					if (IsElimination && TryTranslateElimination((IGameRoomEventsType)code, s)) return;
					switch ((IGameRoomEventsType)code) {
						case IGameRoomEventsType.AllPlayers: {
							var infos = ListProxy<GameActorInfo>.Deserialize(s, GameActorInfoProxy.Deserialize);
							var moves = ListProxy<PlayerMovement>.Deserialize(s, PlayerMovementProxy.Deserialize);
							lock (sync) {
								foreach (var a in infos) actors[a.PlayerId] = a;
								foreach (var m in moves) movements[m.Number] = m;
							}
							var list = new List<LegacySyncObject>();
							var positions = new List<LegacyVector3>();
							foreach (var a in infos) {
								list.Add(BuildCharacter(a, MovementOf(a.PlayerId)));
								positions.Add(ToLegacy(MovementOf(a.PlayerId)?.Position ?? Vector3.zero));
							}
							Call(Rpc.FullPlayerListUpdate, list, positions);
							break;
						}
						case IGameRoomEventsType.PlayerJoinedGame: {
							var a = GameActorInfoProxy.Deserialize(s);
							var m = PlayerMovementProxy.Deserialize(s);
							lock (sync) { actors[a.PlayerId] = a; movements[a.PlayerId] = m; }
							Call(Rpc.Join, BuildCharacter(a, m), ToLegacy(m.Position));
							break;
						}
						case IGameRoomEventsType.PlayerLeftGame: {
							var cmid = Int32Proxy.Deserialize(s);
							if (TryGetPlayerId(cmid, out var pid)) {
								lock (sync) { actors.Remove(pid); movements.Remove(pid); lastLookSent.Remove(pid); }
								Call(Rpc.Leave, ActorIdOf(pid));
							}
							break;
						}
						case IGameRoomEventsType.AllPlayerPositions: {
							var moves = ListProxy<PlayerMovement>.Deserialize(s, PlayerMovementProxy.Deserialize);
							SendPositions(moves);
							break;
						}
						case IGameRoomEventsType.AllPlayerDeltas: {
							var deltas = ListProxy<GameActorInfoDelta>.Deserialize(s, GameActorInfoDeltaProxy.Deserialize);
							SendDeltas(deltas);
							break;
						}
						case IGameRoomEventsType.PlayerDelta: {
							SendDeltas(new List<GameActorInfoDelta> { GameActorInfoDeltaProxy.Deserialize(s) });
							break;
						}
						case IGameRoomEventsType.PlayerRespawned: {
							var cmid = Int32Proxy.Deserialize(s);
							var position = Vector3Proxy.Deserialize(s);
							if (!TryGetPlayerId(cmid, out var pid)) break;
							if (pid == MyPlayerId) {
								// The phone spawns itself at one of its own spawn points; tell it which.
								Call(Rpc.SetNextSpawnPoint, PickSpawnIndex(), 0);
							} else {
								Call(Rpc.SetPlayerSpawnPosition, (byte)ActorIdOf(pid), ToLegacy(position));
							}
							break;
						}
						case IGameRoomEventsType.MatchStart: {
							var round = Int32Proxy.Deserialize(s);
							var endTime = Int32Proxy.Deserialize(s);
							Log.Info($"cmid {owner.Cmid}: match start (round {round}) -> phone");
							Call(Rpc.MatchStart, round, endTime);
							break;
						}
						case IGameRoomEventsType.MatchEnd: {
							var data = EndOfMatchDataProxy.Deserialize(s);
							Call(Rpc.MatchEnd, BuildEndOfMatch(data));
							break;
						}
						case IGameRoomEventsType.DamageEvent: {
							var ev = DamageEventProxy.Deserialize(s);
							var legacy = new LegacyDamageEvent {
								DamageEffectFlag = ev.DamageEffectFlag,
								DamageEffectValue = ev.DamgeEffectValue,
								BodyPartFlag = ev.BodyPartFlag,
							};
							if (ev.Damage != null) foreach (var d in ev.Damage) legacy.Damage[d.Key] = d.Value;
							Call(Rpc.DamageEvent, legacy);
							break;
						}
						case IGameRoomEventsType.PlayerKilled: {
							var shooter = Int32Proxy.Deserialize(s);
							var target = Int32Proxy.Deserialize(s);
							var weaponClass = ByteProxy.Deserialize(s);
							UInt16Proxy.Deserialize(s);
							var bodyPart = ByteProxy.Deserialize(s);
							if (TryGetPlayerId(shooter, out var sp) && TryGetPlayerId(target, out var tp)) {
								Call(Rpc.SplatGameEvent, ActorIdOf(sp), ActorIdOf(tp), weaponClass, bodyPart);
							}
							break;
						}
						case IGameRoomEventsType.KillsRemaining:
						case IGameRoomEventsType.UpdateRoundScore:
							SendScore(code == (byte)IGameRoomEventsType.UpdateRoundScore ? s : null);
							break;
						case IGameRoomEventsType.SingleBulletFire: {
							var cmid = Int32Proxy.Deserialize(s);
							if (TryGetPlayerId(cmid, out var pid) && pid != MyPlayerId) Call(Rpc.SingleBulletFire, ActorIdOf(pid));
							break;
						}
						case IGameRoomEventsType.EmitProjectile: {
							var cmid = Int32Proxy.Deserialize(s);
							var origin = Vector3Proxy.Deserialize(s);
							var direction = Vector3Proxy.Deserialize(s);
							var slot = ByteProxy.Deserialize(s);
							var id = Int32Proxy.Deserialize(s);
							var explode = BooleanProxy.Deserialize(s);
							if (TryGetPlayerId(cmid, out var pid) && pid != MyPlayerId) {
								Call(Rpc.EmitProjectile, ActorIdOf(pid), ToLegacy(origin), ToLegacy(direction), slot, id, explode);
							}
							break;
						}
						case IGameRoomEventsType.RemoveProjectile: {
							var id = Int32Proxy.Deserialize(s);
							var explode = BooleanProxy.Deserialize(s);
							Call(Rpc.RemoveProjectile, id, explode);
							break;
						}
						case IGameRoomEventsType.EmitQuickItem: {
							var origin = Vector3Proxy.Deserialize(s);
							var direction = Vector3Proxy.Deserialize(s);
							var itemId = Int32Proxy.Deserialize(s);
							var playerNumber = ByteProxy.Deserialize(s);
							var id = Int32Proxy.Deserialize(s);
							if (playerNumber != MyPlayerId) {
								Call(Rpc.EmitQuickItem, ToLegacy(origin), ToLegacy(direction), itemId, (byte)ActorIdOf(playerNumber), id);
							}
							break;
						}
						case IGameRoomEventsType.ActivateQuickItem:
						case IGameRoomEventsType.QuickItemEvent: {
							var cmid = Int32Proxy.Deserialize(s);
							var logic = code == (byte)IGameRoomEventsType.ActivateQuickItem
								? (byte)EnumProxy<QuickItemLogic>.Deserialize(s)
								: ByteProxy.Deserialize(s);
							var robot = Int32Proxy.Deserialize(s);
							var scraps = Int32Proxy.Deserialize(s);
							var instant = BooleanProxy.Deserialize(s);
							if (TryGetPlayerId(cmid, out var pid) && pid != MyPlayerId) {
								Call(Rpc.QuickItemEvent, ActorIdOf(pid), logic, robot, scraps, instant);
							}
							break;
						}
						case IGameRoomEventsType.PlayerHit:
							Call(Rpc.PlayerHit, ToLegacy(Vector3Proxy.Deserialize(s)));
							break;
						case IGameRoomEventsType.PowerUpPicked: {
							var id = Int32Proxy.Deserialize(s);
							var flag = ByteProxy.Deserialize(s);
							Call(Rpc.PowerUpPicked, id, flag);
							break;
						}
						case IGameRoomEventsType.SetPowerupState:
							Call(Rpc.SetPowerupState, ListProxy<int>.Deserialize(s, Int32Proxy.Deserialize));
							break;
						case IGameRoomEventsType.DoorOpen:
							Call(Rpc.DoorOpen, Int32Proxy.Deserialize(s));
							break;
						case IGameRoomEventsType.PlayerChangedTeam: {
							var cmid = Int32Proxy.Deserialize(s);
							var team = EnumProxy<TeamID>.Deserialize(s);
							if (TryGetPlayerId(cmid, out var pid)) Call(Rpc.TeamChange, ActorIdOf(pid), (byte)team);
							break;
						}
						case IGameRoomEventsType.ChatMessage: {
							// Room chat -> the phone's in-game chat, through the comm server it listens on.
							var cmid = Int32Proxy.Deserialize(s);
							var name = StringProxy.Deserialize(s);
							var message = StringProxy.Deserialize(s);
							var access = EnumProxy<MemberAccessLevel>.Deserialize(s);
							var context = ByteProxy.Deserialize(s);
							if (cmid == owner.Cmid) break; // the phone already shows its own line
							var actorId = TryGetPlayerId(cmid, out var pid) ? ActorIdOf(pid) : 0;
							GameServerApplication.Instance.SocketClient?.SendSync(PacketType.LegacyChatDeliver, new Dictionary<string, object> {
								{ "TargetCmid", owner.Cmid }, { "Cmid", cmid }, { "ActorId", actorId }, { "Name", name },
								{ "Message", message }, { "AccessLevel", (int)access }, { "Context", (int)context },
							});
							break;
						}
						case IGameRoomEventsType.KickPlayer:
						case IGameRoomEventsType.JoinGameFailed:
							Call(Rpc.KickFromGame, StringProxy.Deserialize(s));
							break;
						default:
							break; // countdowns, jumps, chat, ready flags: handled by the 4.3.10 client itself
					}
				}
			} catch (Exception ex) {
				Log.Warn($"cmid {owner.Cmid}: could not translate room event {(IGameRoomEventsType)code}", ex);
			}
		}

		/// <summary>
		/// Team Elimination rounds: the 4.3.10 mode has its own round RPCs. Returns true when the event
		/// was handled here (otherwise the generic translation runs).
		/// </summary>
		private bool TryTranslateElimination(IGameRoomEventsType type, MemoryStream s) {
			switch (type) {
				case IGameRoomEventsType.TeamWins:
					Call(Rpc.TeamEliminationRoundEnd, (int)EnumProxy<TeamID>.Deserialize(s));
					return true;
				case IGameRoomEventsType.UpdateRoundScore: {
					var round = Int32Proxy.Deserialize(s);
					int blue = Int16Proxy.Deserialize(s);
					int red = Int16Proxy.Deserialize(s);
					Call(Rpc.UpdateRoundStats, round, blue, red);
					return true;
				}
				case IGameRoomEventsType.WaitingForPlayers:
					Call(Rpc.SetWaitingForPlayers);
					return true;
				case IGameRoomEventsType.MatchStartCountdown:
					Call(Rpc.GraceTimeCountDown, room.RoundNumber, (int)ByteProxy.Deserialize(s));
					return true;
				case IGameRoomEventsType.MatchStart: {
					var round = Int32Proxy.Deserialize(s);
					var endTime = Int32Proxy.Deserialize(s);
					Call(Rpc.MatchStart, round, endTime);
					Call(Rpc.SyncRoundTime, endTime);
					return true;
				}
				case IGameRoomEventsType.JoinedAsSpectator:
					Call(Rpc.PlayerSpectator, MyActorId);
					return true;
				default:
					return false;
			}
		}

		/// <summary>83 OnPositionsUpdate(List&lt;byte&gt;): [byte n] then n x [byte number][int time][ShortVector3].</summary>
		private void SendPositions(List<PlayerMovement> moves) {
			var bytes = new List<byte> { 0 };
			var time = BaseGameRoom.GetWireTimestamp();
			var count = 0;
			var looks = new List<LegacySyncObject>();
			lock (sync) {
				foreach (var m in moves) {
					movements[m.Number] = m;
					if (m.Number == MyPlayerId) continue; // the phone moves itself
					bytes.Add((byte)ActorIdOf(m.Number));
					LegacySerialization.WriteInt(bytes, time);
					new LegacyShortVector3(ToLegacy(m.Position)).WriteRaw(bytes);
					count++;

					// Rotation / keys / move state travel in GameFrameUpdate deltas; send only changes.
					var look = (m.HorizontalRotation << 24) | (m.VerticalRotation << 16) | (m.KeyState << 8) | m.MovementState;
					if (!lastLookSent.TryGetValue(m.Number, out var previous) || previous != look) {
						lastLookSent[m.Number] = look;
						var delta = new LegacySyncObject { Id = ActorIdOf(m.Number) };
						delta[LegacyCharField.HorizontalRotation] = m.HorizontalRotation;
						delta[LegacyCharField.VerticalRotation] = m.VerticalRotation;
						delta[LegacyCharField.Keys] = m.KeyState;
						delta[LegacyCharField.MoveState] = ToLegacyMoveState(m.MovementState, actors.TryGetValue(m.Number, out var a) ? a.PlayerState : PlayerStates.None);
						looks.Add(delta);
					}
				}
			}
			bytes[0] = (byte)count;
			if (count > 0) Call(Rpc.PositionUpdate, bytes.ToArray());
			if (looks.Count > 0) Call(Rpc.GameFrameUpdate, looks);
		}

		/// <summary>5 OnGameFrameUpdate(List&lt;SyncObject&gt;) built from PC actor deltas.</summary>
		private void SendDeltas(List<GameActorInfoDelta> deltas) {
			var list = new List<LegacySyncObject>();
			lock (sync) {
				foreach (var delta in deltas) {
					if (!actors.TryGetValue(delta.Id, out var a)) continue;
					delta.Apply(a);
					var sync = new LegacySyncObject { Id = ActorIdOf(delta.Id) };
					foreach (var key in delta.Changes.Keys) {
						switch (key) {
							case GameActorInfoDelta.Keys.Health: sync[LegacyCharField.Health] = a.Health; break;
							case GameActorInfoDelta.Keys.ArmorPoints:
							case GameActorInfoDelta.Keys.ArmorPointCapacity: sync[LegacyCharField.Armor] = Armor(a); break;
							case GameActorInfoDelta.Keys.Kills:
							case GameActorInfoDelta.Keys.Deaths: sync[LegacyCharField.Stats] = Stats(a); break;
							case GameActorInfoDelta.Keys.TeamID: sync[LegacyCharField.TeamID] = (byte)a.TeamID; break;
							case GameActorInfoDelta.Keys.CurrentWeaponSlot: sync[LegacyCharField.CurrentWeaponSlot] = a.CurrentWeaponSlot; break;
							case GameActorInfoDelta.Keys.CurrentFiringMode: sync[LegacyCharField.CurrentFiringMode] = (byte)a.CurrentFiringMode; break;
							case GameActorInfoDelta.Keys.Weapons: sync[LegacyCharField.Weapons] = Weapons(a); break;
							case GameActorInfoDelta.Keys.Gear: sync[LegacyCharField.Gear] = Gear(a); break;
							case GameActorInfoDelta.Keys.Level: sync[LegacyCharField.Level] = (byte)Math.Min(255, a.Level); break;
							case GameActorInfoDelta.Keys.Ping: sync[LegacyCharField.Ping] = a.Ping; break;
							case GameActorInfoDelta.Keys.PlayerState:
								sync[LegacyCharField.MoveState] = ToLegacyMoveState(MovementOf(delta.Id)?.MovementState ?? 1, a.PlayerState);
								sync[LegacyCharField.IsFiring] = a.IsFiring;
								sync[LegacyCharField.IsReadyForGame] = a.IsReadyForGame;
								break;
						}
					}
					if (sync.Fields.Count > 0) list.Add(sync);
				}
			}
			if (list.Count > 0) Call(Rpc.GameFrameUpdate, list);
		}

		/// <summary>79 OnUpdateSplatCount: DM (short mine, short best other, bool leading); team modes (int blue, int red, bool leading).</summary>
		private void SendScore(MemoryStream roundScore) {
			if (ModeNetworkId == LegacyGameMetaData.ModeDeathMatch) {
				short mine = 0, other = 0;
				lock (sync) {
					foreach (var a in actors.Values) {
						if (a.PlayerId == MyPlayerId) mine = a.Kills;
						else if (a.Kills > other) other = a.Kills;
					}
				}
				Call(Rpc.UpdateSplatCount, mine, other, mine >= other);
			} else if (roundScore != null) {
				Int32Proxy.Deserialize(roundScore); // round
				int blue = Int16Proxy.Deserialize(roundScore);
				int red = Int16Proxy.Deserialize(roundScore);
				var myTeam = proxy.Actor?.Team ?? TeamID.NONE;
				var leading = myTeam == TeamID.BLUE ? blue >= red : red >= blue;
				Call(Rpc.UpdateSplatCount, blue, red, leading);
			}
		}

		// ============================================================== 4.3.10 data builders

		private LegacySyncObject BuildCharacter(GameActorInfo a, PlayerMovement m) {
			var actorId = ActorIdOf(a.PlayerId);
			var c = new LegacySyncObject { Id = actorId };
			c[LegacyCharField.Cmid] = a.Cmid;
			c[LegacyCharField.PlayerName] = a.PlayerName ?? "";
			c[LegacyCharField.Channel] = (byte)a.Channel;
			c[LegacyCharField.CurrentRoom] = roomId;
			c[LegacyCharField.Ping] = a.Ping;
			c[LegacyCharField.ClanTag] = a.ClanTag ?? "";
			c[LegacyCharField.AccessLevel] = (byte)a.AccessLevel;
			c[LegacyCharField.IsFiring] = a.IsFiring;
			c[LegacyCharField.MoveState] = ToLegacyMoveState(m?.MovementState ?? 1, a.PlayerState);
			c[LegacyCharField.HorizontalRotation] = m?.HorizontalRotation ?? (byte)0;
			c[LegacyCharField.VerticalRotation] = m?.VerticalRotation ?? (byte)0;
			c[LegacyCharField.QuickItems] = Fixed(a.QuickItems, 3);
			c[LegacyCharField.Stats] = Stats(a);
			c[LegacyCharField.FunctionalItems] = Fixed(a.FunctionalItems, 3);
			c[LegacyCharField.Level] = (byte)Math.Max(1, Math.Min(255, a.Level));
			c[LegacyCharField.TeamID] = (byte)a.TeamID;
			c[LegacyCharField.SurfaceSound] = (byte)a.StepSound;
			c[LegacyCharField.Keys] = m?.KeyState ?? (byte)0;
			c[LegacyCharField.Health] = a.Health;
			c[LegacyCharField.SkinColor] = new LegacyColor(a.SkinColor.r, a.SkinColor.g, a.SkinColor.b);
			c[LegacyCharField.Weapons] = Weapons(a);
			c[LegacyCharField.CurrentWeaponSlot] = a.CurrentWeaponSlot;
			c[LegacyCharField.CurrentFiringMode] = (byte)a.CurrentFiringMode;
			c[LegacyCharField.Armor] = Armor(a);
			c[LegacyCharField.Gear] = Gear(a);
			c[LegacyCharField.IsReadyForGame] = a.IsReadyForGame;
			c[LegacyCharField.PlayerNumber] = (byte)actorId;
			return c;
		}

		private static List<int> Fixed(List<int> source, int length) {
			var list = new List<int>(source ?? new List<int>());
			while (list.Count < length) list.Add(0);
			return list.Take(length).ToList();
		}

		private static LegacyArmorInfo Armor(GameActorInfo a) =>
			new LegacyArmorInfo { ArmorPoints = a.ArmorPoints, ArmorPointCapacity = a.ArmorPointCapacity };

		private static LegacyStatsInfo Stats(GameActorInfo a) =>
			new LegacyStatsInfo { Kills = a.Kills, Deaths = a.Deaths };

		/// <summary>PC Gear is [holo, head, face, gloves, upper, lower, boots]; 4.3.10 GearLoadout reads the reverse.</summary>
		private static List<int> Gear(GameActorInfo a) {
			var gear = Fixed(a.Gear, 7);
			gear.Reverse();
			return gear;
		}

		private LegacyWeaponInfo Weapons(GameActorInfo a) {
			var info = new LegacyWeaponInfo();
			var weapons = a.Weapons ?? new List<int>();
			for (var k = 0; k < 4 && k < weapons.Count; k++) {
				info.ItemIds[k] = weapons[k];
				if (weapons[k] != 0 && room.ShopManager.WeaponItems.TryGetValue(weapons[k], out var view)) {
					info.Categories[k] = (byte)view.ItemClass;
				}
			}
			return info;
		}

		/// <summary>4.3.10 EndOfMatchData (type 109).</summary>
		private static readonly string[] StatOrder = {
			"Headshots", "Nutshots", "ConsecutiveSnipes", "Xp", "Deaths", "DamageReceived", "ArmorPickedUp", "HealthPickedUp",
			"MeleeKills", "MeleeShotsFired", "MeleeShotsHit", "MeleeDamageDone", "HandgunKills", "HandgunShotsFired", "HandgunShotsHit", "HandgunDamageDone",
			"MachineGunKills", "MachineGunShotsFired", "MachineGunShotsHit", "MachineGunDamageDone", "ShotgunSplats", "ShotgunShotsFired", "ShotgunShotsHit", "ShotgunDamageDone",
			"SniperKills", "SniperShotsFired", "SniperShotsHit", "SniperDamageDone", "SplattergunKills", "SplattergunShotsFired", "SplattergunShotsHit", "SplattergunDamageDone",
			"CannonKills", "CannonShotsFired", "CannonShotsHit", "CannonDamageDone", "LauncherKills", "LauncherShotsFired", "LauncherShotsHit", "LauncherDamageDone",
			"Suicides", "Points",
		};

		private static void WriteStats(List<byte> bytes, StatsCollection stats) {
			foreach (var name in StatOrder) {
				var value = 0;
				if (stats != null) {
					var prop = typeof(StatsCollection).GetProperty(name) ?? typeof(StatsCollection).GetProperty(name.Replace("Splats", "Kills"));
					if (prop != null) value = Convert.ToInt32(prop.GetValue(stats, null));
				}
				LegacySerialization.WriteInt(bytes, value);
			}
		}

		private static LegacyRawValue BuildEndOfMatch(EndOfMatchData data) {
			var raw = new LegacyRawValue { Type = LegacySerialization.Type.EndOfMatchData };
			var b = raw.Payload;
			LegacySerialization.WriteInt(b, data?.MostEffecientWeaponId ?? 0);
			var mvps = (data?.MostValuablePlayers ?? new List<StatsSummary>()).Take(255).ToList();
			b.Add((byte)mvps.Count);
			foreach (var p in mvps) {
				LegacySerialization.WriteString(b, p.Name ?? "");
				LegacySerialization.WriteInt(b, p.Kills);
				LegacySerialization.WriteInt(b, p.Deaths);
				b.Add((byte)Math.Max(0, Math.Min(255, p.Level)));
				b.Add((byte)p.Team);
				LegacySerialization.WriteInt(b, p.Cmid);
				var achievements = (p.Achievements ?? new Dictionary<byte, ushort>()).Take(254).ToList();
				LegacySerialization.WriteInt(b, achievements.Count);          // FromByteCollection: [int count][bytes]
				foreach (var kv in achievements) b.Add(kv.Key);
				b.Add((byte)achievements.Count);                              // FromUShortCollection: [byte count][ushorts]
				foreach (var kv in achievements) LegacySerialization.WriteUShort(b, kv.Value);
			}
			WriteStats(b, data?.PlayerStatsTotal);
			WriteStats(b, data?.PlayerStatsBestPerLife);
			var xp = (data?.PlayerXpEarned ?? new Dictionary<byte, ushort>()).Take(254).ToList();
			LegacySerialization.WriteInt(b, xp.Count);
			foreach (var kv in xp) b.Add(kv.Key);
			b.Add((byte)xp.Count);
			foreach (var kv in xp) LegacySerialization.WriteUShort(b, kv.Value);
			return raw;
		}

		// ============================================================== helpers

		private bool warnedNoMode;

		private void Call(byte methodId, params object[] args) {
			if (ModeNetworkId == 0) {
				if (!warnedNoMode) { warnedNoMode = true; Log.Warn($"cmid {owner.Cmid}: room event dropped - the phone has not registered its game mode yet (method {methodId})"); }
				return;
			}
			owner.SendGameModeCall(ModeNetworkId, methodId, args);
		}

		private PlayerMovement MovementOf(byte playerId) {
			lock (sync) return movements.TryGetValue(playerId, out var m) ? m : null;
		}

		private bool TryGetPlayerId(int cmid, out byte playerId) {
			lock (sync) {
				foreach (var a in actors.Values) {
					if (a.Cmid == cmid) { playerId = a.PlayerId; return true; }
				}
			}
			// The room may know players this bridge has not seen an event for yet.
			var peer = room.Peers.FirstOrDefault(p => p.Actor?.Cmid == cmid);
			playerId = peer?.Actor?.ActorInfo?.PlayerId ?? 0;
			return peer?.Actor != null;
		}

		private bool TryGetCmid(int actorId, out int cmid) {
			var playerId = actorId - 1;
			lock (sync) {
				foreach (var a in actors.Values) {
					if (a.PlayerId == playerId) { cmid = a.Cmid; return true; }
				}
			}
			var peer = room.Peers.FirstOrDefault(p => p.Actor?.ActorInfo?.PlayerId == playerId);
			cmid = peer?.Actor?.Cmid ?? 0;
			return peer?.Actor != null;
		}

		private int PickSpawnIndex() {
			var team = proxy.Actor?.Team ?? TeamID.NONE;
			var count = spawnPointCounts.TryGetValue(team, out var c) && c > 0 ? c
				: spawnPointCounts.TryGetValue(TeamID.NONE, out var n) && n > 0 ? n : 1;
			return random.Next(count);
		}

		private static Vector3 ToUnity(LegacyVector3 v) => new Vector3(v.X, v.Y, v.Z);
		private static LegacyVector3 ToLegacy(Vector3 v) => new LegacyVector3(v.x, v.y, v.z);

		/// <summary>PC MoveStates (Grounded 1, Jumping 2, Flying 4, Ducked 8, Wading 16, Swimming 32, Diving 64) + PlayerStates -> 4.3.10 PlayerStates.</summary>
		private static ushort ToLegacyMoveState(byte pcMove, PlayerStates pcState) {
			var m = (MoveStates)pcMove;
			var l = LegacyPlayerStates.Idle;
			if ((m & MoveStates.Grounded) != 0) l |= LegacyPlayerStates.Grounded;
			if ((m & MoveStates.Jumping) != 0) l |= LegacyPlayerStates.Jumping;
			if ((m & MoveStates.Flying) != 0) l |= LegacyPlayerStates.Flying;
			if ((m & MoveStates.Ducked) != 0) l |= LegacyPlayerStates.Ducked;
			if ((m & MoveStates.Wading) != 0) l |= LegacyPlayerStates.Wading;
			if ((m & MoveStates.Swimming) != 0) l |= LegacyPlayerStates.Swimming;
			if ((m & MoveStates.Diving) != 0) l |= LegacyPlayerStates.Diving;
			if ((pcState & PlayerStates.Dead) != 0) l |= LegacyPlayerStates.Dead;
			if ((pcState & PlayerStates.Paused) != 0) l |= LegacyPlayerStates.Paused;
			if ((pcState & PlayerStates.Spectator) != 0) l |= LegacyPlayerStates.Spectator;
			return (ushort)l;
		}

		private static byte ToPcMoveState(ushort legacy) {
			var l = (LegacyPlayerStates)legacy;
			MoveStates m = 0;
			if ((l & LegacyPlayerStates.Grounded) != 0) m |= MoveStates.Grounded;
			if ((l & LegacyPlayerStates.Jumping) != 0) m |= MoveStates.Jumping;
			if ((l & LegacyPlayerStates.Flying) != 0) m |= MoveStates.Flying;
			if ((l & LegacyPlayerStates.Ducked) != 0) m |= MoveStates.Ducked;
			if ((l & LegacyPlayerStates.Wading) != 0) m |= MoveStates.Wading;
			if ((l & LegacyPlayerStates.Swimming) != 0) m |= MoveStates.Swimming;
			if ((l & LegacyPlayerStates.Diving) != 0) m |= MoveStates.Diving;
			return (byte)m;
		}

		/// <summary>The live web-service session of an Android player, from the web service over the master socket.</summary>
		public static string FetchSessionToken(int cmid) {
			try {
				var reply = GameServerApplication.Instance.SocketClient?.SendSync(PacketType.LegacySession,
					new Dictionary<string, object> { { "Cmid", cmid } }, oneWay: false);
				if (reply is IDictionary<string, object> dict && dict.TryGetValue("SessionId", out var id)) return id?.ToString() ?? "";
			} catch (Exception ex) {
				Log.Warn($"cmid {cmid}: could not fetch session token", ex);
			}
			return "";
		}
	}
}
