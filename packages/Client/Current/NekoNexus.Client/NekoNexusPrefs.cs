using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Serialization;
using UberStrike.DataCenter.Common.Entities;
using UnityEngine;

namespace NekoNexus.Client {
	public class NekoNexusPrefs {
		public enum Key {
			HasMigratedSettings,

			TelemetryChoice,
			MainMenuMusic,

			EnableDiscordRichPresence,
			ShowDetailedItemStatistics,
			ShowWeaponInKillfeed,
			EnableLandingGrunt,

			EncryptWebServiceTraffic,

			WebServiceBaseUrls,
			WebServiceUrlIndex,
			WebServiceEndpoint,
			WebServicePrefix,
			WebServiceSuffix,

			FileServerUrls,
			FileServerUrlIndex,
			ImagePathEndpoint,

			AutoUpdates,
			UpdateChannel,
			UpdateEndpoint,

			NekoNexusVersion
		}

		public enum TelemetryChoice {
			None,
			Yes,
			No
		}

		public enum MainMenuMusicType {
			None,
			Default,
			Seletron,
			Catalyst,
			ApexTwin,
			GhostIsland,
			DangerZone
		}

		public static Dictionary<Key, object> Defaults => new Dictionary<Key, object> {
			[Key.TelemetryChoice] = TelemetryChoice.None,
			[Key.MainMenuMusic] = MainMenuMusicType.Default,

			[Key.EnableDiscordRichPresence] = true,
			[Key.ShowDetailedItemStatistics] = false,
			[Key.ShowWeaponInKillfeed] = false,
			[Key.EnableLandingGrunt] = true,

			[Key.EncryptWebServiceTraffic] = true,

			[Key.WebServiceBaseUrls] = new List<string> { "https://nekonexus.nekosunevr.co.uk" },
			[Key.WebServiceUrlIndex] = 0,
			[Key.WebServiceEndpoint] = "/2.0",
			[Key.WebServicePrefix] = "UberStrike.DataCenter.WebService.CWS.",
			[Key.WebServiceSuffix] = "Contract.svc",

			[Key.FileServerUrls] = new List<string> { "https://nekonexus.nekosunevr.co.uk" },
			[Key.FileServerUrlIndex] = 0,
			[Key.ImagePathEndpoint] = "/images",

			[Key.AutoUpdates] = false,
			[Key.UpdateChannel] = UpdateChannel.Stable,
			[Key.UpdateEndpoint] = "/updates"
		};

		public TelemetryChoice Telemetry;
		public bool AllowTelemetry => Telemetry == TelemetryChoice.Yes;
		public MainMenuMusicType MainMenuMusic;

		public bool EnableDiscordRichPresence;
		public bool ShowDetailedItemStatistics;
		public bool ShowWeaponInKillfeed;
		public bool EnableLandingGrunt;

		public bool EncryptWebServiceTraffic;

		public List<string> WebServiceBaseUrls;
		public int WebServiceUrlIndex;
		public string WebServiceBaseUrl => WebServiceBaseUrls[WebServiceUrlIndex];

		public string WebServiceEndpoint;
		public string WebServicePrefix;
		public string WebServiceSuffix;

		public List<string> FileServerUrls;
		public int FileServerUrlIndex;
		public string FileServerUrl => FileServerUrls[FileServerUrlIndex];

		public string ImagePathEndpoint;

		public bool AutoUpdates;
		public UpdateChannel UpdateChannel;
		public string UpdateEndpoint;

		public string NekoNexusVersion => Assembly.GetExecutingAssembly().GetName().Version.ToString();

		// Server host renames: existing clients keep the OLD host in their saved settings (the
		// settings XML is imported only once), so when a server moves domains we rewrite it here.
		// Delivered via auto-update, this migrates everyone without a re-patch. Add pairs as needed.
		private static readonly Dictionary<string, string> HostMigrations = new Dictionary<string, string> {
			{ "nekonexustest.nekosunevr.co.uk", "nekonexus.nekosunevr.co.uk" },
		};

		public NekoNexusPrefs() {
			MigrateSettingsIfNeeded();
			ReloadSettings();
			MigrateServerHosts();
		}

