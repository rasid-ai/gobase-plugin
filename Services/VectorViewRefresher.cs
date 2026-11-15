using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArcGIS.Core.Events;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Events;
using GoBase.Models;

namespace GoBase.Services
{
	// Keeps GoBase vector layers showing what is on screen, as the web map does. Shortly after the
	// active map stops moving, each asset with a layer in it is streamed again for the new view. Moving
	// again stops a stream that is no longer wanted.
	//
	// Which asset a layer shows comes from its feature class's name (GoBaseFeatureClass). Only which
	// view each asset was last streamed for is remembered, so that panning inside it fetches nothing.
	internal static class VectorViewRefresher
	{
		// How long the map must stay still before fetching, so a pan is one fetch, not dozens.
		private static readonly TimeSpan SettleFor = TimeSpan.FromMilliseconds(400);

		private record Fetched(GeoBox Box, bool Truncated);

		private static readonly ConcurrentDictionary<string, Fetched> LastFetched = new();

		// One stream at a time, whether from "Add to map" or from a move.
		private static readonly SemaphoreSlim Gate = new(1, 1);

		private static DispatcherTimer _settle;
		private static SubscriptionToken _cameraChanged;
		private static CancellationTokenSource _refresh;

		// Safe to call more than once. Must be called on the UI thread.
		public static void Start()
		{
			if (_settle != null)
				return;

			_settle = new DispatcherTimer { Interval = SettleFor };
			_settle.Tick += async (_, _) =>
			{
				_settle.Stop();
				await RefreshAsync();
			};
			_cameraChanged = MapViewCameraChangedEvent.Subscribe(OnCameraChanged);
		}

		public static void Stop()
		{
			if (_cameraChanged != null)
				MapViewCameraChangedEvent.Unsubscribe(_cameraChanged);
			_cameraChanged = null;
			_settle?.Stop();
			_settle = null;
			_refresh?.Cancel();
		}

		// "Add to map": streams the asset's features in bbox into new layers in the active map. A move
		// does not stop it; once it is done, the view it ended on is caught up with.
		public static async Task<VectorLoadResult> AddAsync(
			GoBaseApiClient api, AssetItem asset, GeoBox bbox, IProgress<string> progress)
		{
			var map = MapView.Active?.Map
				?? throw new InvalidOperationException("Open a map in ArcGIS Pro before loading this layer.");

			_refresh?.Cancel();
			await Gate.WaitAsync();
			try
			{
				LastFetched.TryRemove(asset.AssetId, out _);
				var root = await QueuedTask.Run(() => LayerLoader.RootGroup(map));
				var result = await VectorLoader.StreamAsync(
					api, asset.AssetId, [], (root, asset.Name), bbox, progress, CancellationToken.None);
				Remember(asset.AssetId, bbox, result.Truncated);
				ErrorLog.Write($"GoBase: added vector {asset.AssetId} for {bbox?.ToQueryValue() ?? "the whole asset"}: {result.FeatureCount:N0} features");
				return result;
			}
			finally
			{
				Gate.Release();
				Settle();
			}
		}

		private static void Remember(string assetId, GeoBox bbox, bool truncated) =>
			LastFetched[assetId] = new Fetched(bbox, truncated);

		// Raised many times while the map moves, possibly off the UI thread: stops what is streaming
		// and restarts the wait.
		private static void OnCameraChanged(MapViewCameraChangedEventArgs args)
		{
			if (args.MapView != MapView.Active)
				return;
			_refresh?.Cancel();
			Settle();
		}

		private static void Settle() =>
			_settle?.Dispatcher.BeginInvoke(() =>
			{
				if (_settle == null)
					return;
				_settle.Stop();
				_settle.Start();
			});

		private static async Task RefreshAsync()
		{
			_refresh?.Cancel();
			var refresh = _refresh = new CancellationTokenSource();

			await Gate.WaitAsync();
			try
			{
				if (!refresh.IsCancellationRequested)
					await RefreshActiveMapAsync(refresh.Token);
			}
			catch (OperationCanceledException) when (refresh.IsCancellationRequested)
			{
				// The map moved on; the next refresh streams the view it stopped at.
			}
			catch (Exception ex)
			{
				// A timer callback has no one to report to. The next move tries again.
				ErrorLog.Write($"GoBase: refreshing vector layers failed: {ex}");
			}
			finally
			{
				Gate.Release();
			}
		}

		private static async Task RefreshActiveMapAsync(CancellationToken cancellationToken)
		{
			var map = MapView.Active?.Map;
			if (map == null)
				return;

			var layers = await QueuedTask.Run(() => FindAssetLayers(map));
			ErrorLog.Write($"GoBase: map stopped moving, {layers.Count} GoBase vector layer(s) in it");
			if (layers.Count == 0)
				return;

			// Zoomed out past the edge of the world: nothing sensible to ask for.
			var box = await MapExtent.GetCurrentAsync();
			if (box == null)
				return;

			// Signed out, or the key was forgotten: the layers keep what they show.
			if (string.IsNullOrEmpty(AuthService.Instance.ApiKey) && !AuthService.Instance.Load())
				return;

			var api = new GoBaseApiClient(ApiClient.Instance);
			foreach (var asset in layers.GroupBy(layer => layer.FeatureClass.AssetId))
			{
				if (AlreadyShown(asset.Key, box))
				{
					ErrorLog.Write($"GoBase: vector {asset.Key} already shows {box.ToQueryValue()}");
					continue;
				}

				// Half-written if the stream is stopped, so not to be skipped next time.
				LastFetched.TryRemove(asset.Key, out _);
				try
				{
					var existing = asset.Select(layer => (layer.Layer, layer.FeatureClass.GeometryType)).ToList();
					var newLayers = await QueuedTask.Run(() => PlaceForNewLayers(existing[0].Layer));
					var result = await VectorLoader.StreamAsync(
						api, asset.Key, existing, newLayers, box, null, cancellationToken);
					Remember(asset.Key, box, result.Truncated);
					ErrorLog.Write($"GoBase: refreshed vector {asset.Key} for {box.ToQueryValue()}: {result.FeatureCount:N0} features"
						+ (result.Truncated ? ", cut short by the page limit" : ""));
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					ErrorLog.Write($"GoBase: refreshing vector {asset.Key} failed: {ex}");
				}
			}
		}

		// The last stream covered this view completely, or it was this very view and the server's cap
		// stopped it: asking again would bring the same features.
		private static bool AlreadyShown(string assetId, GeoBox box)
		{
			if (!LastFetched.TryGetValue(assetId, out var last))
				return false;
			if (last.Box == null)
				return !last.Truncated;
			return last.Truncated ? last.Box.SameAs(box) : last.Box.Contains(box);
		}

		private record AssetLayer(FeatureLayer Layer, GoBaseFeatureClass FeatureClass);

		// Runs on the MCT.
		private static List<AssetLayer> FindAssetLayers(Map map) =>
			map.GetLayersAsFlattenedList()
				.OfType<FeatureLayer>()
				.Select(layer => new AssetLayer(layer, GoBaseFeatureClass.Of(layer)))
				.Where(layer => layer.FeatureClass != null)
				.ToList();

		// Runs on the MCT. Where a geometry type the asset had no layer for goes, and what it is called:
		// next to the asset's other layers, named after their group, or after the single layer.
		private static (ILayerContainerEdit Container, string Name) PlaceForNewLayers(FeatureLayer layer)
		{
			if (layer.Parent is GroupLayer group && !LayerLoader.IsRootGroup(group))
				return (group, group.Name);
			return ((ILayerContainerEdit)layer.Parent, layer.Name);
		}
	}
}
