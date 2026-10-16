using System;
using System.Linq;
using UberStrike.Core.Models;
using UberStrike.Core.Types;
using static NekoNexus.Realtime.Server.Game.BaseGameRoom;

namespace NekoNexus.Realtime.Server.Game {
	internal class MatchRunningState : BaseMatchState {
		public MatchRunningState(BaseGameRoom room) : base(room) { }

		public override void OnEnter() {
			Room.MatchEnded += OnMatchEnded;
			Room.PlayerJoined += OnPlayerJoined;
			Room.PlayerLeft += OnPlayerLeft;
			Room.PlayerKilled += OnPlayerKilled;
			Room.PlayerRespawned += OnPlayerRespawned;

			Room.RoundStartedAtUtc = DateTime.UtcNow;
			Room.RoundStartTime = BaseGameRoom.GetWireTimestamp();

			if (Room.MetaData.TimeLimit > 0) {
				Room.RoundDeadlineUtc = Room.RoundStartedAtUtc.AddSeconds(Room.MetaData.TimeLimit);
				Room.RoundEndTime = BaseGameRoom.AddWireMilliseconds(Room.RoundStartTime, (long)Room.MetaData.TimeLimit * 1000);
			} else {
				Room.RoundDeadlineUtc = DateTime.MaxValue;
				Room.RoundEndTime = Room.RoundStartTime;
			}

			foreach (var player in Room.Players) {
				player.GameEventSender.SendMatchStart(Room.RoundNumber, Room.RoundEndTime);
				player.State.SetState(PlayerStateId.Playing);
			}

			if (Room.MetaData.GameMode == GameModeType.EliminationMode) {
				Room.GetCurrentScore(out _, out short blueTeamScore, out short redTeamScore);
				foreach (var peer in Room.Peers) {
					peer.GameEventSender.SendUpdateRoundScore(Room.RoundNumber, blueTeamScore, redTeamScore);
				}
			}
		}

		public override void OnExit() {
			Room.MatchEnded -= OnMatchEnded;
			Room.PlayerJoined -= OnPlayerJoined;
			Room.PlayerLeft -= OnPlayerLeft;
			Room.PlayerKilled -= OnPlayerKilled;
			Room.PlayerRespawned -= OnPlayerRespawned;
		}

		public override void OnResume() { }

		public override void OnUpdate() {
			Room.PowerUpManager.Update();

			if (Room.MetaData.TimeLimit > 0 && Room.RoundDeadlineUtc != DateTime.MinValue && DateTime.UtcNow >= Room.RoundDeadlineUtc) {
				Room.HasRoundEnded = true;
			}
		}



		private void OnMatchEnded(object sender, EventArgs args) {
			Room.State.SetState(GameStateId.EndOfMatch);
		}

		private void OnPlayerJoined(object sender, PlayerJoinedEventArgs args) {
			args.Player.Actor.PreviousSpawnPoints.Clear();

			if (Room.MetaData.GameMode == GameModeType.EliminationMode) {
				args.Player.Actor.ActorInfo.PlayerState = PlayerStates.Spectator;
			}

			Room.PreparePlayer(args.Player, args.Player.Actor.ActorInfo.IsSpectator);

			// A spectator (joined mid-round in Elimination, or staff JoinAsSpectator) has NO spawned
			// body - SpawnPlayer no-ops for them. Don't then broadcast a fake respawn or force their
			// state to Playing: that left the joiner's client controlling a non-existent actor (broken
			// spectator view) and showed everyone else a ghost at the origin. Route them to the
			// Spectating state, which sends match-start + score + JoinedAsSpectator on its own.
			if (args.Player.Actor.ActorInfo.IsSpectator) {
				args.Player.State.SetState(PlayerStateId.Spectating);
				return;
			}

			Room.SpawnPlayer(args.Player, true);

			args.Player.GameEventSender.SendMatchStart(Room.RoundNumber, Room.RoundEndTime);
			//args.Player.GameEvents.SendWaitingForPlayers();

			foreach (var peer in Room.Peers) {
				peer.GameEventSender.SendPlayerRespawned(args.Player.Actor.Cmid, args.Player.Actor.Movement.Position, args.Player.Actor.Movement.HorizontalRotation);
			}

			args.Player.State.SetState(PlayerStateId.Playing);

			if (Room.MetaData.GameMode == GameModeType.DeathMatch) {
				short killsRemaining = (short)Room.MetaData.KillLimit;

				Room.GetCurrentScore(out killsRemaining, out _, out _);

				args.Player.GameEventSender.SendKillsRemaining(killsRemaining, 0);
			} else {
				Room.GetCurrentScore(out _, out short blueTeamScore, out short redTeamScore);

				args.Player.GameEventSender.SendUpdateRoundScore(Room.RoundNumber, blueTeamScore, redTeamScore);
			}
		}

		private void OnPlayerLeft(object sender, PlayerLeftEventArgs args) {
			if (!Room.CanStartMatch) {
				Log.Info($"{args.Player.Actor?.Name}({args.Player.Actor?.Cmid}) leaving mid-match dropped CanStartMatch to false " +
					$"(blue={Room.Players.Count(p => p.Actor.Team == TeamID.BLUE)}, red={Room.Players.Count(p => p.Actor.Team == TeamID.RED)}, " +
					$"total={Room.Players.Count}) - ending round early.");
				Room.HasRoundEnded = true;
			}
		}

		private void OnPlayerKilled(object sender, PlayerKilledEventArgs args) {
			foreach (var peer in Room.Peers) {
				peer.GameEventSender.SendPlayerKilled(args.AttackerCmid, args.VictimCmid, (byte)args.ItemClass, args.Damage, (byte)args.Part, args.Direction);

				if (peer.Actor.Cmid.CompareTo(args.VictimCmid) == 0) {
					peer.State.SetState(PlayerStateId.Killed);
				}
			}

			if (Room.MetaData.GameMode == GameModeType.DeathMatch) {
				short killsRemaining = (short)Room.MetaData.KillLimit;

				Room.GetCurrentScore(out killsRemaining, out _, out _);
				foreach (var peer in Room.Peers) {
					peer.GameEventSender.SendKillsRemaining(killsRemaining, 0);
				}
			} else {
				Room.GetCurrentScore(out _, out short blueTeamScore, out short redTeamScore);
				foreach (var peer in Room.Peers) {
					peer.GameEventSender.SendUpdateRoundScore(Room.RoundNumber, blueTeamScore, redTeamScore);
				}
			}
		}

		private void OnPlayerRespawned(object sender, PlayerRespawnedEventArgs args) {
			Log.Debug("OnPlayerRespawned");
			if (Room.MetaData.GameMode == GameModeType.EliminationMode) return;

			Room.SpawnPlayer(args.Player, false);

			args.Player.State.ResetState();
			args.Player.State.SetState(PlayerStateId.Playing);
		}
	}
}
