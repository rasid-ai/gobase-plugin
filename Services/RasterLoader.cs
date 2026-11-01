using System;
using System.Net.Http;
using System.Threading.Tasks;
using GoBase.Models;

namespace GoBase.Services
{
	// Adds a raster asset (a COG in the lake) to the active map. Pro reads it in place, fetching only
	// the parts on screen, so nothing is downloaded first.
	internal static class RasterLoader
	{
		// Its own client, without the GoBase API key: the lake must never be sent that.
		private static readonly HttpClient LakeProbe = new() { Timeout = TimeSpan.FromSeconds(5) };

		public static async Task LoadAsync(GoBaseApiClient api, AssetItem asset, IProgress<string> progress)
		{
			progress?.Report("Asking GoBase for access to the raster...");
			var access = await api.GetRasterAccessAsync(asset.AssetId);

			progress?.Report("Reaching the lake...");
			await EnsureReachableAsync(access.Endpoint);

			progress?.Report("Connecting ArcGIS Pro to the lake...");
			var connection = await CloudRasterConnection.CreateAsync(access);

			progress?.Report("Adding the raster to the map...");
			// The credentials last an hour by default. RasterCredentialRefresher, started with the
			// module, renews them while the layer is in an open map.
			await LayerLoader.AddRasterAsync(connection.DatasetPath(access.Key), asset.Name);
		}

		// Any HTTP answer will do, even 403: it shows this computer can reach the lake. Without this
		// check, an unreachable lake shows up only as a layer that never draws.
		private static async Task EnsureReachableAsync(string endpoint)
		{
			try
			{
				using var response = await LakeProbe.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
			{
				throw new InvalidOperationException(
					$"This computer cannot reach the lake at {endpoint}. "
					+ "If the lake is only on Tailscale, connect this computer to the tailnet.");
			}
		}
	}
}
