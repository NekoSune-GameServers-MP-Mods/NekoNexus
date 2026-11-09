using log4net;
using Photon.SocketServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using static NekoNexus.WebSocket;

namespace NekoNexus.Realtime.Server.Game {
	public class GameServerApplication : BaseRealtimeApplication {
		protected static readonly new ILog Log = LogManager.GetLogger(nameof(GameServerApplication));
		protected static readonly ILog ChatLog = LogManager.GetLogger("ChatLog");

		public static new GameServerApplication Instance => (GameServerApplication)ApplicationBase.Instance;
		public override ServerType ServerType => ServerType.Game;
		public GameRoomManager RoomManager { get; private set; } = new GameRoomManager();

		// Global coin/xp boost event, set from admin and pushed over the master socket (SetBoost).
		// Read at match-end scoring (StatisticsManager). Volatile reference so the socket thread's
		// swap is visible to match threads immediately.
		private static volatile BoostState boost = new BoostState();
		public static BoostState Boost => boost;

		// Admin-controlled AI fill-bots toggle (BETA), set from admin and pushed over the master
		// socket (SetBotsConfig). Read by BaseGameRoom.Bots.cs. Off by default until admin (or the
		// pushed value from ws on connect) enables it.
		private static volatile BotsConfigState botsConfig = new BotsConfigState();
		public static BotsConfigState BotsConfig => botsConfig;

		private static readonly ProfanityFilter.ProfanityFilter ProfanityFilter = new ProfanityFilter.ProfanityFilter();

		// Periodically pushes the live room/player list to the master so the in-game server
		// browser and admin dashboard update in realtime (not just once on connect).
		private System.Threading.Timer monitoringTimer;

		public override int Peers {
			get {
				var count = 0;
				foreach (var room in RoomManager.Rooms.Values) {
					count += room.Peers.Count;
				}
				return count;
			}
		}

		public int Players {
			get {
				var count = 0;
				foreach (var room in RoomManager.Rooms.Values) {
					count += room.Players.Count;
				}
				return count;
			}
		}

		protected override PeerBase OnCreatePeer(InitRequest initRequest) {
			// UberStrike 4.3.10 (Android) clients connect with their CMID as the application id and
			// speak the legacy Cmune RPC protocol; PC clients keep using GamePeer.
			if (Legacy.LegacyClient.IsLegacy(initRequest.ApplicationId)) {
				return new Legacy.LegacyGamePeer(initRequest);
			}

			return new GamePeer(initRequest);
		}

		protected override void OnBeforeSetup() {
			if (Configuration.GameApplicationSettings.ApplicationIdentifier == null) {
				Log.Fatal("ApplicationIdentifier is null!");
				throw new ArgumentNullException(nameof(Configuration.GameApplicationSettings.ApplicationIdentifier));
			}

			Identifier = Configuration.GameApplicationSettings.ApplicationIdentifier;

			if (Configuration.GameApplicationSettings.PhotonId == 0) {
				Log.Fatal("PhotonId is null!");
				throw new ArgumentNullException(nameof(Configuration.GameApplicationSettings.PhotonId));
			}

			PhotonId = Configuration.GameApplicationSettings.PhotonId;

			Log.Info($"Starting GameServer[{Identifier}]...");
		}