		private void MigrateServerHosts() {
			var changed = false;

			List<string> Rewrite(List<string> urls) {
				if (urls == null) return urls;
				for (var i = 0; i < urls.Count; i++) {
					foreach (var map in HostMigrations) {
						if (!string.IsNullOrEmpty(urls[i]) && urls[i].Contains(map.Key)) {
							urls[i] = urls[i].Replace(map.Key, map.Value);
							changed = true;
						}
					}
				}
				return urls;
			}

			WebServiceBaseUrls = Rewrite(WebServiceBaseUrls);
			FileServerUrls = Rewrite(FileServerUrls);

			if (changed) {
				SetKey(Key.WebServiceBaseUrls, WebServiceBaseUrls);
				SetKey(Key.FileServerUrls, FileServerUrls);
			}
		}

		public void SaveSettings() {
			SetKey(Key.TelemetryChoice, Telemetry);
			SetKey(Key.MainMenuMusic, MainMenuMusic);

			SetKey(Key.EnableDiscordRichPresence, EnableDiscordRichPresence);
			SetKey(Key.ShowDetailedItemStatistics, ShowDetailedItemStatistics);
			SetKey(Key.ShowWeaponInKillfeed, ShowWeaponInKillfeed);
			SetKey(Key.EnableLandingGrunt, EnableLandingGrunt);

			SetKey(Key.EncryptWebServiceTraffic, EncryptWebServiceTraffic);

			//SetKey(Key.WebServiceBaseUrls, WebServiceBaseUrls);
			//SetKey(Key.WebServiceUrlIndex, WebServiceUrlIndex);
			//SetKey(Key.WebServiceEndpoint, WebServiceEndpoint);
			//SetKey(Key.WebServicePrefix, WebServicePrefix);
			//SetKey(Key.WebServiceSuffix, WebServiceSuffix);

			//SetKey(Key.FileServerUrls, FileServerUrls);
			//SetKey(Key.FileServerUrlIndex, FileServerUrlIndex);
			//SetKey(Key.ImagePathEndpoint, ImagePathEndpoint);

			SetKey(Key.AutoUpdates, AutoUpdates);
			SetKey(Key.UpdateChannel, (int)UpdateChannel);
			//SetKey(Key.UpdateEndpoint, UpdateEndpoint);
		}

		public void ResetDefaults() {
			foreach (var item in Defaults) {
				SetKey(item.Key, item.Value);
			}

			ReloadSettings();
		}

		private void ReloadSettings() {
			Telemetry = GetKey(Key.TelemetryChoice, (TelemetryChoice)Defaults[Key.TelemetryChoice]);
			MainMenuMusic = GetKey(Key.MainMenuMusic, (MainMenuMusicType)Defaults[Key.MainMenuMusic]);

			EnableDiscordRichPresence = GetKey(Key.EnableDiscordRichPresence, (bool)Defaults[Key.EnableDiscordRichPresence]);
			ShowDetailedItemStatistics = GetKey(Key.ShowDetailedItemStatistics, (bool)Defaults[Key.ShowDetailedItemStatistics]);
			ShowWeaponInKillfeed = GetKey(Key.ShowWeaponInKillfeed, (bool)Defaults[Key.ShowWeaponInKillfeed]);
			EnableLandingGrunt = GetKey(Key.EnableLandingGrunt, (bool)Defaults[Key.EnableLandingGrunt]);

			EncryptWebServiceTraffic = GetKey(Key.EncryptWebServiceTraffic, (bool)Defaults[Key.EncryptWebServiceTraffic]);

			WebServiceBaseUrls = GetKey(Key.WebServiceBaseUrls, (List<string>)Defaults[Key.WebServiceBaseUrls]);
			WebServiceUrlIndex = GetKey(Key.WebServiceUrlIndex, (int)Defaults[Key.WebServiceUrlIndex]);
			WebServiceEndpoint = GetKey(Key.WebServiceEndpoint, (string)Defaults[Key.WebServiceEndpoint]);
			WebServicePrefix = GetKey(Key.WebServicePrefix, (string)Defaults[Key.WebServicePrefix]);
			WebServiceSuffix = GetKey(Key.WebServiceSuffix, (string)Defaults[Key.WebServiceSuffix]);

			FileServerUrls = GetKey(Key.FileServerUrls, (List<string>)Defaults[Key.FileServerUrls]);
			FileServerUrlIndex = GetKey(Key.FileServerUrlIndex, (int)Defaults[Key.FileServerUrlIndex]);
			ImagePathEndpoint = GetKey(Key.ImagePathEndpoint, (string)Defaults[Key.ImagePathEndpoint]);

			AutoUpdates = GetKey(Key.AutoUpdates, (bool)Defaults[Key.AutoUpdates]);
			UpdateChannel = (UpdateChannel)GetKey(Key.UpdateChannel, (int)Defaults[Key.UpdateChannel]);
			UpdateEndpoint = GetKey(Key.UpdateEndpoint, (string)Defaults[Key.UpdateEndpoint]);
		}

