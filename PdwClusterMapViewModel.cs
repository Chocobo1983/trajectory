using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Mvvm;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Infozahyst.RSAAS.Client.NatoApp6d;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Client.ViewModels.LinkedStations;
using Infozahyst.RSAAS.Client.ViewModels.Messages;
using Infozahyst.RSAAS.Common.Dto.Pdw;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Tools;
using Infozahyst.RSAAS.Common.Tools.NatoApp6d;
using Microsoft.Extensions.Options;
using Color = System.Drawing.Color;
using Geometry = Esri.ArcGISRuntime.Geometry.Geometry;
using IMessenger = CommunityToolkit.Mvvm.Messaging.IMessenger;

namespace Infozahyst.RSAAS.Client.ViewModels.Sources;

public class PdwClusterMapViewModel : BaseViewModel, IPdwClusterMapViewModel
{
    private readonly IPositionerMessagingService _positionerMessagingService;
    private const string OverlayId = "MainOverlay";
    private const double LineWidth = 2;
    private readonly MapSettings _mapSettings;
    private readonly Color _lineColor;
    private readonly Dictionary<uint, MapPoint> _receiverPoints = [];
    private readonly Dictionary<uint, double> _antennaDirections = [];
    private decimal _doaPeriod = 1;
    private GraphicsOverlay? _mainOverlay;
    private Graphic? _directionGraphic;
    private Graphic? _receiverGraphic;
    private Graphic? _positiveInfinitySectorGraphic;
    private Graphic? _negativeInfinitySectorGraphic;
    private double? _directionAvg;
    private Esri.ArcGISRuntime.Mapping.Map? _map;
    private readonly Dictionary<uint, bool> _isManualCoordinateMode = [];
    private readonly Dictionary<uint, Color> _linkedStationColors = [];
    private uint _localStationId;
    private GraphicsOverlay? _gekataOverlay;

    private double LineDistance => _mapSettings.BearingDistanceKm;

    public SourcesViewModel? ParentViewModel { get; set; }
    public GraphicsOverlayCollection Overlays { get; }

    public Esri.ArcGISRuntime.Mapping.Map Map {
        get => _map!;
        set => Set(ref _map, value);
    }

    public decimal DoaPeriod {
        get => _doaPeriod;
        set => Set(ref _doaPeriod, value);
    }

    public IAsyncCommand RefreshCommand { get; }

    public PdwClusterViewModel? SelectedCluster { get; set; }

    public PdwClusterMapViewModel(IOptions<MapSettings> mapOptions,
        IPositionerMessagingService positionerMessagingService, IMessenger messenger) {
        _positionerMessagingService = positionerMessagingService;
        _mapSettings = mapOptions.Value;
        Overlays = new GraphicsOverlayCollection();
        Map = CreateMap();
#pragma warning disable VSTHRD110 // Observe result of async calls
        Map.Loaded += (_, _) => Task.Run(CreateOverlays);
#pragma warning restore VSTHRD110 // Observe result of async calls
        _positionerMessagingService.PositionChanged += ReceiverPositionReceived;
        RefreshCommand = new AsyncCommand<IReadOnlyCollection<PdwDto>>(Refresh);
        _lineColor = _mapSettings.DirectionColor;

        messenger.Register<AntennaDirectionUpdatedMessage>(this, (_, m) => OnAntennaDirectionUpdated(m.Value));
        messenger.Register<PositionerModeOrManualCoordinatesChangedMessage>(this,
            (_, m) => SetTunerPositionManual(m.Value.Item1, m.Value.Item2, m.Value.Item3));
        messenger.Register<LinkedStationAddedMessage>(this, (_, m) => OnLinkedStationAdded(m.Value));
        messenger.Register<LocalStationInfoChangedMessage>(this, (_, m) => _localStationId = m.Value.StationId);
        messenger.Register<LinkedStationColorChangedMessage>(this, (_, m) => OnLinkedStationColorChanged(m.Value));
    }

    private void OnLinkedStationColorChanged((uint id, System.Windows.Media.Color color, Color drawingColor) data) {
        _linkedStationColors[data.id] = data.drawingColor;
        if (SelectedCluster?.StationId == data.id) {
            if (_directionGraphic is { Symbol: SimpleLineSymbol symbol }) {
                symbol.Color = data.drawingColor;
            }

            if (SelectedCluster.StationType.IsGekata()) {
                UpdateGekataGraphicsColor(data.drawingColor);
            }
        }
    }

