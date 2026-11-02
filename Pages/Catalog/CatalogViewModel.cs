using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using GoBase.Models;
using GoBase.Pages.Base;
using GoBase.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GoBase.Pages.Catalog
{
    // The catalog list: search, filter by type and map view, page through, act on one asset.
    internal class CatalogViewModel : PageViewModelBase
    {
        private const int PageSize = 50;

        // What the list shows. "unparsed" assets are left out on purpose: there is nothing to open.
        private static readonly TypeOption[] AllTypeOptions =
        {
            new TypeOption("All types", new[] { "vector", "raster", "document" }),
            new TypeOption("Vectors", new[] { "vector" }),
            new TypeOption("Rasters", new[] { "raster" }),
            new TypeOption("Documents", new[] { "document" }),
        };

        public IReadOnlyList<TypeOption> TypeOptions => AllTypeOptions;

        private readonly GoBaseApiClient _api = new(ApiClient.Instance);
        private int _offset;
        private int _totalCount;

        // Set when a filter changes during a load, so the list is reloaded once that load ends.
        private bool _searchPending;

        public ObservableCollection<AssetRowViewModel> Assets { get; } = new();

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        private TypeOption _selectedType = AllTypeOptions[0];
        public TypeOption SelectedType
        {
            get => _selectedType;
            set
            {
                if (SetProperty(ref _selectedType, value))
                    _ = SearchAsync();
            }
        }

        private bool _onlyInMapView;
        public bool OnlyInMapView
        {
            get => _onlyInMapView;
            set
            {
                if (SetProperty(ref _onlyInMapView, value))
                    _ = SearchAsync();
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _pageText;
        public string PageText
        {
            get => _pageText;
            set => SetProperty(ref _pageText, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                SetProperty(ref _isBusy, value);
                NotifyPropertyChanged(nameof(CanGoPrevious));
                NotifyPropertyChanged(nameof(CanGoNext));
            }
        }

        public bool CanGoPrevious => !IsBusy && _offset > 0;
        public bool CanGoNext => !IsBusy && _offset + PageSize < _totalCount;

        public ICommand SearchCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }

        public CatalogViewModel()
        {
            SearchCommand = new RelayCommand(async () => await SearchAsync());
            PreviousPageCommand = new RelayCommand(async () => await LoadPageAsync(Math.Max(0, _offset - PageSize)));
            NextPageCommand = new RelayCommand(async () => await LoadPageAsync(_offset + PageSize));

            _ = SearchAsync();
        }

        private Task SearchAsync() => LoadPageAsync(0);

        private async Task LoadPageAsync(int offset)
        {
            if (IsBusy)
            {
                if (offset == 0)
                    _searchPending = true;
                return;
            }

            IsBusy = true;
            StatusText = "Loading the catalog...";
            try
            {
                GeoBox bbox = null;
                if (OnlyInMapView)
                {
                    bbox = await MapExtent.GetCurrentAsync();
                    if (bbox == null)
                    {
                        StatusText = "Open a map to filter by the map view.";
                        return;
                    }
                }

                var page = await _api.ListAssetsAsync(SearchText, SelectedType.DataTypes, bbox, PageSize, offset);

                _offset = offset;
                _totalCount = page.Count;
                Assets.Clear();
                foreach (var asset in page.Results)
                    Assets.Add(new AssetRowViewModel(asset, _api));

                PageText = page.Count == 0
                    ? null
                    : $"{offset + 1}–{offset + page.Results.Count} of {page.Count}";
                StatusText = page.Count == 0 ? "No assets match." : null;
            }
            catch (ApiException ex) when (ex.IsAuthFailure)
            {
                StatusText = "The server refused the API key. Log out and connect again.";
            }
            catch (Exception ex)
            {
                StatusText = $"Could not load the catalog: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }

            if (_searchPending)
            {
                _searchPending = false;
                await SearchAsync();
            }
        }
    }

    internal record TypeOption(string Label, string[] DataTypes);

    // One asset in the list, with the actions it supports.
    internal class AssetRowViewModel : PropertyChangedBase
    {
        private readonly GoBaseApiClient _api;

        public AssetItem Asset { get; }
        public string Name => Asset.Name;

        public string Subtitle => string.Join("  ·  ", new[]
        {
            TypeLabel(Asset.DataType),
            FormatLabel(Asset.Format),
            PagesLabel(Asset.Document?.Pages),
            Asset.DatasetLabel ?? Asset.Dataset,
            SizeLabel(Asset.Bytes)
        }.Where(part => !string.IsNullOrEmpty(part)));

        public string Glyph => Asset.DataType switch
        {
            "vector" => "◆",
            "raster" => "▦",
            "document" => "▤",
            _ => "•"
        };

        // Vectors and rasters go to the map and documents open as PDFs. A document whose PDF the
        // catalog does not record gets no working button rather than one that fails.
        public bool CanAct => Asset.IsVector || Asset.IsRaster || Asset.HasPdf;
        public string ActionText => Asset.DataType switch
        {
            "vector" or "raster" => "Add to map",
            "document" => Asset.HasPdf ? "Open PDF" : "No PDF",
            _ => "Not supported"
        };
        public bool CanZoom => Asset.Bbox is { Length: 4 };

        private string _rowStatus;
        public string RowStatus
        {
            get => _rowStatus;
            set => SetProperty(ref _rowStatus, value);
        }

        private bool _isWorking;
        public bool IsWorking
        {
            get => _isWorking;
            set
            {
                SetProperty(ref _isWorking, value);
                NotifyPropertyChanged(nameof(IsActionEnabled));
            }
        }

        public bool IsActionEnabled => CanAct && !IsWorking;

        public ICommand ActionCommand { get; }
        public ICommand ZoomCommand { get; }

        public AssetRowViewModel(AssetItem asset, GoBaseApiClient api)
        {
            Asset = asset;
            _api = api;
            ActionCommand = new RelayCommand(async () => await (Asset.DataType switch
            {
                "document" => OpenPdfAsync(),
                "raster" => AddRasterAsync(),
                _ => AddToMapAsync()
            }));
            ZoomCommand = new RelayCommand(async () => await ZoomAsync());
        }

        private async Task OpenPdfAsync()
        {
            if (!IsActionEnabled)
                return;

            IsWorking = true;
            RowStatus = "Downloading the PDF...";
            string path;
            try
            {
                path = await DocumentOpener.DownloadPdfAsync(_api, Asset);
            }
            catch (ApiException ex) when (ex.IsAuthFailure)
            {
                RowStatus = "The server refused the API key.";
                return;
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                RowStatus = "The server has no PDF for this document.";
                return;
            }
            catch (Exception ex)
            {
                RowStatus = $"Could not download the PDF: {ex.Message}";
                return;
            }
            finally
            {
                IsWorking = false;
            }

            try
            {
                DocumentOpener.Open(path);
                RowStatus = $"Opened {Path.GetFileName(path)}.";
            }
            catch (Exception ex)
            {
                // Downloaded, but Windows has no app for it: say where the file is.
                RowStatus = $"Saved to {path}, but it could not be opened: {ex.Message}";
            }
        }

        private async Task AddToMapAsync()
        {
            if (!IsActionEnabled)
                return;

            if (!MapExtent.HasActiveMap)
            {
                RowStatus = "Open a map in ArcGIS Pro first.";
                return;
            }

            IsWorking = true;
            try
            {
                // Only what is on screen is read. The server caps each page, and the loader caps the pages.
                var bbox = await MapExtent.GetCurrentAsync();
                var progress = new Progress<string>(message => RowStatus = message);
                var result = await VectorLoader.LoadAsync(_api, Asset, bbox, progress);

                if (result.FeatureCount == 0)
                {
                    RowStatus = CanZoom
                        ? "No features in the map view. Zoom to the asset, then try again."
                        : "No features in the map view.";
                    return;
                }

                RowStatus = $"Added {result.FeatureCount:N0} features"
                    + (result.LayerCount > 1 ? $" as {result.LayerCount} layers" : "")
                    + (result.Truncated ? ". Zoom in for the rest." : ".")
                    + (result.Skipped > 0 ? $" {result.Skipped:N0} without a usable geometry were skipped." : "");
            }
            catch (ApiException ex) when (ex.IsAuthFailure)
            {
                RowStatus = "The server refused the API key.";
            }
            catch (Exception ex)
            {
                // The row has room for the message only; DebugView shows where it came from.
                System.Diagnostics.Debug.WriteLine($"GoBase: adding vector {Asset.AssetId} failed: {ex}");
                RowStatus = $"Could not add the layer: {ex.Message}";
            }
            finally
            {
                IsWorking = false;
            }
        }

        private async Task AddRasterAsync()
        {
            if (!IsActionEnabled)
                return;

            if (!MapExtent.HasActiveMap)
            {
                RowStatus = "Open a map in ArcGIS Pro first.";
                return;
            }

            IsWorking = true;
            try
            {
                var progress = new Progress<string>(message => RowStatus = message);
                await RasterLoader.LoadAsync(_api, Asset, progress);
                RowStatus = "Added. ArcGIS Pro reads it from the lake, only the parts on screen.";
            }
            catch (ApiException ex) when (ex.IsAuthFailure)
            {
                RowStatus = "The server refused the API key.";
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                RowStatus = "The server has no such raster. Search again: it may have been replaced.";
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // Either the server is not set up to share rasters, or it cannot reach the lake.
                RowStatus = $"The server cannot share this raster now. {ex.Detail}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GoBase: adding raster {Asset.AssetId} failed: {ex}");
                RowStatus = $"Could not add the raster: {ex.Message}";
            }
            finally
            {
                IsWorking = false;
            }
        }

        private async Task ZoomAsync()
        {
            if (!MapExtent.HasActiveMap)
            {
                RowStatus = "Open a map in ArcGIS Pro first.";
                return;
            }

            try
            {
                await MapExtent.ZoomToAsync(GeoBox.FromArray(Asset.Bbox));
            }
            catch (Exception ex)
            {
                RowStatus = $"Could not zoom: {ex.Message}";
            }
        }

        private static string TypeLabel(string dataType) => dataType switch
        {
            "vector" => "Vector",
            "raster" => "Raster",
            "document" => "Document",
            _ => dataType
        };

        private static string FormatLabel(string format) => format?.ToLowerInvariant() switch
        {
            "geoparquet" => "GeoParquet",
            "cog" => "COG",
            "pdf" => "PDF",
            null => null,
            _ => format
        };

        private static string PagesLabel(int? pages) => pages switch
        {
            null or <= 0 => null,
            1 => "1 page",
            _ => $"{pages} pages"
        };

        private static string SizeLabel(long? bytes) => bytes switch
        {
            null or <= 0 => null,
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
        };
    }
}
