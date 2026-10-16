using System;
using System.Collections.Generic;
using System.Linq;
using Cmune.DataCenter.Common.Entities;
using UberStrike.Core.Models;
using UberStrike.Core.Models.Views;
using UberStrike.Core.Types;
using UberStrike.DataCenter.Common.Entities;
using UberStrike.Realtime.UnitySdk;
using UnityEngine;

namespace NekoNexus.Realtime.Server.Game {
	// AI fill bots: keep a room populated so solo/low-population players still have someone to
	// play against, then back off (one bot removed per human that joins) as real players show up.
	//
	// Bots are added/removed the exact same way a real player joining/leaving the match is
	// (mutate peers/players, then raise PlayerJoined/PlayerLeft) so every existing per-state
	// handler (PreparePlayer/SpawnPlayer/score updates/team balancing) runs unchanged for them.
	public partial class BaseGameRoom {
		private static readonly string[] BotNamePool = {
			"Reaper", "Viper", "Ghost", "Nomad", "Raptor", "Havoc", "Widow", "Jinx",
			"Cobra", "Rocket", "Blitz", "Echo", "Static", "Nitro", "Vortex", "Rogue",
			"Fable", "Wraith", "Talon", "Ember", "Frost", "Glitch", "Hexbolt", "Zephyr",
		};

		private readonly Dictionary<GamePeer, BotBrain> botBrains = new Dictionary<GamePeer, BotBrain>();
		private readonly System.Random botRandom = new System.Random((int)DateTime.UtcNow.Ticks);
		private readonly object botRouteLock = new object();
		private readonly Dictionary<GamePeer, Queue<BotRoutePoint>> botRoutes = new Dictionary<GamePeer, Queue<BotRoutePoint>>();

		private sealed class BotRoutePoint {
			public Vector3 Position;
			public byte MovementState;
			public byte KeyState;
			public DateTime Time;
		}

		// Reuse movement that a real client has already proven valid on this map. The realtime
		// server has no Unity collision or water geometry, so it cannot safely invent jumps,
		// swim surfaces, or paths around walls on its own.
		private void RecordBotRoutePoint(GamePeer peer) {
			if (peer == null || peer.IsBot || peer.Actor == null || !peer.Actor.ActorInfo.IsAlive)
				return;

			var now = DateTime.UtcNow;
			var position = (Vector3)peer.Actor.Movement.Position;
			lock (botRouteLock) {
				if (!botRoutes.TryGetValue(peer, out var route)) {
					route = new Queue<BotRoutePoint>();
					botRoutes[peer] = route;
				}

				var last = route.Count == 0 ? null : route.Last();
				if (last != null && Vector3.Distance(last.Position, position) < 0.65f && (now - last.Time).TotalMilliseconds < 300)
					return;

				route.Enqueue(new BotRoutePoint {
					Position = position,
					MovementState = peer.Actor.Movement.MovementState,
					KeyState = peer.Actor.Movement.KeyState,
					Time = now,
				});
				while (route.Count > 96 || (route.Count > 1 && (now - route.Peek().Time).TotalSeconds > 12))
					route.Dequeue();
			}
		}

		private bool TryGetBotRoutePoint(GamePeer preferredTarget, Vector3 botPosition, out BotRoutePoint point) {
			point = null;
			lock (botRouteLock) {
				var closestDistance = 8f;
				foreach (var pair in botRoutes.OrderByDescending(pair => pair.Key == preferredTarget)) {
					if (pair.Value.Count < 2)
						continue;

					var points = pair.Value.ToArray();
					for (var i = 0; i < points.Length - 1; i++) {
						var distance = Vector3.Distance(botPosition, points[i].Position);
						if (distance < closestDistance) {
							closestDistance = distance;
							point = points[i + 1];
						}
					}
				}
				return point != null;
			}
		}

		private void NotifyBotOfConfirmedHit(GamePeer bot, GamePeer attacker) {
			lock (botBrains) {
				if (botBrains.TryGetValue(bot, out var brain))
					brain.OnConfirmedHit(attacker);
			}
		}

		private void ClearBotRoute(GamePeer peer) {
			lock (botRouteLock)
				botRoutes.Remove(peer);
		}

		public int BotCount { get { lock (peers) { return players.Count(p => p.IsBot); } } }

		private int ComputeTargetBotCount(int humanCount) {
			// Admin-controlled (BETA, off by default) instead of a YAML setting, so it can be flipped
			// live with no realtime restart - see BotsConfigState/GameServerApplication.BotsConfig.
			var settings = GameServerApplication.BotsConfig;

			if (!settings.Enabled || humanCount <= 0) {
				return 0;
			}

			var target = settings.FillTarget - humanCount;
			return Math.Max(0, Math.Min(settings.MaxBots, target));
		}

