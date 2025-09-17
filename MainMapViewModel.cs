using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Mvvm;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Infozahyst.RSAAS.Client.NatoApp6d;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Client.Tools.Dispatcher;
using Infozahyst.RSAAS.Client.ViewModels.LinkedStations;
using Infozahyst.RSAAS.Client.ViewModels.Messages;
using Infozahyst.RSAAS.Client.ViewModels.Sources;
using Infozahyst.RSAAS.Client.Views;
using Infozahyst.RSAAS.Common.Collections;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Models.Tdoa;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Tools;
using Infozahyst.RSAAS.Common.Tools.NatoApp6d;
using Infozahyst.RSAAS.Core.Tools;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using SciChart.Core.Extensions;
using IMessenger = CommunityToolkit.Mvvm.Messaging.IMessenger;
using Point = System.Windows.Point;

namespace Infozahyst.RSAAS.Client.ViewModels.Map;

public class MainMapViewModel : BaseViewModel, IMainMapViewModel
{
    private const string OverlayId = "MainOverlay";
    private const string ObjectId = "ObjectId";
    private const string ClusterIdAttribute = "ClusterId";
    private const string ClusterImpulseCountAttribute = "ClusterImpulseCount";

    private const string CreationDate = "CreationDate";
    private const string Longitude = "Longitude";
    private const string Latitude = "Latitude";
    private const string Width = "Width";
    private const string Height = "Height";
    private const string Angle = "Angle";
    private const string PtoaPoint = "_ptoaPoint";
    private const string PtoaEllipse = "_ptoaEllipse";

    private const float BaseObjectSize = 64.0f;
    private const int Scale = 20000;
    private const int TrajectoryLineWidthPx = 1;
    private const int TrajectoryPointSizePx = 3;
    private readonly Color TrajectoryColor = Color.White;
    private Esri.ArcGISRuntime.Mapping.Map? _map;
    private Esri.ArcGISRuntime.Mapping.Map? _overviewMap;
    private readonly MapSettings _mapSettings;
    private readonly MessageLogger _messageLogger;
    private readonly IReadOnlyPolicyRegistry<string> _policyRegistry;
    private readonly IMessenger _messenger;

    private readonly Dictionary<uint, Graphic> _reviewSectors = new();
    private readonly DispatcherTimer _clearDirectionTimer;
    private readonly Dictionary<uint, Graphic> _receiverGraphics = new();
    protected GraphicsOverlay? MainOverlay;
    protected GraphicsOverlay? ReviewSectorOverlay;
    protected GraphicsOverlay? DirectionsOverlay;
    protected GraphicsOverlay? TdoaResultsOverlay;
    protected GraphicsOverlay? TdoaSimulationHeatmapOverlay;
    protected GraphicsOverlay? TdoaSimulationEllipseOverlay;
    protected GraphicsOverlay? GekataTrajectoryOverlay;
    private Viewpoint? _viewpoint;
    private readonly ConcurrentDictionary<uint, MapPoint> _receiverPoints = [];
    private readonly ConcurrentDictionary<uint, Color> _linkedStationColors = [];
    private readonly ConcurrentDictionary<uint, SimpleLineSymbol> _linkedStationDirectionLineSymbols = [];
    private MapPoint? _currentPosition;
    private Guid? _currentObjectId;
    private double _antennaDirection;
    private Cursor? _mapCursor;
    private Graphic? _selectedClusterGraphic;
    private readonly SimpleLineSymbol _directionLineSymbol;
    private readonly SimpleLineSymbol _selectedDirectionLineSymbol;
    private readonly IDatabaseMessagingService _databaseMessagingService;
    private readonly IReceiverMessagingService _receiverMessagingService;
    protected readonly List<MapObject> MapObjects = [];
    private readonly ConcurrentDictionary<uint, bool> _manualCoordinateModeDictionary = new();
    private bool _isOpenContextMenu;
    private MapPoint? _mousePosition;
    private bool _isDoaVisible;
    private bool _isPtoaPointVisible = true;
    private bool _isPtoaEllipseVisible;
    private bool _isPtoaHyperbolaVisible;
    private bool _isControlEnabled;
    private readonly CircularBuffer<TrajectoryPoint> _trajectoryPoints;

    public class PtoaGraphics
    {
        public List<Graphic>? Points { get; set; }
        public Graphic? Ellipse { get; set; }
        public Graphic? EllipseCenterPoint { get; set; }
        public GraphicsOverlay? Heatmap { get; set; }
    }

    protected ConcurrentDictionary<string, PtoaGraphics> PtoaStorage = new();
    private Graphic? _tdoaSimulationEllipseCenterPoint;
    private MapObjectViewModel? _editMapObjectVm;
    private int _currentObjectSize;

    private double LineDistance => _mapSettings.BearingDistanceKm;

    public Esri.ArcGISRuntime.Mapping.Map? Map {
        get => _map;
        set => Set(ref _map, value);
    }

    public Esri.ArcGISRuntime.Mapping.Map? OverviewMap {
        get => _overviewMap;
        set => Set(ref _overviewMap, value);
    }

    public Viewpoint? Viewpoint {
        get => _viewpoint;
        set => Set(ref _viewpoint, value);
    }

    public GraphicsOverlayCollection Overlays { get; set; }

    public bool IsServerConnected {
        get => _isServerConnected;
        set {
            if (Set(ref _isServerConnected, value)) {
                OnPropertyChanged(nameof(IsUiAvailable));
            }
        }
    }

    public bool IsControlEnabled {
        get => _isControlEnabled;
        set {
            if (Set(ref _isControlEnabled, value)) {
                OnPropertyChanged(nameof(IsUiAvailable));
            }
        }
    }

    public double AntennaDirection {
        get => _antennaDirection;
        set => Set(ref _antennaDirection, value);
    }

    public Cursor? MapCursor {
        get => _mapCursor;
        set => Set(ref _mapCursor, value);
    }

    public bool IsOpenContextMenu {
        get => _isOpenContextMenu;
        set => Set(ref _isOpenContextMenu, value);
    }

    public MapPoint? MousePosition {
        get => _mousePosition;
        set => Set(ref _mousePosition, value);
    }

    public bool ReceiverIsConnected { get; private set; }
    public int ClustersDirectionCount => _mapSettings.ClustersDirectionCount;
    private readonly Dictionary<uint, List<Graphic>> _directionGraphicLists = new();
    private bool _isServerConnected;
    private uint? _localStationId;
    private readonly IDispatcherWrapper _dispatcher;
    private bool _isMeasurementEnabled;
    private bool _isTdoaSimulationVisible;
    private bool _isOverviewMapVisible = true;
    private StationType _localStationType;
    private PositionSource _currentPositionSource;
   
    public SourcesViewModel? SourcesViewModel { get; set; }

    public MapPoint? ReceiverPoint => GetLocalReceiverPoint();

    public bool IsCurrentObjectNotNull => _currentObjectId != null;

    public bool IsMeasurementEnabled {
        get => _isMeasurementEnabled;
        set => Set(ref _isMeasurementEnabled, value);
    }

    public bool IsDoaVisible {
        get => _isDoaVisible;
        set {
            if (!Set(ref _isDoaVisible, value)) {
                return;
            }

            if (DirectionsOverlay != null) {
                DirectionsOverlay.IsVisible = value;
            }
        }
    }

    public bool IsPtoaHyperbolaVisible {
        get => _isPtoaHyperbolaVisible;
        set {
            if (!Set(ref _isPtoaHyperbolaVisible, value)) {
                return;
            }

            if (PtoaStorage.Any()) {
                foreach (var tuple in PtoaStorage) {
                    if (tuple.Value.Heatmap != null) {
                        tuple.Value.Heatmap.IsVisible = value;
                    }
                }
            }
        }
    }

