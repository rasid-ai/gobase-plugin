using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;
using GoBase.Models;

namespace GoBase.Services
{
	// A cloud storage connection file (.acs) holding one set of temporary lake credentials.
	// Pro cannot open a COG from a URL; it reads one through such a file, signing its own
	// range requests to the bucket, the way QGIS reads /vsis3/ paths.
	internal record ConnectionFile(string Path, string AssetId, DateTimeOffset ExpiresAt)
	{
		// The raster inside the connection: <file>.acs\raster\<dataset>\<name>.tif
		public string DatasetPath(string key) =>
			System.IO.Path.Combine(Path, key.Replace('/', System.IO.Path.DirectorySeparatorChar));
	}

	internal static class CloudRasterConnection
	{
		// Per Windows user and outside every project, because each file holds credentials.
		public static string Folder { get; } = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoBase", "connections");

		// <asset id>_<expiry, Unix seconds>_<random>.acs. The name alone says which asset a layer
		// shows and when it needs new credentials, so nothing else has to be stored, and a project
		// reopened tomorrow can still be refreshed.
		private static readonly Regex FileName = new(
			@"(?<asset>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})_(?<expires>\d{9,11})_[0-9a-f]{6}\.acs",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public static async Task<ConnectionFile> CreateAsync(RasterAccess access)
		{
			Directory.CreateDirectory(Folder);

			var expires = access.Credentials.ExpiresAt;
			// The random part keeps two clicks within one second from sharing a file.
			var random = Guid.NewGuid().ToString("N")[..6];
			var name = FormattableString.Invariant($"{access.AssetId}_{expires.ToUnixTimeSeconds()}_{random}.acs");
			var endpoint = new Uri(access.Endpoint);

			// Provider options, as the tool's value table: one "NAME VALUE" row each, rows joined by ';'.
			// MinIO is Pro's provider for any S3-compatible store. Path-style addressing, because the
			// bucket is in the path, not the host name. The session token is what makes the keys temporary.
			var options = string.Join(";",
				"AWS_HTTPS " + (endpoint.Scheme == Uri.UriSchemeHttps ? "YES" : "NO"),
				"AWS_VIRTUAL_HOSTING FALSE",
				"AWS_SESSION_TOKEN " + access.Credentials.SessionToken);

			// Only the first nine parameters: the folder goes into the dataset path instead.
			var parameters = Geoprocessing.MakeValueArray(
				Folder,
				name,
				"MINIO",
				access.Bucket,
				access.Credentials.AccessKeyId,
				access.Credentials.SecretAccessKey,
				access.Region,
				endpoint.Authority,
				options);

			// No flags: in particular not AddToHistory, which would keep the credentials in the project.
			var result = await Geoprocessing.ExecuteToolAsync(
				"management.CreateCloudStorageConnectionFile", parameters, null, null, GPExecuteToolFlags.None);

			var path = Path.Combine(Folder, name);
			if (result.IsFailed || !File.Exists(path))
			{
				// The tool only warns when it cannot reach the bucket, so a failure here is about the inputs.
				var messages = string.Join("\n", result.ErrorMessages.Select(message => message.Text));
				throw new InvalidOperationException($"ArcGIS Pro could not create the lake connection.\n{messages}");
			}

			return new ConnectionFile(path, access.AssetId, expires);
		}

		// The GoBase connection file a layer's data source points into, or null for any other layer.
		public static ConnectionFile Find(string dataSource)
		{
			if (string.IsNullOrEmpty(dataSource))
				return null;

			var match = FileName.Match(dataSource);
			if (!match.Success)
				return null;

			var expires = DateTimeOffset.FromUnixTimeSeconds(
				long.Parse(match.Groups["expires"].Value, CultureInfo.InvariantCulture));
			return new ConnectionFile(Path.Combine(Folder, match.Value), match.Groups["asset"].Value, expires);
		}

		// Deletes connection files whose credentials have run out. They can read nothing any more,
		// and a layer still pointing at one gets a new file when it is refreshed.
		public static void DeleteExpired()
		{
			if (!Directory.Exists(Folder))
				return;

			foreach (var path in Directory.EnumerateFiles(Folder, "*.acs"))
			{
				var file = Find(Path.GetFileName(path));
				if (file == null || file.ExpiresAt > DateTimeOffset.UtcNow)
					continue;

				try
				{
					File.Delete(path);
				}
				catch (IOException)
				{
					// Still open in Pro. The next pass tries again.
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
	}
}
