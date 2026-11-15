using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GoBase.Services
{
	// One GeoJSON file holding a single geometry type, and the layer it becomes.
	internal record GeoJsonPart(string Path, string GeometryType, string LayerName);

	// A feature class made from a GoBase vector asset. Its name says which asset and which geometry
	// type it holds: gobase_<asset id, 32 hex>_<geometry type>_<UTC time>. So a layer's data source is
	// enough for VectorViewRefresher to know what to fetch, in a project reopened tomorrow too.
	internal record GoBaseFeatureClass(string AssetId, string GeometryType, string WorkspacePath, string Name)
	{
		private static readonly Regex NamePattern = new(
			@"^gobase_([0-9a-f]{32})_(point|multipoint|polyline|polygon)_\d{17}$", RegexOptions.IgnoreCase);

		public string Path => System.IO.Path.Combine(WorkspacePath, Name);

		// Null for an asset id that is not a GUID: its feature class gets a plain name and is not refreshed.
		public static string NameFor(string assetId, string geometryType) =>
			Guid.TryParse(assetId, out var id)
				? $"gobase_{id:N}_{geometryType.ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
				: null;

		// Runs on the MCT. Null for a layer that does not show a GoBase feature class.
		public static GoBaseFeatureClass Of(FeatureLayer layer)
		{
			if (layer.GetDataConnection() is not CIMStandardDataConnection connection)
				return null;

			var match = NamePattern.Match(connection.Dataset ?? "");
			var workspace = WorkspacePathOf(connection.WorkspaceConnectionString);
			if (!match.Success || workspace == null)
				return null;

			return new GoBaseFeatureClass(
				Guid.ParseExact(match.Groups[1].Value, "N").ToString("D"),
				match.Groups[2].Value.ToUpperInvariant(),
				workspace,
				connection.Dataset);
		}

		// "DATABASE=C:\...\Project.gdb" for a file geodatabase.
		private static string WorkspacePathOf(string connectionString)
		{
			const string prefix = "DATABASE=";
			return connectionString?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
				? connectionString.Substring(prefix.Length)
				: null;
		}
	}

	internal static class LayerLoader
	{
		private const string RootGroupName = "GoBase";

		// Runs on the MCT.
		public static FeatureLayer AddFeatureLayer(string featureClassPath, ILayerContainerEdit container, string layerName) =>
			(FeatureLayer)LayerFactory.Instance.CreateLayer(new System.Uri(featureClassPath), container, layerName: layerName);

		// Adds a raster to the active map, under the "GoBase" group. datasetPath points into a cloud
		// storage connection, so Pro reads the file where it is, fetching only what is on screen.
		public static async Task AddRasterAsync(string datasetPath, string layerName)
		{
			var mapView = MapView.Active;
			if (mapView?.Map == null)
			{
				throw new System.InvalidOperationException(
					"Open a map in ArcGIS Pro before loading this layer.");
			}

			await QueuedTask.Run(() =>
			{
				// A COG carries its own overviews, and building pyramids or statistics would read the
				// whole file over the network, or ask first. Both options are Pro-wide, so they are
				// put back as they were.
				var options = ApplicationOptions.RasterImageryOptions;
				var pyramids = options.GetPyramidOption();
				var statistics = options.GetStatisticsOption();
				try
				{
					options.SetPyramidOption(BuildPyramidOption.NeverBuild);
					options.SetStatisticsOption(CalculateStatisticOption.NeverCalculate);

					var parameters = new RasterLayerCreationParams(new System.Uri(datasetPath)) { Name = layerName };
					LayerFactory.Instance.CreateLayer<RasterLayer>(parameters, RootGroup(mapView.Map));
				}
				finally
				{
					options.SetPyramidOption(pyramids);
					options.SetStatisticsOption(statistics);
				}
			});
		}

		// The "GoBase" group at the top of the map, created the first time. Runs on the MCT.
		public static GroupLayer RootGroup(Map map) =>
			map.GetLayersAsFlattenedList()
				.OfType<GroupLayer>()
				.FirstOrDefault(layer => layer.Name == RootGroupName)
			?? LayerFactory.Instance.CreateGroupLayer(map, 0, RootGroupName);

		// The GoBase group itself, which holds the layers of several assets, not of one.
		public static bool IsRootGroup(ILayerContainer container) =>
			container is GroupLayer group && group.Name == RootGroupName;

		public static async Task<string> ConvertGeoJsonAsync(GeoJsonPart part, string assetId)
		{
			var geodatabasePath = Project.Current?.DefaultGeodatabasePath;
			if (string.IsNullOrWhiteSpace(geodatabasePath))
			{
				throw new System.InvalidOperationException(
					"The ArcGIS Pro project does not have a default geodatabase.");
			}

			var outputPath = Path.Combine(
				geodatabasePath,
				GoBaseFeatureClass.NameFor(assetId, part.GeometryType) ?? PlainName(part.LayerName));

			// The third parameter tells JSONToFeatures which geometry type to keep from the GeoJSON.
			var result = await RunToolAsync("conversion.JSONToFeatures", part.Path, outputPath, part.GeometryType);

			if (result == null || result.IsFailed)
			{
				var messages = string.Join("\n", (result?.ErrorMessages ?? []).Select(message => message.Text));
				throw new System.InvalidOperationException(
					$"ArcGIS Pro could not convert the features ({part.GeometryType}).\n{messages}");
			}

			return outputPath;
		}

		// Geodatabase names allow letters, digits and underscores, and cannot start with a digit.
		private static string PlainName(string layerName)
		{
			var safeName = Regex.Replace(layerName ?? "gobase_layer", @"[^A-Za-z0-9_]", "_");
			if (string.IsNullOrWhiteSpace(safeName))
				safeName = "gobase_layer";
			if (char.IsDigit(safeName[0]))
				safeName = "_" + safeName;
			return $"{safeName}_{System.DateTime.UtcNow:yyyyMMddHHmmssfff}";
		}

		// The CancellationToken overload: the CancelableProgressor one dereferences the progressor,
		// so passing null there throws a NullReferenceException inside Pro.
		private static Task<IGPResult> RunToolAsync(string tool, params object[] values) =>
			Geoprocessing.ExecuteToolAsync(
				tool,
				Geoprocessing.MakeValueArray(values),
				null,
				CancellationToken.None,
				null,
				GPExecuteToolFlags.None);
	}
}
