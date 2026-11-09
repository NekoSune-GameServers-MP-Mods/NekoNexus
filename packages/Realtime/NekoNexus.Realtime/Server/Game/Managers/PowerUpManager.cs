using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using UberStrike.Core.Models;
using UnityEngine;

namespace NekoNexus.Realtime.Server.Game {
	public class PowerUpManager {
		protected static readonly ILog Log = LogManager.GetLogger(nameof(PowerUpManager));

		private readonly BaseGameRoom room;

		private readonly List<Vector3> positions = new List<Vector3>();
		private readonly List<TimeSpan> respawnTimes = new List<TimeSpan>();

		public List<int> pendingRespawns { get; private set; } = new List<int>();
		public Dictionary<int, TimeSpan> pendingRespawnTimes { get; private set; } = new Dictionary<int, TimeSpan>();

		public bool IsLoaded => positions?.Count > 0 && respawnTimes?.Count > 0;

		public PowerUpManager(BaseGameRoom room) {
			this.room = room ?? throw new ArgumentNullException(nameof(room));
		}

		/// <summary>True while the pickups were loaded from a 4.3.10 (Android) client, which sends
		/// respawn times but no positions; see <see cref="LoadWithoutPositions"/>.</summary>
		public bool HasUnknownPositions { get; private set; }

		private static readonly Vector3 UnknownPosition = new Vector3(float.NaN, float.NaN, float.NaN);

		public void Load(List<Vector3> positions, List<ushort> respawnTimes) {
			// A PC client's upload (real positions) replaces pickups loaded from an Android client.
			if (IsLoaded && HasUnknownPositions && positions != null && positions.Count == this.positions.Count) {
				for (var i = 0; i < positions.Count; i++) this.positions[i] = positions[i];
				HasUnknownPositions = false;
				return;
			}
			if (IsLoaded) return;

			this.positions.AddRange(positions);
			this.respawnTimes.AddRange(respawnTimes.Select(_ => TimeSpan.FromSeconds(_)));

			for (var i = 0; i < this.respawnTimes.Count; i++) {
				pendingRespawnTimes.Add(i, TimeSpan.FromSeconds(0));
			}
		}

		/// <summary>
		/// 4.3.10 clients report only each pickup's respawn time. Each position is learned from where
		/// the player stands the first time the pickup is taken, and checked as usual from then on.
		/// </summary>
		public void LoadWithoutPositions(List<ushort> respawnTimes) {
			if (IsLoaded || respawnTimes == null || respawnTimes.Count == 0) return;
			Load(respawnTimes.Select(_ => UnknownPosition).ToList(), respawnTimes);
			HasUnknownPositions = true;
		}

		public void PickUp(GamePeer peer, int pickupId, PickupItemType type, byte value) {
			if (pickupId < 0 || pickupId >= positions.Count) {
				Log.Warn($"Player {peer.Actor.Name}({peer.Actor.Cmid}) attempting to pick up unknown power-up with ID: {pickupId}");
				return;
			}

			if (float.IsNaN(positions[pickupId].x)) {
				positions[pickupId] = peer.Actor.Movement.Position;
			}

			var distance = Vector3.Distance(peer.Actor.Movement.Position, positions[pickupId]);
			if (distance > 2.5) return;

			if (pendingRespawns.Contains(pickupId) || pendingRespawnTimes[pickupId].TotalMilliseconds > 0) return;
			pendingRespawns.Add(pickupId);
			pendingRespawnTimes[pickupId] = respawnTimes[pickupId];

			foreach (var otherPeer in room.Peers) {
				otherPeer.GameEventSender.SendPowerUpPicked(pickupId, 1);
			}

			switch (type) {
				case PickupItemType.Health:
					room.StatisticsManager.IncreaseHealthPickedUp(peer, Math.Min(200 - peer.Actor.ActorInfo.Health, value));
					peer.Actor.ActorInfo.Health = (byte)Math.Min(200, peer.Actor.ActorInfo.Health + value);
					break;
				case PickupItemType.Armor:
					room.StatisticsManager.IncreaseArmorPickedUp(peer, Math.Min(200 - peer.Actor.ActorInfo.ArmorPoints, value));
					peer.Actor.ActorInfo.ArmorPoints = (byte)Math.Min(200, peer.Actor.ActorInfo.ArmorPoints + value);
					break;
				default: break;
			}
		}

		public void Update() {
			for (var i = 0; i < pendingRespawns.Count; i++) {
				var remainingTime = pendingRespawnTimes[pendingRespawns[i]].Subtract(TimeSpan.FromMilliseconds(room.Loop.DeltaTime));

				if (remainingTime.TotalMilliseconds <= 0) {
					foreach (var peer in room.Peers) {
						peer.GameEventSender.SendPowerUpPicked(pendingRespawns[i], 0);
					}

					pendingRespawns.RemoveAt(i);
				} else {
					pendingRespawnTimes[pendingRespawns[i]] = remainingTime;
				}
			}
		}

		public void RespawnItems() {
			if (IsLoaded) {
				for (int i = 0; i < pendingRespawns.Count; i++) {
					pendingRespawnTimes[pendingRespawns[i]] = TimeSpan.FromSeconds(0);

					pendingRespawns.RemoveAt(i);
				}

				foreach (var peer in room.Peers) {
					peer.GameEventSender.SendResetAllPowerups();
				}
			}
		}
	}
}