		protected override void OnSetup() {
			SocketClient = new SocketClient(Identifier, ServerType.Game, PhotonId, Configuration.GameApplicationSettings.EncryptionPassPhrase);

			SocketClient.Connected += (sender, e) => {
				Log.Info("Game: CONNECTED TO SOCKET");

				PublishMonitoringData();
			};

			SocketClient.Disconnected += (sender, e) => {
				Log.Info("Game: DISCONNECTED FROM SOCKET");
				SocketClient.Reconnect(25);
			};

			SocketClient.ConnectionRejected += (sender, e) => {
				Log.Info($"Game: Rejected connection by socket server (Reason: {e.Reason})");
			};

			SocketClient.DataReceived += (sender, e) => {
				try {
				switch (e.Type) {
					case PacketType.ChatMessage:
						var message = (SocketChatMessage)e.Data;

						if (RoomManager.TryGetRoom(message.RoomNumber, out var room) && room != null) {
							var senderPeer = room.Peers.FirstOrDefault(_ => _.Actor != null && _.Actor.Cmid == message.Cmid);

							if (senderPeer != null) {
								var censored = ProfanityFilter.CensorString(message.Message);
								var trimmed = censored.Substring(0, Math.Min(censored.Length, 140));

								if (Configuration.EnableChatLog) {
									ChatLog.Info($"[{room.RoomId}] {message.Name}: {message.Message}");
								}

								foreach (var peer in room.Peers) {
									peer.GameEventSender.SendChatMessage(message.Cmid, message.Name, trimmed, senderPeer.Actor.AccessLevel, (byte)ChatContext.None);
								}
							}
						}

						break;
					case PacketType.SetBoost: {
						// Global coin/xp boost event set in admin and broadcast to all Game servers. Swap
						// the whole state atomically so an in-flight match-end never reads a half-updated
						// boost. Also re-sent to this server right after it (re)connects, so a restart
						// can't drop an active event.
						try {
							var data = (Dictionary<string, object>)e.Data;
							boost = new BoostState {
								PointsMultiplier = Convert.ToInt32(data["PointsMultiplier"]),
								XpMultiplier = Convert.ToInt32(data["XpMultiplier"]),
								EndsAt = Convert.ToInt64(data["EndsAt"]),
							};
							Log.Info($"Boost updated: {boost.PointsMultiplier}x coins / {boost.XpMultiplier}x xp (active={boost.IsActive}, endsAt={boost.EndsAt}).");
						} catch (Exception ex) { Log.Error("SetBoost failed", ex); }
						break;
					}
					case PacketType.SetBotsConfig: {
						// Admin-controlled AI fill-bots toggle (BETA), broadcast to all Game servers. Swap
						// the whole state atomically, same reasoning as SetBoost. Also re-sent to this
						// server right after it (re)connects, so a restart can't silently re-enable/
						// disable bots against the admin's last choice.
						try {
							var data = (Dictionary<string, object>)e.Data;
							botsConfig = new BotsConfigState {
								Enabled = Convert.ToBoolean(data["Enabled"]),
								FillTarget = Convert.ToInt32(data["FillTarget"]),
								MaxBots = Convert.ToInt32(data["MaxBots"]),
							};
							Log.Info($"Bots config updated: enabled={botsConfig.Enabled}, fillTarget={botsConfig.FillTarget}, maxBots={botsConfig.MaxBots}.");
						} catch (Exception ex) { Log.Error("SetBotsConfig failed", ex); }
						break;
					}
				}
				} catch (Exception ex) {
					// A throw here would bubble into the websocket OnMessage callback and tear down the
					// master-socket connection (chat/boost/monitoring stop). Contain it.
					Log.Error("Game master-socket DataReceived handler failed", ex);
				}
			};

			var tcpAddress = Dns.GetHostAddresses(Configuration.MasterHostname).Where(_ => _.AddressFamily == AddressFamily.InterNetwork).First();

			if (tcpAddress != null) {
				SocketClient.Connect(tcpAddress, Configuration.SocketPort);
			}

			monitoringTimer = new System.Threading.Timer(_ => {
				try { PublishMonitoringData(); } catch { }
			}, null, 5000, 5000);

			Log.Info($"Started GameServer[{Identifier}].");
		}

		protected override void OnBeforeTearDown() {
			Log.Info($"Stopping GameServer[{Identifier}]...");
		}

		protected override void OnTearDown() {
			monitoringTimer?.Dispose();
			Log.Info($"Stopped GameServer[{Identifier}].");
		}

		private void PublishMonitoringData() {
			SocketClient?.SendSync(PacketType.Monitoring, GetStatus(), serverType: ServerType.Game);
		}

		private Dictionary<string, object> GetStatus() {
			try {
				return new Dictionary<string, object> {
					["ConnectedPeers"] = Peers,
					["Players"] = Players,
					["Rooms"] = RoomManager.Rooms.Values.Select(room => {
						return new Dictionary<string, object> {
							["RoomId"] = room.RoomId,
							["IsTeamGame"] = room.IsTeamGame,
							["MetaData"] = room.MetaData,
							["Peers"] = room.Peers.Select(_ => _.Actor.Cmid),
							["Players"] = room.Players.Select(_ => _.Actor.Cmid),
							["RoundNumber"] = room.RoundNumber,
							["RoundStartTime"] = room.RoundStartTime,
							["RoundEndTime"] = room.RoundEndTime,
							["HasRoundEnded"] = room.HasRoundEnded,
							["State"] = room.State.CurrentStateId
						};
					})
				};
			} catch (Exception e) {
				Log.Error(e);

				return new Dictionary<string, object> { ["Error"] = e.Message };
			}
		}
	}
}