    private void UpdateGekataGraphicsColor(Color color) {
        if (_gekataOverlay is null) {
            return;
        }

        foreach (var graphic in _gekataOverlay.Graphics) {
            if (graphic.Symbol is SimpleLineSymbol lineSymbol) {
                lineSymbol.Color = color;
            }
        }
    }

    private void OnLinkedStationAdded(LinkedStationViewModel station) {
        _linkedStationColors[station.StationId] = station.DrawingColor;
    }

    private void SetTunerPositionManual(PositionSource positionSource, MapPoint point, uint stationId) {
        bool isManualMode = positionSource == PositionSource.Manual;
        _isManualCoordinateMode[stationId] = isManualMode;
        _receiverPoints[stationId] = point;

        if (stationId == SelectedCluster?.StationId) {
            UpdateReceiverGraphic();
        }
    }

    private void ReceiverPositionReceived(object? sender, PositionChangedNotification? positionerData) {
        if (positionerData is null) {
            return;
        }

        var stationId = positionerData.StationId;
        var isManualMode = _isManualCoordinateMode.TryGetValue(stationId, out var isManual) && isManual;
        if (isManualMode) {
            return;
        }

        var position = positionerData.Data.Position;
        var point = new MapPoint(position.Longitude, position.Latitude, SpatialReferences.Wgs84);
        _receiverPoints[stationId] = point;

        if (_receiverGraphic == null) {
            return;
        }

        _receiverGraphic.Attributes.TryGetValue(App6StyleTextFieldNames.AdditionalInformation,
            out object? currentStationId);
        if (currentStationId != null && (uint)currentStationId == stationId) {
            _receiverGraphic.Attributes[App6StyleSymbolFieldNames.Status] = (int)SymbolStatus.Present;
        }
    }

    private void OnAntennaDirectionUpdated(AntennaDirectionUpdatedData messageValue) {
        _antennaDirections[messageValue.StationId] = messageValue.AntennaDirection;
        if (_receiverGraphic == null) {
            return;
        }

        _receiverGraphic.Attributes.TryGetValue(App6StyleTextFieldNames.AdditionalInformation,
            out object? currentStationId);
        if (currentStationId != null && (uint)currentStationId == messageValue.StationId) {
            _receiverGraphic.Attributes[App6StyleSymbolFieldNames.Direction] = messageValue.AntennaDirection;
        }
    }

    private Esri.ArcGISRuntime.Mapping.Map CreateMap() {
        var map = new Esri.ArcGISRuntime.Mapping.Map();

        if (string.IsNullOrEmpty(_mapSettings.ServiceUri) || string.IsNullOrEmpty(_mapSettings.LayerId)) {
            return map;
        }

        var layer = new WmtsLayer(new Uri(_mapSettings.ServiceUri), _mapSettings.LayerId);
        map.Basemap = new Basemap(layer);
        return map;
    }

    private async Task CreateOverlays() {
        var stylePath = $"{Directory.GetCurrentDirectory()}\\Resources\\app6d.stylx";
        var app6dStyle = await DictionarySymbolStyle.CreateFromFileAsync(stylePath).ConfigureAwait(false);

        _mainOverlay = new GraphicsOverlay { Id = OverlayId, Renderer = new DictionaryRenderer(app6dStyle) };
        await Application.Current.Dispatcher.InvokeAsync(() => Overlays.Add(_mainOverlay));

        _gekataOverlay = new GraphicsOverlay { Id = "GekataOverlay" };
        await Application.Current.Dispatcher.InvokeAsync(() => Overlays.Add(_gekataOverlay));
    }

    private async Task Refresh(IReadOnlyCollection<PdwDto> pdwList) {
        if (_directionGraphic != null) {
            _mainOverlay?.Graphics.Remove(_directionGraphic);
            _directionGraphic = null;
        }

        if (_positiveInfinitySectorGraphic != null) {
            _mainOverlay?.Graphics.Remove(_positiveInfinitySectorGraphic);
            _positiveInfinitySectorGraphic = null;
        }

        if (_negativeInfinitySectorGraphic != null) {
            _mainOverlay?.Graphics.Remove(_negativeInfinitySectorGraphic);
            _negativeInfinitySectorGraphic = null;
        }

        if (_receiverGraphic != null) {
            _receiverGraphic.IsVisible = false;
        }

        _gekataOverlay?.Graphics.Clear();

        if (pdwList.Count == 0 || SelectedCluster?.StationId == null) {
            return;
        }

        if (SelectedCluster.StationType.IsArchont()) {
            DrawArchontGraphics(pdwList);
        } else {
            DrawGekataGraphics(pdwList);
        }
    }

