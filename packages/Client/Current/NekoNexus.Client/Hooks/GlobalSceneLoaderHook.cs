using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cmune.Core.Models.Views;
using Cmune.DataCenter.Common.Entities;
using HarmonyLib;
using UberStrike.Core.Types;
using UberStrike.DataCenter.Common.Entities;
using UberStrike.WebService.Unity;
using UnityEngine;

namespace NekoNexus.Client {
	[HarmonyPatch(typeof(GlobalSceneLoader))]
	public static class GlobalSceneLoaderHook {
		private static NekoNexusTraverse<GlobalSceneLoader> traverse;

		private static string ErrorMessage {
			get {
				return traverse.GetProperty<string>("ErrorMessage");
			}

			set {
				traverse.SetProperty("ErrorMessage", value);
			}
		}

		[HarmonyPatch("Start"), HarmonyPrefix]
		public static bool Start_Prefix(GlobalSceneLoader __instance) {
			traverse = NekoNexusTraverse<GlobalSceneLoader>.Create(__instance);

			if (!NekoNexusClient.Settings.HasKey(NekoNexusPrefs.Key.TelemetryChoice)) {
				PopupSystem.Show(new NekoNexusPopupDialog(
					NekoNexusLocalizedStrings.TelemetryPopupTitle,
					NekoNexusLocalizedStrings.TelemetryPopupText,
					PopupSystem.AlertType.OKCancel,
					NekoNexusLocalizedStrings.Allow,
					() => {
						NekoNexusClient.Settings.SetKey(NekoNexusPrefs.Key.TelemetryChoice, NekoNexusPrefs.TelemetryChoice.Yes);
						__instance.StartCoroutine(StartWithCheckingUpdates());
					},
					NekoNexusLocalizedStrings.Disallow,
					() => {
						NekoNexusClient.Settings.SetKey(NekoNexusPrefs.Key.TelemetryChoice, NekoNexusPrefs.TelemetryChoice.No);
						__instance.StartCoroutine(StartWithCheckingUpdates());
					},
					NekoNexusLocalizedStrings.PrivacyPolicy,
					() => {
						ApplicationDataManager.OpenUrl(string.Empty, "https://nekonexus.festival.tf/privacy-policy");
					}));
			} else {
				__instance.StartCoroutine(StartWithCheckingUpdates());
			}


			return false;
		}

		[HarmonyPatch("InitializeGlobalScene"), HarmonyPrefix]
		public static bool InitializeGlobalScene_Prefix(GlobalSceneLoader __instance) {
			ApplicationDataManager.CurrentLocale = LocaleType.en_US;

			ApplicationDataManager.ApplicationOptions.IsUsingCustom = true;
			ApplicationDataManager.ApplicationOptions.VideoTextureQuality = 0;
			ApplicationDataManager.ApplicationOptions.VideoAntiAliasing = 4;
			ApplicationDataManager.ApplicationOptions.Initialize();

			__instance.StartCoroutine(GUITools.StartScreenSizeListener(1f));

			QualitySettings.masterTextureLimit = ApplicationDataManager.ApplicationOptions.VideoTextureQuality;
			QualitySettings.vSyncCount = ApplicationDataManager.ApplicationOptions.VideoVSyncCount;
			QualitySettings.antiAliasing = ApplicationDataManager.ApplicationOptions.VideoAntiAliasing;

			AutoMonoBehaviour<SfxManager>.Instance.EnableAudio(ApplicationDataManager.ApplicationOptions.AudioEnabled);
			AutoMonoBehaviour<SfxManager>.Instance.UpdateMasterVolume();
			AutoMonoBehaviour<SfxManager>.Instance.UpdateMusicVolume();
			AutoMonoBehaviour<SfxManager>.Instance.UpdateEffectsVolume();
			AutoMonoBehaviour<InputManager>.Instance.ReadAllKeyMappings();

			return false;
		}