		// Reconciles the bot population against however many humans are actually in the match
		// right now. Call this whenever that number could have changed (a human joins/leaves the
		// match) - NOT from bot add/remove itself, or this would recurse.
		private void SyncBotPopulation() {
			if (IsDisposed)
				return;

			int humanCount;
			List<GamePeer> currentBots;
			lock (peers) {
				humanCount = players.Count(p => !p.IsBot);
				currentBots = players.Where(p => p.IsBot).ToList();
			}

			var targetBotCount = ComputeTargetBotCount(humanCount);

			if (currentBots.Count > targetBotCount) {
				foreach (var bot in currentBots.Take(currentBots.Count - targetBotCount)) {
					RemoveBot(bot);
				}
			} else if (currentBots.Count < targetBotCount) {
				for (var i = currentBots.Count; i < targetBotCount; i++) {
					AddBot();
				}
			}
		}

		private void RemoveAllBots() {
			List<GamePeer> currentBots;
			lock (peers) {
				currentBots = peers.Where(p => p.IsBot).ToList();
			}

			foreach (var bot in currentBots) {
				RemoveBot(bot);
			}
		}

		private void AddBot() {
			var name = $"[BOT] {BotNamePool[botRandom.Next(BotNamePool.Length)]}";

			var level = botRandom.Next(1, Math.Max(2, XpPointsUtil.MaxPlayerLevel + 1));
			XpPointsUtil.GetXpRangeForLevel(level, out var minXp, out var maxXp);
			var xp = botRandom.Next(minXp, Math.Max(minXp + 1, maxXp));

			var bot = GamePeer.CreateBot(name, xp);
			bot.Room = this;

			var actorInfo = new GameActorInfo {
				TeamID = TeamID.NONE,
				Health = 100,
				Level = XpPointsUtil.GetLevelForXp(xp),
				Channel = ChannelType.Steam,
				PlayerState = PlayerStates.None,
				Ping = 0,
				PlayerId = NextPlayerId++,

				Cmid = bot.Member.CmuneMemberView.PublicProfile.Cmid,
				ClanTag = string.Empty,
				AccessLevel = bot.Member.CmuneMemberView.PublicProfile.AccessLevel,
				PlayerName = bot.Member.CmuneMemberView.PublicProfile.Name,
			};

			bot.Actor = new GameActor(bot, actorInfo);

			lock (peers) {
				peers.Add(bot);
			}

			AssignRandomBotLoadout(bot);

			var team = IsTeamGame ? PickBotTeam() : TeamID.NONE;

			bot.Actor.Team = team;
			bot.Actor.ActorInfo.Health = 100;
			bot.Actor.ActorInfo.PlayerState = PlayerStates.None;
			bot.Actor.ActorInfo.Kills = 0;
			bot.Actor.ActorInfo.Deaths = 0;

			if (!CanJoinMatch) {
				bot.Actor.ActorInfo.PlayerState = PlayerStates.Spectator;
			}

			lock (peers) {
				players.Add(bot);
			}

			// botBrains is read (as a snapshot) from the loop thread every tick (UpdateBots) while
			// Join/Leave (network threads) add/remove entries here - guard it like peers/players.
			lock (botBrains) {
				botBrains[bot] = new BotBrain(this, bot, botRandom.Next());
			}

			Log.Info($"Added bot {bot.Actor.Name}({bot.Actor.Cmid}) to {this}({RoomId}), team={team}");

			OnPlayerJoined(new PlayerJoinedEventArgs { Player = bot, Team = team });
		}

		private void RemoveBot(GamePeer bot) {
			lock (botBrains) {
				botBrains.Remove(bot);
			}

			foreach (var otherPeer in Peers) {
				otherPeer.GameEventSender.SendPlayerLeftGame(bot.Actor.Cmid);
			}

			lock (peers) {
				peers.Remove(bot);
				players.Remove(bot);
			}

			Log.Info($"Removed bot {bot.Actor.Name}({bot.Actor.Cmid}) from {this}({RoomId})");

			bot.Actor = null;
			bot.Room = null;

			OnPlayerLeft(new PlayerLeftEventArgs { Player = bot });
		}

		private TeamID PickBotTeam() {
			var snapshot = Players;
			var blue = snapshot.Count(p => p.Actor.Team == TeamID.BLUE);
			var red = snapshot.Count(p => p.Actor.Team == TeamID.RED);

			return blue <= red ? TeamID.BLUE : TeamID.RED;
		}

