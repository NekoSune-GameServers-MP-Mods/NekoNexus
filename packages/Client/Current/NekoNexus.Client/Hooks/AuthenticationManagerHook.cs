using Cmune.DataCenter.Common.Entities;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UberStrike.Core.Models.Views;
using UberStrike.Core.Types;
using UberStrike.Core.ViewModel;
using UberStrike.WebService.Unity;
using UnityEngine;

namespace NekoNexus.Client {
	[HarmonyPatch(typeof(AuthenticationManager))]
	public static class AuthenticationManagerHook {
		private static string STEAMID_FILE => string.Join("/", new string[] { Application.dataPath, "PlayerSteamID" });
		private static string STEAMID_EMU => string.Join("/", new string[] { Application.dataPath, "../steam_settings/force_steamid.txt" });

		[HarmonyPatch("LoginByChannel"), HarmonyPrefix]
		public static bool LoginByChannel_Prefix(AuthenticationManager __instance) {
			string steamId = string.Empty;
			if (File.Exists(STEAMID_FILE)) {
				steamId = File.ReadAllText(STEAMID_FILE);
			} else if (File.Exists(STEAMID_EMU)) {
				steamId = File.ReadAllText(STEAMID_EMU);
			}

			Debug.Log(string.Format("SteamWorks SteamID:{0}, PlayerPrefs SteamID:{1}", PlayerDataManager.SteamId, steamId));

			UnityRuntime.StartRoutine(NekoNexusTraverse.InvokeMethod<IEnumerator>(__instance, "StartLoginMemberSteam", true));

			return false;
		}

		[HarmonyPatch("CompleteAuthentication"), HarmonyPrefix]
		public static bool CompleteAuthentication_Prefix() {
			return false;
		}

		[HarmonyPatch("CompleteAuthentication"), HarmonyPostfix]
		public static void CompleteAuthentication_Postfix(AuthenticationManager __instance, ref IEnumerator __result, MemberAuthenticationResultView authView, bool isRegistrationLogin) {
			__result = CompleteAuthentication(__instance, authView, isRegistrationLogin);

			if (authView.MemberAuthenticationResult == MemberAuthenticationResult.Ok) {
				File.WriteAllText(STEAMID_FILE, PlayerDataManager.SteamId.ToString());
			}
		}

		private static IEnumerator CompleteAuthentication(AuthenticationManager __instance, MemberAuthenticationResultView authView, bool isRegistrationLogin) {
			var traverse = NekoNexusTraverse.Create(__instance);

			if (authView == null) {
				Debug.LogError("Account authentication error: MemberAuthenticationResultView was null, isRegistrationLogin: " + isRegistrationLogin);
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.Error, "There was an error logging you in. Please try again or contact us at http://support.cmune.com");
				yield break;
			}

			if (authView.MemberAuthenticationResult == MemberAuthenticationResult.IsBanned || authView.MemberAuthenticationResult == MemberAuthenticationResult.IsIpBanned) {
				ApplicationDataManager.LockApplication(LocalizedStrings.YourAccountHasBeenBanned);
				yield break;
			}

			if (authView.MemberAuthenticationResult == MemberAuthenticationResult.InvalidEsns) {
				Debug.Log("Result: " + authView.MemberAuthenticationResult);
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.Error, "Sorry this account is linked already.");
				yield break;
			}

