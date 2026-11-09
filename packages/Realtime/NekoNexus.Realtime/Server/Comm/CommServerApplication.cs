using log4net;
using Photon.SocketServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using static NekoNexus.WebSocket;

namespace NekoNexus.Realtime.Server.Comm {
	public class CommServerApplication : BaseRealtimeApplication {
		protected static readonly new ILog Log = LogManager.GetLogger(nameof(CommServerApplication));
		protected static readonly ILog ChatLog = LogManager.GetLogger("ChatLog");

		public static new CommServerApplication Instance => (CommServerApplication)ApplicationBase.Instance;
		public override ServerType ServerType => ServerType.Comm;

		private static readonly ProfanityFilter.ProfanityFilter ProfanityFilter = new ProfanityFilter.ProfanityFilter();

		// Periodically pushes the live lobby roster to the master so the online-player list and
		// admin dashboard update in realtime (not just once on connect).
		private System.Threading.Timer monitoringTimer;

		public override int Peers {
			get {
				return LobbyManager.Instance.Peers.Count;
			}
		}

		protected override PeerBase OnCreatePeer(InitRequest initRequest) {
			// UberStrike 4.3.10 (Android) clients connect with their CMID as the application id and
			// speak the legacy Cmune RPC protocol; PC clients keep using CommPeer.
			if (Legacy.LegacyClient.IsLegacy(initRequest.ApplicationId)) {
				return new Legacy.LegacyCommPeer(initRequest);
			}

			return new CommPeer(initRequest);
		}

		protected override void OnBeforeSetup() {
			if (Configuration.CommApplicationSettings.ApplicationIdentifier == null) {
				Log.Fatal("ApplicationIdentifier is null!");
				throw new ArgumentNullException(nameof(Configuration.CommApplicationSettings.ApplicationIdentifier));
			}

			Identifier = Configuration.CommApplicationSettings.ApplicationIdentifier;

			if (Configuration.CommApplicationSettings.PhotonId == 0) {
				Log.Fatal("PhotonId is null!");
				throw new ArgumentNullException(nameof(Configuration.CommApplicationSettings.PhotonId));
			}

			PhotonId = Configuration.CommApplicationSettings.PhotonId;

			Log.Info($"Starting CommServer[{Identifier}]...");
		}