		// Every "real weapon" item class a bot could plausibly wield - excludes weapon mods
		// (scope/muzzle/attachment), which share the Weapon item type but aren't equippable guns.
		private static readonly UberstrikeItemClass[] RangedWeaponClasses = {
			UberstrikeItemClass.WeaponMachinegun,
			UberstrikeItemClass.WeaponShotgun,
			UberstrikeItemClass.WeaponSniperRifle,
			UberstrikeItemClass.WeaponCannon,
			UberstrikeItemClass.WeaponSplattergun,
			UberstrikeItemClass.WeaponLauncher,
		};

		private void AssignRandomBotLoadout(GamePeer bot) {
			int PickRandom(IEnumerable<UberStrikeItemWeaponView> source) {
				var list = source.ToList();
				return list.Count == 0 ? 0 : list[botRandom.Next(list.Count)].ID;
			}

			int PickRandomGear(UberstrikeItemClass itemClass) {
				var list = ShopManager.GearItems.Values.Where(g => g.ItemClass == itemClass).ToList();
				return list.Count == 0 ? 0 : list[botRandom.Next(list.Count)].ID;
			}

			var melee = PickRandom(ShopManager.WeaponItems.Values.Where(w => w.ItemClass == UberstrikeItemClass.WeaponMelee));

			// Give each bot a "loadout personality": 1-3 distinct ranged weapons, since the game
			// has no formal class system to pick from (see TODO.md).
			var rangedPool = ShopManager.WeaponItems.Values.Where(w => RangedWeaponClasses.Contains(w.ItemClass)).ToList();
			var weaponCount = Math.Min(rangedPool.Count, botRandom.Next(1, 4));
			var chosenWeapons = rangedPool.OrderBy(_ => botRandom.Next()).Take(weaponCount).Select(w => w.ID).ToList();

			bot.Loadout = new LoadoutView {
				Cmid = bot.Actor.Cmid,
				MeleeWeapon = melee,
				Weapon1 = chosenWeapons.Count > 0 ? chosenWeapons[0] : 0,
				Weapon2 = chosenWeapons.Count > 1 ? chosenWeapons[1] : 0,
				Weapon3 = chosenWeapons.Count > 2 ? chosenWeapons[2] : 0,
				Head = PickRandomGear(UberstrikeItemClass.GearHead),
				Face = PickRandomGear(UberstrikeItemClass.GearFace),
				Gloves = PickRandomGear(UberstrikeItemClass.GearGloves),
				UpperBody = PickRandomGear(UberstrikeItemClass.GearUpperBody),
				LowerBody = PickRandomGear(UberstrikeItemClass.GearLowerBody),
				Boots = PickRandomGear(UberstrikeItemClass.GearBoots),
				Webbing = PickRandomGear(UberstrikeItemClass.GearHolo),
			};
		}

		private void UpdateBots() {
			BotBrain[] brains;
			lock (botBrains) {
				if (botBrains.Count == 0)
					return;

				brains = botBrains.Values.ToArray();
			}

			foreach (var brain in brains) {
				try {
					brain.Tick();
				} catch (Exception ex) {
					Log.Error("Bot AI tick failed", ex);
				}
			}
		}

		// A deliberately simple FSM: patrol toward a random spawn point when no target is in
		// range, engage (face + shoot, closing distance for melee) the nearest valid enemy
		// otherwise, and self-trigger a respawn after death (a real client would send
		// RespawnRequest; bots have none, so they drive PlayerRespawned themselves).
		private class BotBrain {
			private const float MeleeRange = 2.5f;
			private const float DefaultRangedRange = 16f;

			// Per-weapon-class effective engagement range. UberStrikeItemWeaponView.CombatRange is
			// NOT usable for this - in the shop data it's just an alias of SplashRadius (only ever
			// set on splash weapons, 1 on every hitscan weapon), so range is keyed off the actual
			// weapon archetype instead: shotgun/splattergun are close range, sniper is long range,
			// everything else medium. Without this a shotgun bot could "hit" from sniper distance.
			private static readonly Dictionary<UberstrikeItemClass, float> WeaponRangeByClass = new Dictionary<UberstrikeItemClass, float> {
				{ UberstrikeItemClass.WeaponShotgun, 9f },
				{ UberstrikeItemClass.WeaponSplattergun, 10f },
				{ UberstrikeItemClass.WeaponCannon, 14f },
				{ UberstrikeItemClass.WeaponMachinegun, 22f },
				{ UberstrikeItemClass.WeaponLauncher, 18f },
				{ UberstrikeItemClass.WeaponSniperRifle, 45f },
			};