			if (authView.MemberAuthenticationResult != MemberAuthenticationResult.Ok) {
				Debug.Log("Result: " + authView.MemberAuthenticationResult);
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.Error, "Your login credentials are not correct. Please try to login again.");
				yield break;
			}

			Singleton<PlayerDataManager>.Instance.SetLocalPlayerMemberView(authView.MemberView);
			PlayerDataManager.AuthToken = authView.AuthToken;

			if (!PlayerDataManager.IsTestBuild) {
				PlayerDataManager.MagicHash = UberDaemon.Instance.GetMagicHash(authView.AuthToken);
				Debug.Log("Magic Hash:" + PlayerDataManager.MagicHash);
			}

			ApplicationDataManager.ServerDateTime = authView.ServerTime;
			EventHandler.Global.Fire(new GlobalEvents.Login(authView.MemberView.PublicProfile.AccessLevel));

			var _progress = traverse.GetField<ProgressPopupDialog>("_progress");

			_progress.Text = LocalizedStrings.LoadingFriendsList;
			_progress.Progress = 0.2f;

			yield return UnityRuntime.StartRoutine(Singleton<CommsManager>.Instance.GetContactsByGroups());

			_progress.Text = LocalizedStrings.LoadingCharacterData;
			_progress.Progress = 0.3f;

			yield return ApplicationWebServiceClient.GetConfigurationData("4.7.1", (ApplicationConfigurationView appConfigView) => {
				XpPointsUtil.Config = appConfigView;
			}, (Exception ex) => {
				ApplicationDataManager.LockApplication(LocalizedStrings.ErrorLoadingData);
			});

			Singleton<PlayerDataManager>.Instance.SetPlayerStatisticsView(authView.PlayerStatisticsView);

			_progress.Text = LocalizedStrings.LoadingMapData;
			_progress.Progress = 0.5f;

			bool mapsLoadedSuccessfully = false;

			yield return ApplicationWebServiceClient.GetMaps("4.7.1", DefinitionType.StandardDefinition, (List<MapView> callback) => {
				mapsLoadedSuccessfully = Singleton<MapManager>.Instance.InitializeMapsToLoad(callback);
			}, (Exception ex) => {
				ApplicationDataManager.LockApplication(LocalizedStrings.ErrorLoadingMaps);
			});

			if (!mapsLoadedSuccessfully) {
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.Error, LocalizedStrings.ErrorLoadingMapsSupport);
				PopupSystem.HideMessage(_progress);
				yield break;
			}

			_progress.Text = NekoNexusLocalizedStrings.LoadingCustomMapData;
			_progress.Progress = 0.55f;

			mapsLoadedSuccessfully = false;

			yield return NekoNexusWebServiceClient.GetCustomMaps("4.7.1", DefinitionType.StandardDefinition, (List<NekoNexusMapView> callback) => {
				var mapManager = NekoNexusTraverse.Create(Singleton<MapManager>.Instance);

				foreach (var mapView in callback) {
					mapManager.InvokeMethod("AddMapView", mapView, true, false);
				}

				mapsLoadedSuccessfully = NekoNexusMapManager.LoadMaps(callback);
			}, (Exception ex) => {
				ApplicationDataManager.LockApplication(LocalizedStrings.ErrorLoadingMaps);
			});

			if (!mapsLoadedSuccessfully) {
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.Error, LocalizedStrings.ErrorLoadingMapsSupport);
				PopupSystem.HideMessage(_progress);
				yield break;
			}

			_progress.Progress = 0.6f;
			_progress.Text = LocalizedStrings.LoadingWeaponAndGear;

			yield return UnityRuntime.StartRoutine(Singleton<ItemManager>.Instance.StartGetShop());

			if (!Singleton<ItemManager>.Instance.ValidateItemMall()) {
				PopupSystem.HideMessage(_progress);
				yield break;
			}

			_progress.Progress = 0.7f;
			_progress.Text = LocalizedStrings.LoadingPlayerInventory;

			yield return UnityRuntime.StartRoutine(Singleton<ItemManager>.Instance.StartGetInventory(false));

			_progress.Progress = 0.8f;
			_progress.Text = LocalizedStrings.GettingPlayerLoadout;

			yield return UnityRuntime.StartRoutine(Singleton<PlayerDataManager>.Instance.StartGetLoadout());

			if (!Singleton<LoadoutManager>.Instance.ValidateLoadout()) {
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.ErrorGettingPlayerLoadout, LocalizedStrings.ErrorGettingPlayerLoadoutSupport);
				yield break;
			}

			_progress.Progress = 0.85f;
			_progress.Text = LocalizedStrings.LoadingPlayerStatistics;

			yield return UnityRuntime.StartRoutine(Singleton<PlayerDataManager>.Instance.StartGetMember());

			if (!Singleton<PlayerDataManager>.Instance.ValidateMemberData()) {
				traverse.InvokeMethod("ShowLoginErrorPopup", LocalizedStrings.ErrorGettingPlayerStatistics, LocalizedStrings.ErrorPlayerStatisticsSupport);
				yield break;
			}

			_progress.Progress = 0.9f;
			_progress.Text = LocalizedStrings.LoadingClanData;

			yield return ClanWebServiceClient.GetMyClanId(PlayerDataManager.AuthToken, (int id) => {
				PlayerDataManager.ClanID = id;
			}, null);

			if (PlayerDataManager.ClanID > 0) {
				yield return ClanWebServiceClient.GetOwnClan(PlayerDataManager.AuthToken, PlayerDataManager.ClanID, (ClanView ev) => {
					Singleton<ClanDataManager>.Instance.SetClanData(ev);
				}, null);
			}

			GameState.Current.Avatar.SetDecorator(AvatarBuilder.CreateLocalAvatar());
			GameState.Current.Avatar.UpdateAllWeapons();

			yield return new WaitForEndOfFrame();
			Singleton<InboxManager>.Instance.Initialize();
			yield return new WaitForEndOfFrame();
			Singleton<BundleManager>.Instance.Initialize();
			yield return new WaitForEndOfFrame();

			PopupSystem.HideMessage(_progress);

			if (!authView.IsAccountComplete) {
				PanelManager.Instance.OpenPanel(PanelType.CompleteAccount);
			} else {
				MenuPageManager.Instance.LoadPage(PageType.Home, false);
				traverse.SetProperty("IsAuthComplete", true);
			}

			Debug.LogWarning(string.Format("AuthToken:{0}, MagicHash:{1}", PlayerDataManager.AuthToken, PlayerDataManager.MagicHash));
			yield break;
		}

		[HarmonyPatch("ShowLoginErrorPopup"), HarmonyPrefix]
		public static bool ShowLoginErrorPopup_Prefix(AuthenticationManager __instance, string title, string message) {
			var traverse = NekoNexusTraverse.Create(__instance);

			Debug.Log("Login Error!");
			PopupSystem.HideMessage(traverse.GetField<ProgressPopupDialog>("_progress"));
			PopupSystem.Show(new NekoNexusPopupDialog(
				title,
				message + Environment.NewLine + Environment.NewLine + NekoNexusLocalizedStrings.RetryOrQuit,
				PopupSystem.AlertType.OKCancel,
				NekoNexusLocalizedStrings.Retry,
				new Action(() => Singleton<AuthenticationManager>.Instance.LoginByChannel()),
				NekoNexusLocalizedStrings.Quit,
				new Action(Application.Quit),
				string.Empty,
				null));

			return false;
		}

		[HarmonyPatch("CompleteAuthentication"), HarmonyPostfix]
		public static void CompleteAuthentication_Postfix(MemberAuthenticationResultView authView, bool isRegistrationLogin) {
			if (authView.MemberAuthenticationResult == MemberAuthenticationResult.Ok) {
				File.WriteAllText(string.Join("/", new string[] { Application.dataPath, "PlayerSteamID" }), PlayerDataManager.SteamId.ToString());
			}

			if (authView.IsAccountComplete) {
				CommConnectionManagerHook.Connect(AutoMonoBehaviour<CommConnectionManager>.Instance);
			}
		}
	}
}
