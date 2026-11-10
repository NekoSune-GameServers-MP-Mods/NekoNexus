using System;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NekoNexus.Client {
	/// <summary>
	/// A GET download with the members of UnityEngine.WWW the client uses (isDone, progress, error,
	/// bytes, text). HTTPS on Windows goes through WinHTTP (TLS 1.2/1.3); everything else through WWW.
	/// </summary>
	public sealed class WebDownload : IDisposable {
		private readonly WWW www;
		private volatile WinHttp.Response response;

		private WebDownload(string url) {
			if (WinHttp.IsAvailable && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
				var worker = new Thread(() => response = WinHttp.Send("GET", url, null, null, 120000));
				worker.IsBackground = true;
				worker.Start();
			} else {
				www = new WWW(url);
			}
		}

		public static WebDownload Get(string url) => new WebDownload(url);

		public bool isDone => www != null ? www.isDone : response != null;
		public float progress => www != null ? www.progress : (response != null ? 1f : 0f);
		public string error => www != null ? www.error : response?.Error;
		public byte[] bytes => www != null ? www.bytes : (response?.Body ?? new byte[0]);
		public string text => www != null ? www.text : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

		public void Dispose() {
			www?.Dispose();
		}
	}
}
