using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NekoNexus.Client {
	[HarmonyPatch(typeof(PlayPageGUI))]
	public static class PlayPageGUIHook {
		private static float nextServerCheckTime;

		[HarmonyPatch("SelectedServerUpdated"), HarmonyPostfix]
		public static void SelectedServerUpdates_Postfix(PhotonServer view) {
			nextServerCheckTime = (view == null) ? 0 : Time.time + 10f;
		}

		[HarmonyPatch("DrawQuickSearch"), HarmonyPrefix]
		public static bool DrawQuickSearch_Prefix(PlayPageGUI __instance, Rect rect) {
			GUITools.PushGUIState();

			GUI.enabled = Time.time > nextServerCheckTime;
			if (GUITools.Button(new Rect(rect.x - (8 + 64), rect.y, 64, rect.height), new GUIContent((nextServerCheckTime >= Time.time) ? $"{LocalizedStrings.Refresh} ({Math.Ceiling(nextServerCheckTime - Time.time):N0})" : LocalizedStrings.Refresh), BlueStonez.buttondark_medium)) {
				Singleton<GameStateController>.Instance.Client.RefreshGameLobby();
				nextServerCheckTime = Time.time + 10f;
			}
			GUI.enabled = true;

			GUITools.PopGUIState();

			var searchBar = typeof(PlayPageGUI).GetField("_searchBar", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
			NekoNexusTraverse.InvokeMethod(searchBar, "Draw", rect);

			return false;
		}
	}

	[HarmonyPatch(typeof(PhotonServer), "get_Name")]
	public static class PhotonServerNameHook {
		[HarmonyPostfix]
		public static void Postfix(ref string __result) {
			if (string.IsNullOrEmpty(__result) || __result.Length < 5 || __result[0] != '[') return;
			var tokenEnd = __result.IndexOf(']');
			if (tokenEnd == 3 && char.IsUpper(__result[1]) && char.IsUpper(__result[2])) {
				__result = __result.Substring(tokenEnd + 1).TrimStart();
			}
		}
	}
}
