using HarmonyLib;
using UberStrike.Core.Models;
using UnityEngine;

namespace NekoNexus.Client {
	/// <summary>
	/// Instantiates prefabs for remotely emitted Quick Items that are not in a local player's inventory.
	/// </summary>
	[HarmonyPatch(typeof(GameState))]
	public static class GameStateHook {
		[HarmonyPatch("EmitRemoteQuickItem"), HarmonyPrefix]
		public static bool EmitRemoteQuickItem_Postfix(GameState __instance, Vector3 origin, Vector3 direction, int itemId, byte playerNumber, int projectileID) {
			IUnityItem itemInShop = Singleton<ItemManager>.Instance.GetItemInShop(itemId);
			if (itemInShop != null) {
				if (!itemInShop.Prefab) {
					var gameObject = itemInShop.Create(Vector3.zero, Quaternion.identity).GetComponent<QuickItem>();
					for (int i = 0; i < gameObject.transform.childCount; i++) {
						gameObject.transform.GetChild(i).gameObject.SetActive(false);
					}
				}
			}

			return true;
		}

		[HarmonyPatch("StartMatch"), HarmonyPostfix]
		public static void StartMatch_Postfix(GameState __instance, int roundNumber, int endTime) {
			var timeLimit = GameState.Current.RoomData.TimeLimit;
			var roundStartTime = timeLimit == 0
				? (endTime & int.MaxValue)
				: unchecked((endTime - timeLimit * 1000) & int.MaxValue);
			NekoNexusTraverse.SetField(__instance, "roundStartTime", roundStartTime);
		}

		[HarmonyPatch("get_GameTime"), HarmonyPostfix]
		public static void GameTime_Postfix(ref float __result) {
			if (GameState.Current.RoomData == null || GameState.Current.RoomData.TimeLimit <= 0)
				return;

			var serverTime = Singleton<GameStateController>.Instance.Client.ServerTimeTicks;
			var roundStartTime = NekoNexusTraverse.GetField<int>(GameState.Current, "roundStartTime");
			var elapsedMilliseconds = unchecked((serverTime - roundStartTime) & int.MaxValue);
			__result = elapsedMilliseconds / 1000f;
		}

		[HarmonyPatch("GameStateHelper", "UpdateMatchTime"), HarmonyPrefix]
		public static bool GameStateHelper_UpdateMatchTime_Prefix() {
			if (GameState.Current.RoomData.TimeLimit == 0) {
				GameState.Current.PlayerData.RemainingTime.Value = Mathf.CeilToInt(GameState.Current.GameTime);
				return false;
			}

			return true;
		}

		public static bool UpdatePlayerStatistics(StatsCollection totalStats, StatsCollection bestPerLife) {
			Debug.Log(totalStats.GetProps().ToString());
			Debug.Log(bestPerLife.GetProps().ToString());
			return true;
		}
	}
}
