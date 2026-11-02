# GoBase for ArcGIS Pro

An ArcGIS Pro add-in over the GoBase catalog. It lists the catalog's
vectors, rasters and documents through the GoBase-Web API, loads vector
layers into the active map, and opens documents' PDFs.

Forked from the RASID GoPilot add-in (`upstream` remote). The shell (ribbon
button, side panel, login, credential storage, layer loading) comes from
there; the GoPilot chat and the RASID project pages were removed and stay in
git history for when chat comes back.

## Status

| | |
|---|---|
| Sign in with server URL, username and password | built: traded once for an API key named after the computer, and only the key is kept; needs GoBase-Web ADR-021 (branch feat/api-keys) |
| Catalog list: search, type filter, map-view filter, paging | built |
| Vectors: add to map | built: reads `/api/map/assets/{id}/data` for the map view, at most 6 pages of 10,000 features |
| Zoom to an asset | built |
| Rasters (COG): add to map | built: Pro reads the COG straight from the lake with temporary credentials from `/api/map/assets/{id}/raster-access`, renewed while the layer is open; needs GoBase-Web ADR-022 (branch feat/raster-access) |
| Documents (PDF) | built: downloads `/api/catalog/assets/{id}/pdf` whole and opens it in the default PDF viewer; offered only when the catalog records a PDF |

## How it fits together

ArcGIS Pro reads `Config.daml` at start-up and adds a **GoBase** tab with
one button. The button opens the side panel. The panel shows one page at a
time:

```
Config.daml                          tab, button, panel (no code)
RibbonButton/ShowGoBaseDockpaneButton.cs   opens the panel
Dockpane/GoBaseDockpaneViewModel.cs  picks the page: Login, Catalog, About
Pages/<Page>/<Page>View.xaml         the page's layout
Pages/<Page>/<Page>ViewModel.cs      the page's data and button actions
Services/                            everything that talks to the server or the map
  ApiClient.cs        HTTP, server URL, the Api-Key header
  GoBaseApiClient.cs  one method per GoBase-Web endpoint
  AuthService.cs      trades username + password for a key; keeps server URL + key
                      in Windows Credential Manager, never the password
  VectorLoader.cs     pages through /data, splits by geometry type
  RasterLoader.cs     raster-access -> a cloud storage connection -> a raster layer
  CloudRasterConnection.cs  writes one .acs per set of credentials
  RasterCredentialRefresher.cs  renews the credentials before they run out
  DocumentOpener.cs   downloads a document's PDF, opens it in the default viewer
  LayerLoader.cs      GeoJSON -> feature class (JSONToFeatures) -> map layer
  MapExtent.cs        the map view as a lon/lat box; zoom to a box
Models/               the JSON shapes the API returns
```

Two things that differ from QGIS plugins:

- **Map work runs on Pro's map thread.** Anything that touches layers or
  geometry is wrapped in `QueuedTask.Run(...)`. Calling it from the UI thread
  throws.
- **A layer holds one geometry type.** Many GoBase vector assets mix points,
  lines and polygons, so the loader writes one layer per type under a group
  named after the asset.

Loaded vectors are copies: each one becomes a feature class in the project's
default geodatabase, inside a **GoBase** group layer.

Rasters are not copied. Pro cannot open a COG from a web address, so the
add-in asks GoBase for temporary credentials that read that one file, writes
them into a *cloud storage connection file* (`.acs`, Pro's equivalent of a
QGIS `/vsis3/` path plus its keys) and adds the raster from it. Pro then
reads only the parts on screen. The credentials last an hour; ten minutes
before they run out the add-in gets new ones and points the layer at a new
`.acs`. The files live in `%LOCALAPPDATA%\GoBase\connections` and are
deleted once expired. Pyramids and statistics are not built on add: a COG
has its own overviews. Right-click the layer → Calculate Statistics if a
stretch looks wrong.

## Build

Targets ArcGIS Pro **3.7** (.NET 10). References come from Esri's NuGet
package `Esri.ArcGISPro.Extensions30`, so ArcGIS Pro does not have to be
installed to compile.

- **Command line, Windows or Linux:** the .NET 10 SDK, then `dotnet build`.
  It writes `bin/Debug/net10.0-windows/GoBase.esriAddinX`; double-click that
  file on a machine with Pro 3.7 to install the add-in. Esri's own packaging
  step needs Visual Studio's MSBuild, so under `dotnet build` a plain
  replacement builds the same file (`Build/DotnetCliPackaging.targets`). On
  Linux, `EnableWindowsTargeting` lets it compile the WPF project; nothing
  can run there.
- **Visual Studio 2026**, to debug (workload ".NET desktop development").
  Open `GoBase.slnx` and start: the build installs the add-in and Pro
  launches with it loaded (`Properties/launchSettings.json`). The ArcGIS Pro
  SDK extension adds project templates; this project does not need it.

## Test against the server

1. You need a Geo Portal account (the same username and password as the web).
2. In Pro: open a map, click **GoBase**, enter the server address
   (e.g. `https://portal.example`), your username and your password. The
   add-in gets a key called "ArcGIS Pro on <computer name>" and forgets the
   password. Signing in again from this computer replaces that key.
3. Catalog → pick a vector → **Zoom to** → **Add to map**.
4. Catalog → type filter **Documents** → **Open PDF**. The file is saved under
   `%TEMP%\gobase_downloads\<asset id>\`.
5. Catalog → type filter **Rasters** → **Add to map**. Until the lake has a
   public address, this computer must be on the tailnet. The row says so if
   the lake cannot be reached.

The add-in calls the server over HTTPS. The host must be in the backend's
`DJANGO_ALLOWED_HOSTS`.
