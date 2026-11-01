using System;
using System.Text.Json.Serialization;

namespace GoBase.Models
{
	// GET /api/map/assets/{asset_id}/raster-access: where one COG is in the lake, and
	// short-lived credentials that read it. Pro reads the file itself; the server never sends pixels.
	internal class RasterAccess
	{
		[JsonPropertyName("asset_id")]
		public string AssetId { get; set; }

		// endpoint/bucket/key, path-style. Reading it needs the credentials; it is not a presigned link.
		[JsonPropertyName("url")]
		public string Url { get; set; }

		// The lake as this computer reaches it: scheme, host and port, e.g. "http://100.64.0.10:9000".
		[JsonPropertyName("endpoint")]
		public string Endpoint { get; set; }

		[JsonPropertyName("region")]
		public string Region { get; set; }

		[JsonPropertyName("bucket")]
		public string Bucket { get; set; }

		// The object's key inside the bucket, e.g. "raster/esri_landcover_landuse/lulc_2021.tif".
		[JsonPropertyName("key")]
		public string Key { get; set; }

		[JsonPropertyName("credentials")]
		public LakeCredentials Credentials { get; set; }
	}

	// Temporary S3 credentials. They read this one object and list the names in its folder.
	internal class LakeCredentials
	{
		[JsonPropertyName("access_key_id")]
		public string AccessKeyId { get; set; }

		[JsonPropertyName("secret_access_key")]
		public string SecretAccessKey { get; set; }

		[JsonPropertyName("session_token")]
		public string SessionToken { get; set; }

		// After this the lake refuses them, and the layer needs new ones.
		[JsonPropertyName("expires_at")]
		public DateTimeOffset ExpiresAt { get; set; }
	}
}