		public bool HasKey(Key key) {
			return PlayerPrefs.HasKey($"NekoNexus_{key}");
		}

		public T GetKey<T>(Key key) {
			return GetKey<T>(key, (T)Defaults[key]);
		}

		public T GetKey<T>(Key key, T defaultValue) {
			var _key = $"NekoNexus_{key}";
			var value = defaultValue;

			if (!PlayerPrefs.HasKey(_key))
				return value;

			if (typeof(T) == typeof(bool)) {
				value = (T)(object)Convert.ToBoolean(PlayerPrefs.GetInt(_key));
			} else if (typeof(T) == typeof(int) || value.GetType().IsEnum) {
				value = (T)(object)PlayerPrefs.GetInt(_key);
			} else if (typeof(T) == typeof(float)) {
				value = (T)(object)PlayerPrefs.GetFloat(_key);
			} else if (typeof(T) == typeof(string)) {
				value = (T)(object)PlayerPrefs.GetString(_key);
			} else if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>)) {
				var listType = typeof(T).GetGenericArguments()[0];

				if (listType == typeof(string)) {
					value = (T)(object)PlayerPrefs.GetString(_key).Split(',').ToList();
				} else {
					value = (T)(object)PlayerPrefs.GetString(_key).Split(',').Select(_ => Convert.ChangeType(_, listType)).ToList();
				}
			} else {
				Debug.LogError($"NekoNexusPrefs: Could not read key \"{key}\" because type {typeof(T)} is not supported.");
			}

			return value;
		}

		public bool SetKey<T>(Key key, T value) {
			var _key = $"NekoNexus_{key}";

			if (value.GetType() == typeof(bool)) {
				PlayerPrefs.SetInt(_key, Convert.ToInt32(value));
			} else if (value.GetType() == typeof(int) || value.GetType().IsEnum) {
				PlayerPrefs.SetInt(_key, (int)(object)value);
			} else if (value.GetType() == typeof(float)) {
				PlayerPrefs.SetFloat(_key, (float)(object)value);
			} else if (value.GetType() == typeof(string)) {
				PlayerPrefs.SetString(_key, (string)(object)value);
			} else if (value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(List<>)) {
				var listType = value.GetType().GetGenericArguments()[0];

				if (listType == typeof(string)) {
					PlayerPrefs.SetString(_key, string.Join(",", ((IEnumerable<string>)value).Select(_ => _.ToString()).ToArray()));
				} else {
					PlayerPrefs.SetString(_key, string.Join(",", ((IEnumerable<object>)value).Select(_ => _.ToString()).ToArray()));
				}
			} else {
				Debug.LogError($"NekoNexusPrefs: Could not set key \"{key}\" because type {value.GetType()} is not supported.");
				return false;
			}

			return true;
		}

		private bool HasMigratedSettings => GetKey(Key.HasMigratedSettings, false);

