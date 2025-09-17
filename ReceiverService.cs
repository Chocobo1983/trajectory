using System.Collections.Concurrent;
using Infozahyst.RSAAS.Common.Collections;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Streaming.Interfaces;
using Infozahyst.RSAAS.Common.Tools;
using Infozahyst.RSAAS.Core.Tools;
using Infozahyst.RSAAS.Core.Transport.DataStreaming;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Frames;
using Infozahyst.RSAAS.Server.Controllers.Interfaces;
using Infozahyst.RSAAS.Server.DAL;
using Infozahyst.RSAAS.Server.Exceptions;
using Infozahyst.RSAAS.Server.Receiver;
using Infozahyst.RSAAS.Server.Receiver.IQ;
using Infozahyst.RSAAS.Server.Receiver.NetSdr;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;
using Infozahyst.RSAAS.Server.Receiver.PersistenceSpectrum;
using Infozahyst.RSAAS.Server.Receiver.Spectrum;
using Infozahyst.RSAAS.Server.Receiver.StreamRecording.IQ;
using Infozahyst.RSAAS.Server.Receiver.StreamRecording.Pdw;
using Infozahyst.RSAAS.Server.Services.GnssRecordingService;
using Infozahyst.RSAAS.Server.Services.Interfaces;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Microsoft.VisualStudio.Threading;
using DateTimeHelper = Infozahyst.RSAAS.Core.Tools.DateTimeHelper;

namespace Infozahyst.RSAAS.Server.Services;

public class ReceiverService : IReceiverService
{
    private readonly AsyncReaderWriterLock _receiverModeSync = new(new JoinableTaskContext());
    private readonly IControlClient _controlClient;
    private readonly IIQWriterFactory _iqWriterFactory;
    private readonly IPdwWriterFactory _pdwWriterFactory;
    private readonly ISpectrumClient _spectrumClient;
    private readonly IPersistenceSpectrumClient _persistenceSpectrumClient;
    private readonly IThresholdClient _thresholdClient;
    private readonly IIQClient _iqClient;
    private readonly IRemoteClientFactory _remoteClientFactory;
    private readonly IFeatureManager _featureManager;
    private readonly IIQReceivingClient _iqReceivingClient;
    private readonly IIQWithTimestampReceivingClient _iqWithTimestampReceivingClient;
    private readonly IPositionerService _positionerService;
    private readonly IMessageLogger _messageLogger;
    private readonly IOptions<StreamingSettings> _streamingSettings;
    private readonly ILogger<ReceiverService> _logger;
    private IIQWriter? _localIQStreamWriter;

    private readonly ConcurrentDictionary<uint,
        (Guid ClientId, DataFrameType Type, IIQWriter Writer)> _remoteIQWriters = [];

    private readonly SemaphoreSlim _iqWriterLock = new(1, 1);
    private readonly SemaphoreSlim _pdwWriterLock = new(1, 1);
    private readonly IDataAccessManagerFactory _dataAccessManagerFactory;
    private readonly IGnssRecordingService _gnssRecordingService;
    private readonly uint _stationId;
    private readonly StationType _stationType;
    private readonly TimeSpan _monitoringInterval;
    private readonly CircularBuffer<TrajectoryPoint> _trajectoryBuffer;
    private IPdwWriter? _pdwStreamWriter;
    private ReceiverMode? _receiverMode;
    private Guid? _scenarioId;
    private bool _isPanoramaEnabled;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private bool _isConnected;
    
    public bool IsIQRecordingActive => _localIQStreamWriter is not null;
    public bool IsPdwRecordingActive => _pdwStreamWriter is not null;
    public bool IsGnssRecordingActive { get; private set; }

    public event EventHandler<IQSetupParameters>? IQSetupChanged;
    public event EventHandler<IQRecordingNotification>? IQRecordingStatusChanged;
    public event EventHandler<PdwRecordingNotification>? PdwRecordingStatusChanged;
    public event EventHandler<GnssRecordingNotification>? GnssRecordingStatusChanged;
    public event EventHandler<ReceiverParametersChangedNotification>? ReceiverParametersChanged;
    public event EventHandler<DeviceInfoUpdateNotification>? DeviceInfoUpdated;

    public ReceiverService(IControlClient controlClient, IIQWriterFactory iqWriterFactory,
        IPdwWriterFactory pdwWriterFactory, ISpectrumClient spectrumClient,
        IPersistenceSpectrumClient persistenceSpectrumClient, IIQReceivingClient iqReceivingClient,
        IIQWithTimestampReceivingClient iqWithTimestampReceivingClient, IThresholdClient thresholdClient,
        IPositionerService positionerService, IIQClient iqClient, IRemoteClientFactory remoteClientFactory,
        IFeatureManager featureManager, IDataAccessManagerFactory dataAccessManagerFactory,
        IMessageLogger messageLogger, IOptions<StreamingSettings> streamingSettings,
        ILogger<ReceiverService> logger, IGnssRecordingService gnssRecordingService,
        IOptions<GeneralSettings> generalSettings) {
        _controlClient = controlClient;
        _iqWriterFactory = iqWriterFactory;
        _pdwWriterFactory = pdwWriterFactory;
        _spectrumClient = spectrumClient;
        _persistenceSpectrumClient = persistenceSpectrumClient;
        _iqReceivingClient = iqReceivingClient;
        _iqWithTimestampReceivingClient = iqWithTimestampReceivingClient;
        _thresholdClient = thresholdClient;
        _iqClient = iqClient;
        _remoteClientFactory = remoteClientFactory;
        _featureManager = featureManager;
        _positionerService = positionerService;
        _messageLogger = messageLogger;
        _streamingSettings = streamingSettings;
        _logger = logger;
        _dataAccessManagerFactory = dataAccessManagerFactory;
        _gnssRecordingService = gnssRecordingService;
      
        _stationId = generalSettings.Value.StationId;
        _stationType = generalSettings.Value.StationType;
        _monitoringInterval = generalSettings.Value.ReceiverMonitoringInterval;
        _trajectoryBuffer = new CircularBuffer<TrajectoryPoint>(generalSettings.Value.TrajectoryBufferCapacity,
            generalSettings.Value.TrajectoryBufferCapacity);
        _controlClient.ConnectionStatusChanged += OnConnectionStatusChanged;
    }
    
