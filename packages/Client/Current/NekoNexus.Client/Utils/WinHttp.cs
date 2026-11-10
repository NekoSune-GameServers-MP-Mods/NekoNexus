using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NekoNexus.Client {
	/// <summary>
	/// HTTPS through Windows' own WinHTTP (winhttp.dll), called directly with P/Invoke.
	///
	/// UberStrike 4.7.1 runs on Unity 4.6, whose Mono 2.0 TLS stops at TLS 1.0 and whose WWW cannot
	/// reach servers that require TLS 1.2+. WinHTTP is the operating system's HTTP client, so it gets
	/// the system's TLS: TLS 1.2 on Windows 7 (with KB3140245) / 8.1 / 10, and TLS 1.3 on Windows 11.
	/// The flat C API is used because Unity's Mono does not implement COM interop (the WinHttpRequest
	/// COM object fails with "The requested feature is not implemented").
	///
	/// Blocking: call it from a worker thread, not Unity's main thread.
	/// </summary>
	public static class WinHttp {
		public sealed class Response {
			public int Status;
			public byte[] Body = new byte[0];
			public string Error;
			public bool Ok => Error == null && Status >= 200 && Status < 300;
		}

		public static bool IsAvailable => Environment.OSVersion.Platform == PlatformID.Win32NT;

		/// <summary>Send a request and read the whole response. Never throws; failures are in Error.</summary>
		public static Response Send(string method, string url, byte[] body, IDictionary<string, string> headers, int timeoutMs = 30000) {
			var response = new Response();
			IntPtr session = IntPtr.Zero, connection = IntPtr.Zero, request = IntPtr.Zero;
			try {
				var uri = new Uri(url);
				var secure = uri.Scheme == Uri.UriSchemeHttps;

				session = WinHttpOpen("UberStrike-NekoNexus/4.7.1", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, null, null, 0);
				if (session == IntPtr.Zero) return Fail(response, "WinHttpOpen");

				// TLS 1.2 + 1.3; Windows versions without TLS 1.3 reject that flag, then ask for 1.2 only.
				uint protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3;
				if (!WinHttpSetOption(session, WINHTTP_OPTION_SECURE_PROTOCOLS, ref protocols, 4)) {
					protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
					if (!WinHttpSetOption(session, WINHTTP_OPTION_SECURE_PROTOCOLS, ref protocols, 4)) {
						return Fail(response, "TLS 1.2 is not enabled on this Windows (install KB3140245 on Windows 7)");
					}
				}
				WinHttpSetTimeouts(session, timeoutMs, timeoutMs, timeoutMs, timeoutMs);

				connection = WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);
				if (connection == IntPtr.Zero) return Fail(response, "WinHttpConnect");

				request = WinHttpOpenRequest(connection, method, uri.PathAndQuery, null, null, IntPtr.Zero, secure ? WINHTTP_FLAG_SECURE : 0);
				if (request == IntPtr.Zero) return Fail(response, "WinHttpOpenRequest");

				var headerText = new StringBuilder();
				if (headers != null) {
					foreach (var header in headers) headerText.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
				}
				var payload = body ?? new byte[0];
				if (!WinHttpSendRequest(request, headerText.ToString(), (uint)headerText.Length, payload, (uint)payload.Length, (uint)payload.Length, IntPtr.Zero)) {
					return Fail(response, "WinHttpSendRequest");
				}
				if (!WinHttpReceiveResponse(request, IntPtr.Zero)) return Fail(response, "WinHttpReceiveResponse");

				uint status = 0, size = 4;
				if (WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, null, ref status, ref size, IntPtr.Zero)) {
					response.Status = (int)status;
				}

				using (var stream = new MemoryStream()) {
					var buffer = new byte[64 * 1024];
					while (true) {
						if (!WinHttpQueryDataAvailable(request, out var available)) return Fail(response, "WinHttpQueryDataAvailable");
						if (available == 0) break;
						var toRead = (uint)Math.Min(buffer.Length, available);
						if (!WinHttpReadData(request, buffer, toRead, out var read)) return Fail(response, "WinHttpReadData");
						if (read == 0) break;
						stream.Write(buffer, 0, (int)read);
					}
					response.Body = stream.ToArray();
				}

				if (response.Status < 200 || response.Status >= 300) response.Error = "HTTP " + response.Status;
			} catch (Exception e) {
				response.Error = "WinHTTP: " + e.Message;
			} finally {
				if (request != IntPtr.Zero) WinHttpCloseHandle(request);
				if (connection != IntPtr.Zero) WinHttpCloseHandle(connection);
				if (session != IntPtr.Zero) WinHttpCloseHandle(session);
			}
			return response;
		}

		private static Response Fail(Response response, string step) {
			var code = Marshal.GetLastWin32Error();
			response.Error = $"WinHTTP {step} failed ({Describe(code)})";
			return response;
		}

		private static string Describe(int code) {
			switch (code) {
				case 12002: return "timed out";
				case 12007: return "host not found";
				case 12029: return "cannot connect";
				case 12030: return "connection aborted";
				case 12175: return "TLS / certificate error";
				case 12157: return "TLS handshake failed - the server and this Windows share no TLS version";
				case 12038: case 12037: case 12045: return "certificate not trusted";
				default: return "error " + code;
			}
		}

		// ---- winhttp.dll -------------------------------------------------------------------------
		private const uint WINHTTP_ACCESS_TYPE_DEFAULT_PROXY = 0;
		private const uint WINHTTP_FLAG_SECURE = 0x00800000;
		private const uint WINHTTP_OPTION_SECURE_PROTOCOLS = 84;
		private const uint WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 = 0x00000800;
		private const uint WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_3 = 0x00002000;
		private const uint WINHTTP_QUERY_STATUS_CODE = 19;
		private const uint WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;

		[DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr WinHttpOpen(string agent, uint accessType, string proxyName, string proxyBypass, uint flags);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint value, uint length);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);

		[DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);

		[DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string objectName, string version, string referrer, IntPtr acceptTypes, uint flags);

		[DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool WinHttpSendRequest(IntPtr request, string headers, uint headersLength, byte[] optional, uint optionalLength, uint totalLength, IntPtr context);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

		[DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool WinHttpQueryHeaders(IntPtr request, uint infoLevel, string name, ref uint buffer, ref uint bufferLength, IntPtr index);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpQueryDataAvailable(IntPtr request, out uint available);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpReadData(IntPtr request, byte[] buffer, uint toRead, out uint read);

		[DllImport("winhttp.dll", SetLastError = true)]
		private static extern bool WinHttpCloseHandle(IntPtr handle);
	}
}