    public bool IsPtoaPointVisible {
        get => _isPtoaPointVisible;
        set {
            if (!Set(ref _isPtoaPointVisible, value)) {
                return;
            }

            if (!PtoaStorage.Any()) {
                return;
            }

            foreach (var x in PtoaStorage.Select(x => x.Value)) {
                if (x.Points == null) {
                    continue;
                }

                foreach (var point in x.Points) {
                    point.IsVisible = value;
                }
            }
        }
    }

    public bool IsPtoaEllipseVisible {
        get => _isPtoaEllipseVisible;
        set {
            if (!Set(ref _isPtoaEllipseVisible, value)) {
                return;
            }

            if (PtoaStorage.Any()) {
                foreach (var tuple in PtoaStorage) {
                    if (tuple.Value.Ellipse != null) {
                        tuple.Value.Ellipse.IsVisible = value;
                    }

                    if (tuple.Value.EllipseCenterPoint != null) {
                        tuple.Value.EllipseCenterPoint.IsVisible = value;
                    }
                }
            }
        }
    }

    public bool IsTdoaSimulationVisible {
        get => _isTdoaSimulationVisible;
        set {
            if (!Set(ref _isTdoaSimulationVisible, value)) {
                return;
            }

            if (TdoaSimulationHeatmapOverlay != null) {
                TdoaSimulationHeatmapOverlay.IsVisible = value;
            }

            if (TdoaSimulationEllipseOverlay != null) {
                TdoaSimulationEllipseOverlay.IsVisible = value;
            }
        }
    }

    public string MeasurementToolMainColor => _mapSettings.MeasurementToolMainColor;

    public string MeasurementToolLabelBackgroundColor => _mapSettings.MeasurementToolLabelBackgroundColor;

    public List<int> ObjectSizes { get; }

    public int CurrentObjectSize {
        get => _currentObjectSize;
        set {
            if (Set(ref _currentObjectSize, value)) {
                UpdateObjectSizesOnMap(_currentObjectSize);
            }
        }
    }

    public bool IsOverviewMapVisible {
        get => _isOverviewMapVisible;
        set => Set(ref _isOverviewMapVisible, value);
    }

    public double OverviewMapScaleFactor => _mapSettings.OverviewMapScaleFactor;

    public double OverviewMapHeight => _mapSettings.OverviewMapHeight;

    public double OverviewMapWidth => _mapSettings.OverviewMapWidth;

    public bool IsUiAvailable => IsServerConnected && IsControlEnabled;

    public ICommand GoToReceiverPositionCommand { get; }
    public DelegateCommand AddObjectCommand { get; }
    public IAsyncCommand RemoveObjectCommand { get; }
    public DelegateCommand EditObjectCommand { get; }
    public ICommand<object> SavePositionCommand { get; }
    public DelegateCommand<MouseButtonEventArgs> MouseLeftButtonDownCommand { get; }
    public DelegateCommand<MouseButtonEventArgs> MouseLeftButtonUpCommand { get; }
    public DelegateCommand<Guid?> SelectClusterDirectionCommand { get; }
    public DelegateCommand<MouseEventArgs> MouseMoveCommand { get; }

