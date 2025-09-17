using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Mvvm;
using Esri.ArcGISRuntime.Geometry;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Client.Tools.Dispatcher;
using Infozahyst.RSAAS.Client.ViewModels.Messages;
using Infozahyst.RSAAS.Client.ViewModels.Scenarios;
using Infozahyst.RSAAS.Common;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Core.Tools;
using Infozahyst.RSAAS.Core.Transport.DataStreaming;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Frames;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using SciChart.Core.Extensions;
using IMessenger = CommunityToolkit.Mvvm.Messaging.IMessenger;

namespace Infozahyst.RSAAS.Client.ViewModels.LinkedStations;

public partial class LinkedStationsViewModel : ObservableObject, ISupportServices
{
    private readonly IDatabaseMessagingService _databaseMessagingService;
    private readonly ILinkStationMessagingService _linkStationMessagingService;
    private readonly ISystemControlMessagingService _systemControlMessagingService;
    private readonly IPositionerMessagingService _positionerMessagingService;
    private readonly IReceiverMessagingService _receiverMessagingService;
    private readonly IDataStreamingMessagingService _dataStreamingMessagingService;
    private readonly IManualPositionMessagingService _manualPositionMessagingService;
    private readonly IEnumerable<IBaseMessagingService> _messagingServices;
    private readonly GeneralSettings _generalSettings;
    private readonly ILogger<LinkedStationsViewModel> _logger;
    private readonly IReadOnlyPolicyRegistry<string> _policyRegistry;
    private readonly MessageLogger _messageLogger;
    private readonly IMessenger _messenger;
    private readonly ServerSettings _serverSettings;
    private readonly PeriodicTimer _refreshIndicatorsAndPositionTimer;
    private readonly ReaderWriterLockSlim _stationsLock = new();
    private readonly IDispatcherWrapper _dispatcher;
    private readonly double _bandwidth;
    private bool _isUpdatingIndicators;

    private ObservableCollection<ScenarioViewModel> _scenarios = [];

    [ObservableProperty] private ObservableCollection<LinkedStationViewModel> _linkedStations = [];
    [ObservableProperty] private ObservableCollection<LinkedStationViewModel> _networkLinkedStations = [];
    [ObservableProperty] private ObservableCollection<LinkedStationViewModel> _fileLinkedStations = [];