		#region
		private static IEnumerator StartWithCheckingUpdates() {
			AutoMonoBehaviour<PreloadOptionsPanelButton>.Instance.enabled = true;

			UnityRuntime.StartRoutine(NekoNexusUpdater.CleanupUpdates());

			UnityRuntime.StartRoutine(AutoMonoBehaviour<NekoNexusUpdater>.Instance.CheckForUpdatesIfNecessary((updateCatalog) => {
				if (updateCatalog != null) {
					NekoNexusUpdater.HandleUpdateAvailable(updateCatalog, () => {
						traverse.Instance.StartCoroutine(Start());
					});
				} else {
					traverse.Instance.StartCoroutine(Start());
				}
			}, (error) => {
				// A failed/unreachable update check must NOT lock players out (it used to quit the
				// game). Log it and continue into the menu - they just didn't update this launch.
				Debug.LogError("Update check failed, continuing anyway: " + error);
				traverse.Instance.StartCoroutine(Start());
			}));

			yield break;
		}

		private static IEnumerator Start() {
			Application.runInBackground = true;
			Application.LoadLevel("Menu");
			NekoNexusTraverse.SetProperty(Singleton<SceneLoader>.Instance, "CurrentScene", "Menu");

			Configuration.WebserviceBaseUrl = ApplicationDataManager.WebServiceBaseUrl;

			traverse.SetProperty("GlobalSceneProgress", 1f);
			traverse.SetProperty("IsGlobalSceneLoaded", true);
			traverse.SetProperty("ItemAssetBundleProgress", 1f);
			traverse.SetProperty("IsItemAssetBundleLoaded", true);

			traverse.InvokeMethod("InitializeGlobalScene");

			for (float f = 0f; f < 1f; f += Time.deltaTime) {
				yield return new WaitForEndOfFrame();

				var color = traverse.GetField<Color>("_color");
				color.a = 1f - f / 1f;

				traverse.SetField("_color", color);
			}

			//yield break;

			bool continueAuthentication = true;

			yield return traverse.Instance.StartCoroutine(BeginAuthenticateApplication(delegate (AuthenticateApplicationView ev) {
				try {
					GlobalSceneLoader.IsInitialised = true;
					if (ev != null && ev.IsEnabled) {
						Configuration.EncryptionInitVector = ev.EncryptionInitVector;
						Configuration.EncryptionPassPhrase = ev.EncryptionPassPhrase;
						ApplicationDataManager.IsOnline = true;

						Debug.Log("OnAuthenticateApplication");

						Singleton<GameServerManager>.Instance.CommServer = new PhotonServer(ev.CommServer);
						Singleton<GameServerManager>.Instance.AddPhotonGameServers(ev.GameServers.FindAll((PhotonView i) => i.UsageType == PhotonUsageType.All));


						if (ev.WarnPlayer) {
							continueAuthentication = false;

							HandleVersionWarning();
						}
					} else {
						continueAuthentication = false;

						Debug.Log($"OnAuthenticateApplication failed with 4.7.1/{ApplicationDataManager.Channel}: {GlobalSceneLoader.ErrorMessage}");

						GlobalSceneLoaderHook.ErrorMessage = NekoNexusLocalizedStrings.PleaseUpdate;

						HandleVersionError();
					}
				} catch (Exception ex) {
					continueAuthentication = false;

					GlobalSceneLoaderHook.ErrorMessage = ex.Message + " " + ex.StackTrace;

					Debug.LogError($"OnAuthenticateApplication crashed with 4.7.1/{ApplicationDataManager.Channel}: {GlobalSceneLoader.ErrorMessage}");
					HandleApplicationAuthenticationError(NekoNexusLocalizedStrings.CheckConnection);
				}
			}, delegate (Exception e) {
				continueAuthentication = false;

				OnAuthenticateApplicationException(e);
			}));

			if (!continueAuthentication)
				yield break;

			Debug.Log("Start LoginByChannel");

			if (PlayerDataManager.IsTestBuild) {
				PopupSystem.ShowMessage(LocalizedStrings.Warning, NekoNexusLocalizedStrings.ThisIsATestBuild, PopupSystem.AlertType.OK, delegate () {
					Singleton<AuthenticationManager>.Instance.LoginByChannel();
				});
			} else {
				Singleton<AuthenticationManager>.Instance.LoginByChannel();
			}

			yield break;
		}

