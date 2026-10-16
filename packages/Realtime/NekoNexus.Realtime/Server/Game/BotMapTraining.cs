using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UberStrike.Core.Models;
using UnityEngine;

namespace NekoNexus.Realtime.Server.Game {
	/// <summary>
	/// Automatically learns map routes from real player movement, spawn locations, jump/swim
	/// states, and deaths. This is an observation based navigation graph, not guessed geometry.
	/// </summary>
	internal static class BotMapTraining {
		private const int MaxNodesPerMap = 4096;
		private const int MaxEdgesPerMap = 16384;
		private static readonly ConcurrentDictionary<int, MapMemory> Maps = new ConcurrentDictionary<int, MapMemory>();
		private static readonly ConcurrentDictionary<int, object> MapLocks = new ConcurrentDictionary<int, object>();
		private static readonly string MemoryDirectory = ResolveMemoryDirectory();

		private sealed class MapMemory {
			public int MapId { get; set; }
			public List<KnowledgeNode> Nodes { get; set; } = new List<KnowledgeNode>();
			public List<KnowledgeEdge> Edges { get; set; } = new List<KnowledgeEdge>();
			[JsonIgnore] public Dictionary<long, KnowledgeEdge> EdgeIndex { get; set; } = new Dictionary<long, KnowledgeEdge>();
			[JsonIgnore] public Dictionary<int, List<KnowledgeEdge>> Outgoing { get; set; } = new Dictionary<int, List<KnowledgeEdge>>();
			[JsonIgnore] public int ChangesSinceSave { get; set; }
			[JsonIgnore] public DateTime LastSavedUtc { get; set; }
		}

		private sealed class KnowledgeNode {
			public int Id { get; set; }
			public float X { get; set; }
			public float Y { get; set; }
			public float Z { get; set; }
			public int Observations { get; set; }
			public int Deaths { get; set; }
			public int SpawnUses { get; set; }
			public List<int> SpawnTeams { get; set; } = new List<int>();
			public byte MovementState { get; set; }
			public byte KeyState { get; set; }
			public bool JumpLaunchObserved { get; set; }
			public bool WaterObserved { get; set; }
		}

		private sealed class KnowledgeEdge {
			public int From { get; set; }
			public int To { get; set; }
			public int Traversals { get; set; }
			public int DeathsNearEdge { get; set; }
		}

		private static string ResolveMemoryDirectory() {
			var configured = Environment.GetEnvironmentVariable("NEKONEXUS_BOT_MAP_DATA");
			return string.IsNullOrWhiteSpace(configured)
				? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "bot-maps")
				: Path.GetFullPath(configured);
		}

		private static object GetLock(int mapId) => MapLocks.GetOrAdd(mapId, _ => new object());

