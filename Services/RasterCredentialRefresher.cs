using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace GoBase.Services
{
	// Keeps GoBase raster layers readable. Their credentials last an hour by default, so shortly before
	// they run out each layer is pointed at a new connection file holding new ones.
	//
	// Nothing is remembered between checks: a layer's data source names its connection file, and the
	// file's name says which asset it is and when its credentials expire (CloudRasterConnection). So a
	// project saved yesterday is renewed as soon as its map is open and the add-in is signed in.
	internal static class RasterCredentialRefresher
	{
		private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

		// Renew this long before expiry, so a slow server or a missed check never leaves a gap.
		private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);

		private static DispatcherTimer _timer;
		private static bool _checking;

		private record DueLayer(RasterLayer Layer, ConnectionFile File);

		// Safe to call more than once. Must be called on the UI thread.
		public static void Start()
		{
			if (_timer != null)
				return;

			_timer = new DispatcherTimer { Interval = CheckEvery };
			_timer.Tick += async (_, _) => await CheckAsync();
			_timer.Start();
		}

		public static void Stop()
		{
			_timer?.Stop();
			_timer = null;
		}

		public static async Task CheckAsync()
		{
			if (_checking)
				return;

			_checking = true;
			try
			{
				CloudRasterConnection.DeleteExpired();

				// Maps open in a view. A map that is not open is renewed when it is opened.
				var maps = FrameworkApplication.Panes.OfType<IMapPane>()
					.Select(pane => pane.MapView?.Map)
					.Where(map => map != null)
					.Distinct()
					.ToList();
				if (maps.Count == 0)
					return;

				var due = await QueuedTask.Run(() => FindDueLayers(maps));
				if (due.Count == 0)
					return;

				// Signed out, or the key was forgotten: the layers stop drawing when their credentials run out.
				if (string.IsNullOrEmpty(AuthService.Instance.ApiKey) && !AuthService.Instance.Load())
					return;

				var api = new GoBaseApiClient(ApiClient.Instance);
				foreach (var asset in due.GroupBy(layer => layer.File.AssetId))
					await RenewAsync(api, asset.Key, asset.ToList());
			}
			catch (Exception ex)
			{
				// A timer callback has no one to report to. The next check tries again.
				Debug.WriteLine($"GoBase: checking raster credentials failed: {ex}");
			}
			finally
			{
				_checking = false;
			}
		}

		private static async Task RenewAsync(GoBaseApiClient api, string assetId, IReadOnlyList<DueLayer> layers)
		{
			ConnectionFile fresh;
			try
			{
				var access = await api.GetRasterAccessAsync(assetId);
				fresh = await CloudRasterConnection.CreateAsync(access);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"GoBase: could not renew the credentials of raster {assetId}: {ex.Message}");
				return;
			}

			await QueuedTask.Run(() =>
			{
				foreach (var due in layers)
					Repoint(due.Layer, due.File, fresh);
			});
		}

		// Runs on the MCT.
		private static List<DueLayer> FindDueLayers(IEnumerable<Map> maps)
		{
			var renewBy = DateTimeOffset.UtcNow + RenewBefore;
			var due = new List<DueLayer>();
			foreach (var map in maps)
			{
				foreach (var layer in map.GetLayersAsFlattenedList().OfType<RasterLayer>())
				{
					var file = CloudRasterConnection.Find(DataSourceText(layer.GetDataConnection()));
					if (file != null && file.ExpiresAt <= renewBy)
						due.Add(new DueLayer(layer, file));
				}
			}
			return due;
		}

		// Runs on the MCT. Only the connection file's name changes: same folder, same raster inside it.
		private static void Repoint(RasterLayer layer, ConnectionFile old, ConnectionFile fresh)
		{
			var oldName = System.IO.Path.GetFileName(old.Path);
			var freshName = System.IO.Path.GetFileName(fresh.Path);

			if (layer.GetDataConnection() is CIMStandardDataConnection connection
				&& connection.WorkspaceConnectionString?.Contains(oldName, StringComparison.OrdinalIgnoreCase) == true)
			{
				connection.WorkspaceConnectionString = connection.WorkspaceConnectionString.Replace(
					oldName, freshName, StringComparison.OrdinalIgnoreCase);
				layer.SetDataConnection(connection, true);
			}
			else
			{
				layer.FindAndReplaceWorkspacePath(old.Path, fresh.Path, true);
			}
		}

		// The text of a data source that can name a connection file, whatever kind of connection it is.
		private static string DataSourceText(CIMDataConnection connection) => connection switch
		{
			null => null,
			CIMStandardDataConnection standard => standard.WorkspaceConnectionString,
			_ => connection.ToJson()
		};
	}
}
