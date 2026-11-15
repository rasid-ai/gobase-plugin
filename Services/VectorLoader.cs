using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using GoBase.Models;

namespace GoBase.Services
{
	internal record VectorLoadResult(int FeatureCount, int LayerCount, bool Truncated, int Skipped);

	// Reads a vector asset from GET /api/map/assets/{id}/data page by page into the active map. Each page
	// shows up as soon as it arrives: the first of a geometry type makes its feature class and layer,
	// the rest are written straight into it.
	internal static class VectorLoader
	{
		// The web map's own ceiling: at most six pages of up to 10,000 features per view.
		public const int MaxPages = 6;

		// Features written per turn on the MCT: about a fifth of a second each, so the map stays responsive.
		private const int ChunkSize = 1000;

		// Streams the features in bbox into the asset's layers. existing are the layers already showing
		// the asset: they are emptied when the first page arrives, so the old view stays until the new
		// one starts to draw. Geometry types with no layer yet get one, in newLayers.Container.
		public static async Task<VectorLoadResult> StreamAsync(
			GoBaseApiClient api,
			string assetId,
			IReadOnlyList<(FeatureLayer Layer, string GeometryType)> existing,
			(ILayerContainerEdit Container, string Name) newLayers,
			GeoBox bbox,
			IProgress<string> progress,
			CancellationToken cancellationToken)
		{
			var layers = existing
				.GroupBy(layer => layer.GeometryType)
				.ToDictionary(group => group.Key, group => group.Select(layer => layer.Layer).ToList());
			// Read here, on the caller's thread; Invalidate itself must run on the MCT.
			var mapView = MapView.Active;
			var cleared = existing.Count == 0;
			var written = 0;
			var skipped = 0;
			var pages = 0;
			long? cursor = null;

			do
			{
				progress?.Report(written == 0
					? $"Reading features, page {pages + 1}..."
					: $"{written:N0} features on the map, reading page {pages + 1}...");
				var page = await api.GetFeaturePageAsync(assetId, bbox, cursor, cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();

				var buckets = Sort(page.Features, ref skipped);
				if (!cleared)
				{
					await QueuedTask.Run(() =>
					{
						var all = layers.Values.SelectMany(list => list).ToList();
						foreach (var featureClass in FeatureClassesOf(all))
							using (featureClass)
								FeatureWriter.Clear(featureClass);
						Invalidate(mapView, all);
					});
					cleared = true;
				}

				foreach (var (geometryType, features) in buckets)
				{
					if (layers.TryGetValue(geometryType, out var showing))
					{
						// In chunks, so the MCT, which also draws the map, is free between them, and a
						// move stops the stream within one chunk.
						foreach (var chunk in features.Chunk(ChunkSize))
						{
							cancellationToken.ThrowIfCancellationRequested();
							written += await QueuedTask.Run(() =>
							{
								var count = 0;
								foreach (var featureClass in FeatureClassesOf(showing))
									using (featureClass)
										count = FeatureWriter.Insert(featureClass, chunk);
								Invalidate(mapView, showing);
								return count;
							});
						}
						continue;
					}

					// One layer per geometry type. Only an asset with a single type has a layer named
					// just after it; with several, each says what it holds.
					var labelled = layers.Count > 0 || buckets.Count > 1;
					var layerName = labelled ? $"{newLayers.Name} ({LabelOf(geometryType)})" : newLayers.Name;
					var part = await Task.Run(() => WriteGeoJsonFile(assetId, geometryType, features, layerName));
					try
					{
						var featureClassPath = await LayerLoader.ConvertGeoJsonAsync(part, assetId);
						var layer = await QueuedTask.Run(
							() => LayerLoader.AddFeatureLayer(featureClassPath, newLayers.Container, layerName));
						layers[geometryType] = [layer];
						written += features.Count;
					}
					finally
					{
						TryDelete(part.Path);
					}
				}

				cursor = page.NextCursor;
				pages++;
			}
			while (cursor != null && pages < MaxPages);

			return new VectorLoadResult(written, layers.Count, cursor != null, skipped);
		}

		// Runs on the MCT. Writes outside an edit operation do not tell the map, so the layers are
		// redrawn; only they, so the basemap and other layers keep what they have drawn.
		private static void Invalidate(MapView mapView, IEnumerable<FeatureLayer> layers)
		{
			var extent = mapView?.Extent;
			if (extent == null)
				return;
			foreach (var layer in layers)
				mapView.Invalidate(layer, extent);
		}

		// Runs on the MCT. Each feature class once, however many layers show it.
		private static List<ArcGIS.Core.Data.FeatureClass> FeatureClassesOf(IEnumerable<FeatureLayer> layers)
		{
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var featureClasses = new List<ArcGIS.Core.Data.FeatureClass>();
			foreach (var layer in layers)
			{
				var featureClass = layer.GetFeatureClass();
				if (featureClass == null)
					continue;
				if (seen.Add(featureClass.GetPath().ToString()))
					featureClasses.Add(featureClass);
				else
					featureClass.Dispose();
			}
			return featureClasses;
		}

		// An ArcGIS layer holds one geometry type, and many GoBase assets mix them,
		// so features are sorted into one bucket per Pro geometry type.
		private static Dictionary<string, List<JsonElement>> Sort(IEnumerable<JsonElement> features, ref int skipped)
		{
			var buckets = new Dictionary<string, List<JsonElement>>();
			foreach (var feature in features)
			{
				var geometryType = ProGeometryTypeOf(feature);
				if (geometryType == null)
				{
					skipped++;
					continue;
				}

				if (!buckets.TryGetValue(geometryType, out var bucket))
					buckets[geometryType] = bucket = new List<JsonElement>();
				bucket.Add(feature);
			}
			return buckets;
		}

		// The geometry_type keyword JSONToFeatures expects, or null for features it cannot hold
		// (no geometry, or a GeometryCollection). Pro lines and polygons are multipart already.
		private static string ProGeometryTypeOf(JsonElement feature)
		{
			if (!feature.TryGetProperty("geometry", out var geometry) ||
				geometry.ValueKind != JsonValueKind.Object ||
				!geometry.TryGetProperty("type", out var type))
				return null;

			return type.GetString() switch
			{
				"Point" => "POINT",
				"MultiPoint" => "MULTIPOINT",
				"LineString" or "MultiLineString" => "POLYLINE",
				"Polygon" or "MultiPolygon" => "POLYGON",
				_ => null
			};
		}

		private static GeoJsonPart WriteGeoJsonFile(
			string assetId, string geometryType, List<JsonElement> features, string layerName)
		{
			var folder = Path.Combine(Path.GetTempPath(), "gobase_downloads");
			Directory.CreateDirectory(folder);

			var path = Path.Combine(folder, $"{assetId}_{geometryType.ToLowerInvariant()}_{Guid.NewGuid():N}.geojson");
			using (var stream = File.Create(path))
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("type", "FeatureCollection");
				writer.WriteStartArray("features");
				foreach (var feature in features)
					feature.WriteTo(writer);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			return new GeoJsonPart(path, geometryType, layerName);
		}

		private static string LabelOf(string geometryType) => geometryType switch
		{
			"POINT" => "points",
			"MULTIPOINT" => "multipoints",
			"POLYLINE" => "lines",
			"POLYGON" => "polygons",
			_ => geometryType.ToLowerInvariant()
		};

		private static void TryDelete(string path)
		{
			try { File.Delete(path); } catch { }
		}
	}
}