		private static MapMemory GetMap(int mapId) {
			return Maps.GetOrAdd(mapId, id => {
				var map = new MapMemory { MapId = id };
				var file = GetFile(id);
				try {
					if (File.Exists(file)) {
						var loaded = JsonConvert.DeserializeObject<MapMemory>(File.ReadAllText(file));
						if (loaded != null && loaded.MapId == id) map = loaded;
					}
				} catch (Exception ex) {
					log4net.LogManager.GetLogger(nameof(BotMapTraining)).Error($"Could not load bot map training data for map {id}", ex);
				}
				map.Nodes = map.Nodes ?? new List<KnowledgeNode>();
				map.Edges = map.Edges ?? new List<KnowledgeEdge>();
				map.EdgeIndex = map.Edges.GroupBy(e => EdgeKey(e.From, e.To)).ToDictionary(g => g.Key, g => g.First());
				map.Outgoing = map.Edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList());
				return map;
			});
		}

		private static string GetFile(int mapId) => Path.Combine(MemoryDirectory, $"map-{mapId}.json");
		private static long EdgeKey(int from, int to) => ((long)from << 32) | (uint)to;

		private static KnowledgeNode FindOrAddNode(MapMemory map, Vector3 position) {
			var nearest = map.Nodes
				.Select(n => new { Node = n, Distance = Vector3.Distance(position, new Vector3(n.X, n.Y, n.Z)) })
				.Where(x => x.Distance <= 1.25f)
				.OrderBy(x => x.Distance)
				.Select(x => x.Node)
				.FirstOrDefault();
			if (nearest != null) return nearest;
			if (map.Nodes.Count >= MaxNodesPerMap) return null;

			var node = new KnowledgeNode { Id = map.Nodes.Count, X = position.x, Y = position.y, Z = position.z };
			map.Nodes.Add(node);
			return node;
		}

		public static void ObserveSpawn(int mapId, Vector3 position, int teamId) {
			var map = GetMap(mapId);
			lock (GetLock(mapId)) {
				var node = FindOrAddNode(map, position);
				if (node == null) return;
				node.SpawnUses++;
				if (!node.SpawnTeams.Contains(teamId)) node.SpawnTeams.Add(teamId);
				node.Observations++;
				SaveIfNeeded(map, false);
			}
		}

		public static void ObserveMovement(int mapId, Vector3? previous, Vector3 position, byte movementState, byte keyState, Vector3 velocity) {
			var map = GetMap(mapId);
			lock (GetLock(mapId)) {
				var node = FindOrAddNode(map, position);
				if (node == null) return;
				node.Observations++;
				node.MovementState = (byte)(movementState & ~(byte)MoveStates.Flying);
				node.KeyState = keyState;
				var flags = (MoveStates)movementState;
				node.JumpLaunchObserved |= (flags & MoveStates.Jumping) != 0 || velocity.y >= 10f;
				node.WaterObserved |= (flags & (MoveStates.Wading | MoveStates.Swimming | MoveStates.Diving)) != 0;

				if (previous.HasValue && Vector3.Distance(previous.Value, position) <= 18f) {
					var from = FindOrAddNode(map, previous.Value);
					if (from != null && from.Id != node.Id) {
						var edgeKey = EdgeKey(from.Id, node.Id);
						if (!map.EdgeIndex.TryGetValue(edgeKey, out var edge)) {
							if (map.Edges.Count >= MaxEdgesPerMap) {
								SaveIfNeeded(map, false);
								return;
							}
							edge = new KnowledgeEdge { From = from.Id, To = node.Id };
							map.Edges.Add(edge);
							map.EdgeIndex[edgeKey] = edge;
							if (!map.Outgoing.TryGetValue(from.Id, out var outgoing))
								map.Outgoing[from.Id] = outgoing = new List<KnowledgeEdge>();
							outgoing.Add(edge);
						}
						edge.Traversals++;
					}
				}
				SaveIfNeeded(map, false);
			}
		}

		public static void ObserveDeath(int mapId, Vector3 position) {
			var map = GetMap(mapId);
			lock (GetLock(mapId)) {
				var node = FindOrAddNode(map, position);
				if (node == null) return;
				node.Deaths++;
				foreach (var edge in map.Edges.Where(e => e.To == node.Id)) edge.DeathsNearEdge++;
				SaveIfNeeded(map, false);
			}
		}

		public static void Flush(int mapId) {
			var map = GetMap(mapId);
			lock (GetLock(mapId))
				SaveIfNeeded(map, true);
		}

		public static bool TryGetNext(int mapId, Vector3 position, int preferredTargetCmid, IReadOnlyList<GamePeer> players, out Vector3 next, out byte movementState, out byte keyState) {
			next = Vector3.zero;
			movementState = (byte)MoveStates.Grounded;
			keyState = (byte)KeyState.Forward;
			var map = GetMap(mapId);
			lock (GetLock(mapId)) {
				var source = map.Nodes
					.Select(n => new { Node = n, Distance = Vector3.Distance(position, new Vector3(n.X, n.Y, n.Z)) })
					.Where(x => x.Distance <= 8f)
					.OrderBy(x => x.Distance)
					.Select(x => x.Node)
					.FirstOrDefault();
				if (source == null) return false;

				if (!map.Outgoing.TryGetValue(source.Id, out var sourceEdges)) return false;
				var candidates = sourceEdges.Where(e => e.To >= 0 && e.To < map.Nodes.Count)
					.Select(e => new { Edge = e, Node = map.Nodes[e.To] })
					.Where(x => Vector3.Distance(position, new Vector3(x.Node.X, x.Node.Y, x.Node.Z)) > 0.7f)
					.Select(x => new {
						x.Node,
						Score = x.Edge.Traversals + x.Edge.DeathsNearEdge * 12 + x.Node.Deaths * 20
							- (x.Node.SpawnUses > 0 ? 2 : 0) - Math.Min(x.Node.Observations, 100) * 0.02f
					})
					.ToList();
				if (candidates.Count == 0) return false;

				// Explore lower-traffic, low-death routes first. A recent player route toward a target
				// gets a small preference while still avoiding known dangerous nodes.
				var target = preferredTargetCmid == 0 ? null : players.FirstOrDefault(p => p.Actor != null && p.Actor.Cmid == preferredTargetCmid);
				var choice = candidates.OrderBy(c => c.Score - (target?.Actor != null && Vector3.Distance(new Vector3(c.Node.X, c.Node.Y, c.Node.Z), target.Actor.Movement.Position) < 12f ? 1.5f : 0))
					.ThenBy(c => c.Node.Id).First();
				next = new Vector3(choice.Node.X, choice.Node.Y, choice.Node.Z);
				movementState = choice.Node.MovementState;
				keyState = choice.Node.KeyState;
				return true;
			}
		}

		private static void SaveIfNeeded(MapMemory map, bool force) {
			map.ChangesSinceSave++;
			if (!force && map.ChangesSinceSave < 64 && (DateTime.UtcNow - map.LastSavedUtc).TotalSeconds < 15)
				return;

			try {
				Directory.CreateDirectory(MemoryDirectory);
				var file = GetFile(map.MapId);
				var temp = file + ".tmp";
				File.WriteAllText(temp, JsonConvert.SerializeObject(map, Formatting.Indented));
				if (File.Exists(file)) File.Replace(temp, file, null);
				else File.Move(temp, file);
				map.ChangesSinceSave = 0;
				map.LastSavedUtc = DateTime.UtcNow;
			} catch (Exception ex) {
				log4net.LogManager.GetLogger(nameof(BotMapTraining)).Error($"Could not save bot map training data for map {map.MapId}", ex);
			}
		}
	}
}