		private static IEnumerator BeginAuthenticateApplication(Action<AuthenticateApplicationView> callback, Action<Exception> errorCallback) {
			Debug.Log("BeginAuthenticateApplication " + Configuration.WebserviceBaseUrl);

			yield return ApplicationWebServiceClient.AuthenticateApplication("4.7.1", ApplicationDataManager.Channel, string.Empty, delegate (AuthenticateApplicationView authView) {
				Debug.Log("Connected to : " + Configuration.WebserviceBaseUrl);

				callback(authView);
			}, delegate (Exception exception) {
				errorCallback(exception);
			});

			yield break;
		}

		private static void OnAuthenticateApplicationException(Exception exception) {
			ErrorMessage = exception.Message;

			Debug.LogError($"An exception occurred while authenticating the application with 4.7.1/{ApplicationDataManager.Channel}: {exception.Message}");
			HandleApplicationAuthenticationError(NekoNexusLocalizedStrings.CheckConnection);
		}

		private static void RetryAuthenticateApplication() {
			if (traverse == null || traverse.Instance == null) {
				Debug.LogError("Cannot retry application authentication because the scene loader is unavailable.");
				return;
			}

			ErrorMessage = string.Empty;
			ApplicationDataManager.IsOnline = false;
			traverse.Instance.StartCoroutine(RetryAuthenticateApplicationAfterDelay());
		}

		private static IEnumerator RetryAuthenticateApplicationAfterDelay() {
			// Give a transient service restart or network interruption a moment to recover.
			yield return new WaitForSeconds(2f);
			if (traverse != null && traverse.Instance != null) {
				traverse.Instance.StartCoroutine(Start());
			}
		}

		private static void HandleApplicationAuthenticationError(string message) {
			ChannelType channel = ApplicationDataManager.Channel;
			switch (channel) {
				case ChannelType.Steam:
				case ChannelType.WindowsStandalone:
				case ChannelType.OSXStandalone:
					PopupSystem.Show(new NekoNexusPopupDialog(
						LocalizedStrings.Error,
						message + Environment.NewLine + NekoNexusLocalizedStrings.WSConnectionFailed + Environment.NewLine + Environment.NewLine + NekoNexusLocalizedStrings.RetryOrQuit,
						PopupSystem.AlertType.OKCancel,
						NekoNexusLocalizedStrings.Retry,
						new Action(RetryAuthenticateApplication),
						NekoNexusLocalizedStrings.Quit,
						new Action(Application.Quit),
						string.Empty,
						null));
					break;
				case ChannelType.IPhone:
				case ChannelType.IPad:
				case ChannelType.Android:
					PopupSystem.ShowError(LocalizedStrings.Error, message, PopupSystem.AlertType.OK, new Action(RetryAuthenticateApplication));
					break;
				default:
					if (channel != ChannelType.WebPortal && channel != ChannelType.WebFacebook) {
						PopupSystem.ShowError(LocalizedStrings.Error, message + Environment.NewLine + NekoNexusLocalizedStrings.ClientTypeNotSupported, PopupSystem.AlertType.OK, new Action(Application.Quit));
					} else {
						PopupSystem.ShowError(LocalizedStrings.Error, message, PopupSystem.AlertType.None);
					}
					break;
			}
		}