			private readonly BaseGameRoom Room;
			private readonly GamePeer Bot;
			private readonly System.Random Rand;

			private DateTime nextFireTime = DateTime.MinValue;
			private DateTime nextRoamTime = DateTime.MinValue;
			private DateTime roamUntil = DateTime.MinValue;
			private byte previousMovementState;
			private int visibleThreatCmid;
			private Vector3 botPositionWhenSeen;
			private Vector3 threatPositionWhenSeen;
			private DateTime confirmedSightExpires;

			public BotBrain(BaseGameRoom room, GamePeer bot, int seed) {
				Room = room;
				Bot = bot;
				Rand = new System.Random(seed);
			}

			public void Tick() {
				var actor = Bot.Actor;
				if (actor == null)
					return;

				// A real client sends an initial SwitchWeapon op right after spawning; bots have no
				// client to do that, so drive it here once a slot/loadout has been assigned.
				if (actor.CurrentWeapon == null) {
					Room.SwitchWeapon(Bot, actor.ActorInfo.CurrentWeaponSlot);
				}

				if (Room.State.CurrentStateId != GameStateId.MatchRunning) {
					return;
				}

				if (actor.ActorInfo.IsSpectator) {
					return;
				}

				if (!actor.ActorInfo.IsAlive) {
					TickRespawn(actor);
					return;
				}

				// Route samples come from actual players, so grounded, jump, and water movement can
				// be replayed on every map without the server inventing unsupported vertical motion.
				actor.Movement.Velocity = Vector3.zero;
				actor.Movement.KeyState = (byte)KeyState.Still;
				actor.Movement.MovementState = (byte)MoveStates.Grounded;
				actor.UpdatePosition = true;

				var target = AcquireTarget(actor);
				if (target != null) {
					Engage(actor, target);
				} else if (DateTime.UtcNow >= nextRoamTime) {
					roamUntil = DateTime.UtcNow.AddMilliseconds(Rand.Next(900, 1801));
					nextRoamTime = DateTime.UtcNow.AddMilliseconds(Rand.Next(2600, 5001));
				} else if (DateTime.UtcNow < roamUntil && Room.TryGetBotRoutePoint(null, actor.Movement.Position, out var routePoint)) {
					FollowRoutePoint(actor, routePoint);
				}
			}

			private void TickRespawn(GameActor actor) {
				if (actor.NextRespawnTime > DateTime.UtcNow)
					return;

				Room.OnPlayerRespawned(new PlayerRespawnedEventArgs { Player = Bot });
			}

			private float GetWeaponRange(UberStrikeItemWeaponView weapon) {
				if (weapon == null)
					return DefaultRangedRange;
				if (weapon.ItemClass == UberstrikeItemClass.WeaponMelee)
					return MeleeRange;

				return WeaponRangeByClass.TryGetValue(weapon.ItemClass, out var range) ? range : DefaultRangedRange;
			}

			private GamePeer AcquireTarget(GameActor actor) {
				GamePeer best = null;
				var bestDistance = float.MaxValue;
				Vector3 myPosition = actor.Movement.Position;
				var engageRange = GetWeaponRange(actor.CurrentWeapon);

				foreach (var other in Room.Players) {
					if (other == Bot || other.Actor == null)
						continue;
					if (!other.Actor.ActorInfo.IsAlive || other.Actor.ActorInfo.IsSpectator)
						continue;
					if (Room.IsTeamGame && other.Actor.Team != TeamID.NONE && other.Actor.Team == actor.Team)
						continue;

					var distance = Vector3.Distance(myPosition, other.Actor.Movement.Position);
					if (distance < Math.Max(80f, engageRange) && distance < bestDistance) {
						bestDistance = distance;
						best = other;
					}
				}

				return best;
			}

