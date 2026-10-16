using System;
using System.Threading.Tasks;
using UberStrike.Core.Types;
using static NekoNexus.Realtime.Server.Game.BaseGameRoom;

namespace NekoNexus.Realtime.Server.Game {
	internal class EndOfMatchState : BaseMatchState {
		public EndOfMatchState(BaseGameRoom room) : base(room) { }

		public override void OnEnter() {
			Room.PlayerJoined += OnPlayerJoined;
			//Room.PlayerLeft += OnPlayerLeft;

			Room.RoundEndTime = BaseGameRoom.GetWireTimestamp();
			if (Room.RoundStartedAtUtc != DateTime.MinValue)
				Room.RoundDurations.Add(DateTime.UtcNow - Room.RoundStartedAtUtc);

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

			foreach (var peer in Room.Peers) {
				peer.GameEventSender.SendTeamWins(Room.WinningTeam);
			}

			Task t = Task.Run(async () => {
				await Task.Delay(GameServerApplication.Instance.Configuration.GameplaySettings.MatchEndTimeout * 1000);

				// Run the actual transition ON the room's loop thread - the StateMachine stack is not
				// thread-safe, and setting state straight from this thread-pool thread raced the tick
				// (corrupting the state stack -> match wedged / never advancing). Enqueue is processed
				// at the top of the next tick.
				Room.Loop.Enqueue(() => {
					if (Room.MetaData.GameMode == GameModeType.EliminationMode) {
						Room.GetCurrentScore(out _, out short blueTeamScore, out short redTeamScore);

						// Game should end if RoundNumber >= KillLimit (aka "Max Rounds")
						if (Math.Max(blueTeamScore, redTeamScore) >= Room.MetaData.KillLimit || !Room.CanStartMatch) {
							Room.State.SetState(GameStateId.AfterRound);
						} else {
							Room.State.SetState(GameStateId.WaitingForPlayers);
						}
					} else {
						Room.State.SetState(GameStateId.AfterRound);
					}
				});
			});
		}

		public override void OnExit() {
			Room.PlayerJoined -= OnPlayerJoined;
			//Room.PlayerLeft -= OnPlayerLeft;
		}

		public override void OnResume() { }

		public override void OnUpdate() { }



		private void OnPlayerJoined(object sender, PlayerJoinedEventArgs args) {
			var player = args.Player;

			Room.PreparePlayer(player);
			Room.SpawnPlayer(player, true);

			player.GameEventSender.SendWaitingForPlayers();
		}
	}
}