    private void DrawGekataGraphics(IReadOnlyCollection<PdwDto> pdwList) {
        if (_gekataOverlay is null) {
            return;
        }

        var coords = pdwList.Select(x => new MapPoint(x.Longitude, x.Latitude, SpatialReferences.Wgs84)).ToList();
        var polyLineGeometry = new Polyline(coords);

        var polyMarkerSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.Cyan, 5);
        var polySymbolGraphic = new Graphic(polyLineGeometry, polyMarkerSymbol);

        _gekataOverlay.Graphics.Add(polySymbolGraphic);

        AddGekataDoaLines(pdwList);
    }

    private void DrawArchontGraphics(IReadOnlyCollection<PdwDto> pdwList) {
        UpdateReceiverGraphic();
        var end = pdwList.LastOrDefault()!.Timestamp;
        var start = end - (ulong)UnitsNet.Duration.FromSeconds(DoaPeriod).Nanoseconds;
        var filteredPdwList = pdwList.Where(p =>
            p.Timestamp >= start && p.Timestamp <= end && !float.IsNaN(p.DirectionOfArrival));

        var pdws = new List<PdwDto>();
        foreach (var pdw in filteredPdwList) {
            if (float.IsInfinity(pdw.DirectionOfArrival)) {
                DrawInfSectors(pdw.DirectionOfArrival);
            } else {
                pdws.Add(pdw);
            }
        }

        if (pdws.Any()) {
            _directionAvg = pdws.CircularMean(x => x.DirectionOfArrival);
        } else {
            _directionAvg = null;
        }

        AddOrUpdateDirectionLine(_directionAvg);
    }

    private void DrawInfSectors(float direction) {
        if (float.IsPositiveInfinity(direction)) {
            if (_positiveInfinitySectorGraphic != null) {
                return;
            }

            _positiveInfinitySectorGraphic = CreateSector(true);

            if (_positiveInfinitySectorGraphic != null) {
                _mainOverlay?.Graphics.Add(_positiveInfinitySectorGraphic);
            }
        } else {
            if (_negativeInfinitySectorGraphic != null) {
                return;
            }

            _negativeInfinitySectorGraphic = CreateSector(false);

            if (_negativeInfinitySectorGraphic != null) {
                _mainOverlay?.Graphics.Add(_negativeInfinitySectorGraphic);
            }
        }
    }

    private Graphic? CreateSector(bool isPositive) {
        if (SelectedCluster?.StationId == null) {
            return null;
        }

        _receiverPoints.TryGetValue(SelectedCluster?.StationId ?? default, out MapPoint? receiverPoint);
        if (receiverPoint == null) {
            return null;
        }

        _antennaDirections.TryGetValue(SelectedCluster?.StationId ?? default, out double antennaDirection);

        int k = 10;
        var p = _mapSettings.InfinityDirectionOfArrivalSectorAngle / k;
        List<MapPoint> points = new List<MapPoint> { receiverPoint };
        var azimuth = isPositive
            ? antennaDirection + _mapSettings.AntennaSectorAngle / 2
            : antennaDirection - +_mapSettings.AntennaSectorAngle / 2;
        for (int i = 0; i <= k; i++) {
            var point = GeometryEngine.MoveGeodetic(
                new[] { receiverPoint }, LineDistance, LinearUnits.Kilometers,
                azimuth, AngularUnits.Degrees, GeodeticCurveType.Geodesic)[0];
            points.Add(point);
            azimuth = isPositive ? azimuth + p : azimuth - p;
        }

        var polygon = new Polygon(points);
        var symbol = new SimpleFillSymbol {
            Color = Color.FromArgb(50, Color.Red),
            Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.Red, 1)
        };

        return new Graphic(polygon, symbol);
    }

    private Color GetLinkedStationColor(uint stationId) {
        if (stationId == _localStationId) {
            return _lineColor;
        }

        return _linkedStationColors.TryGetValue(stationId, out var color) ? color : _lineColor;
    }

    private void AddGekataDoaLines(IReadOnlyCollection<PdwDto> pdws) {
        if (_gekataOverlay == null || SelectedCluster?.StationId == null) {
            return;
        }

        var lineGraphics = new List<Graphic>();
        var lineColor = GetLinkedStationColor(SelectedCluster?.StationId ?? default);
        var lineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, lineColor, LineWidth);

        foreach (var pdw in pdws) {
            if (float.IsNaN(pdw.DirectionOfArrival) || float.IsInfinity(pdw.DirectionOfArrival)) {
                continue;
            }

            var startPoint = new MapPoint(pdw.Longitude, pdw.Latitude, SpatialReferences.Wgs84);

            var points = new[] { startPoint }.MoveGeodetic(
                LineDistance, LinearUnits.Kilometers,
                pdw.DirectionOfArrival, AngularUnits.Degrees, GeodeticCurveType.Geodesic);
            var endPoint = points[0];
            var line = new Polyline(new[] { startPoint, endPoint });
            var geometry = line.DensifyGeodetic(50, LinearUnits.Kilometers);

            var directionGraphic = new Graphic(geometry, lineSymbol);
            lineGraphics.Add(directionGraphic);
        }

        _gekataOverlay.Graphics.AddRange(lineGraphics);
    }

    private void AddOrUpdateDirectionLine(double? direction) {
        if (_mainOverlay == null || SelectedCluster?.StationId == null) {
            return;
        }

        if (direction is null) {
            if (_directionGraphic != null) {
                _mainOverlay?.Graphics.Remove(_directionGraphic);
                _directionGraphic = null;
            }

            return;
        }

        _receiverPoints.TryGetValue(SelectedCluster?.StationId ?? default, out MapPoint? receiverPoint);
        if (receiverPoint == null) {
            return;
        }

        var points = GeometryEngine.MoveGeodetic(
            new[] { receiverPoint }, LineDistance, LinearUnits.Kilometers,
            direction.Value, AngularUnits.Degrees, GeodeticCurveType.Geodesic);
        var endPoint = points[0];
        Polyline line = new Polyline(new[] { receiverPoint, endPoint });
        Geometry geometry = GeometryEngine.DensifyGeodetic(line, 1, LinearUnits.Kilometers);

        var lineColor = GetLinkedStationColor(SelectedCluster?.StationId ?? default);
        if (_directionGraphic == null) {
            SimpleLineSymbol lineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, lineColor, LineWidth);
            _directionGraphic = new Graphic(geometry, lineSymbol);
            _mainOverlay.Graphics.Add(_directionGraphic);
        } else {
            _directionGraphic.Geometry = geometry;
            if (_directionGraphic.Symbol is SimpleLineSymbol symbol) {
                symbol.Color = lineColor;
            }
        }
    }

    private void UpdateReceiverGraphic() {
        if (SelectedCluster?.StationId == null) {
            return;
        }

        var receiverPoint = GetOrCreateReceiverMapPoint(SelectedCluster?.StationId ?? default);
        if (_receiverGraphic == null) {
            AddReceiverGraphic(receiverPoint);
        }

        var isDirectionFound =
            _antennaDirections.TryGetValue(SelectedCluster?.StationId ?? default, out double antennaDirection);
        _receiverGraphic!.Attributes[App6StyleSymbolFieldNames.Direction] = isDirectionFound ? antennaDirection : null;

        _receiverGraphic!.Geometry = receiverPoint;
        _receiverGraphic.Attributes[App6StyleTextFieldNames.AdditionalInformation] = SelectedCluster?.StationId;
        _receiverGraphic.IsVisible = true;

        AddOrUpdateDirectionLine(_directionAvg);
    }

    private void AddReceiverGraphic(MapPoint receiverPoint) {
        if (_mainOverlay == null) {
            return;
        }

        _receiverGraphic = new Graphic(receiverPoint) {
            ZIndex = _mainOverlay.Graphics.Count + 1,
            Attributes = {
                { App6StyleSymbolFieldNames.Affiliation, (int)Affiliation.Friend },
                { App6StyleSymbolFieldNames.Status, (int)SymbolStatus.Suspected },
                { App6StyleSymbolFieldNames.SymbolSet, (int)SymbolSet.LandUnits },
                { App6StyleSymbolFieldNames.SymbolEntity, (int)SymbolEntity.DirectionFinding },
            }
        };

        _mainOverlay.Graphics.Add(_receiverGraphic);
    }

    private MapPoint GetOrCreateReceiverMapPoint(uint stationId) {
        if (_receiverPoints.TryGetValue(stationId, out var point)) {
            return point;
        }

        point = new MapPoint(0, 0, SpatialReferences.Wgs84);
        _receiverPoints[stationId] = point;

        return point;
    }
}