    private void OnConnectionStatusChanged(object? sender, bool isConnected) {
        _isConnected = isConnected;
    }

    public TrajectoryPoint[] GetDeviceTrajectory() {
        return _trajectoryBuffer.ToArray();
    }

    public async Task StartMonitoringDevice() {
        await StopMonitoringDevice();
        _monitoringCts = new CancellationTokenSource();
        _monitoringTask = Task.Run(() => MonitorDevice(_monitoringCts.Token));
    }

    private async Task MonitorDevice(CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(_monitoringInterval);

        try {
            while (await timer.WaitForNextTickAsync(cancellationToken)) {
                if (!_isConnected) {
                    continue;
                }

                await UpdateDeviceInfo();
            }
        } catch (OperationCanceledException) {
            _logger.LogInformation("Receiver monitoring stopped.");
        } catch (Exception ex) {
            _logger.LogError(ex, ex.Message);
        }
    }

    private async Task UpdateDeviceInfo() {
        try {
            DateTime dateTime = DateTime.UnixEpoch;
            var deviceInfoResponse = await _controlClient.GetDeviceInfo();

            if (!deviceInfoResponse.IsSuccess) {
                _logger.LogWarning("Failed to get device info");
                return;
            }

            var timeResponse = await _controlClient.GetTime();
            if (timeResponse.IsSuccess) {
                dateTime = DateTimeHelper.ConvertUnixTimeWithMicroseconds(timeResponse.Value.TimeSec,
                    timeResponse.Value.TimeUsec);
            }

            var deviceInfo = deviceInfoResponse.Value;
            _positionerService.UpdateReceiverPosition(deviceInfo.GnssInfo, dateTime);
            DeviceInfoUpdated?.Invoke(this, new DeviceInfoUpdateNotification(_stationId, deviceInfo, dateTime));

            if (_stationType == StationType.GekataGA) {
                var gnssInfo = deviceInfo.GnssInfo;
                var trajectoryPoint = new TrajectoryPoint(dateTime, gnssInfo.Longitude, gnssInfo.Latitude);
                _trajectoryBuffer.PushBack(trajectoryPoint);
            }
        } catch (Exception ex) {
            _logger.LogError(ex, "Error while updating device information");
        }
    }

    public async Task StopMonitoringDevice() {
        if (_monitoringCts is not null) {
            await TasksHelper.SafeComplete(_monitoringCts, _monitoringTask);
            _monitoringCts.Dispose();
            _monitoringCts = null;
        }
    }

    public async Task RestoreReceiverMode(bool isPdwStreamControlRequired = false) =>
        await SetReceiverMode(_receiverMode.GetValueOrDefault(),
            isPdwStreamControlRequired: isPdwStreamControlRequired, isPanoramaSpectrumEnabled: _isPanoramaEnabled);

    public async Task<(ReceiverMode ReceiverMode, bool IsPanoramaMode, Guid? ScenarioId)?> GetReceiverMode() {
        (ReceiverMode ReceiverMode, bool IsPanoramaMode, Guid? ScenarioId)? result = null;
        await using (_ = await _receiverModeSync.ReadLockAsync()) {
            var fResponse = await _controlClient.GetChannelFunctions();
            if (!fResponse.IsSuccess) {
                return result;
            }

            result = fResponse.Value switch {
                ChannelFunction.PanoramaScan => (ReceiverMode.Esm, true, null),
                ChannelFunction.ESM => (ReceiverMode.Esm, false, null),
                ChannelFunction.None => (ReceiverMode.Elint, false, null),
                ChannelFunction.Com => (ReceiverMode.Comint, false, null),
                ChannelFunction.EsmScenario => (ReceiverMode.EsmScenario, false, _scenarioId),
                ChannelFunction.EsmScenarioAndSpectrum => (ReceiverMode.EsmScenario, true, _scenarioId),
                _ => result
            };
        }

        return result;
    }

    public async Task<(int? ScanListLength, ScanRange? ScenarioRange)?> GetScenarioRange() {
        var receiverMode = await GetReceiverMode();
        if (receiverMode is not { ReceiverMode: ReceiverMode.EsmScenario }) {
            return null;
        }

        var scanList = await _controlClient.GetScanList();
        var scanListLength = scanList.Value?.Length;
        var scenarioRange = GetScenarioScanRange(scanList);

        return (scanListLength, scenarioRange);
    }

    private ScanRange? GetScenarioScanRange(NetSdrResponse<ScanListItem[]> scanList) {
        if (scanList.Value == null || scanList.Value.Length == 0) {
            return null;
        }

        var minScanListFrequency = scanList.Value.Min(x => x.Frequency.Value);
        var maxScanListFrequency = scanList.Value.Max(x => x.Frequency.Value);
        var from = (long)UnitsNet.Frequency.FromHertz(minScanListFrequency).Megahertz;
        var to = (long)UnitsNet.Frequency.FromHertz(maxScanListFrequency).Megahertz;
        return new ScanRange { FreqFrom = from, FreqTo = to };
    }