		protected override void OnSetup() {
			SocketClient = new SocketClient(Identifier, ServerType.Comm, PhotonId, Configuration.CommApplicationSettings.EncryptionPassPhrase);

			SocketClient.Connected += (sender, e) => {
				Log.Info("Comm: CONNECTED TO SOCKET");

				PublishMonitoringData();
			};

			SocketClient.Disconnected += (sender, e) => {
				Log.Info("Comm: DISCONNECTED FROM SOCKET");

				SocketClient.Reconnect(25);
			};

			SocketClient.ConnectionRejected += (sender, e) => {
				Log.Info($"Comm: Rejected connection by socket server (Reason: {e.Reason})");
			};

			SocketClient.DataReceived += (sender, e) => {
				switch (e.Type) {
					case PacketType.ChatMessage:
						var message = (SocketChatMessage)e.Data;

						var censored = ProfanityFilter.CensorString(message.Message);
						var trimmed = censored.Substring(0, Math.Min(censored.Length, 140));

						if (Configuration.EnableChatLog) {
							ChatLog.Info($"[Lobby] {message.Name}: {message.Message}");
						}

						foreach (var peer in LobbyManager.Instance.Peers) {
							peer.LobbyEventSender.SendLobbyChatMessage(message.Cmid, message.Name, trimmed);
						}
						// The same PC/master chat feed is translated into Android 4.3.10
						// MessageToAll RPCs for peers with a discovered comm instance ID.
						Legacy.LegacyCommPeer.BroadcastLobbyChat(message.Cmid, message.Name, trimmed);
						break;
					case PacketType.BanPlayer: {
						var data = (Dictionary<string, object>)e.Data;
						var targetPeer = LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == (long)data["TargetCmid"]);

						if (targetPeer != null) {
							if ((long)data["Duration"] == 0) {
								targetPeer.SendError($"You have been banned permanently.\n\nReason: {data["Reason"]}");
							} else {
								var expireTime = ((DateTime)data["ExpireTime"]).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss \"GMT\"zzz");
								targetPeer.SendError($"You have been banned for {data["Duration"]} minute(s).\nYour ban will expire at {expireTime}\n\nReason: {data["Reason"]}");
							}
						}

						break;
					}
					case PacketType.NotifyInboxMessage: {
						// New mail (or a System announcement) arrived for this player - tell their
						// client to pull it in immediately instead of waiting for a manual refresh.
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							var messageId = Convert.ToInt32(data["MessageId"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendUpdateInboxMessages(messageId);
							Legacy.LegacyCommPeer.NotifyInboxMessage(targetCmid, messageId);
						} catch (Exception ex) { Log.Error("NotifyInboxMessage failed", ex); }
						break;
					}
					case PacketType.NotifyInboxRequests: {
						// A clan/contact invitation was created for this player - refresh their
						// requests list in realtime (the same way friend requests already work).
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendUpdateInboxRequests();
							Legacy.LegacyCommPeer.NotifyInboxRequests(targetCmid);
						} catch (Exception ex) { Log.Error("NotifyInboxRequests failed", ex); }
						break;
					}
					case PacketType.NotifyClanMembers: {
						// The clan roster changed (member joined/left/accepted) - refresh this
						// member's clan view in realtime.
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendUpdateClanMembers();
						} catch (Exception ex) { Log.Error("NotifyClanMembers failed", ex); }
						break;
					}
					case PacketType.NotifyWallet: {
						// A player's wallet changed (admin gift, store purchase, or match payout). Push
						// the new balance to their lobby client so credits/coins update live instead of
						// only on next login. Offline target = harmless no-op.
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							var credits = Convert.ToInt32(data["Credits"]);
							var points = Convert.ToInt32(data["Points"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendUpdateWallet(credits, points);
						} catch (Exception ex) { Log.Error("NotifyWallet failed", ex); }
						break;
					}
					case PacketType.NotifyStats: {
						// A player's stats changed (admin edit). Push the new xp/points to their lobby
						// client so level/xp/points update live without relogging.
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							var xp = Convert.ToInt32(data["Xp"]);
							var points = Convert.ToInt32(data["Points"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendUpdateStats(xp, points);
						} catch (Exception ex) { Log.Error("NotifyStats failed", ex); }
						break;
					}
					case PacketType.NotifyClanChat: {
						// Push a clan chat line (e.g. a "X joined the clan" system message) to an
						// online clan member.
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							var fromCmid = Convert.ToInt32(data["Cmid"]);
							var name = Convert.ToString(data["Name"]);
							var msg = Convert.ToString(data["Message"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendClanChatMessage(fromCmid, name, msg);
						} catch (Exception ex) { Log.Error("NotifyClanChat failed", ex); }
						break;
					}
					case PacketType.NotifyPrivateChat: {
						// Deliver a website friend DM to the target as an in-game private (whisper) chat
						// message, if they're online. Offline = harmless no-op (the DM is stored on the web).
						try {
							var data = (Dictionary<string, object>)e.Data;
							var targetCmid = Convert.ToInt64(data["TargetCmid"]);
							var fromCmid = Convert.ToInt32(data["Cmid"]);
							var name = Convert.ToString(data["Name"]);
							var msg = Convert.ToString(data["Message"]);
							LobbyManager.Instance.Peers.FirstOrDefault(_ => _.Actor.Cmid == targetCmid)
								?.LobbyEventSender.SendPrivateChatMessage(fromCmid, name, msg);
						} catch (Exception ex) { Log.Error("NotifyPrivateChat failed", ex); }
						break;
					}
				}
			};

			var tcpAddress = Dns.GetHostAddresses(Configuration.MasterHostname).Where(_ => _.AddressFamily == AddressFamily.InterNetwork).First();

			if (tcpAddress != null) {
				SocketClient.Connect(tcpAddress, Configuration.SocketPort);
			}

			monitoringTimer = new System.Threading.Timer(_ => {
				try { PublishMonitoringData(); } catch { }
			}, null, 5000, 5000);

			Log.Info($"Started CommServer[{Identifier}].");
		}

		protected override void OnBeforeTearDown() {
			Log.Info($"Stopping CommServer[{Identifier}]...");
		}

		protected override void OnTearDown() {
			monitoringTimer?.Dispose();
			Log.Info($"Stopped CommServer[{Identifier}].");
		}

		private void PublishMonitoringData() {
			SocketClient?.SendSync(PacketType.Monitoring, GetStatus(), serverType: ServerType.Comm);
		}

		private Dictionary<string, object> GetStatus() {
			try {
				return new Dictionary<string, object> {
					["Peers"] = LobbyManager.Instance.Peers.Select(peer => new Dictionary<string, object> {
						["Cmid"] = peer.Actor.Cmid,
						["RemoteIP"] = peer.RemoteIPAddress.ToString(),
						["RemotePort"] = peer.RemotePort,
						["Channel"] = peer.Actor.ActorInfo.Channel,
						["LocalIP"] = peer.LocalIPAddress.ToString(),
						["LocalPort"] = peer.LocalPort
					})
				};
			} catch (Exception e) {
				Log.Error(e);

				return new Dictionary<string, object> {
					["Error"] = e.Message
				};
			}
		}
	}
}
