using Cmune.DataCenter.Common.Entities;
using log4net;
using log4net.Config;
using Photon.SocketServer;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel;
using System.Xml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static NekoNexus.WebSocket;

namespace NekoNexus.Realtime.Server {
	public abstract class BaseRealtimeApplication : ApplicationBase {
		protected static readonly ILog Log = LogManager.GetLogger(typeof(BaseRealtimeApplication));

		public static new BaseRealtimeApplication Instance => (BaseRealtimeApplication)ApplicationBase.Instance;
		public virtual WebSocket.ServerType ServerType { get; }

		public string EncryptionPassPhrase { get; private set; }
		public string EncryptionInitVector { get; private set; }

		public Guid Identifier;
		public int PhotonId;
		public WebSocket.SocketClient SocketClient;

		public ApplicationConfiguration Configuration { get; private set; }
		private PeerConfiguration PeerConfiguration;

		public abstract int Peers { get; }

		protected virtual void OnBeforeSetup() { }
		protected virtual void OnSetup() { }
		protected virtual void OnBeforeTearDown() { }
		protected virtual void OnTearDown() { }

		protected abstract PeerBase OnCreatePeer(InitRequest initRequest);

		protected sealed override void Setup() {
			AppDomain.CurrentDomain.UnhandledException += (sender, e) => {
				Log.Error(e.ExceptionObject);
				HandleException(e.ExceptionObject as Exception);
			};

			GlobalContext.Properties["Photon:ApplicationLogPath"] = Path.Combine(ApplicationPath, "logs");

			using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NekoNexus.Realtime.log4net.config")) {
				using (StreamReader reader = new StreamReader(stream)) {
					var logConfig = new XmlDocument();
					logConfig.LoadXml(reader.ReadToEnd());

					XmlConfigurator.Configure(logConfig.DocumentElement);
				}
			}

			try {
				using (var input = File.OpenText(Path.GetFullPath(Path.Combine(BinaryPath, "NekoNexus.Realtime.yml")))) {
					var deserializer = new DeserializerBuilder()
						.WithNamingConvention(PascalCaseNamingConvention.Instance)
						.Build();

					Configuration = deserializer.Deserialize<ApplicationConfiguration>(input);

					Configuration.Validate();
				}
			} catch (Exception e) {
				Log.Error($"There was an error parsing the settings file: {e.Message}");
				Log.Debug(e);

				Configuration = new ApplicationConfiguration();
			}

			// Allow overriding the master host without editing the yml (handy for Docker /
			// running the same build against different servers). Set by the host launcher's
			// --master-host flag, or directly via the environment.
			var masterHostOverride = Environment.GetEnvironmentVariable("PARADISE_MASTER_HOST");
			if (!string.IsNullOrWhiteSpace(masterHostOverride)) {
				Log.Info($"Overriding MasterHostname -> {masterHostOverride} (PARADISE_MASTER_HOST)");
				Configuration.MasterHostname = masterHostOverride;
			}

			// Per-instance identity overrides (env vars / host flags) so each node can be configured
			// WITHOUT a bespoke yml - this is what makes running multiple game-server nodes practical:
			// give each Docker container its own NEKONEXUS_IDENTIFIER / NEKONEXUS_PHOTON_ID /
			// NEKONEXUS_PASSPHRASE and they stop all colliding on the default 2222.../PhotonId 2.
			// Applies to THIS process's app (Comm or Game). Unset values fall back to the yml.
			var appSettings = ServerType == WebSocket.ServerType.Comm
				? Configuration.CommApplicationSettings
				: Configuration.GameApplicationSettings;
			if (appSettings != null) {
				var idOverride = Environment.GetEnvironmentVariable("NEKONEXUS_IDENTIFIER");
				if (!string.IsNullOrWhiteSpace(idOverride) && Guid.TryParse(idOverride, out var guidOverride)) {
					Log.Info($"Overriding {ServerType} ApplicationIdentifier -> {guidOverride} (NEKONEXUS_IDENTIFIER)");
					appSettings.ApplicationIdentifier = guidOverride;
				}

				var photonOverride = Environment.GetEnvironmentVariable("NEKONEXUS_PHOTON_ID");
				if (!string.IsNullOrWhiteSpace(photonOverride) && int.TryParse(photonOverride, out var photonIdOverride)) {
					Log.Info($"Overriding {ServerType} PhotonId -> {photonIdOverride} (NEKONEXUS_PHOTON_ID)");
					appSettings.PhotonId = photonIdOverride;
				}

				var passOverride = Environment.GetEnvironmentVariable("NEKONEXUS_PASSPHRASE");
				if (!string.IsNullOrWhiteSpace(passOverride)) {
					Log.Info($"Overriding {ServerType} EncryptionPassPhrase (NEKONEXUS_PASSPHRASE)");
					appSettings.EncryptionPassPhrase = passOverride;
				}
			}

			// Android (4.3.10) -> PC lobby chat relay, settable without a custom yml
			// (the Docker image ships its own NekoNexus.Realtime.yml). true/false/1/0.
			var guestChatOverride = Environment.GetEnvironmentVariable("NEKONEXUS_LEGACY_GUEST_CHAT");
			if (!string.IsNullOrWhiteSpace(guestChatOverride)) {
				var value = guestChatOverride.Trim();
				var enabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
				Log.Info($"Overriding EnableLegacyGuestChat -> {enabled} (NEKONEXUS_LEGACY_GUEST_CHAT)");
				Configuration.EnableLegacyGuestChat = enabled;
			}

			OnBeforeSetup();

			PeerConfiguration = new PeerConfiguration(
				heartbeatInterval: Configuration.HeartbeatTimeout,
				heartbeatTimeout: Configuration.HeartbeatInterval,
				compositeHashes: Configuration.CompositeHashBytes.AsReadOnly(),
				junkHashes: Configuration.JunkHashBytes.AsReadOnly()
			) {
				EnableHashVerification = Configuration.EnableHashVerification
			};

			try {
				if (ApplicationWebServiceClient.Instance.AuthenticateApplication("4.7.1", ChannelType.Steam, $"NekoNexusRealtime{ServerType}") is var data) {
					EncryptionInitVector = data.EncryptionInitVector;
					EncryptionPassPhrase = data.EncryptionPassPhrase;
				}
			} catch (EndpointNotFoundException) {
				Log.Fatal("Could not connect to the Web Services. Ensure the Web Services are running and restart the Realtime application.");

				return;
			}

			OnSetup();
		}

		protected sealed override void TearDown() {
			OnBeforeTearDown();

			OnTearDown();
		}

		protected sealed override PeerBase CreatePeer(InitRequest initRequest) {
			Log.Info($"Accepted new connection from {initRequest.RemoteIP}:{initRequest.RemotePort}.");

			initRequest.UserData = PeerConfiguration;
			return OnCreatePeer(initRequest);
		}

		public void HandleException(Exception exception) {
			SocketClient?.SendSync(PacketType.Error, new RealtimeError {
				Type = ServerType,
				ExceptionType = exception.GetType(),
				Message = exception.Message,
				StackTrace = exception.StackTrace
			});
		}
	}
}