    public MainMapViewModel(IOptions<MapSettings> mapOptions,
        MessageLogger messageLogger, IDatabaseMessagingService databaseMessagingService,
        IReceiverMessagingService receiverMessagingService, IPositionerMessagingService positionerMessagingService,
        IReadOnlyPolicyRegistry<string> policyRegistry, IMessenger messenger,
        ISystemControlMessagingService systemControlMessagingService,
        IDispatcherWrapper? dispatcherWrapper = null) {
        _mapSettings = mapOptions.Value;
        _messageLogger = messageLogger;
        _databaseMessagingService = databaseMessagingService;
        _receiverMessagingService = receiverMessagingService;
        _policyRegistry = policyRegistry;
        _messenger = messenger;
        _directionLineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, _mapSettings.DirectionColor,
            _mapSettings.DirectionLineWidth);
        _selectedDirectionLineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, _mapSettings.DirectionColor,
            _mapSettings.DirectionLineWidth * 2);
        Overlays = new GraphicsOverlayCollection();
        _mousePosition = new MapPoint(0, 0);
        _trajectoryPoints =
            new CircularBuffer<TrajectoryPoint>(_mapSettings.TrajectoryBufferCapacity,
                _mapSettings.TrajectoryBufferCapacity);

        GoToReceiverPositionCommand = new DelegateCommand(GoToLocalReceiverLocation);
        AddObjectCommand = new DelegateCommand(AddObject);
        RemoveObjectCommand = new AsyncCommand(RemoveObject);
        EditObjectCommand = new DelegateCommand(EditObject);
        SavePositionCommand = new DelegateCommand<object>(SavePosition);
        MouseLeftButtonDownCommand = new DelegateCommand<MouseButtonEventArgs>(MouseLeftButtonDown);
        MouseLeftButtonUpCommand = new DelegateCommand<MouseButtonEventArgs>(MouseLeftButtonUp);
        SelectClusterDirectionCommand = new DelegateCommand<Guid?>(SelectClusterDirection);
        MouseMoveCommand = new DelegateCommand<MouseEventArgs>(MouseMove);
        _dispatcher = dispatcherWrapper ?? new DispatcherWrapper(Application.Current.Dispatcher);

        Map = CreateMap();
        OverviewMap = CreateMap();
        Map.Loaded += MapLoaded;

        positionerMessagingService.PositionChanged += OnPositionChanged;
        receiverMessagingService.ReceiverConnectionChanged += ReceiverMessagingServiceOnConnectionChanged;
        _clearDirectionTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher.Dispatcher) {
            Interval = _mapSettings.DirectionsCleanupInterval
        };
        _clearDirectionTimer.Tick += (_, _) => ClearDirectionsTimerOnTick();

        CurrentObjectSize = _mapSettings.DefaultObjectSize;
        ObjectSizes = _mapSettings.ObjectSizes;

        messenger.Register<PositionerModeOrManualCoordinatesChangedMessage>(this,
            (_, m) => OnPositionerModeChanged(m.Value.Item1, m.Value.Item2, m.Value.Item3));
        messenger.Register<ServerConnectionStateChangedMessage>(this,
            (_, m) => OnServerConnectionChanged(m.Value));
        messenger.Register<LinkedStationAddedMessage>(this,
            (_, m) => OnLinkedStationAdded(m.Value));
        messenger.Register<LinkedStationColorChangedMessage>(this,
            (_, m) => OnLinkedStationColorChanged(m.Value));
        messenger.Register<LinkedStationDeletedMessage>(this,
            (_, m) => OnLinkedStationDeleted(m.Value));
        messenger.Register<AntennaDirectionUpdatedMessage>(this,
            (_, m) => OnAntennaDirectionUpdated(m.Value));
        messenger.Register<LocalStationInfoChangedMessage>(this,
            (_, m) => OnLocalStationInfoChanged(m.Value));
        messenger.Register<VisibleClustersUpdatedMessage>(this,
            (_, m) => OnVisibleClustersUpdated(m.Value));
        messenger.Register<PtoaResultReceivedMessage>(this,
            (_, m) => OnPtoaResultsReceived(m.Value.CalculationId, m.Value.IsCalculationStopped, m.Value.ObjectPosition,
                m.Value.Time, m.Value.Ellipse, m.Value.ThinHyperboles));
        messenger.Register<TdoaMapSimulationResultReceivedMessage>(this,
            (_, m) => OnTdoaMapSimulationResultReceived(m.Value.Time, m.Value.ThinHyperboles, m.Value.Ellipse));
        _messenger.Register<ControlStatusChangedMessage>(this,
            (_, m) => _dispatcher.Invoke(() => IsControlEnabled = m.Value));
        _messenger.Register<DeviceInfoReceivedMessage>(this,
            (_, m) => OnDeviceInfoReceived(m.Value.DeviceInfo, m.Value.StationId, m.Value.DateTime));
        systemControlMessagingService.MapObjectUpdated += OnMapObjectUpdated;
    }

    private void OnDeviceInfoReceived(DeviceInfo deviceInfo, uint stationId, DateTime dateTime) {
        if (_currentPositionSource != PositionSource.Receiver || !IsLocalGekataGAStation(stationId)) {
            return;
        }

        AddPointToTrajectory(deviceInfo.GnssInfo.Longitude, deviceInfo.GnssInfo.Latitude, dateTime);
        DrawTrajectory();
    }

    private bool IsLocalGekataGAStation(uint stationId) {
        return _localStationType == StationType.GekataGA && stationId == _localStationId;
    }

    private void AddPointToTrajectory(float longitude, float latitude, DateTime dateTime) {
        var point = new TrajectoryPoint(dateTime, longitude, latitude);
        _trajectoryPoints.PushBack(point);
    }

    public void DrawTrajectory() {
        if (GekataTrajectoryOverlay == null || _trajectoryPoints.IsEmpty) {
            return;
        }

        GekataTrajectoryOverlay.Graphics.Clear();
        var points = new List<MapPoint>();
        foreach (var point in _trajectoryPoints) {
            var mapPoint = new MapPoint(point.Longitude, point.Latitude, SpatialReferences.Wgs84);
            points.Add(mapPoint);
            var pointGraphic = new Graphic(mapPoint,
                new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, TrajectoryColor, TrajectoryPointSizePx));
            GekataTrajectoryOverlay.Graphics.Add(pointGraphic);
        }

        var polylineBuilder = new PolylineBuilder(points, SpatialReferences.Wgs84);
        var lineGraphic = new Graphic(polylineBuilder.ToGeometry(),
            new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, TrajectoryColor, TrajectoryLineWidthPx));
        GekataTrajectoryOverlay.Graphics.Add(lineGraphic);
    }

    private void UpdateObjectSizesOnMap(int currentSize) {
        if (MainOverlay is not { Renderer: DictionaryRenderer render }) {
            return;
        }

        var scale = currentSize / BaseObjectSize;
        render.ScaleExpression = new ArcadeExpression(scale.ToString("0.##", CultureInfo.InvariantCulture));
        MainOverlay.Renderer = render;
    }

    private void OnTdoaMapSimulationResultReceived(DateTime time, GeographicalPosition[][]? thinHyperboles, Ellipse? ellipse) {
        TdoaSimulationHeatmapOverlay?.Graphics.Clear();
        if (thinHyperboles is not null && TdoaSimulationHeatmapOverlay != null) {
            CreateHeatmap(thinHyperboles, TdoaSimulationHeatmapOverlay);
        }

        if (ellipse is not null) {
            _tdoaSimulationEllipseCenterPoint = AddOrUpdatePtoaEllipseCenterPoint(ellipse, time,
                TdoaSimulationEllipseOverlay, _tdoaSimulationEllipseCenterPoint);
        } else {
            TdoaSimulationEllipseOverlay?.Graphics.Clear();
            _tdoaSimulationEllipseCenterPoint = null;
        }
    }

    private Graphic? AddOrUpdatePtoaEllipseCenterPoint(Ellipse ellipse, DateTime? time, GraphicsOverlay? overlay,
        Graphic? ellipseCenterPoint) {
        if (overlay == null) {
            return null;
        }

        if (ellipseCenterPoint is null) {
            ellipseCenterPoint = new Graphic(null,
                new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, _mapSettings.PtoaPointColor,
                    _mapSettings.PtoaPointSize));
            _dispatcher.Invoke(() => overlay.Graphics.Add(ellipseCenterPoint));
            ellipseCenterPoint.Attributes[PtoaPoint] = true;
        }

        ellipseCenterPoint.Geometry = new MapPoint((double)ellipse.GeoCenterCoordinates.Lon,
            (double)ellipse.GeoCenterCoordinates.Lat, SpatialReferences.Wgs84);
        ellipseCenterPoint.Attributes[CreationDate] = time?.ToLocalTime();
        ellipseCenterPoint.Attributes[Longitude] = $"{ellipse.GeoCenterCoordinates.Lon:F4} E";
        ellipseCenterPoint.Attributes[Latitude] = $"{ellipse.GeoCenterCoordinates.Lat:F4} N";
        return ellipseCenterPoint;
    }

    private Graphic? AddOrUpdatePtoaEllipse(Ellipse ellipse, DateTime? time, GraphicsOverlay? overlay,
        Graphic? ellipseGraphic) {
        if (overlay == null) {
            return null;
        }

        if (ellipseGraphic is null) {
            var curvedLineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Dot, _mapSettings.PtoaEllipseColor, 3);
            ellipseGraphic = new Graphic(MakeEllipseGeometry(ellipse),
                new SimpleFillSymbol(SimpleFillSymbolStyle.Solid, Color.Transparent, curvedLineSymbol));
            _dispatcher.Invoke(() => overlay.Graphics.Add(ellipseGraphic));
            ellipseGraphic.Attributes[PtoaEllipse] = true;
        }

        ellipseGraphic.Geometry = MakeEllipseGeometry(ellipse);
        ellipseGraphic.Attributes[CreationDate] = time?.ToLocalTime();
        ellipseGraphic.Attributes[Longitude] = $"{ellipse.GeoCenterCoordinates.Lon:F4} E";
        ellipseGraphic.Attributes[Latitude] = $"{ellipse.GeoCenterCoordinates.Lat:F4} N";
        ellipseGraphic.Attributes[Width] = $"{ellipse.Width:F3}";
        ellipseGraphic.Attributes[Height] = $"{ellipse.Height:F3}";
        ellipseGraphic.Attributes[Angle] = $"{ellipse.RotationAngleDegrees:F3}";
        return ellipseGraphic;
    }

    private void ClearAllPtoaResults() {
        TdoaResultsOverlay?.Graphics.Clear();

        foreach (var (_, value) in PtoaStorage) {
            var heatmap = value.Heatmap;
            if (heatmap is not null) {
                heatmap.Graphics.Clear();
                Overlays.Remove(heatmap);
            }

            value.Heatmap?.Graphics.Clear();
        }

        PtoaStorage.Clear();
    }

    private void OnPtoaResultsReceived(string calculationId, bool isCalculationStopped, GeographicalPosition? objectPosition,
        DateTime? time, Ellipse? ellipse, GeographicalPosition[][]? valueThinHyperboles) {
        var calculation = PtoaStorage.GetOrAdd(calculationId, new PtoaGraphics());

        if (isCalculationStopped) {
            TdoaResultsOverlay?.Graphics.Clear();
            calculation.Heatmap?.Graphics.Clear();
            return;
        }

        if (valueThinHyperboles is not null && TdoaResultsOverlay is not null) {
            if (calculation.Heatmap == null) {
                calculation.Heatmap ??= new GraphicsOverlay();
                _dispatcher.Invoke(() => Overlays.Insert(2, calculation.Heatmap));
            } else {
                calculation.Heatmap.Graphics.Clear();
            }

            if (calculation.Points == null) {
                calculation.Points ??= [];
            } else {
                foreach (var point in calculation.Points) {
                    TdoaResultsOverlay.Graphics.Remove(point);
                }

                calculation.Points.Clear();
            }

            var polylines = AddPtoaHyperboles(calculation.Heatmap, valueThinHyperboles);
            var intersectPoints =
                AddPtoaHyperbolesIntersectPoints(TdoaResultsOverlay, polylines, calculation.Points, time);
            LogPtoaResult(intersectPoints);
        }
    }

    private Geometry MakeEllipseGeometry(Ellipse ellipseParameters) {
        var isRotationRequired = ellipseParameters.Height > ellipseParameters.Width;
        // do not change - arcgis automatically selects the largest axis as width
        var angle = isRotationRequired
            ? CircularHelper.Get360Azimuth(-ellipseParameters.RotationAngleDegrees + 90)
            : -ellipseParameters.RotationAngleDegrees;

        var parameters = new GeodesicEllipseParameters {
            Center =
                new MapPoint((double)ellipseParameters.GeoCenterCoordinates.Lon,
                    (double)ellipseParameters.GeoCenterCoordinates.Lat,
                    SpatialReferences.Wgs84),
            GeometryType = GeometryType.Polygon,
            SemiAxis1Length = ellipseParameters.Width / 2,
            SemiAxis2Length = ellipseParameters.Height / 2,
            AxisDirection = angle,
            MaxPointCount = 200,
            AngularUnit = AngularUnits.Degrees,
            LinearUnit = LinearUnits.Meters,
            MaxSegmentLength = 200
        };

        return GeometryEngine.EllipseGeodesic(parameters);
    }

    private void ReceiverMessagingServiceOnConnectionChanged(object? sender,
        ConnectionChangedNotification notification) {
        if (notification.StationId != _localStationId) {
            return;
        }

        ReceiverIsConnected = notification.IsConnected;
        OnPropertyChanged(nameof(ReceiverIsConnected));

        if (ReceiverIsConnected) {
            _ = Task.Run(async () => {
                var result = await _receiverMessagingService.GetAntennaAzimuth();
                if (result.IsSuccess) {
                    await _dispatcher.InvokeAsync(() => {
                        AntennaDirection = (double)result.Azimuth!;
                    });
                }
            });
        }
    }

    private void MouseMove(MouseEventArgs e) {
        if (e.Source is not MapView mapView || IsOpenContextMenu) {
            return;
        }

        var mapLocation = mapView.ScreenToLocation(e.GetPosition(mapView));
        MousePosition = mapLocation?.Project(SpatialReferences.Wgs84) as MapPoint;
    }

    private void OnLocalStationInfoChanged(GetStationInfoResponse stationInfo) {
        ClearReceiverGraphics();
        _localStationId = stationInfo.StationId;
        _localStationType = stationInfo.StationType;
        AddLocalReceiverPoint();
    }

    private void OnServerConnectionChanged(bool isConnected) {
        _dispatcher.Invoke(() => {
            IsServerConnected = isConnected;
            if (!IsServerConnected) {
                ClearAllPtoaResults();
            }
        });
        if (IsServerConnected && _localStationType == StationType.GekataGA) {
            _ = Task.Run(LoadStationTrajectory);
        }
    }

    private async Task LoadStationTrajectory() {
        try {
            var res = await _receiverMessagingService.GetStationTrajectory();
            if (res is { IsSuccess: true, TrajectoryBuffer: not null }) {
                _trajectoryPoints.PushRange(res.TrajectoryBuffer);
                DrawTrajectory();
            }
        } catch (Exception e) {
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private void OnLinkedStationAdded(LinkedStationViewModel station) {
        _manualCoordinateModeDictionary[station.StationId] = station.IsInManualCoordinatesMode;

        SetLinkedStationColor(station.StationId, station.DrawingColor);

        RefreshTunerPosition(station.StationId, station.Longitude, station.Latitude);
    }

    private void SetLinkedStationColor(uint stationId, Color color) {
        _linkedStationColors[stationId] = color;
        var linkedStationDirectionLineSymbol = (SimpleLineSymbol)_directionLineSymbol.Clone();
        linkedStationDirectionLineSymbol.Color = color;
        _linkedStationDirectionLineSymbols[stationId] = linkedStationDirectionLineSymbol;
    }

    private void OnLinkedStationColorChanged((uint id, System.Windows.Media.Color color, Color drawingColor) data) {
        SetLinkedStationColor(data.id, data.drawingColor);
        AddOrUpdateReviewSector(data.id);
    }

    private void OnLinkedStationDeleted(uint stationId) {
        DeleteReceiverGraphic(stationId);
        DeleteReviewSector(stationId);
    }

    private MapPoint? GetLocalReceiverPoint() {
        if (_localStationId == null) {
            return null;
        }

        return GetOrCreateReceiverMapPoint(_localStationId.Value);
    }

    private MapPoint? GetOrCreateReceiverMapPoint(uint stationId) {
        if (_receiverPoints.TryGetValue(stationId, out var point)) {
            return point;
        }

        point = new MapPoint(0, 0, SpatialReferences.Wgs84);
        _receiverPoints[stationId] = point;

        return point;
    }

    private void DeleteReceiverMapPoint(uint stationId) {
        _receiverPoints.TryRemove(stationId, out _);
    }

    private void OnPositionerModeChanged(PositionSource positionSource, MapPoint tunerPosition, uint stationId) {
        _currentPositionSource = positionSource;
        var isManualMode = _currentPositionSource == PositionSource.Manual;
        _manualCoordinateModeDictionary[stationId] = isManualMode;

        var graphic = GetOrCreateReceiverGraphic(stationId);
        if (graphic == null) {
            return;
        }

        graphic.Geometry = tunerPosition;
        _receiverPoints[stationId] = tunerPosition;
        graphic.Attributes[App6StyleSymbolFieldNames.Status] =
            isManualMode ? (int)SymbolStatus.Present : (int)SymbolStatus.Suspected;
        AddOrUpdateReviewSector(stationId);
    }

    private void OnAntennaDirectionUpdated(AntennaDirectionUpdatedData data) {
        var stationId = data.StationId;
        var receiverGraphic = GetOrCreateReceiverGraphic(stationId);
        if (receiverGraphic != null) {
            receiverGraphic.Attributes[App6StyleSymbolFieldNames.Direction] = data.AntennaDirection;
        }

        AddOrUpdateReviewSector(stationId);
    }

    private void ShowMapObjectWindow(MapObjectViewModel? viewModel) {
        if (viewModel == null) {
            return;
        }

        _dispatcher.Invoke(() => {
            var win = new MapObjectView { DataContext = viewModel, Owner = Application.Current.MainWindow };
            win.ShowDialog();
        });
    }

    private Graphic? AddLine(GraphicsOverlay? overlay, double startPointX, double startPointY, double direction,
        double distance, Symbol lineSymbol) {
        if (overlay == null) {
            return null;
        }

        MapPoint startPoint = new MapPoint(startPointX, startPointY, SpatialReferences.Wgs84);
        var points = GeometryEngine.MoveGeodetic(new[] { startPoint }, distance,
            LinearUnits.Kilometers, direction, AngularUnits.Degrees, GeodeticCurveType.Geodesic);
        var endPoint = points[0];
        Polyline line = new Polyline(new[] { startPoint, endPoint });
        Geometry geometry = GeometryEngine.DensifyGeodetic(line, distance * 0.02, LinearUnits.Kilometers);
        Graphic graphic = new Graphic(geometry, lineSymbol);

        _dispatcher.Invoke(() => overlay.Graphics.Add(graphic));

        return graphic;
    }

    private static void UpdateLine(Graphic? graphic, MapPoint startPoint, double direction, double distance) {
        if (graphic == null) {
            return;
        }

        var endPoint = GeometryEngine.MoveGeodetic(new[] { startPoint }, distance,
            LinearUnits.Kilometers, direction, AngularUnits.Degrees, GeodeticCurveType.Geodesic)[0];

        var polyline = new Polyline(new[] { startPoint, endPoint });
        graphic.Geometry = GeometryEngine.DensifyGeodetic(polyline, distance * 0.02, LinearUnits.Kilometers);
    }

    private void GoToLocalReceiverLocation() {
        if (_localStationId == null) {
            return;
        }

        _receiverPoints.TryGetValue(_localStationId.Value, out var receiverPoint);
        if (receiverPoint == null) {
            return;
        }

        _dispatcher.Invoke(() => Viewpoint = new Viewpoint(receiverPoint, Scale));
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

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void MapLoaded(object? sender, EventArgs e) {
        ReviewSectorOverlay = new GraphicsOverlay();
        DirectionsOverlay = new GraphicsOverlay();
        TdoaResultsOverlay = new GraphicsOverlay();
        TdoaSimulationHeatmapOverlay = new GraphicsOverlay();
        TdoaSimulationEllipseOverlay = new GraphicsOverlay();
        GekataTrajectoryOverlay = new GraphicsOverlay();
        _dispatcher.InvokeAsync(() =>
            Overlays.AddRange([
                ReviewSectorOverlay,
                DirectionsOverlay,
                TdoaResultsOverlay,
                TdoaSimulationHeatmapOverlay,
                TdoaSimulationEllipseOverlay,
                GekataTrajectoryOverlay
            ]));
        IsDoaVisible = true;
        IsPtoaEllipseVisible = true;
        IsPtoaPointVisible = true;
        IsPtoaHyperbolaVisible = true;
        IsTdoaSimulationVisible = true;

        await CreateMainOverlay();
        await LoadObjects();
    }

#pragma warning restore VSTHRD100 // Avoid async void methods
    private async Task CreateMainOverlay() {
        if (MainOverlay != null) {
            return;
        }

        var stylePath = $"{Directory.GetCurrentDirectory()}\\Resources\\app6d.stylx";
        var app6dStyle = await DictionarySymbolStyle.CreateFromFileAsync(stylePath).ConfigureAwait(false);
        var scale = CurrentObjectSize / BaseObjectSize;
        MainOverlay = new GraphicsOverlay {
            Id = OverlayId,
            Renderer = new DictionaryRenderer(app6dStyle) {
                ScaleExpression = new ArcadeExpression(scale.ToString("0.##", CultureInfo.InvariantCulture))
            }
        };

        AddLocalReceiverPoint();
        _dispatcher.Invoke(() => Overlays.Add(MainOverlay));
    }

    private void AddLocalReceiverPoint() {
        if (_localStationId == null) {
            return;
        }

        GetOrCreateReceiverGraphic(_localStationId.Value);
        AddOrUpdateReviewSector(_localStationId.Value);
    }

    private void DeleteReceiverGraphic(uint stationId) {
        if (MainOverlay == null) {
            return;
        }

        DeleteReceiverMapPoint(stationId);
        DeleteDirectionsById(stationId);
        DeleteDirectionGraphicList(stationId);

        _manualCoordinateModeDictionary.TryRemove(stationId, out _);

        if (_receiverGraphics.TryGetValue(stationId, out var receiverGraphic)) {
            _dispatcher.Invoke(() => MainOverlay?.Graphics.Remove(receiverGraphic));
            _receiverGraphics.Remove(stationId);
        }
    }

    private void ClearReceiverGraphics() {
        var ids = _receiverGraphics.Keys.ToList();
        foreach (var id in ids) {
            DeleteReceiverGraphic(id);
        }
    }

    private Graphic? GetOrCreateReceiverGraphic(uint stationId) {
        if (MainOverlay == null) {
            return null;
        }

        if (_receiverGraphics.TryGetValue(stationId, out var receiverGraphic)) {
            return receiverGraphic;
        }

        var point = GetOrCreateReceiverMapPoint(stationId);

        receiverGraphic = new Graphic(point) {
            ZIndex = MainOverlay.Graphics.Count + 1
        };

        SetReceiverAttributes(receiverGraphic, GetStationType(stationId), stationId);

        _dispatcher.Invoke(() => MainOverlay.Graphics.Add(receiverGraphic));
        _receiverGraphics[stationId] = receiverGraphic;
        return receiverGraphic;
    }

    private void SetReceiverAttributes(Graphic graphic, StationType? stationType, uint stationId) {
        graphic.Attributes[App6StyleSymbolFieldNames.Affiliation] = (int)Affiliation.Friend;
        graphic.Attributes[App6StyleSymbolFieldNames.Direction] = AntennaDirection;
        graphic.Attributes[App6StyleTextFieldNames.AdditionalInformation] = stationId;

        if (stationType == StationType.GekataGA) {
            graphic.Attributes[App6StyleSymbolFieldNames.Status] = (int)SymbolStatus.Present;
            graphic.Attributes[App6StyleSymbolFieldNames.SymbolSet] = (int)SymbolSet.AirUnits;
            graphic.Attributes[App6StyleSymbolFieldNames.SymbolEntity] = (int)SymbolEntity.UAV;
        } else {
            graphic.Attributes[App6StyleSymbolFieldNames.Status] = (int)SymbolStatus.Suspected;
            graphic.Attributes[App6StyleSymbolFieldNames.SymbolSet] = (int)SymbolSet.LandUnits;
            graphic.Attributes[App6StyleSymbolFieldNames.SymbolEntity] = (int)SymbolEntity.DirectionFinding;
        }
    }

    private void OnPositionChanged(object? _, PositionChangedNotification? res) {
        if (res is null) {
            return;
        }

        var stationId = res.StationId;
        var isValuePresent = _manualCoordinateModeDictionary.TryGetValue(stationId, out var isInManualMode);
        if (isValuePresent && isInManualMode) {
            return;
        }

        var position = res.Data.Position;
        RefreshTunerPosition(stationId, position?.Longitude, position?.Latitude);
    }

    private void RefreshTunerPosition(uint stationId, double? longitude, double? latitude) {
        var receiverGraphic = GetOrCreateReceiverGraphic(stationId);
        if (receiverGraphic == null) {
            return;
        }

        var status = longitude != null && latitude != null ? (int)SymbolStatus.Present : (int)SymbolStatus.Suspected;

        if (longitude != null && latitude != null) {
            var receiverPoint = new MapPoint(longitude.Value, latitude.Value, SpatialReferences.Wgs84);
            receiverGraphic.Geometry = receiverPoint;
            _receiverPoints[stationId] = receiverPoint;
        }

        receiverGraphic.Attributes[App6StyleSymbolFieldNames.Status] = status;
        AddOrUpdateReviewSector(stationId);
    }

    private async Task LoadObjects() {
        try {
            _policyRegistry.TryGet(PolicyNameConstants.WaitAndRetryForeverAsync, out IAsyncPolicy policy);
            var mapObjects = await policy.ExecuteAsync(_databaseMessagingService.GetMapObjectList);
            if (mapObjects != null) {
                MapObjects.AddRange(mapObjects);
                foreach (var mapObject in MapObjects) {
                    AddOrUpdateObjectToMap(mapObject);
                }

                _messenger.Send(new MapObjectsLoadedMessage(MapObjects));
            }
        } catch (Exception ex) {
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    private void AddObject() {
        if (_currentPosition == null) {
            return;
        }

        var viewModel = new MapObjectViewModel(_currentPosition.Y, _currentPosition.X);
        viewModel.ObjectSaving += MapObjectSaving;
        ShowMapObjectWindow(viewModel);
    }

#pragma warning disable VSTHRD100
    private async void MapObjectSaving(MapObjectViewModel mapObjectVm) {
#pragma warning restore VSTHRD100
        var response = await _databaseMessagingService.SaveMapObject(mapObjectVm.ToModel());
        if (response is { IsSuccess: true, MapObject: not null }) {
            var mapObj = MapObjects.FirstOrDefault(x => x.Id == response.MapObject.Id);
            if (mapObj != null) {
                MapObjects.Remove(mapObj);
            }

            MapObjects.Add(response.MapObject);
            _messenger.Send(new MapObjectUpdatedMessage(response.MapObject));
            AddOrUpdateObjectToMap(response.MapObject);
        } else {
            _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage ?? "Error while saving map object",
                MessageLevel.Error);
        }
    }

    private void AddOrUpdateObjectToMap(MapObject mapObjectVm) {
        ArgumentNullException.ThrowIfNull(mapObjectVm);
        Graphic? graphic = null;
        _dispatcher.Invoke(() => graphic = MainOverlay?.Graphics.FirstOrDefault(g => g.Attributes.Values.Contains(mapObjectVm.Id)));
        bool isNew = false;
        if (graphic == null) {
            graphic = new Graphic();
            isNew = true;
        }

        MapPoint point = new MapPoint(mapObjectVm.Lon, mapObjectVm.Lat, SpatialReferences.Wgs84);
        graphic.Geometry = point;
        graphic.Attributes[App6StyleSymbolFieldNames.Sidc] = mapObjectVm.Sidc;
        graphic.Attributes[App6StyleTextFieldNames.DatetimeValid] =
            mapObjectVm.DateTime?.ToString("HH:mm dd.MM.yyyy", CultureInfo.CurrentCulture);
        graphic.Attributes[App6StyleTextFieldNames.UniqueDesignation] = mapObjectVm.Name;
        graphic.Attributes[ObjectId] = mapObjectVm.Id;
        if (isNew) {
            _dispatcher.Invoke(() => MainOverlay?.Graphics.Add(graphic));
        }
    }

    private void EditObject() {
        if (_currentObjectId == null) {
            return;
        }

        var mapObject = MapObjects.FirstOrDefault(x => x.Id == _currentObjectId);
        if (mapObject == null) {
            return;
        }

        _editMapObjectVm = new MapObjectViewModel(mapObject);
        _editMapObjectVm.ObjectSaving += MapObjectSaving;
        ShowMapObjectWindow(_editMapObjectVm);
    }

    private async Task RemoveObject() {
        if (_currentObjectId == null) {
            return;
        }

        var response = await _databaseMessagingService.RemoveMapObjects(new List<Guid> { (Guid)_currentObjectId });
        if (response.IsSuccess) {
            _dispatcher.Invoke(() => MainOverlay?.Graphics.RemoveWhere(g =>
                g.Attributes.TryGetValue(ObjectId, out var value) && value != null && value.Equals(_currentObjectId)));
            var mapObject = MapObjects.FirstOrDefault(x => x.Id == _currentObjectId);
            if (mapObject != null) {
                MapObjects.Remove(mapObject);
                _messenger.Send(new MapObjectDeletedMessage(mapObject.Id));
            }
        }
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void SavePosition(object obj) {
        if (obj is MouseButtonEventArgs mouseArgs) {
            if (mouseArgs.Source is MapView mapView) {
                var screenPoint = mouseArgs.GetPosition(mapView);
                var mapLocation = mapView.ScreenToLocation(screenPoint);
                if (mapLocation != null) {
                    if (GeometryEngine.NormalizeCentralMeridian(mapLocation) is MapPoint normalizeLocation) {
                        _currentPosition =
                            GeometryEngine.Project(normalizeLocation, SpatialReferences.Wgs84) as MapPoint;
                    }

                    if (MainOverlay == null) {
                        return;
                    }

                    var identifyResults = await mapView.IdentifyGraphicsOverlayAsync(MainOverlay, screenPoint,
                        10d, false, 1).ConfigureAwait(false);
                    object? objId = null;
                    if (identifyResults.Graphics.Count > 0) {
                        var graphicAttributes = identifyResults.Graphics[0].Attributes;
                        graphicAttributes.TryGetValue(ObjectId, out objId);
                    }

                    _currentObjectId = objId as Guid?;
                    OnPropertyChanged(nameof(IsCurrentObjectNotNull));
                }
            }
        }
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    protected List<Graphic> GetOrCreateDirectionGraphicList(uint stationId) {
        if (_directionGraphicLists.TryGetValue(stationId, out var directionGraphicList)) {
            return directionGraphicList;
        }

        directionGraphicList = new List<Graphic>();
        _directionGraphicLists.TryAdd(stationId, directionGraphicList);

        return directionGraphicList;
    }

    private void DeleteDirectionGraphicList(uint stationId) {
        if (_directionGraphicLists.TryGetValue(stationId, out _)) {
            _directionGraphicLists.Remove(stationId);
        }
    }

    private void OnVisibleClustersUpdated(IEnumerable<PdwClusterViewModel> visibleClusters) {
        var groupedData = visibleClusters.GroupBy(x => x.StationId);

        HideDirections();
        foreach (var group in groupedData) {
            var list = group.OrderByDescending(x => x.ImpulseCount).Take(ClustersDirectionCount)
                .ToList();
            var stationId = group.Key;
            RefreshDirections(list, stationId);
        }
    }

    private void HideDirections() {
        _dispatcher.Invoke(() => {
            foreach (var graphic in _directionGraphicLists.Values.SelectMany(list => list)) {
                graphic.IsVisible = false;
            }
        });
    }

    private Color? GetStationColor(uint stationId) {
        if (stationId == _localStationId) {
            return _mapSettings.DirectionColor;
        }

        if (_linkedStationColors.TryGetValue(stationId, out var color)) {
            return color;
        }

        return null;
    }

    private SimpleLineSymbol? GetStationDirectionLineSymbol(uint stationId) {
        if (stationId == _localStationId) {
            return _directionLineSymbol;
        }

        return _linkedStationDirectionLineSymbols.TryGetValue(stationId, out var symbol) ? symbol : null;
    }

    private void RefreshDirections(List<PdwClusterViewModel> data, uint stationId) {
        _clearDirectionTimer.Stop();
        _clearDirectionTimer.Start();

        if (!_receiverPoints.TryGetValue(stationId, out var receiverPoint)) {
            return;
        }

        var directionGraphicList = GetOrCreateDirectionGraphicList(stationId);

        var stationColor = GetStationColor(stationId);
        var directionLineSymbol = GetStationDirectionLineSymbol(stationId);
        if (stationColor is null || directionLineSymbol is null) {
            return;
        }

        if (directionGraphicList.FirstOrDefault()?.Symbol is SimpleLineSymbol lineSymbol &&
            lineSymbol.Color != stationColor) {
            foreach (var graphic in directionGraphicList) {
                if (graphic.Symbol is SimpleLineSymbol symbol) {
                    symbol.Color = stationColor.Value;
                }
            }
        }

        for (int i = 0; i < data.Count; i++) {
            var direction = data[i].DirectionOfArrival;
            Graphic? directionGraphic;

            if (directionGraphicList.Count > i) {
                directionGraphic = directionGraphicList[i];
                UpdateLine(directionGraphicList[i], receiverPoint, direction, LineDistance);
            } else {
                directionGraphic = AddLine(DirectionsOverlay, receiverPoint.X, receiverPoint.Y,
                    direction, LineDistance, directionLineSymbol);
                if (directionGraphic != null) {
                    directionGraphicList.Add(directionGraphic);
                }
            }

            if (directionGraphic != null) {
                directionGraphic.Attributes[ClusterIdAttribute] = data[i].Id;
                directionGraphic.Attributes[ClusterImpulseCountAttribute] = data[i].ImpulseCount;
                directionGraphic.IsVisible = true;
            }
        }
    }

    private void DeleteDirectionsById(uint stationId) {
        var directionGraphicList = GetOrCreateDirectionGraphicList(stationId);
        _dispatcher.Invoke(() => {
            foreach (var graphic in directionGraphicList) {
                DirectionsOverlay?.Graphics.Remove(graphic);
            }
        });

        directionGraphicList.Clear();
    }

    private void MouseLeftButtonUp(MouseButtonEventArgs args) {
        if (args.Source is not MapView mapView) {
            return;
        }

        _dispatcher.Invoke(mapView.DismissCallout);
    }

    private void ShowPtoaPointCallout(Graphic graphic, MapView mapView, Point screenPoint) {
        if (!graphic.Attributes.ContainsKey(PtoaPoint)) {
            return;
        }

        var mapLocation = mapView.ScreenToLocation(screenPoint);
        if (mapLocation == null) {
            return;
        }

        var text = $"""
                    PTOA
                    {graphic.Attributes[CreationDate]}
                    {graphic.Attributes[Latitude]} {graphic.Attributes[Longitude]}
                    """;

        var callout = new Callout { Content = text };
        _dispatcher.Invoke(() => mapView.ShowCalloutAt(mapLocation, callout));
    }

    private void ShowPtoaEllipseCallout(Graphic graphic, MapView mapView, Point screenPoint) {
        if (!graphic.Attributes.ContainsKey(PtoaEllipse)) {
            return;
        }

        var mapLocation = mapView.ScreenToLocation(screenPoint);
        if (mapLocation == null) {
            return;
        }

        var text = $"""
                    PTOA
                    {graphic.Attributes[CreationDate]}
                    {graphic.Attributes[Latitude]} {graphic.Attributes[Longitude]}
                    {graphic.Attributes[Height]} m x {graphic.Attributes[Width]} m, {graphic.Attributes[Angle]}°
                    """;

        var callout = new Callout { Content = text };
        _dispatcher.Invoke(() => mapView.ShowCalloutAt(mapLocation, callout));
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void MouseLeftButtonDown(MouseButtonEventArgs args) {
        if (args.Source is not MapView mapView) {
            return;
        }

        _dispatcher.Invoke(mapView.DismissCallout);

        var screenPoint = args.GetPosition(mapView);

        var clusterGraphic = await FindClusterGraphic(mapView, screenPoint);
        var clusterId = (Guid?)clusterGraphic?.Attributes[ClusterIdAttribute];
        if (clusterId != null) {
            SourcesViewModel?.SelectPdwClusterCommand.Execute((Guid)clusterId);
            SelectDirection(clusterGraphic);
        }

        var ptoaPoint = await FindPtoaGraphic(mapView, screenPoint, PtoaPoint, TdoaResultsOverlay);
        ptoaPoint ??= await FindPtoaGraphic(mapView, screenPoint, PtoaPoint, TdoaSimulationEllipseOverlay);
        if (ptoaPoint is not null) {
            ShowPtoaPointCallout(ptoaPoint, mapView, screenPoint);
            return;
        }

        var ptoaEllipse = await FindPtoaGraphic(mapView, screenPoint, PtoaEllipse, TdoaResultsOverlay);
        ptoaEllipse ??= await FindPtoaGraphic(mapView, screenPoint, PtoaEllipse, TdoaSimulationEllipseOverlay);
        if (ptoaEllipse is not null) {
            ShowPtoaEllipseCallout(ptoaEllipse, mapView, screenPoint);
        }
    }

#pragma warning restore VSTHRD100 // Avoid async void methods

    private async Task<Graphic?> FindClusterGraphic(MapView mapView, Point screenPoint) {
        if (DirectionsOverlay == null) {
            return null;
        }

        var identifyResults = await mapView.IdentifyGraphicsOverlayAsync(DirectionsOverlay, screenPoint,
            _mapSettings.IdentifyObjectTolerance, false, 5);

        var clusterGraphic = identifyResults.Graphics
            .Where(x => x.Attributes.ContainsKey(ClusterIdAttribute)
                        && x.Attributes.ContainsKey(ClusterImpulseCountAttribute))
            .MaxBy(x => (long?)x.Attributes[ClusterImpulseCountAttribute]);
        return clusterGraphic;
    }

    private async Task<Graphic?> FindPtoaGraphic(MapView mapView, Point screenPoint, string key,
        GraphicsOverlay? overlay) {
        if (overlay == null) {
            return null;
        }

        var identifyResults = await mapView.IdentifyGraphicsOverlayAsync(overlay, screenPoint,
            _mapSettings.IdentifyObjectTolerance, false, 5);
        return identifyResults.Graphics.FirstOrDefault(x => x.Attributes.ContainsKey(key));
    }

    private void ClearDirectionsTimerOnTick() {
        _clearDirectionTimer.Stop();
        foreach (var list in _directionGraphicLists.Values) {
            foreach (var graphic in list) {
                DirectionsOverlay?.Graphics.Remove(graphic);
            }

            list.Clear();
        }
    }

    private void SelectDirection(Graphic? graphic) {
        if (_selectedClusterGraphic != null) {
            var previousColor = (_selectedClusterGraphic.Symbol as SimpleLineSymbol)?.Color;
            var previousSymbol = (SimpleLineSymbol)_directionLineSymbol.Clone();
            previousSymbol.Color = previousColor ?? _mapSettings.DirectionColor;
            _selectedClusterGraphic.Symbol = previousSymbol;
            _selectedClusterGraphic = null;
        }

        if (graphic == null) {
            return;
        }

        var color = (graphic.Symbol as SimpleLineSymbol)?.Color;
        var selectedSymbol = (SimpleLineSymbol)_selectedDirectionLineSymbol.Clone();
        selectedSymbol.Color = color ?? _mapSettings.DirectionColor;

        graphic.Symbol = selectedSymbol;
        _selectedClusterGraphic = graphic;
    }

    private void SelectClusterDirection(Guid? clusterId) {
        if (clusterId == (Guid?)_selectedClusterGraphic?.Attributes[ClusterIdAttribute]) {
            return;
        }

        Graphic? graphic = null;

        foreach (var list in _directionGraphicLists.Values) {
            graphic = list.FirstOrDefault(x => (Guid?)x.Attributes[ClusterIdAttribute] == clusterId);
            if (graphic is not null) {
                break;
            }
        }

        SelectDirection(graphic);
    }

    private void AddOrUpdateReviewSector(uint stationId) {
        DeleteReviewSector(stationId);
        var stationType = GetStationType(stationId);
        if (stationType == StationType.ArchontT) {
            return;
        }

        var stationGraphic = GetOrCreateReceiverGraphic(stationId);

        if (stationGraphic?.Geometry is not MapPoint stationPoint) {
            return;
        }

        var antennaDirection = (double)(stationGraphic.Attributes[App6StyleSymbolFieldNames.Direction] ?? 0);
        var stationColor = GetStationColor(stationId) ?? _mapSettings.DirectionColor;
        var sector = CreateSector(antennaDirection, _mapSettings.ReviewSectorAngle, stationPoint, stationColor,
            LineDistance);

        _dispatcher.Invoke(() => ReviewSectorOverlay?.Graphics.Add(sector));
        _reviewSectors[stationId] = sector;
    }

    private StationType? GetStationType(uint stationId) {
        if (_localStationId == stationId) {
            return _localStationType;
        }

        var res = _messenger.Send(new GetLinkedStationTypeRequestMessage(stationId));
        return res.Response;
    }

    private void DeleteReviewSector(uint stationId) {
        if (_reviewSectors.TryGetValue(stationId, out var existingSector)) {
            _dispatcher.Invoke(() => ReviewSectorOverlay?.Graphics.Remove(existingSector));
        }

        _reviewSectors.Remove(stationId);
    }

    private Graphic CreateSector(double direction, double sectorAngle, MapPoint startPoint, Color color,
        double distance) {
        int sectorCount = 10;
        var angleStepSize = sectorAngle / sectorCount;
        var azimuth = direction - sectorAngle / 2;
        List<MapPoint> points = new List<MapPoint> { startPoint };

        for (int i = 0; i <= sectorCount; i++) {
            var point = new[] { startPoint }.MoveGeodetic(distance, LinearUnits.Kilometers, azimuth,
                AngularUnits.Degrees, GeodeticCurveType.Geodesic)[0];

            points.Add(point);
            azimuth += angleStepSize;
        }

        var polygon = new Polygon(points);
        var symbol = new SimpleFillSymbol {
            Color = Color.FromArgb(15, color),
            Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.FromArgb(255, color), 0.5)
        };

        return new Graphic(polygon, symbol);
    }

    private void CreateHeatmap(GeographicalPosition[][] thinHyperboles, GraphicsOverlay? overlay) {
        if (overlay == null) {
            return;
        }

        overlay.Graphics.Clear();
        if (thinHyperboles.Length == 0) {
            return;
        }

        AddPtoaHyperboles(overlay, thinHyperboles);
    }

    private List<Polyline> AddPtoaHyperboles(GraphicsOverlay graphicsOverlay, GeographicalPosition[][] hyperboles) {
        var hyperbolesCount = hyperboles.GetLength(0);

        var polylinePointLists = new List<MapPoint[]>(hyperbolesCount);
        for (var i = 0; i < hyperbolesCount; i++) {
            var pointsCount = hyperboles[i].Length;
            var polylinePoints = new MapPoint[pointsCount];

            for (int j = 0; j < pointsCount; j++) {
                var point = hyperboles[i][j];
                polylinePoints[j] = (new MapPoint(point.Lon, point.Lat, SpatialReferences.Wgs84));
            }

            polylinePointLists.Add(polylinePoints);
        }

        const int polylineWidth = 2;
        var polylineColor = Color.Aquamarine;
        var lineSymbol = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, polylineColor, polylineWidth);

        var polylines = GetPtoaPolylines(polylinePointLists);

        DrawPtoaPolylines(graphicsOverlay, polylines, lineSymbol);
        return polylines;
    }

    private static List<Polyline> GetPtoaPolylines(List<MapPoint[]> polylinePointLists) {
        var polylines = new List<Polyline>(polylinePointLists.Count);
        polylines.AddRange(polylinePointLists.Select(polylinePoint => new Polyline(polylinePoint, SpatialReferences.Wgs84)));

        return polylines;
    }

    private void DrawPtoaPolylines(GraphicsOverlay overlay, List<Polyline> polylines, Symbol symbol) {
        foreach (var polylineGraphic in polylines.Select(polyline => new Graphic(polyline, symbol))) {
            _dispatcher.Invoke(() => overlay.Graphics.Add(polylineGraphic));
        }
    }

    private List<MapPoint> AddPtoaHyperbolesIntersectPoints(GraphicsOverlay graphicsOverlay, List<Polyline> polylines,
        List<Graphic> calculationPoints, DateTime? dateTime) {
        var pointSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, _mapSettings.PtoaPointColor,
            _mapSettings.PtoaPointSize);
        var intersectPoints = GetPtoaIntersectPoints(polylines);
        DrawPtoaIntersectPoints(graphicsOverlay, calculationPoints, intersectPoints, dateTime, pointSymbol);
        return intersectPoints;
    }

    private static List<MapPoint> GetPtoaIntersectPoints(List<Polyline> polylines) {
        var intersectPoints = new List<MapPoint>();

        for (int i = 0; i < polylines.Count; i++) {
            for (int j = i + 1; j < polylines.Count; j++) {
                var intersections = polylines[i].Intersections(polylines[j]);

                if (intersections.Count == 0) {
                    continue;
                }

                foreach (var intersection in intersections) {
                    switch (intersection) {
                        case MapPoint mapPoint:
                            intersectPoints.Add(mapPoint);
                            break;
                        case Multipoint multipoint:
                            intersectPoints.AddRange(multipoint.Points);
                            break;
                    }
                }
            }
        }

        return intersectPoints;
    }

    private void DrawPtoaIntersectPoints(GraphicsOverlay overlay, List<Graphic> calculationPoints,
        List<MapPoint> intersectPoints, DateTime? time, Symbol symbol) {
        foreach (var intersectPoint in intersectPoints) {
            var graphic = new Graphic(intersectPoint, symbol) {
                Attributes = {
                    [CreationDate] = time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    [PtoaPoint] = true,
                    [Longitude] = $"{intersectPoint.X:F3} E",
                    [Latitude] = $"{intersectPoint.Y:F3} N"
                }
            };
            calculationPoints.Add(graphic);

            _dispatcher.Invoke(() => overlay.Graphics.Add(graphic));
        }
    }

    private void LogPtoaResult(List<MapPoint> intersectPoints) {
        _messageLogger.AddMessage(MessageCategory.System, "PTOA: calculation completed", MessageLevel.Info);
        if (intersectPoints.Count == 0) {
            _messageLogger.AddMessage(MessageCategory.System, "PTOA: no intersections found", MessageLevel.Info);
        } else {
            var intersectionsMessage = new StringBuilder();
            intersectionsMessage.Append($"PTOA: {intersectPoints.Count} intersections found ");
            intersectionsMessage.Append(String.Join(", ",
                intersectPoints.Select(value => $"{{{value.Y:F3},{value.X:F3}}}")));
            _messageLogger.AddMessage(MessageCategory.System, intersectionsMessage.ToString(), MessageLevel.Info);
        }
    }

    private void OnMapObjectUpdated(object? sender, MapObjectUpdatedNotification notification) {
        foreach (var mapObject in notification.MapObjects) {
            var mapObj = MapObjects.FirstOrDefault(x => x.Id == mapObject.Id);
            if (mapObj != null) {
                MapObjects.Remove(mapObj);
            }

            if (_editMapObjectVm != null && _editMapObjectVm.Id == mapObject.Id) {
                _dispatcher.Invoke(() => {
                    _editMapObjectVm.Lat = mapObject.Lat;
                    _editMapObjectVm.Lon = mapObject.Lon;
                });
            }

            MapObjects.Add(mapObject);
            _messenger.Send(new MapObjectUpdatedMessage(mapObject));
            AddOrUpdateObjectToMap(mapObject);
        }
    }
}