			private void Engage(GameActor actor, GamePeer target) {
				var myPosition = (Vector3)actor.Movement.Position;
				var targetPosition = (Vector3)target.Actor.Movement.Position;
				var toTarget = targetPosition - myPosition;
				var distance = toTarget.magnitude;

				FaceDirection(actor, toTarget);

				var weapon = actor.CurrentWeapon;
				var isMelee = weapon != null && weapon.ItemClass == UberstrikeItemClass.WeaponMelee;
				var range = GetWeaponRange(weapon);
				var desiredRange = isMelee ? range * 0.5f : range * 0.65f;
				var now = DateTime.UtcNow;

				if (now >= nextRoamTime) {
					roamUntil = now.AddMilliseconds(Rand.Next(900, 1801));
					nextRoamTime = now.AddMilliseconds(Rand.Next(2600, 5001));
				}
				if ((distance > desiredRange + 0.5f || now < roamUntil) && Room.TryGetBotRoutePoint(target, myPosition, out var routePoint)) {
					FollowRoutePoint(actor, routePoint, false);
				}

				if (weapon == null || DateTime.UtcNow < nextFireTime)
					return;

				// Hard range cutoff per weapon archetype - a shotgun/splattergun bot can no longer
				// "hit" from sniper distance just because a target wandered into the shared 32-unit
				// search radius; each weapon only ever fires within its own effective range.
				if (distance > Math.Min(range, 22f) || !HasRecentLineOfSightConfirmation(target))
					return;

				// Bots should fire in deliberate bursts and miss often enough to feel human.
				nextFireTime = DateTime.UtcNow.AddMilliseconds(Math.Max(650, weapon.RateOfFire) + Rand.Next(250, 701));

				Room.SingleBulletFire(Bot);

				// Simple accuracy model: closer shots land more often; there's always some chance
				// to miss so bots aren't hitscan-perfect.
				var hitChance = Math.Max(0.08, 0.42 - (distance / range) * 0.28);
				if (Rand.NextDouble() > hitChance)
					return;

				var bodyPart = Rand.NextDouble() < 0.12 ? BodyPart.Head : BodyPart.Body;
				Room.DirectHitDamage(Bot, target.Actor.Cmid, (byte)bodyPart, 1);
			}

			public void OnConfirmedHit(GamePeer attacker) {
				if (attacker == null || attacker.IsBot || attacker.Actor == null || Bot.Actor == null)
					return;

				visibleThreatCmid = attacker.Actor.Cmid;
				botPositionWhenSeen = Bot.Actor.Movement.Position;
				threatPositionWhenSeen = attacker.Actor.Movement.Position;
				confirmedSightExpires = DateTime.UtcNow.AddMilliseconds(600);
			}

			private bool HasRecentLineOfSightConfirmation(GamePeer target) {
				if (target == null || target.Actor == null || visibleThreatCmid != target.Actor.Cmid || DateTime.UtcNow > confirmedSightExpires)
					return false;

				// A real client reports DirectHitDamage only after its local weapon ray hit the bot.
				// In static map geometry that confirms reciprocal visibility; expire quickly and
				// require both actors to remain near the confirmed positions to avoid wall shots.
				return Vector3.Distance(Bot.Actor.Movement.Position, botPositionWhenSeen) <= 2f &&
					Vector3.Distance(target.Actor.Movement.Position, threatPositionWhenSeen) <= 2f;
			}

			private void FollowRoutePoint(GameActor actor, BotRoutePoint point, bool faceMovement = true) {
				var current = (Vector3)actor.Movement.Position;
				var next = Vector3.MoveTowards(current, point.Position, 7.6f / BaseGameRoom.TICK_TRATE);

				var direction = next - current;
				if (direction.sqrMagnitude > 0.0001f) {
					if (faceMovement)
						FaceDirection(actor, direction);
					actor.Movement.Velocity = (next - current) * BaseGameRoom.TICK_TRATE;
					actor.Movement.KeyState = point.KeyState;
				}

				// Never copy the synthetic Flying flag. Jump/swim/wade states are copied from
				// real movement samples, and the jump event drives the normal client animation.
				var safeState = (byte)(point.MovementState & ~(byte)MoveStates.Flying);
				if ((((MoveStates)safeState & MoveStates.Jumping) != 0) && (((MoveStates)previousMovementState & MoveStates.Jumping) == 0))
					Room.Jump(Bot, next);
				previousMovementState = safeState;
				actor.Movement.MovementState = safeState;
				actor.Movement.Position = next;
				actor.UpdatePosition = true;
			}

			private void FaceDirection(GameActor actor, Vector3 direction) {
				if (direction.sqrMagnitude < 0.0001f)
					return;

				var back = new Vector3(0, 0, -1);
				var angle = Vector3.Angle(direction, back);
				if (direction.x < 0)
					angle = 360 - angle;

				actor.Movement.HorizontalRotation = Conversion.Angle2Byte(angle);
				var horizontal = new Vector3(direction.x, 0, direction.z);
				var pitch = Vector3.Angle(horizontal, direction);
				if (direction.y < 0)
					pitch = 360 - pitch;
				actor.Movement.VerticalRotation = Conversion.Angle2Byte(pitch);
				actor.UpdatePosition = true;
			}
		}
	}
}
