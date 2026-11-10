using Cmune.DataCenter.Common.Entities;
using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NekoNexus.Client {
	public enum UpdateChannel {
		Stable,
		Beta
	}

	internal class UpdateCatalog {
		[YamlMember(Alias = "version")]
		public string Version { get; set; }

		[YamlMember(Alias = "build")]
		public string Build { get; set; }

		[YamlMember(Alias = "channel")]
		public UpdateChannel Channel { get; set; }

		[YamlMember(Alias = "platforms")]
		public Dictionary<string, UpdatePlatformDefinition> Platforms { get; set; }
	}

	internal class UpdatePlatformDefinition {
		[YamlMember(Alias = "platform")]
		public string Platform { get; set; }

		[YamlMember(Alias = "files")]
		public List<UpdateFile> Files { get; set; }

		[YamlMember(Alias = "removedFiles")]
		public List<UpdateFile> RemovedFiles { get; set; }
	}

	internal class UpdateFile {
		[YamlMember(Alias = "filename")]
		public string FileName { get; set; }

		[YamlMember(Alias = "description")]
		public string Description { get; set; }

		[YamlMember(Alias = "localPath")]
		public string LocalPath { get; set; }

		[YamlMember(Alias = "remoteURL")]
		public string RemoteURL { get; set; }

		[YamlMember(Alias = "remotePath")]
		public string RemotePath { get; set; }

		[YamlMember(Alias = "filesize")]
		public long FileSize { get; set; }

		[YamlMember(Alias = "optional")]
		public bool IsOptional { get; set; }

		[YamlMember(Alias = "md5sum")]
		public string MD5 { get; set; }

		[YamlMember(Alias = "sha256")]
		public string SHA256 { get; set; }

		[YamlMember(Alias = "sha512")]
		public string SHA512 { get; set; }
	}

	internal class NekoNexusUpdater : AutoMonoBehaviour<NekoNexusUpdater> {
		private static readonly ILog Log = LogManager.GetLogger(nameof(NekoNexusUpdater));

		private DateTime LastUpdateCheckTimeStamp = DateTime.MinValue;

		private UpdateCatalog CachedUpdates;
		private readonly List<UpdateFile> FilesToUpdate = new List<UpdateFile>();
		private readonly List<UpdateFile> FilesToRemove = new List<UpdateFile>();

		private static string UpdateCatalogUrl => $"{NekoNexusClient.Settings.FileServerUrl}/{NekoNexusClient.Settings.UpdateEndpoint}/v2/";
		private static string UpdateCatalog => $"{NekoNexusClient.Settings.UpdateChannel.ToString().ToLower()}/updates.yml";
		private static string UpdatePlatform {
			get {
				switch (Application.platform) {
					case RuntimePlatform.WindowsPlayer:
					case RuntimePlatform.WindowsEditor:
					case RuntimePlatform.WindowsWebPlayer:
						return "win";
					case RuntimePlatform.OSXPlayer:
					case RuntimePlatform.OSXEditor:
					case RuntimePlatform.OSXWebPlayer:
						return "darwin";
					default: break;
				}

				return null;
			}
		}

		private static bool CanIgnoreUpdates => !PlayerDataManager.IsPlayerLoggedIn || PlayerDataManager.AccessLevel >= MemberAccessLevel.SeniorQA;

		private ProgressPopupDialog progressPopup;

		public IEnumerator CheckForUpdatesIfNecessary(Action<UpdateCatalog> catalogDownloadedCallback, Action<string> errorCallback) {
			// Only throttle rapid re-entries (e.g. menu flicker), NOT real checks - we want a freshly
			// published version to be picked up the next time the player hits the menu, not up to an
			// hour later. 20s is enough to avoid spamming the server on quick navigation.
			if (Math.Abs((LastUpdateCheckTimeStamp - DateTime.Now).TotalSeconds) < 20) {
				yield break;
			}

			LastUpdateCheckTimeStamp = DateTime.Now;

			// Make HTTPS downloads work this early in startup (see EnsureHttpsTrust): the update check
			// runs before the game installs its own accept-all cert policy, so without this the WWW
			// download of the catalog fails cert validation ("Update download failed") against modern
			// CAs the ancient Mono runtime doesn't trust.
			EnsureHttpsTrust();

			if (!NekoNexusClient.Settings.AutoUpdates) {
				Log.Info("Automatic updates disabled");

				if (!CanIgnoreUpdates) {
					PopupSystem.ShowMessage("Automatic Updates disabled", "You have disabled automatic updates. If you want to play UberStrike, you may need to update the NekoNexus runtime.\n\nPlease enable automatic updates in order to receive updates.", PopupSystem.AlertType.OK, () => {
						catalogDownloadedCallback?.Invoke(null);
					});
				} else {
					catalogDownloadedCallback?.Invoke(null);
				}

				yield break;
			}

			FilesToUpdate.Clear();
			FilesToRemove.Clear();

			var updateUri = NormalizeUri(string.Join("", new string[] { UpdateCatalogUrl, UpdateCatalog }));

			progressPopup = PopupSystem.ShowProgress(LocalizedStrings.SettingUp, "Checking for updates...");

			UnityRuntime.StartRoutine(DownloadUpdateCatalog(updateUri, delegate (UpdateCatalog updateCatalog) {
				UnityRuntime.StartRoutine(OnUpdateCatalogDownloadComplete(updateCatalog, catalogDownloadedCallback, errorCallback));
			}, delegate (HttpResponse responseHeader) {
				// Tear down the "Checking for updates..." progress popup so it doesn't stay frozen
				// on screen when we continue into the menu after a failed check.
				if (progressPopup != null) {
					PopupSystem.HideMessage(progressPopup);
				}
				errorCallback?.Invoke($"Failed to download update catalog.\n{responseHeader.StatusText}");
			}));

			yield break;
		}

		public static IEnumerator CleanupUpdates() {
			Log.Info("Cleaning cached update files");

			// Delete downloaded update files
			if (Directory.Exists(string.Join("/", new string[] { Application.dataPath, "Updates" }))) {
				var files = Directory.GetFiles(string.Join("/", new string[] { Application.dataPath, "Updates" }), "*");

				foreach (var file in files) {
					File.Delete(file);
				}
			}

			// Delete backup files
			if (Directory.Exists(string.Join("/", new string[] { Application.dataPath, "Updates", "bak" }))) {
				Directory.Delete(string.Join("/", new string[] { Application.dataPath, "Updates", "bak" }), true);
			}

			yield break;
		}

		public static void HandleUpdateAvailable(UpdateCatalog updateCatalog) {
			HandleUpdateAvailable(updateCatalog, null);
		}

		public static void HandleUpdateAvailable(UpdateCatalog updateCatalog, Action ignoreCallback) {
			PopupSystem.ShowMessage("Update available", $"A mandatory update is available. You need to install this update in order to play.\n\nVersion: {updateCatalog.Version ?? "Unknown"} ({updateCatalog.Build ?? "Unknown"})\nChannel: {updateCatalog.Channel.ToString() ?? "Unknown"}\n\nIf you choose to ignore this update, you will be asked again the next time you launch UberStrike.", PopupSystem.AlertType.OKCancel, delegate {
				UnityRuntime.StartRoutine(Instance.InstallUpdates(HandleUpdateComplete, delegate (string message) {
					HandleUpdateError(message, delegate {
						ignoreCallback?.Invoke();
					});
				}));
			}, "Update", delegate {
				if (!CanIgnoreUpdates) {
					Application.Quit();
				} else {
					ignoreCallback?.Invoke();
				}
			}, CanIgnoreUpdates ? "Ignore" : "Quit");
		}

		public static void HandleUpdateComplete() {
			PopupSystem.ShowMessage("Update Complete", "Updates have been installed successfully. In order to complete the installation, UberStrike needs to be restarted.", PopupSystem.AlertType.OK, delegate {
				System.Diagnostics.Process.Start(Path.Combine(Directory.GetCurrentDirectory(), "UberStrike.exe")).WaitForExit(1000);
				Application.Quit();
			});
		}

		public static void HandleUpdateError(string message) {
			HandleUpdateError(message, null);
		}

		public static void HandleUpdateError(string message, Action callback = null) {
			Log.Error(message);
			PopupSystem.ShowMessage("Error", message, PopupSystem.AlertType.OK, () => {
				callback?.Invoke();
			});
		}



		private IEnumerator DownloadUpdateCatalog(string updateUri, Action<UpdateCatalog> successCallback, Action<HttpResponse> errorCallback) {
			Log.Info($"Attempting to download update catalog from {updateUri}");

			// Not System.Net: Mono's TLS can't complete a TLS 1.2+ handshake. WebDownload uses Windows'
			// WinHTTP for https (TLS 1.2/1.3) and Unity's WWW for http.
			using (var loader = WebDownload.Get(updateUri)) {
				// Hard timeout: a hung request must never lock the game on "Checking for updates".
				float elapsed = 0f;
				while (!loader.isDone) {
					elapsed += Time.deltaTime;
					if (elapsed > 25f) {
						Log.Error("Update catalog download timed out.");
						errorCallback?.Invoke(new HttpResponse { StatusCode = HttpStatusCode.RequestTimeout, StatusText = "Update check timed out" });
						yield break;
					}
					yield return null;
				}

				// Success = no transport error and we actually got content. Do NOT require a "STATUS"
				// response header: the update host is fronted by a relay/CDN whose response doesn't
				// expose one to Unity's WWW, and the previous check rejected a perfectly good download
				// as "Update download failed" (empty error). This mirrors how SoapClient validates a
				// WWW response - error + content only.
				if (!string.IsNullOrEmpty(loader.error)) {
					Log.Error($"Update catalog download failed: {loader.error}");
					errorCallback?.Invoke(new HttpResponse { StatusCode = HttpStatusCode.ServiceUnavailable, StatusText = loader.error });
					yield break;
				}

				if (string.IsNullOrEmpty(loader.text)) {
					Log.Error("Update catalog download returned no content.");
					errorCallback?.Invoke(new HttpResponse { StatusCode = HttpStatusCode.ServiceUnavailable, StatusText = "Update download failed (empty response)" });
					yield break;
				}

				progressPopup.Progress = 0.3f;
				Log.Info("Successfully downloaded update catalog");

				yield return new WaitForSeconds(0.5f);

				UpdateCatalog updateCatalog = null;
				try {
					var deserializer = new DeserializerBuilder()
						.WithNamingConvention(CamelCaseNamingConvention.Instance)
						.Build();

					updateCatalog = deserializer.Deserialize<UpdateCatalog>(loader.text);

					Log.Info("Successfully parsed update catalog");
					progressPopup.Progress = 0.6f;
				} catch (Exception e) {
					Log.Error($"An error occured while checking for updates:\n{e.Message}");
					Log.Debug(e);

					// Never silently yield break here - that is what hung the game on
					// "Checking for updates". Notify the caller so it continues into the menu.
					errorCallback?.Invoke(new HttpResponse { StatusCode = HttpStatusCode.UnsupportedMediaType, StatusText = "Update manifest could not be parsed" });
					yield break;
				}

				// Invoke the success path OUTSIDE the try so an exception thrown by downstream
				// update handling can't be swallowed into the parse-error branch above.
				if (updateCatalog != null) {
					successCallback?.Invoke(updateCatalog);
				}
			}

			yield break;
		}

		private IEnumerator OnUpdateCatalogDownloadComplete(UpdateCatalog updateCatalog, Action<UpdateCatalog> updateAvailableCallback, Action<string> errorCallback) {
			yield return new WaitForSeconds(1f);

			CachedUpdates = updateCatalog;

			// TryGetValue, not indexer: a manifest that only ships some platforms (e.g. "universal"
			// but no "win") would otherwise throw KeyNotFoundException here and hang the game. The
			// downstream code already null-checks both.
			updateCatalog.Platforms.TryGetValue("universal", out var universalUpdates);
			updateCatalog.Platforms.TryGetValue(UpdatePlatform, out var platformUpdates);

			var totalFiles = (universalUpdates?.Files.Count ?? 0) + (platformUpdates?.Files.Count ?? 0);

			// CRITICAL: file checking AND all popup / callback work must run on the MAIN (Unity)
			// thread. This previously ran inside `new Thread(...)`, which touched PopupSystem and
			// progressPopup (Unity UI) and invoked the callbacks from a background thread. Unity API
			// calls off the main thread silently fail on Mono, so the "Update available" popup never
			// appeared and the game slipped straight into the menu without offering the update. Run
			// it inline on the coroutine (the main thread) and yield between the hash passes so the
			// progress popup stays responsive.
			progressPopup.Text = "Checking files for updates...";

			var checkFailed = false;
			Action<string> wrappedError = (msg) => {
				checkFailed = true;
				errorCallback?.Invoke(msg);
			};

			if (universalUpdates == null) {
				Log.Error("Update catalog does not contain universal platform.");
			} else {
				if (universalUpdates.Files != null) {
					CheckUpdatedFiles(universalUpdates, 0, totalFiles, wrappedError);
				}

				if (universalUpdates.RemovedFiles != null) {
					CheckRemovedFiles(universalUpdates);
				}
			}

			// Let the UI paint the progress between the two (potentially slow) hash passes.
			yield return null;

			if (checkFailed) {
				yield break;
			}

			if (platformUpdates == null) {
				Log.Error($"Update catalog does not contain platform \"{UpdatePlatform}\".");
			} else {
				if (platformUpdates.Files != null) {
					CheckUpdatedFiles(platformUpdates, universalUpdates?.Files.Count ?? 0, totalFiles, wrappedError);
				}

				if (platformUpdates.RemovedFiles != null) {
					CheckRemovedFiles(platformUpdates);
				}
			}

			if (checkFailed) {
				yield break;
			}

			if (FilesToUpdate.Count > 0) {
				PopupSystem.HideMessage(progressPopup);
				Log.Info($"Update available: {CachedUpdates.Version} (Build {CachedUpdates.Build}), files to update: {FilesToUpdate.Count}; files to remove: {FilesToRemove.Count}");
				updateAvailableCallback?.Invoke(CachedUpdates);
			} else {
				Log.Info("No update available - all files match. Up to date.");

				// All hashes match: briefly show an "up to date" confirmation, then continue.
				progressPopup.Text = "NekoNexus is up to date!";
				yield return new WaitForSeconds(0.9f);
				PopupSystem.HideMessage(progressPopup);

				if (FilesToRemove.Count > 0) {
					DeleteRemovedFiles();
				}

				updateAvailableCallback?.Invoke(null);
			}

			yield break;
		}

		private void CheckUpdatedFiles(UpdatePlatformDefinition updateDef, int startIndex, int totalFiles, Action<string> errorCallback) {
			if (updateDef.Files.Count == 0) {
				Log.Info($"Update catalog doesn't contain any files for platform \"{updateDef.Platform}\", aborting...");
				return;
			}

			foreach (var file in updateDef.Files) {
				progressPopup.Text = $"Checking files for updates...\n{file.FileName}";
				progressPopup.Progress = 0.6f + (((float)startIndex + (float)updateDef.Files.IndexOf(file)) / (float)totalFiles) * 0.4f;

				string path = $"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}\\{file.FileName}";

				if (!File.Exists(path)) {
					if (!file.IsOptional) {
						Log.Info($"Local file {file.FileName} doesn't exist");

						FilesToUpdate.Add(file);
					}

					continue;
				} else {
					try {
						var fileNeedsUpdate = false;

						if (!string.IsNullOrEmpty(file.MD5)) {
							using (FileStream fileStream = File.OpenRead(path)) {
								using (var md5 = MD5.Create()) {
									var md5sum = BitConverter.ToString(md5.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();

									if (!md5sum.Equals(file.MD5)) {
										Log.Info($"Local file {file.FileName}: MD5 doesn't match (expected {file.MD5}, got {md5sum})");
										fileNeedsUpdate = true;
									}
								}
							}
						}

						if (!string.IsNullOrEmpty(file.SHA256)) {
							using (FileStream fileStream = File.OpenRead(path)) {
								using (var sha256 = SHA256.Create()) {
									var sha256sum = BitConverter.ToString(sha256.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();

									if (!sha256sum.Equals(file.SHA256)) {
										Log.Info($"Local file {file.FileName}: SHA256 doesn't match (expected {file.SHA256}, got {sha256sum})");
										fileNeedsUpdate = true;
									}
								}
							}
						}

						if (!string.IsNullOrEmpty(file.SHA512)) {
							using (FileStream fileStream = File.OpenRead(path)) {
								using (var sha512 = SHA512.Create()) {
									var sha512sum = BitConverter.ToString(sha512.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();

									if (!sha512sum.Equals(file.SHA512)) {
										Log.Info($"Local file {file.FileName}: SHA512 doesn't match (expected {file.SHA512}, got {sha512sum})");
										fileNeedsUpdate = true;
									}
								}
							}
						}

						if (fileNeedsUpdate) {
							FilesToUpdate.Add(file);
						}
					} catch (Exception e) {
						errorCallback?.Invoke($"An error occured while checking for updates:\n{e.Message}");
						Log.Debug(e);
						return;
					}
				}
			}
		}

		private void CheckRemovedFiles(UpdatePlatformDefinition updateDef) {
			foreach (var file in updateDef.RemovedFiles) {
				string path = $"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}\\{file.FileName}";

				if (File.Exists(path)) {
					Log.Info($"Local file {file.FileName} exists, marked for removal");
					FilesToRemove.Add(file);
				}
			}
		}

		private void DeleteRemovedFiles() {
			if (FilesToRemove.Count == 0) return;

			var backupPath = $"{Application.dataPath}\\Updates\\bak";

			if (!Directory.Exists(backupPath)) {
				Directory.CreateDirectory(backupPath);
			}

			foreach (var file in FilesToRemove) {
				try {
					if (!Directory.Exists($"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}")) {
						continue;
					}

					var filename = $"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}\\{file.FileName}";

					if (File.Exists(filename)) {
						// File will be removed on game restart
						var fileBackupPath = Path.Combine(backupPath, Path.GetFileName(filename) + ".bak");

						if (File.Exists(fileBackupPath)) {
							File.Delete(fileBackupPath);
						}

						File.Move(filename, fileBackupPath);
					}
				} catch (Exception e) {
					Log.Error(e);
				}
			}
		}

		private IEnumerator InstallUpdates(Action updateCompleteCallback, Action<string> errorCallback) {
			if (FilesToUpdate.Count == 0) yield break;

			var backupPath = $"{Application.dataPath}\\Updates\\bak";

			if (!Directory.Exists(backupPath)) {
				Directory.CreateDirectory(backupPath);
			}

			var verifyFailures = new List<string>();

			foreach (var file in FilesToUpdate) {
				progressPopup = PopupSystem.ShowProgress($"Downloading updates... ({FilesToUpdate.IndexOf(file) + 1}/{FilesToUpdate.Count})", $"{file.FileName} ({NekoNexusGUITools.FormatSize(file.FileSize)})");

				var fileUri = NormalizeUri(string.Join("/", new string[] { file.RemoteURL ?? UpdateCatalogUrl, file.RemotePath, file.FileName }));
				using (var loader = WebDownload.Get(fileUri)) {
					Log.Info($"Downloading remote file: {fileUri}");

					// Hard timeout so a hung/stalled download can't freeze the install forever.
					float elapsed = 0f;
					while (!loader.isDone) {
						progressPopup.Progress = loader.progress;
						elapsed += Time.deltaTime;
						if (elapsed > 120f) {
							PopupSystem.HideMessage(progressPopup);
							Log.Error($"Download of {file.FileName} timed out.");
							errorCallback?.Invoke($"Failed to download {file.FileName}:\nDownload timed out. Check your connection and the update server.");
							yield break;
						}
						yield return null;
					}

					PopupSystem.HideMessage(progressPopup);

					// Success = no transport error and we got bytes. No "STATUS" header check (see the
					// catalog download): the relay/CDN doesn't always expose it to WWW.
					if (!string.IsNullOrEmpty(loader.error) || loader.bytes == null || loader.bytes.Length == 0) {
						Log.Error($"Failed to download {file.FileName}: {loader.error}");
						errorCallback?.Invoke($"Failed to download {file.FileName}:\n{(string.IsNullOrEmpty(loader.error) ? "connection failed" : loader.error)}");
						yield break;
					}

					var fileBytes = loader.bytes;

					try {
						if (!Directory.Exists($"{Application.dataPath}\\Updates")) {
							Directory.CreateDirectory($"{Application.dataPath}\\Updates");
						}

						if (!string.IsNullOrEmpty(file.MD5)) {
							using (var md5 = MD5.Create()) {
								var md5sum = BitConverter.ToString(md5.ComputeHash(fileBytes)).Replace("-", "").ToLowerInvariant();

								if (!md5sum.Equals(file.MD5)) {
									Log.Error($"Downloaded file {file.FileName}: MD5 doesn't match (expected {file.MD5}, got {md5sum})");
									verifyFailures.Add(file.FileName);

									continue;
								}
							}
						}

						if (!string.IsNullOrEmpty(file.SHA256)) {
							using (var sha256 = SHA256.Create()) {
								var sha256sum = BitConverter.ToString(sha256.ComputeHash(fileBytes)).Replace("-", "").ToLowerInvariant();

								if (!sha256sum.Equals(file.SHA256)) {
									Log.Error($"Downloaded file {file.FileName}: SHA256 doesn't match (expected {file.SHA256}, got {sha256sum})");
									verifyFailures.Add(file.FileName);

									continue;
								}
							}
						}

						if (!string.IsNullOrEmpty(file.SHA512)) {
							using (var sha512 = SHA512.Create()) {
								var sha512sum = BitConverter.ToString(sha512.ComputeHash(fileBytes)).Replace("-", "").ToLowerInvariant();

								if (!sha512sum.Equals(file.SHA512)) {
									Log.Error($"Downloaded file {file.FileName}: SHA512 doesn't match (expected {file.SHA512}, got {sha512sum})");
									verifyFailures.Add(file.FileName);

									continue;
								}
							}
						}
					} catch (Exception e) {
						PopupSystem.HideMessage(progressPopup);

						errorCallback?.Invoke($"An error occured while downloading updates: {e.Message}");
						Log.Error(e);

						yield break;
					}

					try {
						var tempFilename = $"{Application.dataPath}\\Updates\\{file.FileName}";
						File.WriteAllBytes(tempFilename, fileBytes);

						if (!Directory.Exists($"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}")) {
							Directory.CreateDirectory($"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}");
						}

						var filename = $"{Path.GetDirectoryName(Application.dataPath)}\\{file.LocalPath}\\{file.FileName}";

						if (File.Exists(filename)) {
							// File will be removed on game restart
							var fileBackupPath = Path.Combine(backupPath, Path.GetFileName(filename) + ".bak");

							if (File.Exists(fileBackupPath)) {
								File.Delete(fileBackupPath);
							}

							File.Move(filename, fileBackupPath);
						}

						File.Move(tempFilename, filename);
					} catch (Exception e) {
						PopupSystem.HideMessage(progressPopup);

						errorCallback?.Invoke($"An error occured while installing updates: {e.Message}");
						Log.Error(e);

						yield break;
					}
				}
			}

			DeleteRemovedFiles();

			if (verifyFailures.Count > 0) {
				PopupSystem.HideMessage(progressPopup);

				errorCallback?.Invoke($"One or more downloaded files could not be verified. Please contact your server administrator.\n\n{string.Join("\n", verifyFailures.Select(_ => $"- {_}").ToArray())}");

				yield break;
			}

			updateCompleteCallback?.Invoke();
		}

		// Set an accept-all TLS certificate callback before any download, in case anything goes through
		// System.Net (whose Mono TLS stack lacks roots for the host's modern CA). The actual update
		// downloads use Unity's WWW, which has its own working HTTPS path to this host (the game's
		// SoapClient uses WWW over the same HTTPS host successfully), so this is just belt-and-braces.
		private static bool _httpsTrustReady;
		private static void EnsureHttpsTrust() {
			if (_httpsTrustReady) return;
			_httpsTrustReady = true;

			try {
				ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
			} catch (Exception e) {
				Log.Warn("Could not relax certificate validation for updates: " + e.Message);
			}
		}

		private static string NormalizeUri(string uri) {
			var uriBuilder = new UriBuilder(uri);
			uriBuilder.Path = uriBuilder.Path.Replace("//", "/");

			return uriBuilder.Uri.AbsoluteUri;
		}
	}
}