    public virtual async Task<NetSdrResponse<ChannelFunction>?> SetReceiverMode(ReceiverMode receiverMode,
        Scenario? scenario = null, bool isPanoramaSpectrumEnabled = false, bool isPdwStreamControlRequired = true) {
        ScanListItem[]? scanListItems = null;
        if (receiverMode == ReceiverMode.EsmScenario && scenario != null) {
            _scenarioId = scenario.Id;
            scanListItems = await FilterScanListItems(scenario.ScenarioDetails);
        } else if (receiverMode != ReceiverMode.EsmScenario) {
            _scenarioId = null;
        }

        if (isPdwStreamControlRequired) {
            await _controlClient.StopStreaming(CaptureMode.Continuous);
        }

        NetSdrResponse<ChannelFunction>? receiverResponse = null;
        ScanRange? esmScenarioRange = null;
        int? scanListLength = null;
        switch (receiverMode) {
            case ReceiverMode.Elint:
                receiverResponse = await _controlClient.SetChannelFunctions(ChannelFunction.None);
                if (isPdwStreamControlRequired) {
                    await _controlClient.StartStreaming(CaptureMode.Continuous);
                }

                break;
            case ReceiverMode.Esm: {
                    if (isPanoramaSpectrumEnabled) {
                        await using (_ = await _receiverModeSync.WriteLockAsync()) {
                            var channelFunction = ChannelFunction.PanoramaScan;
                            await _controlClient.SetChannelFunctions(ChannelFunction.None);
                            receiverResponse = await _controlClient.SetChannelFunctions(channelFunction);
                        }
                    } else {
                        receiverResponse = await _controlClient.SetChannelFunctions(ChannelFunction.ESM);
                    }

                    if (isPdwStreamControlRequired) {
                        await _controlClient.StartStreaming(CaptureMode.Continuous);
                    }

                    break;
                }
            case ReceiverMode.Comint: {
                    if (_receiverMode != ReceiverMode.Elint) {
                        await _controlClient.SetChannelFunctions(ChannelFunction.None);
                    }

                    receiverResponse = await _controlClient.SetChannelFunctions(ChannelFunction.Com);
                    if (isPdwStreamControlRequired) {
                        await _controlClient.StartStreaming(CaptureMode.Continuous);
                    }

                    break;
                }
            case ReceiverMode.EsmScenario: {
                    if ((scanListItems?.Any()).GetValueOrDefault()) {
                        await _controlClient.SetChannelFunctions(ChannelFunction.None);
                        var response = await _controlClient.SetScanList(scanListItems!);
                        esmScenarioRange = GetScenarioScanRange(response);
                        scanListLength = response.Value?.Length;
                        var channelFunction = isPanoramaSpectrumEnabled
                            ? ChannelFunction.EsmScenarioAndSpectrum
                            : ChannelFunction.EsmScenario;
                        receiverResponse = await _controlClient.SetChannelFunctions(channelFunction);
                        if (isPdwStreamControlRequired) {
                            await _controlClient.StartStreaming(CaptureMode.Continuous);
                        }
                    } else {
                        receiverResponse = await _controlClient.SetChannelFunctions(ChannelFunction.None);
                    }

                    receiverResponse.Value = ChannelFunction.EsmScenarioAndSpectrum;
                    break;
                }
        }

        if (receiverResponse is { IsNak: true }) {
            var getModeResponse = await _controlClient.GetChannelFunctions();
            if (getModeResponse.IsSuccess) {
                receiverResponse.Value = getModeResponse.Value;
            }
        }

        if (receiverResponse is not { IsSuccess: true }) {
            return receiverResponse;
        }

        _receiverMode = receiverResponse.Value switch {
            ChannelFunction.None => ReceiverMode.Elint,
            ChannelFunction.Com => ReceiverMode.Comint,
            ChannelFunction.ESM => ReceiverMode.Esm,
            ChannelFunction.PanoramaScan => ReceiverMode.Esm,
            _ => ReceiverMode.EsmScenario
        };

        if (receiverResponse.IsSuccess &&  _receiverMode != receiverMode ) {
            receiverResponse.State = CommandState.Warning;
        }

        _isPanoramaEnabled = isPanoramaSpectrumEnabled;
        ReceiverParametersChanged?.Invoke(this,
            new ReceiverParametersChangedNotification {
                Mode = _receiverMode,
                IsPanoramaMode = isPanoramaSpectrumEnabled,
                Scenario = scenario?.ToDto(),
                ScanListLength = scanListLength,
                ScenarioRange = esmScenarioRange
            });

        return receiverResponse;
    }