#pragma warning disable CS0618
		private void MigrateSettingsIfNeeded() {
			if (HasMigratedSettings)
				return;

			var settingsFile = Path.Combine(Application.dataPath, "NekoNexus.Settings.Client.xml");

			if (!File.Exists(settingsFile)) {
				SetKey(Key.HasMigratedSettings, true);
				SetKey(Key.NekoNexusVersion, NekoNexusVersion);
			} else {
				var ser = new XmlSerializer(typeof(NekoNexusClientSettings));

				using (var reader = XmlReader.Create(settingsFile, new XmlReaderSettings { IgnoreComments = true })) {
					try {
						var settings = (NekoNexusClientSettings)ser.Deserialize(reader);

						SetKey(Key.EnableDiscordRichPresence, settings.EnableDiscordRichPresence);

						var webServiceUri = new Uri(settings.WebServiceBaseUrl);
						SetKey(Key.WebServiceBaseUrls, new List<string> { webServiceUri.GetLeftPart(UriPartial.Authority) });
						SetKey(Key.WebServiceUrlIndex, 0);
						SetKey(Key.WebServiceEndpoint, webServiceUri.AbsolutePath);
						SetKey(Key.WebServicePrefix, settings.WebServicePrefix);
						SetKey(Key.WebServiceSuffix, settings.WebServiceSuffix);

						var fileServerUri = new Uri(settings.ImagePath);
						SetKey(Key.FileServerUrls, new List<string> { fileServerUri.GetLeftPart(UriPartial.Authority) });
						SetKey(Key.FileServerUrlIndex, 0);

						SetKey(Key.ImagePathEndpoint, fileServerUri.AbsolutePath);

						SetKey(Key.AutoUpdates, settings.AutoUpdates);
						SetKey(Key.UpdateChannel, (int)settings.UpdateChannel);

						var updateUri = new Uri(settings.UpdateUrl);
						SetKey(Key.UpdateEndpoint, updateUri.AbsolutePath);

						SetKey(Key.HasMigratedSettings, true);
						SetKey(Key.NekoNexusVersion, NekoNexusVersion);
					} catch (Exception e) {
						Debug.LogError($"Error while loading NekoNexus settings: {e.Message}");
						Debug.Log(e);
					}
				}
			}
		}
#pragma warning restore CS0618

		public override string ToString() {
			var settings = new List<string> {
				$"[Telemetry:{Telemetry}]",
				$"[MainMenuMusic:{MainMenuMusic}]",
				$"[EnableDiscordRichPresence:{EnableDiscordRichPresence}]",
				$"[ShowDetailedItemStatistics:{ShowDetailedItemStatistics}]",
				$"[ShowWeaponInKillfeed:{ShowWeaponInKillfeed}]",
				$"[EnableLandingGrunt:{EnableLandingGrunt}]",
				$"[EncryptWebServiceTraffic:{EncryptWebServiceTraffic}]",
				$"[WebServiceBaseUrls:{{{string.Join(",", WebServiceBaseUrls.ToArray())}}}]",
				$"[WebServiceUrlIndex:{WebServiceUrlIndex}]",
				$"[@WebServiceBaseUrl:{WebServiceBaseUrl}]",
				$"[WebServiceEndpoint:{WebServiceEndpoint}]",
				$"[WebServicePrefix:{WebServicePrefix}]",
				$"[WebServiceSuffix:{WebServiceSuffix}]",
				$"[FileServerUrls:{{{string.Join(",", FileServerUrls.ToArray())}}}]",
				$"[FileServerUrlIndex:{FileServerUrlIndex}]",
				$"[@FileServerUrl:{FileServerUrl}]",
				$"[ImagePathEndpoint:{ImagePathEndpoint}]",
				$"[AutoUpdates:{AutoUpdates}]",
				$"[UpdateChannel:{UpdateChannel}]",
				$"[UpdateEndpoint:{UpdateEndpoint}]",
			};

			return $"[NekoNexusPrefs:{string.Join("\n", settings.ToArray())}]";
		}
	}

	[Obsolete("Replaced by NekoNexusPrefs")]
	public class NekoNexusClientSettings {
		[XmlIgnore]
		public static string SettingsFilename => Path.Combine(Application.dataPath, "NekoNexus.Settings.Client.xml");

		[XmlElement]
		public bool EnableDiscordRichPresence = true;

		[XmlElement]
		public string WebServiceBaseUrl = "https://nekonexus.nekosunevr.co.uk/2.0/";

		[XmlElement]
		public string WebServicePrefix = "UberStrike.DataCenter.WebService.CWS.";

		[XmlElement]
		public string WebServiceSuffix = "Contract.svc";

		[XmlElement]
		public string ImagePath = "https://nekonexus.nekosunevr.co.uk/images/";

		[XmlElement]
		public bool AutoUpdates = true;

		[XmlElement]
		public UpdateChannel UpdateChannel = UpdateChannel.Stable;

		[XmlElement]
		public string UpdateUrl = "https://nekonexus.nekosunevr.co.uk/updates/";

		[XmlElement]
		public AuthenticateApplicationView ServerOverrides;
	}
}