		private static void HandleVersionWarning() {
			ChannelType channel = ApplicationDataManager.Channel;
			switch (channel) {
				case ChannelType.Steam:
					PopupSystem.ShowMessage(
						LocalizedStrings.Warning,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateSteam),
						PopupSystem.AlertType.OKCancel,
						new Action(OpenSteamStorePage),
						NekoNexusLocalizedStrings.Update,
						new Action(Singleton<AuthenticationManager>.Instance.LoginByChannel),
						LocalizedStrings.Continue
					);
					break;
				case ChannelType.IPhone:
				case ChannelType.IPad:
					PopupSystem.ShowMessage(
						LocalizedStrings.Warning,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateIOS),
						PopupSystem.AlertType.OKCancel,
						new Action(OpenIosAppStoreUpdatesPage),
						NekoNexusLocalizedStrings.Update,
						new Action(Singleton<AuthenticationManager>.Instance.LoginByChannel),
						LocalizedStrings.Continue
					);
					break;
				case ChannelType.Android:
					PopupSystem.ShowMessage(
						LocalizedStrings.Warning,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateAndroid),
						PopupSystem.AlertType.OKCancel,
						new Action(OpenAndroidAppStoreUpdatesPage),
						NekoNexusLocalizedStrings.Update,
						new Action(Singleton<AuthenticationManager>.Instance.LoginByChannel),
						LocalizedStrings.Continue
					);
					break;
				default:
					if (channel != ChannelType.WebPortal && channel != ChannelType.WebFacebook) {
						PopupSystem.ShowError(LocalizedStrings.Error, string.Format(NekoNexusLocalizedStrings.ClientNotSupported, channel), PopupSystem.AlertType.OK, new Action(Application.Quit));
					} else {
						PopupSystem.ShowMessage(
							LocalizedStrings.Warning,
							string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateBrowser),
							PopupSystem.AlertType.OK,
							LocalizedStrings.Continue,
							new Action(Singleton<AuthenticationManager>.Instance.LoginByChannel)
						);
					}
					break;
			}
		}

		private static void HandleVersionError() {
			ChannelType channel = ApplicationDataManager.Channel;
			switch (channel) {
				case ChannelType.Steam:
					PopupSystem.ShowMessage(
						LocalizedStrings.Error,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateSteam),
						PopupSystem.AlertType.OK,
						NekoNexusLocalizedStrings.Update,
						new Action(OpenSteamStorePage)
					);
					break;
				case ChannelType.IPhone:
				case ChannelType.IPad:
					PopupSystem.ShowMessage(
						LocalizedStrings.Error,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateIOS),
						PopupSystem.AlertType.OK,
						NekoNexusLocalizedStrings.Update,
						new Action(OpenIosAppStoreUpdatesPage)
					);
					break;
				case ChannelType.Android:
					PopupSystem.ShowMessage(
						LocalizedStrings.Error,
						string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateAndroid),
						PopupSystem.AlertType.OK,
						NekoNexusLocalizedStrings.Update,
						new Action(OpenAndroidAppStoreUpdatesPage)
					);
					break;
				default:
					if (channel != ChannelType.WebPortal && channel != ChannelType.WebFacebook) {
						PopupSystem.ShowError(LocalizedStrings.Error, string.Format(NekoNexusLocalizedStrings.ClientNotSupported, channel), PopupSystem.AlertType.OK, new Action(Application.Quit));
					} else {
						PopupSystem.ShowError(LocalizedStrings.Error, string.Format(NekoNexusLocalizedStrings.ClientOutOfDate, NekoNexusLocalizedStrings.ClientUpdateBrowser), PopupSystem.AlertType.None);
					}
					break;
			}
		}

		private static void OpenSteamStorePage() {
			ApplicationDataManager.OpenUrl(string.Empty, "steam://store/291210");
			Application.Quit();
		}

		private static void OpenIosAppStoreUpdatesPage() {
			ApplicationDataManager.OpenUrl(string.Empty, "itms-apps://itunes.com/apps/uberstrike");
			Application.Quit();
		}

		private static void OpenAndroidAppStoreUpdatesPage() {
			ApplicationDataManager.OpenUrl(string.Empty, "market://details?id=com.cmune.uberstrike.android");
			Application.Quit();
		}
		#endregion
	}
}