    private async Task<ScanListItem[]?> FilterScanListItems(IList<ScenarioDetail>? scanListItems) {
        if (scanListItems == null) {
            return null;
        }

        var boundaries = await _controlClient.GetFrequencyRange();
        if (boundaries is not { IsSuccess: true, Value: not null }) {
            return null;
        }

        var filteredDetails = new List<ScanListItem>();
        var notPassedProfiles = new List<RadarProfile>();

        var minFreqHz = UnitsNet.Frequency.FromMegahertz(boundaries.Value.FreqFrom).Hertz;
        var maxFreqHz = UnitsNet.Frequency.FromMegahertz(boundaries.Value.FreqTo).Hertz;

        foreach (var scenarioDetail in scanListItems) {
            if (scenarioDetail.RadarProfile?.FrequencyBehaviors is null || (scenarioDetail.RadarProfile
                    .FrequencyBehaviors?.Where(x => x.CenterFrequencyMean.HasValue).Any(x =>
                        x.CenterFrequencyMean > maxFreqHz ||
                        x.CenterFrequencyMean < minFreqHz) ?? false)) {
                if (scenarioDetail.RadarProfile != null) {
                    notPassedProfiles.Add(scenarioDetail.RadarProfile);
                }

                continue;
            }

            filteredDetails.AddRange(scenarioDetail.RadarProfile.FrequencyBehaviors!
                .Where(x => x.CenterFrequencyMean.HasValue)
                .Select(x => new ScanListItem(x.CenterFrequencyMean.GetValueOrDefault(),
                    scenarioDetail.RfAttenuator, scenarioDetail.IfAttenuator)));
        }

        if (filteredDetails.Count == 0) {
            _messageLogger.AddMessage(MessageCategory.System,
                "No radar profiles are in the receiver frequency range", MessageLevel.Warn);
            _logger.LogWarning("No radar profiles are in the receiver frequency range");

            return null;
        }

        if (notPassedProfiles.Count > 0) {
            _messageLogger.AddMessage(MessageCategory.System,
                $"Radar profiles {string.Join(", ", notPassedProfiles.Select(x => $"\"{x.Name}\""))} are not" +
                $" in the receiver frequency range",
                MessageLevel.Warn);
            _logger.LogWarning(
                "Radar profiles {Profiles} are not in the receiver frequency range",
                string.Join(", ", notPassedProfiles.Select(x => $"\"{x.Name}\"")));
        }

        return filteredDetails.ToArray();
    }

    public async Task<NetSdrResponse<ChannelFunction>> SetPanoramaMode(bool isPanoramaEnabled, ScenarioDto? scenario) {
        ChannelFunction channelFunction = isPanoramaEnabled switch {
            true => _receiverMode != ReceiverMode.EsmScenario
                ? ChannelFunction.PanoramaScan
                : ChannelFunction.EsmScenarioAndSpectrum,
            false => _receiverMode != ReceiverMode.EsmScenario
                ? ChannelFunction.ESM
                : ChannelFunction.EsmScenario
        };

        await _controlClient.StopStreaming(CaptureMode.Continuous);
        await _controlClient.SetChannelFunctions(ChannelFunction.None);
        var res = await _controlClient.SetChannelFunctions(channelFunction);
        await _controlClient.StartStreaming(CaptureMode.Continuous);

        if (res.IsSuccess) {
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification {
                    Mode = _receiverMode, IsPanoramaMode = isPanoramaEnabled, Scenario = scenario
                });
        }

        return res;
    }

    public async Task StartIQRecording(IQRecordingParameters parameters, uint stationId,
        IReadOnlyList<LinkedStation>? linkedStationList) {
        ArgumentNullException.ThrowIfNull(parameters);
        try {
            await StopIQRecording(false, false);
            await _iqWriterLock.WaitAsync();
            if (_localIQStreamWriter == null) {
                var mode = await GetReceiverMode();
                if (mode is null) {
                    throw new ValidationException($"Cannot get receiver mode");
                }

                if (mode.Value.ReceiverMode is ReceiverMode.Elint or ReceiverMode.Comint) {
                    var frequency = await GetFrequency();
                    if (frequency is { IsSuccess: false }) {
                        throw new ValidationException($"Cannot get frequency: {frequency.ErrorMessage}");
                    }

                    parameters.Frequency = (ulong)(frequency.Value + parameters.Shift);
                } else {
                    var scanRange = await GetScanRange();
                    if (scanRange is { IsSuccess: false } || scanRange.Value is null) {
                        throw new ValidationException($"Cannot get scan-range: {scanRange.ErrorMessage}");
                    }

                    parameters.Frequency = (ulong)UnitsNet.Frequency
                        .FromMegahertz((scanRange.Value.FreqFrom + scanRange.Value.FreqTo) / 2).Hertz;
                }

                (double Longitude, double Latitude) position = _positionerService.GetActualCoordinates();

                _localIQStreamWriter = _iqWriterFactory.CreateLocal(parameters, position, stationId);
                _localIQStreamWriter.OnWritingStopped += OnIqWritingStopped;

                var setupParameters = await GetIQSetup();
                if (setupParameters != null) {
                    OnIQRecordingStatusChanged(new IQRecordingNotification(true, parameters, setupParameters));
                    await _localIQStreamWriter.Start();
                }
            }

            if (linkedStationList != null && linkedStationList.Any()) {
                var localIpAddress = _streamingSettings.Value.RepeaterLocalIpAddress
                                     ?? await SelfAddressHelper.GetLocalIpAddress(linkedStationList.First().IpAddress);
                var connection = parameters.WithTimestamp
                ? await _iqWithTimestampReceivingClient.StartListening()
                : await _iqReceivingClient.StartListening();

                foreach (var linkedStation in linkedStationList) {
                    var remoteWriter = _iqWriterFactory.CreateRemote(parameters, linkedStation.StationId);
                    await remoteWriter.Start();

                    var clientId = parameters.WithTimestamp
                    ? _iqWithTimestampReceivingClient.GetOrAddSourceSessionId(linkedStation.StationId.ToString())
                    : _iqReceivingClient.GetOrAddSourceSessionId(linkedStation.StationId.ToString());

                    var type = parameters.WithTimestamp ? DataFrameType.IQWithTimestamp : DataFrameType.IQ;
                    _remoteIQWriters.TryAdd(linkedStation.StationId, (clientId, type, remoteWriter));

                    if (connection != null) {
                        var startDataStreamingResult = await StartDataStreaming(
                            connection, localIpAddress, clientId, linkedStation.StationId);
                        if (startDataStreamingResult is { IsSuccess: false }) {
                            _logger.LogWarning("Failed start iq streaming for station {StationId}",
                                linkedStation.StationId);
                        }
                    }
                }
            }

            var currentIQState = await _controlClient.GetDDCState();
            if (currentIQState is { IsSuccess: true, Value.State: State.Idle }) {
                var format = parameters.Format;
                var result = await StartIQStreaming(parameters.Shift, parameters.RateId, format);
                if (result is { IsSuccess: false }) {
                    throw new ValidationException(
                        $"Cannot start IQ streaming {result.ErrorMessage}");
                }
            }
        } finally {
            _iqWriterLock.Release();
        }
    }

    private async Task<StartDataStreamingResponse?> StartDataStreaming(DataStreamingConnection connection,
        string localIpAddress, Guid sessionId, uint stationId) {
        var proxy = await _remoteClientFactory.GetTypeProxy<IDataStreamingController>(stationId);
        var request = new StartDataStreamingRequest {
            StationId = stationId,
            AppId = Guid.NewGuid(),
            Mode = connection.StreamType,
            SessionId = sessionId,
            Host = localIpAddress,
            Port = connection.Port,
            Protocol = connection.DataProtocol,
            IsClient = false
        };
        try {
            if (proxy == null) {
                return new StartDataStreamingResponse {
                    IsSuccess = false,
                    ErrorMessage = $"Station {stationId} was unlinked, ignore sending commands",
                    AppId = request.AppId,
                    StationId = stationId
                };
            }

            return await proxy.Invoke((ins, req) => ins.StartDataStreaming(req), request);
        } catch (Exception e) {
            _logger.LogError(e, "Failed to start data-stream for station {StationId} type {Type}", stationId,
                connection.StreamType);
            return new StartDataStreamingResponse {
                IsSuccess = false, ErrorMessage = e.Message, AppId = request.AppId, StationId = stationId
            };
        }
    }

    private async Task<StopDataStreamingResponse?>
        StopDataStreaming(uint stationId, Guid sessionId, DataFrameType type) {
        var proxy = await _remoteClientFactory.GetTypeProxy<IDataStreamingController>(stationId);
        var request = new StopDataStreamingRequest {
            StationId = stationId, AppId = Guid.NewGuid(), SessionId = sessionId, Mode = type
        };
        try {
            if (proxy == null) {
                return new StopDataStreamingResponse {
                    IsSuccess = false,
                    ErrorMessage = $"Station {stationId} was unlinked, ignore sending commands",
                    AppId = request.AppId,
                    StationId = stationId
                };
            }

            return await proxy.Invoke((ins, req) => ins.StopDataStreaming(req), request);
        } catch (Exception e) {
            _logger.LogError(e, "Failed to stop data-stream for station {StationId} type {Type}", stationId, type);
            return new StopDataStreamingResponse {
                IsSuccess = false, ErrorMessage = e.Message, AppId = request.AppId, StationId = stationId
            };
        }
    }

    public async Task StopIQRecording(bool resetStateAndSetup = true, bool sendNotification = true) {
        try {
            await _iqWriterLock.WaitAsync();
            if (resetStateAndSetup) {
                await _controlClient.SetDDCState(0, 0, State.Idle, IQPacketFormat.Legacy);
                var streamResult = await _controlClient.GetStreamingState();
                if (streamResult is { IsSuccess: true, Value.CaptureMode: CaptureMode.IQTDOA }) {
                    await _controlClient.StopStreaming(CaptureMode.Continuous);
                }

                var setupResult = await _controlClient.GetDDCSetup();
                await _controlClient.SetDDCSetup(IQTDOAModeId.NotActive, setupResult.Value.WaitPulses,
                    setupResult.Value.WaitTime, setupResult.Value.FrameTime);
            }

            foreach (var tuple in _remoteIQWriters) {
                try {
                    await tuple.Value.Writer.Stop();
                    await StopDataStreaming(tuple.Key, tuple.Value.ClientId, tuple.Value.Type);
                } catch (Exception e) {
                    _logger.LogError(e, "Failed stop remote stream writer for station {StationId}", tuple.Key);
                }
            }

            _remoteIQWriters.Clear();
        } finally {
            if (_localIQStreamWriter != null) {
                await _localIQStreamWriter.Stop();
                _localIQStreamWriter.OnWritingStopped -= OnIqWritingStopped;
                _localIQStreamWriter = null;
                if (sendNotification) {
                    OnIQRecordingStatusChanged(new IQRecordingNotification(false));
                }
            }

            _iqWriterLock.Release();
        }
    }

    public async Task<NetSdrResponse<bool>> StartIQStreaming(int shift, byte rateId, IQPacketFormat format) {
        return await _controlClient.SetDDCState(shift, rateId, State.Run, format);
    }

    public async Task StopIQStreaming() {
        await _controlClient.SetDDCState(0, 0, State.Idle, IQPacketFormat.Legacy);
        var streamResult = await _controlClient.GetStreamingState();
        if (streamResult is { IsSuccess: true, Value.CaptureMode: CaptureMode.IQTDOA }) {
            await _controlClient.StopStreaming(CaptureMode.Continuous);
        }

        await _controlClient.SetDDCSetup(IQTDOAModeId.NotActive);
    }

    public async Task SetupIQ(IQSetupParameters parameters) {
        var setupResult = await _controlClient.GetDDCSetup();
        var stateResult = await _controlClient.GetDDCState();

        var tdoaMode = parameters.Mode switch {
            IQRecordingMode.TriggerCycle => IQTDOAModeId.TriggerCycle,
            IQRecordingMode.TriggerOnce => IQTDOAModeId.TriggerOnce,
            IQRecordingMode.Continuous => IQTDOAModeId.NotActive,
            _ => throw new ArgumentOutOfRangeException()
        };
        bool isChanged;
        if (parameters.Mode == IQRecordingMode.Continuous) {
            isChanged = tdoaMode != setupResult.Value.ModeId ||
                        parameters.RateId != stateResult.Value.GetRate ||
                        parameters.WithTimestamp != (stateResult.Value.GetFormat == IQPacketFormat.WithTimeStamp);
        } else {
            isChanged =
                parameters.PulseCounter != setupResult.Value.WaitPulses ||
                parameters.RecordTime != setupResult.Value.FrameTime ||
                parameters.PulseWait != setupResult.Value.WaitTime ||
                tdoaMode != setupResult.Value.ModeId ||
                parameters.Shift != stateResult.Value.Shift ||
                parameters.RateId != stateResult.Value.GetRate ||
                parameters.WithTimestamp != (stateResult.Value.GetFormat == IQPacketFormat.WithTimeStamp);
            var instantViewBandwidth = await _controlClient.GetInstantViewBandwidth();
            if (instantViewBandwidth.IsSuccess &&
                instantViewBandwidth.Value != InstantViewBandwidth.Bandwidth100MHz) {
                throw new ValidationException(
                    $"Instant view bandwidth can be only 100MHz for mode {parameters.Mode}");
            }
        }

        if (isChanged) {
            await StopIQStreaming();
            await _controlClient.SetDDCSetup(tdoaMode, parameters.PulseCounter, parameters.PulseWait, parameters.RecordTime);
            var captureMode = tdoaMode == IQTDOAModeId.NotActive ? CaptureMode.Continuous : CaptureMode.IQTDOA;
            await _controlClient.StartStreaming(captureMode);

            if (_localIQStreamWriter != null) {
                using var dataAccessManager = _dataAccessManagerFactory.Create();
                var linkedStationList = await dataAccessManager.LinkStationRepository.GetList();
                var activeLinkedStations = linkedStationList
                    .Where(a =>
                        a is { IsActive: true, IsIQStreamActive: true, StationDataSource: StationDataSource.Network })
                    .ToList();
                var recordingParams = _localIQStreamWriter.GetRecordingParams();
                await StartIQRecording(recordingParams.Parameters, recordingParams.StationId, activeLinkedStations);
            }

            IQSetupChanged?.Invoke(null, parameters);
        }
    }

    public async Task<(int Shift, byte RateId, IQPacketFormat Format, State StreamState)?> GetIQState() {
        var result = await _controlClient.GetDDCState();
        if (result.IsSuccess) {
            return (result.Value.Shift, result.Value.GetRate, result.Value.GetFormat, result.Value.State);
        }

        return null;
    }

    public async Task CheckIQRecordingStatus() {
        var currentIQState = await _controlClient.GetDDCState();
        if (currentIQState.IsSuccess) {
            if (currentIQState.Value.State == State.Run) {
                if (IsIQRecordingActive) {
                    var recordingParams = _localIQStreamWriter?.GetRecordingParams();
                    var iqSetupParameters = await GetIQSetup();
                    OnIQRecordingStatusChanged(new IQRecordingNotification(true, recordingParams?.Parameters, iqSetupParameters));
                }
            } else {
                await StopIQRecording(false);
            }
        }
    }

    public async Task<IQSetupParameters?> GetIQSetup() {
        var result = await _controlClient.GetDDCSetup();
        if (result.IsSuccess) {
            var tdoaMode = result.Value.ModeId switch {
                IQTDOAModeId.TriggerCycle => IQRecordingMode.TriggerCycle,
                IQTDOAModeId.TriggerOnce => IQRecordingMode.TriggerOnce,
                IQTDOAModeId.NotActive => IQRecordingMode.Continuous,
                _ => throw new ArgumentOutOfRangeException()
            };
            return new IQSetupParameters {
                PulseCounter = result.Value.WaitPulses,
                PulseWait = result.Value.WaitTime,
                RecordTime = result.Value.FrameTime,
                Mode = tdoaMode
            };
        }

        return null;
    }

    public async Task StartGnssRecording() {
        await _gnssRecordingService.Start();
        OnGnssRecordingStatusChanged(new GnssRecordingNotification(true));
        IsGnssRecordingActive = true;
    }

    public async Task StopGnssRecording() {
        await _gnssRecordingService.Stop();
        OnGnssRecordingStatusChanged(new GnssRecordingNotification(false));
        IsGnssRecordingActive = false;
    }

    public async Task StartPdwRecording(PdwRecordingParameters pdwRecordingParameters, uint stationId) {
        ArgumentNullException.ThrowIfNull(pdwRecordingParameters);
        await StopPdwRecording(false);

        try {
            await _pdwWriterLock.WaitAsync();
            _pdwStreamWriter = _pdwWriterFactory.Create(pdwRecordingParameters, stationId);
            if (_pdwStreamWriter != null) {
                _pdwStreamWriter.OnWritingStopped += OnPdwWritingStopped;
                await _pdwStreamWriter.Start();
                OnPDWRecordingStatusChanged(new PdwRecordingNotification(true, pdwParameters: pdwRecordingParameters));
            }
        } finally {
            _pdwWriterLock.Release();
        }
    }

    public async Task StopPdwRecording(bool sendNotification = true) {
        try {
            await _pdwWriterLock.WaitAsync();
            if (_pdwStreamWriter != null) {
                await _pdwStreamWriter.Stop();
                _pdwStreamWriter.OnWritingStopped -= OnPdwWritingStopped;
                _pdwStreamWriter = null;

                if (sendNotification) {
                    OnPDWRecordingStatusChanged(new PdwRecordingNotification(false));
                }
            }
        } finally {
            _pdwWriterLock.Release();
        }
    }

    public Task<NetSdrResponse<long>> GetFrequency() {
        return _controlClient.GetFrequency();
    }

    public async Task<NetSdrResponse<long>> SetFrequency(long frequency) {
        var result = await _controlClient.SetFrequency(frequency);

        if (result.IsSuccess) {
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification { Frequency = result.Value });
        }

        return result;
    }

    public async Task<NetSdrResponse<InstantViewBandwidth>> SetInstantViewBandwidth(
        InstantViewBandwidth instantViewBandwidth) {
        var getChannelFunctionResponse = await _controlClient.GetChannelFunctions();
        if (!getChannelFunctionResponse.IsSuccess) {
            return NetSdrResponse<InstantViewBandwidth>.CreateError(
                $"Failed get channel function {getChannelFunctionResponse.ErrorMessage}");
        }

        var frequencyRangeResponse = await _controlClient.GetFrequencyRange();
        if (frequencyRangeResponse is not { IsSuccess: true, Value: not null }) {
            return NetSdrResponse<InstantViewBandwidth>.CreateError(
                $"Failed get frequency range {frequencyRangeResponse.ErrorMessage}");
        }

        var bandwidth = (long)(instantViewBandwidth switch {
            InstantViewBandwidth.Bandwidth100MHz => UnitsNet.Frequency.FromMegahertz(100).Hertz,
            InstantViewBandwidth.Bandwidth400MHz => UnitsNet.Frequency.FromMegahertz(400).Hertz,
            _ => throw new ArgumentOutOfRangeException(nameof(instantViewBandwidth), "Not allowed bandwidth value")
        });

        var isElintModeEnabled = getChannelFunctionResponse.Value is ChannelFunction.None or ChannelFunction.Com;
        var isPanoramaEnabled = getChannelFunctionResponse.Value is ChannelFunction.PanoramaScan;
        var minFrequencyLimit = (long)UnitsNet.Frequency.FromMegahertz(frequencyRangeResponse.Value.FreqFrom).Hertz;
        var maxFrequencyLimit = (long)UnitsNet.Frequency.FromMegahertz(frequencyRangeResponse.Value.FreqTo).Hertz;

        ScanRange? correctedScanRange = null;
        long? correctedFrequency = null;
        ScanListItem[]? scanList = null;

        if (isElintModeEnabled) {
            var getFrequencyResponse = await _controlClient.GetFrequency();
            if (!getFrequencyResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed get frequency {getFrequencyResponse.ErrorMessage}");
            }

            correctedFrequency = Math.Clamp(getFrequencyResponse.Value, minFrequencyLimit, maxFrequencyLimit);

            if (correctedFrequency != getFrequencyResponse.Value) {
                var setFrequencyResponse = await _controlClient.SetFrequency(correctedFrequency.GetValueOrDefault());
                if (!setFrequencyResponse.IsSuccess) {
                    return NetSdrResponse<InstantViewBandwidth>.CreateError(
                        $"Failed set corrected frequency {setFrequencyResponse.ErrorMessage}");
                }

                correctedFrequency = setFrequencyResponse.Value;
                ReceiverParametersChanged?.Invoke(this,
                    new ReceiverParametersChangedNotification {
                        Frequency = (float)UnitsNet.Frequency.FromHertz(correctedFrequency.GetValueOrDefault())
                            .Megahertz
                    });
            }
        } else {
            if (getChannelFunctionResponse.Value is ChannelFunction.EsmScenario
                or ChannelFunction.EsmScenarioAndSpectrum) {
                var getScanListResponse = await _controlClient.GetScanList();
                if (!getScanListResponse.IsSuccess) {
                    return NetSdrResponse<InstantViewBandwidth>.CreateError(
                        $"Failed get scan list {getScanListResponse.ErrorMessage}");
                }

                scanList = getScanListResponse.Value;
            } else {
                var getScanRangeResponse = await _controlClient.GetScanRange();
                if (!getScanRangeResponse.IsSuccess || getScanRangeResponse.Value == null) {
                    return NetSdrResponse<InstantViewBandwidth>.CreateError(
                        $"Failed get scan range {getScanRangeResponse.ErrorMessage}");
                }

                var initialScanRangeFrequencyFrom =
                    (long)UnitsNet.Frequency.FromMegahertz(getScanRangeResponse.Value.FreqFrom).Hertz;
                var initialScanRangeFrequencyTo =
                    (long)UnitsNet.Frequency.FromMegahertz(getScanRangeResponse.Value.FreqTo).Hertz;
                var minFrequency = minFrequencyLimit - bandwidth / 2;
                var maxFrequency = maxFrequencyLimit + bandwidth / 2;
                var scanRangeFrequencyFrom = Math.Clamp(initialScanRangeFrequencyFrom, minFrequency, maxFrequency);
                var scanRangeFrequencyTo = Math.Clamp(initialScanRangeFrequencyTo, minFrequency, maxFrequency);

                if (scanRangeFrequencyTo - scanRangeFrequencyFrom < bandwidth) {
                    scanRangeFrequencyTo = scanRangeFrequencyFrom + bandwidth;
                    if (scanRangeFrequencyTo > maxFrequency) {
                        scanRangeFrequencyTo = maxFrequency;
                        scanRangeFrequencyFrom = scanRangeFrequencyTo - bandwidth;
                    }
                }

                correctedScanRange = new ScanRange {
                    FreqFrom = UnitsNet.Frequency.FromHertz(scanRangeFrequencyFrom).Megahertz,
                    FreqTo = UnitsNet.Frequency.FromHertz(scanRangeFrequencyTo).Megahertz
                };

                if (scanRangeFrequencyFrom != initialScanRangeFrequencyFrom ||
                    scanRangeFrequencyTo != initialScanRangeFrequencyTo) {
                    var setScanRangeResponse = await SetScanRange(correctedScanRange, isPanoramaEnabled);
                    if (!setScanRangeResponse.IsSuccess) {
                        return NetSdrResponse<InstantViewBandwidth>.CreateError(
                            $"Failed set corrected scan-range {setScanRangeResponse.ErrorMessage}");
                    }

                    ReceiverParametersChanged?.Invoke(this,
                        new ReceiverParametersChangedNotification { ScanRange = correctedScanRange });
                }
            }
        }

        await StopStreaming();

        NetSdrResponse<ChannelFunction>? setChannelFunctionResponse;
        if (getChannelFunctionResponse.Value != ChannelFunction.None) {
            setChannelFunctionResponse = await _controlClient.SetChannelFunctions(ChannelFunction.None);
            if (!setChannelFunctionResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed set channel functions to 'None' {setChannelFunctionResponse.ErrorMessage}");
            }
        }

        var result = await _controlClient.SetInstantViewBandwidth(instantViewBandwidth);
        if (result.IsSuccess) {
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification { InstantViewBandwidth = result.Value });
        }

        //set freq \ scan-range \ scan-list before change channel-functions
        if (!isElintModeEnabled && correctedScanRange != null) {
            var setScanRangeResponse = await SetScanRange(correctedScanRange, isPanoramaEnabled);
            if (!setScanRangeResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed set scan range {setScanRangeResponse.ErrorMessage}");
            }
        } else if (!isElintModeEnabled && scanList != null) {
            var setScanListResponse = await _controlClient.SetScanList(scanList);
            if (!setScanListResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed set scan list {setScanListResponse.ErrorMessage}");
            }

            var scanListLength = setScanListResponse.Value?.Length;
            var scenarioRange = GetScenarioScanRange(setScanListResponse);
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification {
                    ScanListLength = scanListLength ?? 0, ScenarioRange = scenarioRange
                });
        } else if (isElintModeEnabled && correctedFrequency != null) {
            var setFrequencyResponse = await _controlClient.SetFrequency(correctedFrequency.GetValueOrDefault());
            if (!setFrequencyResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed set corrected frequency {setFrequencyResponse.ErrorMessage}");
            }
        }

        if (getChannelFunctionResponse.Value != ChannelFunction.None) {
            setChannelFunctionResponse = await _controlClient.SetChannelFunctions(getChannelFunctionResponse.Value);
            if (!setChannelFunctionResponse.IsSuccess) {
                return NetSdrResponse<InstantViewBandwidth>.CreateError(
                    $"Failed set channel functions'{getChannelFunctionResponse.ErrorMessage}'");
            }
        }

        await StartStreaming();

        return result;
    }

    public async Task<NetSdrResponse<InstantViewBandwidth>> GetInstantViewBandwidth() {
        return await _controlClient.GetInstantViewBandwidth();
    }

    public Task<NetSdrResponse<ScanRange>> GetScanRange() {
        return _controlClient.GetScanRange();
    }

    public Task<NetSdrResponse<ChannelFunction>> GetChannelFunctions() {
        return _controlClient.GetChannelFunctions();
    }

    public async Task<NetSdrResponse<ScanRange>> SetScanRange(ScanRange scanRange, bool isPanoramaMode) {
        var range = await _controlClient.GetFrequencyRange();
        var currentBandwidth = await _controlClient.GetInstantViewBandwidth();
        if (!range.IsSuccess || !currentBandwidth.IsSuccess) {
            return new NetSdrResponse<ScanRange> {
                State = CommandState.Error, ErrorMessage = "Error getting range or bandwidth"
            };
        }

        await _controlClient.StopStreaming(CaptureMode.Continuous);
        await _controlClient.SetChannelFunctions(ChannelFunction.None);

        var halfBW = (currentBandwidth.Value == InstantViewBandwidth.Bandwidth100MHz ? 100 : 400) / 2;
        var min = range.Value?.FreqFrom - halfBW;
        var max = range.Value?.FreqTo + halfBW;
        var adjustedFrom = Math.Clamp(scanRange.FreqFrom, min ?? 0, max ?? 0);
        var adjustedTo = Math.Clamp(scanRange.FreqTo, min ?? 0, max ?? 0);
        var adjustedScanRange = new ScanRange { FreqFrom = adjustedFrom, FreqTo = adjustedTo };
        var result = await _controlClient.SetScanRange(adjustedScanRange);
        var channelFunction = isPanoramaMode ? ChannelFunction.PanoramaScan : ChannelFunction.ESM;
        await _controlClient.SetChannelFunctions(channelFunction);
        await _controlClient.StartStreaming(CaptureMode.Continuous);

        if (result.IsSuccess) {
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification { ScanRange = result.Value });
        }

        return result;
    }

    public virtual async Task StartStreaming(bool isPdwStreamControlRequired = true) {
        await _controlClient.SetSpectrumState(SpectrumType.All, true);
        if (isPdwStreamControlRequired) {
            await _controlClient.StartStreaming(CaptureMode.Continuous);
        }

        await _spectrumClient.Start();
        await _thresholdClient.Start();
        await _persistenceSpectrumClient.Start();
    }

    public virtual async Task StopStreaming() {
        await _spectrumClient.Stop();
        await _persistenceSpectrumClient.Stop();
        await _thresholdClient.Stop();

        await _controlClient.SetSpectrumState(SpectrumType.All, false);
        await _controlClient.StopStreaming(CaptureMode.Continuous);
    }

    public async Task<NetSdrResponse<SpectrumParameters>> SetSpectrumParams(SpectrumParameters spectrumParameters) {
        var response = await _controlClient.SetSpectrumParams(spectrumParameters);
        if (response.IsSuccess) {
            ReceiverParametersChanged?.Invoke(this,
                new ReceiverParametersChangedNotification { SpectrumParameters = response.Value });
        }

        return response;
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void OnIqWritingStopped(object? sender, EventArgs e) {
        await StopIQRecording();
    }

    private async void OnPdwWritingStopped(object? sender, EventArgs e) {
        await StopPdwRecording();
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    private void OnIQRecordingStatusChanged(IQRecordingNotification e) {
        IQRecordingStatusChanged?.Invoke(this, e);
    }

    private void OnPDWRecordingStatusChanged(PdwRecordingNotification e) {
        PdwRecordingStatusChanged?.Invoke(this, e);
    }

    private void OnGnssRecordingStatusChanged(GnssRecordingNotification e) {
        GnssRecordingStatusChanged?.Invoke(this, e);
    }

    public async ValueTask DisposeAsync() {
        _receiverModeSync.Dispose();
        _controlClient.ConnectionStatusChanged -= OnConnectionStatusChanged;
        await StopMonitoringDevice();
    }
}
