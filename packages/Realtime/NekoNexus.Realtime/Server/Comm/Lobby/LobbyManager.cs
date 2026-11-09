using log4net;
using System.Collections.Generic;
using UberStrike.Core.Models;

namespace NekoNexus.Realtime.Server.Comm {
	public class LobbyManager {
		protected static readonly ILog Log = LogManager.GetLogger(nameof(LobbyManager));

		public static LobbyManager Instance { get; } = new LobbyManager();

		public LobbyRoom GlobalLobby { get; private set; } = new LobbyRoom();
		public IReadOnlyList<CommPeer> Peers => GlobalLobby.Peers;

		private readonly object _lock = new object();

		/// <summary>PC lobby players plus Android (4.3.10) players connected to this comm server.</summary>
		public List<CommActorInfo> GetAllActorInfos() {
			var actorsInfo = new List<CommActorInfo>(Peers.Count);
			var cmids = new HashSet<int>();
			foreach (var peer in Peers) {
				if (peer?.Actor?.ActorInfo == null) continue;
				actorsInfo.Add(peer.Actor.ActorInfo);
				cmids.Add(peer.Actor.Cmid);
			}
			actorsInfo.AddRange(Legacy.LegacyCommPeer.GetLobbyActorInfos(cmids));
			return actorsInfo;
		}

		public void UpdatePlayerList() {
			lock (_lock) {
				var actorsInfo = GetAllActorInfos();

				foreach (var peer in Peers) {
					peer.LobbyEventSender.SendFullPlayerListUpdate(actorsInfo);
				}
			}

			Legacy.LegacyCommPeer.BroadcastFullPlayerList();
		}
	}
}