    [ObservableProperty] private bool _isConnectingStation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUiAvailable))]
    private bool _isServerConnected;

    [NotifyCanExecuteChangedFor(nameof(AddNetworkLinkedStationCommand))] [ObservableProperty]
    private string _ipAddress = "";

    [ObservableProperty] private int _port;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddFileLinkedStationCommand))]
    private uint _fileStationId;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddFileLinkedStationCommand))]
    private StationType? _stationType;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUiAvailable))] 
    private bool _isControlEnabled;

    public bool IsUiAvailable => IsServerConnected && IsControlEnabled;

    protected bool IsPanoramaSpectrumEnabled { get; set; } = true;

    private IDialogService DialogService => ServiceContainer.GetService<IDialogService>();
    private IServiceContainer? _serviceContainer;

    private ReceiverMode? _lastReceiverMode;
   
    IServiceContainer ISupportServices.ServiceContainer { get { return ServiceContainer; } }

    protected IServiceContainer ServiceContainer {
        get { return _serviceContainer ??= new ServiceContainer(this); }
    }
   
    public float MaxIfAttenuation => _generalSettings.MaxIfAttenuation;

    public float MaxRfAttenuation => _generalSettings.MaxRfAttenuation;

    public ushort MaxDecayTime => _generalSettings.MaxDecayTime;

    public ushort MaxAttackTime => _generalSettings.MaxAttackTime;

    public bool CanAddNetworkLinkedStation() =>
        !string.IsNullOrEmpty(IpAddress);

    public IAsyncCommand UploadCommand { get; }

    public LinkedStationsViewModel(IDatabaseMessagingService databaseMessagingService,
        ILinkStationMessagingService linkStationMessagingService,
        ISystemControlMessagingService systemControlMessagingService,
        IPositionerMessagingService positionerMessagingService, IReceiverMessagingService receiverMessagingService,
        IDataStreamingMessagingService dataStreamingMessagingService,
        IManualPositionMessagingService manualPositionMessagingService,
        IEnumerable<IBaseMessagingService> messagingServices,
        IMessenger messenger, ILogger<LinkedStationsViewModel> logger, MessageLogger messageLogger,
        IOptions<ServerSettings> serverOptions, IOptions<GeneralSettings> generalOptions,
        IReadOnlyPolicyRegistry<string> policyRegistry, IDispatcherWrapper? dispatcherWrapper = null) {
        _databaseMessagingService = databaseMessagingService;
        _logger = logger;
        _linkStationMessagingService = linkStationMessagingService;
        _systemControlMessagingService = systemControlMessagingService;
        _policyRegistry = policyRegistry;
        _positionerMessagingService = positionerMessagingService;
        _receiverMessagingService = receiverMessagingService;
        _dataStreamingMessagingService = dataStreamingMessagingService;
        _manualPositionMessagingService = manualPositionMessagingService;
        _messagingServices = messagingServices;
        _messageLogger = messageLogger;
        _messenger = messenger;
        _serverSettings = serverOptions.Value;
        _generalSettings = generalOptions.Value;
        Port = _serverSettings.MqttPort;
        _dispatcher = dispatcherWrapper ?? new DispatcherWrapper(Application.Current.Dispatcher);
        _bandwidth = UnitsNet.Frequency.FromHertz(generalOptions.Value.Bandwidth).Megahertz;
        _receiverMessagingService.ReceiverParametersChanged += OnReceiverParametersChanged;
        _receiverMessagingService.ReceiverConnectionChanged += OnReceiverConnectionChanged;
        _positionerMessagingService.PositionChanged += OnPositionChanged;
        _systemControlMessagingService.AnglesParametersChanged += OnAnglesDataReceived;
        _systemControlMessagingService.ReceiverAngleStateChanged += OnReceiverAngleStateChanged;
        messenger.Register<StatusChangedMessage>(this, (_, m) => OnStatusChanged(m.Value));
        messenger.Register<ServerConnectionStateChangedMessage>(this, 
            (_, m) => OnServerConnectionChanged(m.Value));
        messenger.Register<PanoramaSpectrumChangedMessage>(this, (_, m) => {
            IsPanoramaSpectrumEnabled = m.Value;
        });
        messenger.Register<ReceiverModeChangedMessage>(this, 
            (_, m) => OnReceiverModeChanged(m.Value));
        messenger.Register<AppClosingMessage>(this, (_, _) => OnAppClosing());
        messenger.Register<ScenariosLoadedMessage>(this,
            (_, m) => OnScenariosLoaded(m.Value));
        _messenger.Register<ControlStatusChangedMessage>(this,
            (_, m) => _dispatcher.Invoke(() => IsControlEnabled = m.Value));
        _messenger.Register<LinkedStationsViewModel, GetLinkedStationTypeRequestMessage>(this, 
            (r, m) => m.Reply(r.GetStationType(m.StationId)));
        
        var indicatorsRefreshInterval =
            TimeSpan.FromMilliseconds(generalOptions.Value.LinkedStationIndicatorsRefreshInterval);
        _refreshIndicatorsAndPositionTimer = new PeriodicTimer(indicatorsRefreshInterval);
        _ = RefreshIndicatorsAndPosition();
        _linkStationMessagingService.SlaveConnectionChanged += OnLinkedStationConnectionChanged;
        _databaseMessagingService.LinkStationChanged += OnLinkStationChanged;
        UploadCommand = new AsyncCommand<Guid>(Upload);
    }
    
    public StationType? GetStationType(uint stationId) {
        return GetStation(stationId, out var station) ? station?.StationType : null;
    }

    private void OnScenariosLoaded(ObservableCollection<ScenarioViewModel> scenarios) {
        _scenarios = scenarios;
        var stations = GetLinkedStationsCopy();
        _dispatcher.Invoke(() => {
            foreach (var station in stations) {
                station.Scenarios = _scenarios;
                if (station.SelectedScenarioId.HasValue) {
                    station.SelectedScenario = _scenarios.FirstOrDefault(x => x.Id == station.SelectedScenarioId);
                }
            }
        });
    }

    private void OnLinkStationChanged(object? sender, LinkedStationChangedNotification e) {
        if (!GetStation(e.Station.StationId, out var linkedStation) || linkedStation is null) {
            return;
        }

        if (linkedStation.IsSpectrumStreamActive != e.Station.IsSpectrumStreamActive) {
            _dispatcher.Invoke(() => 
                linkedStation.IsSpectrumStreamActive = e.Station.IsSpectrumStreamActive);
            _messenger.Send(new LinkedStationStreamingActivityMessage(linkedStation.IsSpectrumStreamActive,
                linkedStation.StationId));
        }

        if (!e.Station.IsActive) {
            _dispatcher.Invoke(() => {
                linkedStation.IsConnected = false;
                linkedStation.IsActive = false;
                linkedStation.ClearIndicators();
            });
            _messenger.Send(new LinkedStationDeletedMessage(linkedStation.StationId));
        }
    }

    private void OnReceiverModeChanged(ReceiverMode? mode) {
        _lastReceiverMode = mode;
        var stations = GetLinkedStationsCopy();
        foreach (var station in stations) {
            station.IsMasterInElintMode = mode == ReceiverMode.Elint;
        }
    }

    private void OnReceiverParametersChanged(object? sender, ReceiverParametersChangedNotification? notification) {
        if (notification == null) {
            return;
        }

        GetStation(notification.StationId, out var station);
        if (station == null) {
            return;
        }

        _dispatcher.Invoke(() => {
            if (notification.Mode.HasValue) {
                station.ReceiverMode = notification.Mode.Value;
                if (notification.Scenario is not null) {
                    station.SelectedScenarioId = notification.Scenario.Id;

                    if (station.Scenarios.Any()) {
                        station.SelectedScenario =
                            station.Scenarios.FirstOrDefault(a => a.Id == station.SelectedScenarioId);
                    }

                    station.IsScenarioActive = true;
                } else {
                    station.SelectedScenarioId = null;
                    station.SelectedScenario = null;
                    station.IsScenarioActive = false;
                }
            }

            if (notification.InstantViewBandwidth.HasValue) {
                station.InstantViewBandwidth = notification.InstantViewBandwidth.Value;
            }

            if (notification.Frequency.HasValue) {
                var freqMhz = UnitsNet.Frequency.FromHertz(notification.Frequency.Value).Megahertz;
                station.Frequency = (float)Math.Truncate(freqMhz);
                _messenger.Send(new FrequencyChangedMessage(station.Frequency.Value, notification.StationId));
            }

            if (notification.ScanRange != null) {
                station.StartFrequency = (float)notification.ScanRange.FreqFrom;
                station.StopFrequency = (float)notification.ScanRange.FreqTo;
            }

            if (station.Scenarios != _scenarios) {
                station.Scenarios = _scenarios;
            }
        });

        if (notification.Frequency.HasValue && station.Frequency != null) {
            _messenger.Send(new FrequencyChangedMessage(station.Frequency.Value, notification.StationId));
        }

        if (notification.Mode.HasValue) {
            _messenger.Send(
                new LinkedStationReceiverModeChangedMessage(notification.Mode.Value, notification.StationId));
        }
    }

    private async void OnReceiverConnectionChanged(object? sender, ConnectionChangedNotification e) {
        if (!e.IsConnected) {
            return;
        }

        var linkedStation = LinkedStations.FirstOrDefault(a => a.StationId == e.StationId);
        if (linkedStation == null) {
            return;
        }

        await UpdateSynchronizationParameters(linkedStation);
    }

    private void OnAppClosing() {
        _refreshIndicatorsAndPositionTimer.Dispose();
    }

    private void OnLinkedStationConnectionChanged(object? sender, ConnectionChangedNotification e) {
        if (!GetStation(e.StationId, out var station) || station is null) {
            return;
        }

        _dispatcher.Invoke(() => {
            station.IsConnected = e.IsConnected;
            if (station.IsConnected) {
                return;
            }

            station.ReceiverIndicator.Connection = IndicatorStatus.Error;
            station.PositionerIndicator.Connection = IndicatorStatus.Error;
            station.RotatorIndicator.Connection = IndicatorStatus.Error;
        });

        if (station.IsConnected) {
            _ = Task.Run(async () => {
                try {
                    await GetSlaveStationInfo(station);
                    await UpdateSynchronizationParameters(station);
                } catch (Exception ex) {
                    _logger.LogError(exception: ex, message: "");
                    _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
                }
            });
        }
    }

    private async Task GetSlaveStationInfo(LinkedStationViewModel station) {
        try {
            var getInfoRes = await _linkStationMessagingService.GetSlaveStationInfo(station.IpAddress, station.Port);
            if (!getInfoRes.IsSuccess) {
                _logger.LogWarning("Cannot get station-id from linked station with address - {IpAddress}:{Port}",
                    station.IpAddress, station.Port);
                _messageLogger.AddMessage(MessageCategory.System,
                    $"Cannot get station-id from linked station with address - {station.IpAddress}:{station.Port}",
                    MessageLevel.Warn);
                return;
            }

            if (station.StationType == getInfoRes.StationType) {
                return;
            }

            _dispatcher.Invoke(() => {
                station.StationType = getInfoRes.StationType;
                if (station.StationType == Common.Enums.StationType.ArchontT) {
                    SetStationAngleValues(station,
                        new AzimuthParameters { AntennaDirection = 0, AntennaRotatorAngle = 0, Azimuth0 = 0 });
                }
            });

            await SaveStationAllFields(station);
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    private async Task<AddOrUpdateLinkStationResponse> SaveStationAllFields(LinkedStationViewModel station) {
        var saveRes = await _databaseMessagingService.AddOrUpdateLinkStation(station.LinkedStation);
        if (!saveRes.IsSuccess) {
            _logger.LogError(saveRes.ErrorMessage);
            _messageLogger.AddMessage(MessageCategory.System,
                saveRes.ErrorMessage ?? "Error at saving linked station", MessageLevel.Error);
        } else {
            station.SaveAllFields();
        }

        return saveRes;
    }

    private void OnReceiverAngleStateChanged(object? sender, ReceiverAngleStateChangedNotification e) {
        if (!GetStation(e.StationId, out var station) || station is null ||
            station.IsArchontTType) {
            return;
        }

        station.IsAngleOperationError = e.IsErrorDuringOperation;
    }

    private void OnAnglesDataReceived(object? sender, AnglesParametersChangedNotification angleParametersNotification) {
        if (!GetStation(angleParametersNotification.StationId, out var station) || station is null ||
            station.IsArchontTType) {
            return;
        }

        SetStationAngleValues(station, angleParametersNotification.State);
    }

    private bool GetStation(uint stationId, out LinkedStationViewModel? station) {
        _stationsLock.EnterReadLock();
        try {
            station = LinkedStations.FirstOrDefault(x => x.StationId == stationId);
        } finally {
            _stationsLock.ExitReadLock();
        }

        return station is not null;
    }
    
    private bool GetStation(Guid id, out LinkedStationViewModel? station) {
        _stationsLock.EnterReadLock();
        try {
            station = LinkedStations.FirstOrDefault(x => x.Id == id);
        } finally {
            _stationsLock.ExitReadLock();
        }

        return station is not null;
    }

    private async Task RefreshIndicatorsAndPosition() {
        while (await _refreshIndicatorsAndPositionTimer.WaitForNextTickAsync()) {
            if (!_isUpdatingIndicators) {
                continue;
            }

            var stations = GetLinkedStationsCopy();
            stations = stations
                .Where(a => a.StationDataSource == StationDataSource.File
                                           || a is { StationDataSource: StationDataSource.Network, IsAvailable: true })
                .ToList();
            try {
                foreach (var station in stations) {
                    await GetIndicators(station);
                    await GetActualCoordinatesState(station);
                }
            } catch (Exception e) {
                _logger.LogError(exception: e, message: "");
                _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
            }
        }
    }

    private async Task GetIndicators(LinkedStationViewModel station) {
        if (station.StationId == 0) {
            return;
        }

        if (!station.IsActive || !station.IsAvailableForRotation) {
            return;
        }

        var indicators = await _systemControlMessagingService.GetIndicators(station.StationId);
        station.IsConnected = indicators.IsSuccess;
        if (!indicators.IsSuccess) {
            return;
        }

        _dispatcher.Invoke(() => station.RefreshIndicators(indicators.Notification));
        await GetStationReceiverInfo(station);
    }

    private List<LinkedStationViewModel> GetLinkedStationsCopy() {
        List<LinkedStationViewModel> stations;
        _stationsLock.EnterReadLock();
        try {
            stations = LinkedStations.ToList();
        } finally {
            _stationsLock.ExitReadLock();
        }

        return stations;
    }

    private async Task GetStationReceiverInfo(LinkedStationViewModel station) {
        var response = await _receiverMessagingService.GetDeviceInfo(station.StationId);
        if (!response.IsSuccess) {
            return;
        }

        var synthesizerStatus = response.DeviceInfo.SynthesizerInfo.Status;
        var gnssStatus = response.DeviceInfo.GnssInfo.Status;

        _dispatcher.Invoke(() => {
            station.ReceiverIndicator.Synthesizer = synthesizerStatus;
            if (station.PositionerIndicator.Connection != IndicatorStatus.None) {
                station.PositionerIndicator.Gnss = gnssStatus;
            } else {
                station.PositionerIndicator.Gnss = null;
            }
        });
    }

    private void OnServerConnectionChanged(bool isConnected) {
        _dispatcher.Invoke(() => IsServerConnected = isConnected);
    }

    private void OnPositionChanged(object? sender, PositionChangedNotification notification) {
        if (!GetStation(notification.StationId, out var station) || station is null) {
            return;
        }

        station.Longitude = notification.Data.Position.Longitude;
        station.Latitude = notification.Data.Position.Latitude;

        SendPositionerModeOrManualCoordinatesChanged(station, notification.Data.PositionSource);
    }

    private void SendPositionerModeOrManualCoordinatesChanged(LinkedStationViewModel station,
        PositionSource positionSource) {
        bool isInManualCoordinatesMode = positionSource == PositionSource.Manual;
        if (!isInManualCoordinatesMode && station.IsInManualCoordinatesMode == isInManualCoordinatesMode) {
            return;
        }

        station.IsInManualCoordinatesMode = isInManualCoordinatesMode;
        var receiverPoint = new MapPoint(station.Longitude, station.Latitude, SpatialReferences.Wgs84);
        _messenger.Send(
            new PositionerModeOrManualCoordinatesChangedMessage(new ValueTuple<PositionSource, MapPoint, uint>(
                positionSource, receiverPoint, station.StationId)));
    }

    private async void OnStatusChanged(bool isRunning) {
        if (isRunning) {
            try {
                await LoadStations();
            } catch (Exception ex) {
                _logger.LogError(ex, ex.Message);
                _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
            }
        }

        _isUpdatingIndicators = isRunning;
    }

    [RelayCommand(CanExecute = nameof(CanSetAntennaDirectionAndAzimuth0))]
    private async Task SetAntennaDirectionAndAzimuth0(Guid id) {
        if (!GetStation(id, out var station) || station is null || station.IsArchontTType) {
            return;
        }

        try {
            var isAngleChanged = station.IsAntennaDirectionChanged;
            var isAzimuth0Changed = station.IsAzimuth0Changed;
            _dispatcher.Invoke(() => station.IsAvailableForRotation = false);

            var antennaDirectionToSet = station.AntennaDirection;
            if (isAzimuth0Changed) {
                await SetAzimuth0(station);
            }

            if (isAngleChanged) {
                station.AntennaDirection = antennaDirectionToSet;
                await SetAntennaDirection(station);
            }
        } finally {
            _dispatcher.Invoke(() => station.IsAvailableForRotation = true);
        }
    }

    private bool CanSetAntennaDirectionAndAzimuth0(Guid id) {
        if (!GetStation(id, out var station) || station is null || station.IsArchontTType) {
            return false;
        }

        return station.IsAntennaDirectionChanged || station.IsAzimuth0Changed;
    }

    private async Task SetAntennaDirection(LinkedStationViewModel station) {
        if (station.IsArchontTType) {
            return;
        }

        try {
            var res = await _systemControlMessagingService.SetAntennaDirection(station.AntennaDirection,
                station.StationId);
            if (res is { IsSuccess: true, SetReceiverDirection: not null }) {
                _dispatcher.Invoke(() => {
                    station.AntennaDirection = res.SetReceiverDirection.Value;
                    station.LastSavedAngle = res.SetReceiverDirection.Value;
                });

                var receiverAngleState = await _systemControlMessagingService.GetReceiverAngleState(station.StationId);
                if (receiverAngleState is { IsSuccess: true }) {
                    _dispatcher.Invoke(() =>
                        station.IsReceiverAngleDifferentFromReal = !receiverAngleState.IsRealAngleEqualsReceiverAngle);
                }
            } else {
                throw new Exception(res.ErrorMessage);
            }
        } catch (Exception e) {
            _dispatcher.Invoke(() => station.AntennaDirection = station.LastSavedAngle);
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private async Task SetAzimuth0(LinkedStationViewModel station) {
        if (station.IsArchontTType) {
            return;
        }

        try {
            var res = await _systemControlMessagingService.SetAzimuth0(station.Azimuth0, station.StationId);
            if (res is { IsSuccess: true, SetAzimuth0: { } }) {
                _dispatcher.Invoke(() => {
                    station.Azimuth0 = res.SetAzimuth0.Value;
                    station.LastSavedAzimuth0 = res.SetAzimuth0.Value;
                    station.IsReceiverAngleDifferentFromReal = false;
                });
            } else {
                throw new Exception(res.ErrorMessage);
            }
        } catch (Exception e) {
            _dispatcher.Invoke(() => station.Azimuth0 = station.LastSavedAzimuth0);
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddNetworkLinkedStation))]
    private async Task AddNetworkLinkedStation() {
        // todo add station type or command?
        var station = new LinkedStationViewModel(StationDataSource.Network, Common.Enums.StationType.ArchontA) {
            IpAddress = IpAddress,
            Port = Port,
            IsIQStreamActive = false,
            IsSpectrumStreamActive = false,
            Bandwidth = (float)_bandwidth,
            IsMasterInElintMode = _lastReceiverMode == ReceiverMode.Elint,
            IsSynchronized = true,
            AttackTime = _generalSettings.DefaultAttackTime,
            DecayTime = _generalSettings.DefaultDecayTime
        };
        ClearLinkedStationFields();

        if (CheckIfStationWithSameAddressExists(station)) {
            return;
        }

        await SetupStation(station);

        AddLinkedStation(station);
        await GetIndicators(station);
    }

    private void ClearLinkedStationFields() {
        IpAddress = "";
        Port = _serverSettings.MqttPort;
        FileStationId = 0;
        StationType = null;
    }

    [RelayCommand(CanExecute = nameof(CanUpdateLinkedStation))]
    private async Task UpdateLinkedStation(Guid id) {
        if (!GetStation(id, out var station) || station is null) {
            return;
        }

        if (CheckIfStationWithSameAddressExists(station)) {
            station.ReturnLastIpAddressAndPort();
            return;
        }

        var stationId = station.StationId;
        await _linkStationMessagingService.UnLinkStation(stationId);
        _messenger.Send(new LinkedStationDeletedMessage(stationId));

        _dispatcher.Invoke(() => {
            station.SaveAllFields();
            station.ClearIndicators();
        });
        await SetupStation(station);
    }

    private bool CanUpdateLinkedStation(Guid id) {
        if (!GetStation(id, out var station) || station is null) {
            return false;
        }

        return station.IsPortOrIpAddressChanged;
    }

    [RelayCommand(CanExecute = nameof(CanUpdateLinkedStationColor))]
    private async Task UpdateLinkedStationColor(Guid id) {
        if (!GetStation(id, out var station) || station is null) {
            return;
        }

        var saveRes =
            await _databaseMessagingService.AddOrUpdateLinkStation(station.GetOnlyColorChangedLinkedStation());

        if (saveRes.IsSuccess) {
            station.SaveLastColor();
            _messenger.Send(
                new LinkedStationColorChangedMessage((station.StationId, station.Color, station.DrawingColor)));
        } else {
            _logger.LogError(saveRes.ErrorMessage);
            _messageLogger.AddMessage(MessageCategory.System, saveRes.ErrorMessage ?? "Error at saving linked station",
                MessageLevel.Error);
        }
    }

    private bool CanUpdateLinkedStationColor(Guid id) {
        if (!GetStation(id, out var station) || station is null) {
            return false;
        }

        return station.IsColorChanged;
    }

    private bool CheckIfStationWithSameAddressExists(LinkedStationViewModel station) {
        var stations = GetLinkedStationsCopy();
        if (stations.Any(x => x.SavedStation.IpAddress == station.IpAddress && x.SavedStation.Port == station.Port)) {
            var message = "Slave station with identical IP address and Port is already added";
            _logger.LogWarning(message);
            _messageLogger.AddMessage(MessageCategory.System, message, MessageLevel.Warn);
            return true;
        }

        return false;
    }

    private void CheckStationId(LinkedStationViewModel station) {
        var stations = GetLinkedStationsCopy();
        if (stations.Any(x => x.StationId == station.StationId)) {
            var message = "Slave station with identical Station ID is already added";
            _logger.LogWarning(message);
            _messageLogger.AddMessage(MessageCategory.System, message, MessageLevel.Warn);
        }
    }

    private async Task SetupStation(LinkedStationViewModel station) {
        IsConnectingStation = true;
        try {
            var getSlaveStationInfoResponse = await _linkStationMessagingService
                .GetSlaveStationInfo(station.IpAddress, station.Port);
            if (!getSlaveStationInfoResponse.IsSuccess) {
                _logger.LogWarning("Cannot get station-id from linked station with address - {IpAddress}:{Port}",
                    station.IpAddress, station.Port);
                _messageLogger.AddMessage(MessageCategory.System,
                    $"Cannot get station-id from linked station with address - {station.IpAddress}:{station.Port}",
                    MessageLevel.Warn);
                station.IsActive = false;
                station.IsConnected = false;
                station.StationId = 0;
                return;
            }

            LoadEndpointsTimeouts(getSlaveStationInfoResponse.EndpointsTimeouts);
            station.StationId = getSlaveStationInfoResponse.SlaveStationId;
            station.StationType = getSlaveStationInfoResponse.StationType;

            CheckStationId(station);
            CheckIfStationWithSameAddressExists(station);

            await SaveStationAllFields(station);

            await LinkStation(station);
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        } finally {
            IsConnectingStation = false;
        }
    }

    private void LoadEndpointsTimeouts(EndpointTimeoutInfo[] timeouts) {
        foreach (var messagingService in _messagingServices) {
            messagingService.LoadEndpointsTimeouts(timeouts);
        }
    }

    private async Task UpdateStationPositionAndAntennaDirection(LinkedStationViewModel station) {
        try {
            _dispatcher.Invoke(() => station.IsAvailableForRotation = false);

            if (!station.IsArchontTType) {
                var azimuthParameters = await _systemControlMessagingService.GetAzimuthParameters(station.StationId);
                if (azimuthParameters is { IsSuccess: true, AzimuthParameters: not null }) {
                    SetStationAngleValues(station, azimuthParameters.AzimuthParameters);
                }

                var receiverAngleState = await _systemControlMessagingService.GetReceiverAngleState(station.StationId);
                if (receiverAngleState is { IsSuccess: true }) {
                    _dispatcher.Invoke(() =>
                        station.IsReceiverAngleDifferentFromReal = !receiverAngleState.IsRealAngleEqualsReceiverAngle);
                }
            } else {
                SetStationAngleValues(station,
                    new AzimuthParameters { AntennaDirection = 0, AntennaRotatorAngle = 0, Azimuth0 = 0 });
            }

            await GetActualCoordinatesState(station);

            _messenger.Send(new LinkedStationAddedMessage(station));
            _messenger.Send(new AntennaDirectionUpdatedMessage(
                new AntennaDirectionUpdatedData(station.AntennaDirection, station.StationId)));
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        } finally {
            _dispatcher.Invoke(() => station.IsAvailableForRotation = true);
            IsConnectingStation = false;
        }
    }

    private async Task GetActualCoordinatesState(LinkedStationViewModel station) {
        var actualCoordinatesState = await _manualPositionMessagingService.GetActualPositionState(station.StationId);
        if (actualCoordinatesState is not { IsSuccess: true }) {
            return;
        }

        if (actualCoordinatesState.Longitude is not null && actualCoordinatesState.Latitude is not null) {
            station.Longitude = actualCoordinatesState.Longitude.Value;
            station.Latitude = actualCoordinatesState.Latitude.Value;
        } else {
            station.Longitude = 0;
            station.Latitude = 0;
        }

        SendPositionerModeOrManualCoordinatesChanged(station, actualCoordinatesState.PositionSource);
    }

    private void SetStationAngleValues(LinkedStationViewModel station, AzimuthParameters res) {
        if (res.AntennaDirection != null) {
            station.AntennaDirection = res.AntennaDirection.Value;
            station.LastSavedAngle = res.AntennaDirection.Value;
            _messenger.Send(new AntennaDirectionUpdatedMessage(new AntennaDirectionUpdatedData(station.AntennaDirection,
                station.StationId)));
        }

        if (res.Azimuth0 != null) {
            station.Azimuth0 = res.Azimuth0.Value;
            station.LastSavedAzimuth0 = res.Azimuth0.Value;
        }
    }

    private async Task LoadStations() {
        try {
            _policyRegistry.TryGet(PolicyNameConstants.WaitAndRetryForeverAsync, out IAsyncPolicy policy);
            var stations = await policy.ExecuteAsync(_databaseMessagingService.GetLinkStationList);
            if (stations != null) {
                var stationVms = new ObservableCollection<LinkedStationViewModel>(
                    stations.Select(x => new LinkedStationViewModel(x) { Bandwidth = (float)_bandwidth }).ToList());
                _dispatcher.Invoke(() => LinkedStations = stationVms);
                UpdateFilters();

                var isMasterInElintMode = _lastReceiverMode == ReceiverMode.Elint;
                foreach (var station in NetworkLinkedStations) {
                    if (!station.IsActive) {
                        continue;
                    }

                    station.IsMasterInElintMode = isMasterInElintMode;
                    try {
                        await UpdateStationPositionAndAntennaDirection(station);
                        await UpdateSynchronizationParameters(station);
                        await GetIndicators(station);
                        await GetSlaveStationInfo(station);
                    } catch (Exception ex) {
                        _logger.LogError(ex, ex.Message);
                        _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
                    }
                }

                foreach (var station in FileLinkedStations) {
                    _messenger.Send(new LinkedStationColorChangedMessage((station.StationId, station.Color,
                        station.DrawingColor)));
                }
            }
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteLinkedStation(Guid id) {
        var dialogRes = DeleteDialog();
        if (!dialogRes) {
            return;
        }

        if (!GetStation(id, out var station) || station is null) {
            return;
        }

        try {
            var deleteFromDbRes = await _databaseMessagingService.RemoveLinkStations(new List<Guid> { id });
            if (!deleteFromDbRes.IsSuccess) {
                _logger.LogError(deleteFromDbRes.ErrorMessage);
                if (deleteFromDbRes.ErrorMessage != null) {
                    _messageLogger.AddMessage(MessageCategory.System, deleteFromDbRes.ErrorMessage, MessageLevel.Error);
                }
            }

            if (station.StationDataSource == StationDataSource.Network) {
                await _linkStationMessagingService.UnLinkStation(station.StationId);
            }

            if (station.StationDataSource == StationDataSource.File) {
                await _systemControlMessagingService.StopClustering(station.StationId);
            }

            _messenger.Send(new LinkedStationDeletedMessage(station.StationId));

            _stationsLock.EnterWriteLock();
            try {
                LinkedStations.Remove(station);
            } finally {
                _stationsLock.ExitWriteLock();
            }

            UpdateFilters();
        } catch (Exception ex) {
            _logger.LogError(ex, "");
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    private bool DeleteDialog() {
        var yesCommand = new UICommand(
            id: null,
            caption: "Yes",
            command: null,
            isDefault: true,
            isCancel: false
        );

        var noCommand = new UICommand(
            id: MessageBoxResult.Cancel,
            caption: "No",
            command: null,
            isDefault: false,
            isCancel: true
        );

        var result = DialogService.ShowDialog(
            dialogCommands: new[] { yesCommand, noCommand },
            title: "",
            viewModel: null
        );

        return result == yesCommand;
    }

    [RelayCommand]
    private async Task ToggleLinkedStationConnection(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        if (station.IsActive) {
            await UnLinkStation(station);
        } else {
            await LinkStation(station);
        }
    }

    [RelayCommand]
    private async Task SaveSynchronizationFlag(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        station.IsSynchronized = !station.IsSynchronized;

        var saveRes =
            await _databaseMessagingService.AddOrUpdateLinkStation(station.GetSynchronizationChangedLinkedStation());
        if (!saveRes.IsSuccess) {
            _logger.LogError(saveRes.ErrorMessage);
            _messageLogger.AddMessage(MessageCategory.System,
                saveRes.ErrorMessage ?? "Error at saving linked station", MessageLevel.Error);
        } else {
            station.SaveIsSynchronized();
        }
    }

    [RelayCommand]
    private async Task ToggleSpectrumStreaming(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        var isEnabled = station.IsSpectrumStreamActive;
        station.SaveIsSpectrumActive();
        // clientId will be setted on the master server
        var conn = new DataStreamingConnection(Guid.Empty, string.Empty, 0, DataFrameType.Spectrogram);
        if (isEnabled) {
            await _dataStreamingMessagingService.StartDataStreaming(conn, stationId: station.StationId);
        } else {
            await _dataStreamingMessagingService.StopDataStreaming(conn, station.StationId);
        }

        _messenger.Send(new LinkedStationStreamingActivityMessage(isEnabled, station.StationId));
    }

    [RelayCommand]
    private async Task ToggleIQStreaming(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        var saveRes =
            await _databaseMessagingService.AddOrUpdateLinkStation(station.GetIQChangedLinkedStation());
        if (!saveRes.IsSuccess) {
            _logger.LogError(saveRes.ErrorMessage);
            _messageLogger.AddMessage(MessageCategory.System,
                saveRes.ErrorMessage ?? "Error at saving linked station", MessageLevel.Error);
            return;
        }

        station.SaveIsIQStreamActive();
    }

    private bool CanSaveSynchronizationParameters(Guid id) {
        if (!GetStation(id, out var station) || station is null) {
            return false;
        }

        return station is { IsReceiverParametersChanged: true };
    }

    [RelayCommand(CanExecute = nameof(CanSaveSynchronizationParameters))]
    private async Task SaveSynchronizationParameters(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        var freqHz = station.Frequency.HasValue
            ? (float)UnitsNet.Frequency.FromMegahertz(station.Frequency.Value).Hertz
            : (float?)null;
        var res = await _linkStationMessagingService.SetReceiverParameters(
            station.StationId, station.ReceiverMode, freqHz,
            station.StartFrequency, station.StopFrequency, IsPanoramaSpectrumEnabled, station.InstantViewBandwidth,
            station.GetAgcParameters(), station.RfAtt, station.IfAtt, station.SelectedScenario?.Id);
        if (!res.IsSuccess) {
            _logger.LogError(res.ErrorMessage);
            _messageLogger.AddMessage(MessageCategory.System,
                res.ErrorMessage ?? $"Error set parameters synchronization for the station {station.StationId}",
                MessageLevel.Error);
        }

        await UpdateSynchronizationParameters(station);
    }

    [RelayCommand]
    private async Task StartStopScenario(Guid id) {
        var station = LinkedStations.FirstOrDefault(x => x.Id == id);
        if (station is null) {
            return;
        }

        var freqHz = station.Frequency.HasValue
            ? (float)UnitsNet.Frequency.FromMegahertz(station.Frequency.Value).Hertz
            : (float?)null;
        if (station.IsScenarioActive) {
            var res = await _linkStationMessagingService.SetReceiverParameters(
                station.StationId, station.ReceiverMode, freqHz,
                station.StartFrequency, station.StopFrequency, IsPanoramaSpectrumEnabled, station.InstantViewBandwidth,
                null, null, null, station.SelectedScenario?.Id);
            if (!res.IsSuccess) {
                _logger.LogError("Error at starting scenario for slave station: {Message}", res.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System,
                    res.ErrorMessage ?? "Error at starting scenario for slave station",
                    MessageLevel.Error);
                _dispatcher.Invoke(() => station.IsScenarioActive = false);
            }
        } else {
            var res = await _linkStationMessagingService.SetReceiverParameters(
                station.StationId, station.ReceiverMode, freqHz,
                station.StartFrequency, station.StopFrequency, IsPanoramaSpectrumEnabled, station.InstantViewBandwidth,
                station.GetAgcParameters(), station.RfAtt, station.IfAtt);
            if (!res.IsSuccess) {
                _logger.LogError("Error at stopping scenario for slave station: {Message}", res.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System,
                    res.ErrorMessage ?? "Error at stopping scenario for slave station",
                    MessageLevel.Error);
                _dispatcher.Invoke(() => station.IsScenarioActive = false);
            }
        }

        await UpdateSynchronizationParameters(station, true);
    }

    private async Task UnLinkStation(LinkedStationViewModel station) {
        try {
            var res = await _linkStationMessagingService.UnLinkStation(station.StationId);
            if (res.IsSuccess) {
                _dispatcher.Invoke(() => {
                    station.IsConnected = false;
                    station.IsActive = false;
                    station.ClearIndicators();
                });
                _messenger.Send(new LinkedStationDeletedMessage(station.StationId));
            } else {
                _logger.LogError(res.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System,
                    res.ErrorMessage ?? $"Error disconnecting from the station {station.StationId}",
                    MessageLevel.Error);
            }

            await SaveStationAllFields(station);
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    private async Task LinkStation(LinkedStationViewModel station) {
        try {
            var res = await _linkStationMessagingService.LinkStation(station.ToStation());
            _dispatcher.Invoke(() => station.IsActive = res.IsSuccess);
            if (res.IsSuccess) {
                _messenger.Send(new LinkedStationAddedMessage(station));
                station.IsMasterInElintMode = _lastReceiverMode == ReceiverMode.Elint;
                await UpdateSynchronizationParameters(station);
                await UpdateStationPositionAndAntennaDirection(station);
            } else {
                _logger.LogError(res.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System,
                    res.ErrorMessage ?? $"Error connecting to the station {station.StationId}", MessageLevel.Error);
            }

            await SaveStationAllFields(station);
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        }
    }

    private async Task UpdateSynchronizationParameters(LinkedStationViewModel station, bool isFromStopScenario = false) {
        var response = await _linkStationMessagingService.GetReceiverParameters(station.StationId);
        if (response.IsSuccess) {
            _dispatcher.Invoke(() => {
                station.IsSynchronized = response.LinkedStation.IsSynchronized;
                station.ReceiverMode = isFromStopScenario && response.ReceiverMode == ReceiverMode.Elint
                    ? ReceiverMode.EsmScenario 
                    : response.ReceiverMode;
                station.InstantViewBandwidth = response.InstantViewBandwidth;

                if (response.Frequency.HasValue) {
                    var freqMhz = UnitsNet.Frequency.FromHertz(response.Frequency.Value).Megahertz;
                    station.Frequency = (float)Math.Truncate(freqMhz);
                }

                if (response.ScanRange != null) {
                    station.StartFrequency = (float?)response.ScanRange.FreqFrom;
                    station.StopFrequency = (float?)response.ScanRange.FreqTo;
                }

                if (station.Scenarios != _scenarios) {
                    station.Scenarios = _scenarios;
                }

                if (response.ScenarioId.HasValue) {
                    station.SelectedScenarioId = response.ScenarioId;

                    if (station.Scenarios.Any()) {
                        station.SelectedScenario =
                            station.Scenarios.FirstOrDefault(a => a.Id == station.SelectedScenarioId);
                    }

                    station.IsScenarioActive = true;
                } else {
                    station.SelectedScenarioId = null;
                    station.SelectedScenario = null;
                    station.IsScenarioActive = false;
                }

                if (response.AgcParameters != null) {
                    station.IsAgcEnabled = response.AgcParameters.IsEnabled;
                    station.AttackTime = response.AgcParameters.AttackTime;
                    station.DecayTime = response.AgcParameters.DecayTime;
                }

                if (response is { IfAttenuator: not null, RfAttenuator: not null }) {
                    station.RfAtt = response.RfAttenuator.Value;
                    station.IfAtt = response.IfAttenuator.Value;
                }

                station.SaveReceiverParameters();
            });

            if (station.Frequency != null) {
                _messenger.Send(new FrequencyChangedMessage(station.Frequency.Value, station.StationId));
            }

            _messenger.Send(new LinkedStationReceiverModeChangedMessage(station.ReceiverMode, station.StationId));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddFileLinkedStation))]
    private async Task AddFileLinkedStation() {
        if (StationType == null || FileStationId <= 0) {
            return;
        }

        var station = new LinkedStationViewModel(StationDataSource.File, StationType.Value) {
            StationId = FileStationId, IsActive = true, IpAddress = string.Empty, Port = 0
        };

        await SaveLinkedStation(station);
        AddLinkedStation(station);
        ClearLinkedStationFields();
    }

    private bool CanAddFileLinkedStation() => FileStationId > 0 && StationType != null;

    private void AddLinkedStation(LinkedStationViewModel station) {
        _stationsLock.EnterWriteLock();
        try {
            LinkedStations.Add(station);
        } finally {
            _stationsLock.ExitWriteLock();
        }

        UpdateFilters();
    }

    private void UpdateFilters() {
        _dispatcher.Invoke(() => {
            var networkStations = LinkedStations.Where(s =>
                s.StationDataSource == StationDataSource.Network).ToList();
            NetworkLinkedStations.RemoveWhere(x => !networkStations.Contains(x));
            foreach (var station in networkStations.Where(x => !NetworkLinkedStations.Contains(x))) {
                NetworkLinkedStations.Add(station);
            }

            var fileStations = LinkedStations.Where(s =>
                s.StationDataSource == StationDataSource.File).ToList();
            FileLinkedStations.RemoveWhere(x => !fileStations.Contains(x));
            foreach (var station in fileStations.Where(x => !FileLinkedStations.Contains(x))) {
                FileLinkedStations.Add(station);
            }
        });
    }

    private async Task SaveLinkedStation(LinkedStationViewModel station) {
        try {
            var saveRes = await SaveStationAllFields(station);
            if (saveRes.IsSuccess) {
                _messenger.Send(new LinkedStationAddedMessage(station));
            }
        } catch (Exception e) {
            _logger.LogError(e, e.Message);
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private async Task Upload(Guid id) {
        if (!GetStation(id, out var station) || station is null || string.IsNullOrEmpty(station.SourceFilePath)) {
            return;
        }

        string? compressedFilePath = null;

        try {
            compressedFilePath = BrotliCompressionUtils.GetCompressedFilePath
                (AppDataPathService.GetCommonAppDataPath(), station.SourceFilePath);
            await BrotliCompressionUtils.CompressFile(station.SourceFilePath, compressedFilePath,
                UploadCommand.CancellationTokenSource.Token);
            await using FileStream compressedFileStream = new FileStream(compressedFilePath, FileMode.Open);
            var response = await _systemControlMessagingService.UploadPdwFile(compressedFileStream,
                UploadCommand.CancellationTokenSource.Token);
            if (response.IsSuccess && !UploadCommand.IsCancellationRequested) {
                var pdwFilters = new PdwFilters {
                    IsDoaFilterEnabled = station.IsDoaFilterEnabled,
                    IsFrequencyFilterEnabled = station.IsFrequencyFilterEnabled,
                    IsTimeFilterEnabled = station.IsDateTimeFilterEnabled,
                    DoaFilter = new ValueTuple<double, double>(station.MinDoa, station.MaxDoa),
                    FrequencyFilter = new ValueTuple<float, float>(station.MinFrequency, station.MaxFrequency),
                    TimeFilter = new ValueTuple<DateTime, DateTime>(station.MinDateTime, station.MaxDateTime)
                };
                await _systemControlMessagingService.StartClustering(station.StationId, StationDataSource.File,
                    response.FileName, pdwFilters);
            } else if (response is { IsSuccess: false, ErrorMessage: not null }) {
                _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, "Failed upload file, reason: " + ex.Message, 
                MessageLevel.Error);
        } finally {
            if (compressedFilePath != null) {
                File.Delete(compressedFilePath);
            }
        }
    }
}
