using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GoBase.Services
{
	// The one HttpClient for the GoBase-Web API. Holds the server address and the API key.
	internal class ApiClient
	{
		public static ApiClient Instance { get; } = new ApiClient();

		private readonly HttpClient _http;

		// Server root without a trailing slash, e.g. "https://portal.example". Paths start with "/api/".
		public string ServerUrl { get; private set; }
		public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

		private ApiClient()
		{
			// GoBase-Web compresses feature pages when the client accepts gzip.
			var handler = new HttpClientHandler
			{
				AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
			};
			_http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

			// Without this, Django REST Framework may answer with its HTML page instead of JSON.
			_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		}

		public void Configure(string serverUrl, string apiKey)
		{
			ServerUrl = NormalizeServerUrl(serverUrl);

			// "Api-Key", not "Bearer": the backend reads Bearer values as JWTs and rejects anything else.
			_http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(apiKey)
				? null
				: new AuthenticationHeaderValue("Api-Key", apiKey);
		}

		// Accepts "portal.example", "https://portal.example/" or ".../api" and returns the bare root.
		public static string NormalizeServerUrl(string serverUrl)
		{
			if (string.IsNullOrWhiteSpace(serverUrl))
				return null;

			var url = serverUrl.Trim().TrimEnd('/');
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
				!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
				url = "https://" + url;
			if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
				url = url[..^4];
			return url;
		}

		public async Task<T> GetAsync<T>(string pathAndQuery, TimeSpan? timeout = null)
		{
			if (string.IsNullOrEmpty(ServerUrl))
				throw new InvalidOperationException("No GoBase server is configured.");

			using var cts = new CancellationTokenSource(timeout ?? Timeout);
			using var response = await _http.GetAsync(ServerUrl + pathAndQuery, cts.Token).ConfigureAwait(false);
			await EnsureSuccessOrThrowAsync(response).ConfigureAwait(false);

			await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
			return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cts.Token).ConfigureAwait(false);
		}

		public async Task<T> PostJsonAsync<T>(string path, object payload, TimeSpan? timeout = null)
		{
			if (string.IsNullOrEmpty(ServerUrl))
				throw new InvalidOperationException("No GoBase server is configured.");

			using var cts = new CancellationTokenSource(timeout ?? Timeout);
			// Serialized up front so the request carries a Content-Length. PostAsJsonAsync streams
			// the body chunked, and a WSGI server such as Django's runserver then reads it as empty.
			using var content = new StringContent(
				JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
			using var response = await _http.PostAsync(ServerUrl + path, content, cts.Token).ConfigureAwait(false);
			await EnsureSuccessOrThrowAsync(response).ConfigureAwait(false);

			await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
			return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cts.Token).ConfigureAwait(false);
		}

		// Saves a file response into destinationFolder, named as the server's Content-Disposition says.
		// Returns the saved file's path.
		public async Task<string> DownloadAsync(
			string pathAndQuery, string destinationFolder, string fallbackFileName, TimeSpan? timeout = null)
		{
			if (string.IsNullOrEmpty(ServerUrl))
				throw new InvalidOperationException("No GoBase server is configured.");

			using var cts = new CancellationTokenSource(timeout ?? Timeout);
			using var response = await _http.GetAsync(
				ServerUrl + pathAndQuery, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			await EnsureSuccessOrThrowAsync(response).ConfigureAwait(false);

			var disposition = response.Content.Headers.ContentDisposition;
			var fileName = SafeFileName(disposition?.FileNameStar ?? disposition?.FileName, fallbackFileName);
			Directory.CreateDirectory(destinationFolder);
			var path = Path.Combine(destinationFolder, fileName);

			try
			{
				await using var file = File.Create(path);
				await response.Content.CopyToAsync(file, cts.Token).ConfigureAwait(false);
			}
			catch (IOException) when (File.Exists(path))
			{
				// Most likely the copy from an earlier click is still open in a viewer, which locks it.
				// That copy is the same file, so use it.
			}

			return path;
		}

		// A bare file name, safe on Windows: no folders, no reserved characters, never empty.
		private static string SafeFileName(string name, string fallback)
		{
			name = Path.GetFileName(name?.Trim().Trim('"') ?? "");
			foreach (var invalid in Path.GetInvalidFileNameChars())
				name = name.Replace(invalid, '_');
			return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? fallback : name;
		}

		private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response)
		{
			if (response.IsSuccessStatusCode)
				return;

			string detail;
			try
			{
				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				using var document = JsonDocument.Parse(json);
				detail = document.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : json;
			}
			catch
			{
				detail = response.ReasonPhrase;
			}

			throw new ApiException(response.StatusCode, detail);
		}
	}

	// A response from GoBase-Web that was not a success. StatusCode tells a bad key (401/403) from a server fault.
	internal class ApiException : Exception
	{
		public HttpStatusCode StatusCode { get; }

		// The server's own explanation: its "detail" field, or the status text.
		public string Detail { get; }

		public bool IsAuthFailure =>
			StatusCode == HttpStatusCode.Unauthorized || StatusCode == HttpStatusCode.Forbidden;

		public ApiException(HttpStatusCode statusCode, string detail)
			: base($"HTTP {(int)statusCode}: {detail}")
		{
			StatusCode = statusCode;
			Detail = detail;
		}
	}
}
