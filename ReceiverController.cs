using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Notifications.Interfaces;
using Infozahyst.RSAAS.Common.Tools;
using Infozahyst.RSAAS.Core.Tools;
using Infozahyst.RSAAS.Core.Transport.DataStreaming;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Frames;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Commands;
using Infozahyst.RSAAS.Core.Transport.MQTT.Exceptions;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;
using Infozahyst.RSAAS.Server.Attributes;
using Infozahyst.RSAAS.Server.Controllers.Interfaces;
using Infozahyst.RSAAS.Server.DAL;
using Infozahyst.RSAAS.Server.Exceptions;
using Infozahyst.RSAAS.Server.Receiver;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;
using Infozahyst.RSAAS.Server.Receiver.Pdw;
using Infozahyst.RSAAS.Server.Services.Interfaces;
using Infozahyst.RSAAS.Server.Services.StreamingServices.Interfaces;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using Polly.Retry;

namespace Infozahyst.RSAAS.Server.Controllers;
#pragma warning disable IDE0079 // Remove unnecessary suppression
public class ReceiverController : BaseController<IReceiverMessagingNotification>, IReceiverController
{
    private const string ControllerName = nameof(ReceiverController);
    private readonly SemaphoreSlim _sendToSlavesLock = new(1, 1);
    private readonly StreamingSettings _streamingSettings;
    private readonly IControlClient _controlClient;
    private readonly IDataAccessManagerFactory _dataAccessManagerFactory;
    private readonly IReceiverService _receiverService;
    private readonly ISpectrumRepeaterService _spectrumRepeaterService;
    private readonly IPdwClient _pdwClient;
    private readonly ILogger<ReceiverController> _logger;
    private readonly IMessageLogger _messageLogger;
    private readonly IndicatorAggregator _indicatorAggregator;
    private readonly IPAddress? _ipAddress10G;
    private readonly ReceiverSettings _receiverSettings;
    private readonly GeneralSettings _generalSettings;
    private bool _isConnected;

    public event EventHandler<ConnectionChangedNotification>? ReceiverConnectionChanged;
    public event EventHandler<IQRecordingNotification>? IQRecordingStatusChanged;
    public event EventHandler<PdwRecordingNotification>? PdwRecordingStatusChanged;
    public event EventHandler<GnssRecordingNotification>? GnssRecordingStatusChanged;
    public event EventHandler<ReceiverParametersChangedNotification>? ReceiverParametersChanged;
    public event EventHandler<DeviceInfoUpdateNotification>? DeviceInfoUpdated;

    public ReceiverController(IMqttClientFactory mqttFactory, IOptions<GeneralSettings> generalSettings,
        IOptions<ReceiverSettings> receiverOptions, IOptions<ServerSettings> serverSettings,
        IOptions<StreamingSettings> streamingSettings, IControlClient controlClient, IPdwClient pdwClient,
        ILogger<ReceiverController> logger, IMessageLogger messageLogger, IndicatorAggregator indicatorAggregator,
        IDataAccessManagerFactory dataAccessManagerFactory, IReceiverService receiverService,
        TraceListener traceListener, IReadOnlyPolicyRegistry<string> policyRegistry,
        ISpectrumRepeaterService spectrumRepeaterService)
        : base(logger, mqttFactory, serverSettings, traceListener, policyRegistry,
            generalSettings.Value.StationId, generalSettings.Value.ApplicationId) {
        _streamingSettings = streamingSettings.Value;
        _controlClient = controlClient;
        _pdwClient = pdwClient;
        _logger = logger;
        _messageLogger = messageLogger;
        _indicatorAggregator = indicatorAggregator;
        _receiverSettings = receiverOptions.Value;
        _dataAccessManagerFactory = dataAccessManagerFactory;
        _receiverService = receiverService;
        _spectrumRepeaterService = spectrumRepeaterService;
        _generalSettings = generalSettings.Value;
        _ = IPAddress.TryParse(_receiverSettings.DatastreamIpAddress, out _ipAddress10G);
        _controlClient.ConnectionStatusChanged += ControlClientOnConnectionStatusChanged;
        _controlClient.ConnectionStatusChanged += ControlClientConnectionChanged;
        _receiverService.IQRecordingStatusChanged += ReceiverServiceOnIQRecordingStatusChanged;
        _receiverService.PdwRecordingStatusChanged += ReceiverServiceOnPdwRecordingStatusChanged;
        _receiverService.ReceiverParametersChanged += ReceiverServiceOnReceiverParametersChanged;
        _receiverService.GnssRecordingStatusChanged += ReceiverServiceGnssRecordingStatusChanged;
        _receiverService.DeviceInfoUpdated += (s, e) => DeviceInfoUpdated?.Invoke(s, e);
        _receiverService.StartMonitoringDevice();
    }

    private void ReceiverServiceGnssRecordingStatusChanged(object? sender, GnssRecordingNotification e) {
        e.StationId = StationId;
        GnssRecordingStatusChanged?.Invoke(this, e);
    }

    private void ControlClientOnConnectionStatusChanged(object? sender, bool e) {
        _indicatorAggregator.UpdateReceiverConnectionStatus(e);
    }

    private void ReceiverServiceOnIQRecordingStatusChanged(object? sender, IQRecordingNotification e) {
        e.StationId = StationId;
        IQRecordingStatusChanged?.Invoke(this, e);
    }

    private void ReceiverServiceOnPdwRecordingStatusChanged(object? sender, PdwRecordingNotification e) {
        e.StationId = StationId;
        PdwRecordingStatusChanged?.Invoke(this, e);
    }

    private void ReceiverServiceOnReceiverParametersChanged(object? sender, ReceiverParametersChangedNotification e) {
        e.StationId = StationId;
        ReceiverParametersChanged?.Invoke(null, e);
    }

    private void ControlClientConnectionChanged(object? sender, bool isConnected) {
        _isConnected = isConnected;
        ReceiverConnectionChanged?.Invoke(this, new ConnectionChangedNotification(StationId, _isConnected));

        if (isConnected) {
            _ = Task.Run(async () => {
                var policy =
                    PolicyRegistry.Get<AsyncRetryPolicy<bool>>(PolicyNameConstants
                        .WaitAndRetryForeverByBooleanResultAsync);
                await policy.ExecuteAsync(async () => {
                    var result = await StartReceiver(CreateRequest<StartReceiverRequest>(StationId));
                    return result.IsSuccess;
                });
            });
        }
    }

    public virtual async Task<bool> Connect() {
        try {
            if (string.IsNullOrWhiteSpace(_receiverSettings.IpAddress)) {
                throw new Exception("'IpAddress' not setted in 'Receiver' settings");
            }

            var res = await _controlClient.Connect();
            _isConnected = res.Value;
            return res.Value;
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return false;
        }
    }

    protected override async Task<bool> OnInitialized() {
        // do not return result from this method or it will retry forever
        // server may work without receiver as linked station
        await Connect();
        return true;
    }

    public override Task Disconnect() {
        _controlClient.ConnectionStatusChanged -= ControlClientOnConnectionStatusChanged;
        _controlClient.ConnectionStatusChanged -= ControlClientConnectionChanged;
        _receiverService.IQRecordingStatusChanged -= ReceiverServiceOnIQRecordingStatusChanged;
        _receiverService.PdwRecordingStatusChanged -= ReceiverServiceOnPdwRecordingStatusChanged;
        _receiverService.ReceiverParametersChanged -= ReceiverServiceOnReceiverParametersChanged;
        return Task.CompletedTask;
    }

    protected override void SubscribeEvents(Func<IReceiverMessagingNotification> proxyFunc) {
        var proxy = proxyFunc();
        proxy.ReceiverConnectionChanged += (sender, data) => ReceiverConnectionChanged?.Invoke(sender, data);
        proxy.IQRecordingStatusChanged += (sender, data) => IQRecordingStatusChanged?.Invoke(sender, data);
        proxy.PdwRecordingStatusChanged += (sender, data) => PdwRecordingStatusChanged?.Invoke(sender, data);
        proxy.ReceiverParametersChanged += (sender, data) => ReceiverParametersChanged?.Invoke(sender, data);
    }

    private async Task<StartDataStreamingResponse> SendStartDataStreaming(uint stationId, int port,
        LinkedStation slaveStation, CancellationToken cancellationToken = default) {
        var sessionId = _spectrumRepeaterService.GetOrAddSourceSessionId(stationId.ToString());
        var slaveConnection = _spectrumRepeaterService.GetDataStreamingConnection();
        if (slaveConnection == null) {
            _logger.LogWarning("Cannot start data streaming for station {StationId}, listener not started", stationId);
            return new StartDataStreamingResponse {
                AppId = AppId,
                ClientId = ClientId,
                IsSuccess = false,
                StationId = stationId,
                ErrorMessage = $"Cannot start data streaming for station {stationId}, listener not started"
            };
        }

        var localIpAddress = _streamingSettings.RepeaterLocalIpAddress
                             ?? await SelfAddressHelper.GetLocalIpAddress(slaveStation.IpAddress);
        if (string.IsNullOrEmpty(localIpAddress)) {
            _logger.LogWarning("Cannot start data streaming, cannot get local IP address");
            return new StartDataStreamingResponse {
                AppId = AppId,
                ClientId = ClientId,
                IsSuccess = false,
                StationId = stationId,
                ErrorMessage = "Cannot start data streaming, cannot get local IP address"
            };
        }

        var startDataStreamingRequest = CreateRequest<StartDataStreamingRequest>(stationId);
        startDataStreamingRequest.SessionId = sessionId;
        startDataStreamingRequest.Mode = DataFrameType.Spectrogram;
        startDataStreamingRequest.Host = localIpAddress;
        startDataStreamingRequest.Port = port;
        startDataStreamingRequest.Protocol = slaveConnection.DataProtocol;
        startDataStreamingRequest.IsClient = false;

        var startDataStreamingResponses =
            await SendToSlavesAsync<StartDataStreamingRequest, StartDataStreamingResponse>(
                startDataStreamingRequest, nameof(DataStreamingController),
                nameof(DataStreamingController.StartDataStreaming), stationId: stationId,
                cancellationToken: cancellationToken);
        return startDataStreamingResponses.Single();
    }

    private async Task<StopDataStreamingResponse> SendStopDataStreaming(uint stationId,
        CancellationToken cancellationToken = default) {
        var sessionId = _spectrumRepeaterService.GetOrAddSourceSessionId(stationId.ToString());
        var stopDataStreamingRequest = CreateRequest<StopDataStreamingRequest>(stationId);
        stopDataStreamingRequest.SessionId = sessionId;
        stopDataStreamingRequest.Mode = DataFrameType.Spectrogram;

        var stopDataStreamingResponses =
            await SendToSlavesAsync<StopDataStreamingRequest, StopDataStreamingResponse>(
            stopDataStreamingRequest, nameof(DataStreamingController),
            nameof(DataStreamingController.StopDataStreaming), stationId: stationId,
            cancellationToken: cancellationToken);
        return stopDataStreamingResponses.Single();
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 2, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(StartStreaming)}"
    ])]
    public async Task<StartStreamingResponse> StartStreaming(StartStreamingRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<StartStreamingResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            await _receiverService.StartStreaming();

            await SendToSlavesAsync<StartStreamingRequest, StartStreamingResponse>(request, ControllerName);

            return CreateSuccessResponse<StartStreamingResponse>(request);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StartStreamingResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 2, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(StopStreaming)}"
    ])]
    public async Task<StopStreamingResponse> StopStreaming(StopStreamingRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<StopStreamingResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            await _receiverService.StopStreaming();

            await SendToSlavesAsync<StopStreamingRequest, StopStreamingResponse>(request, ControllerName);

            return CreateSuccessResponse<StopStreamingResponse>(request);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StopStreamingResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata]
    public Task<GetStreamRecordingStateResponse> GetStreamRecordingState(GetStreamRecordingStateRequest request) {
        try {
            var response = CreateSuccessResponse<GetStreamRecordingStateResponse>(request);
            response.IsGnssRecordingActive = _receiverService.IsGnssRecordingActive;
            response.IsPdwRecordingActive = _receiverService.IsPdwRecordingActive;
            response.IsIQRecordingActive = _receiverService.IsIQRecordingActive;
            return Task.FromResult(response);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return Task.FromResult(CreateFailedResponse<GetStreamRecordingStateResponse>(request, e.Message));
        }
    }


    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 9, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(StartStreamRecording)}"
    ])]
    public async Task<StartStreamRecordingResponse> StartStreamRecording(StartStreamRecordingRequest request) {
        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var linkedStationList = await dataAccessManager.LinkStationRepository.GetList();
            if (_isConnected) {
                switch (request.FrameType) {
                    case DataFrameType.IQ when request.IQParameters is not null:
                        var activeLinkedStations = linkedStationList
                            .Where(a => a is {
                                StationDataSource: StationDataSource.Network,
                                IsIQStreamActive: true
                            } && SlaveStations.ContainsKey(a.StationId))
                            .ToList();
                        await _receiverService.StartIQRecording(request.IQParameters, request.StationId,
                            activeLinkedStations);
                        break;
                    case DataFrameType.Pdw when request.PdwParameters is not null:
                        await _receiverService.StartPdwRecording(request.PdwParameters, request.StationId);
                        break;
                    case DataFrameType.Gnss:
                        await _receiverService.StartGnssRecording();
                        break;
                    default:
                        return CreateFailedResponse<StartStreamRecordingResponse>(request,
                            $"Not supported stream type: {request.FrameType}");
                }
            }

            var linkedStationWithoutStreaming = request.FrameType == DataFrameType.IQ
                        ? linkedStationList
                            .Where(a => a is {
                                IsActive: true,
                                StationDataSource: StationDataSource.Network,
                                IsIQStreamActive: true
                            })
                            .ToList()
                        : linkedStationList
                            .Where(a => a is {
                                IsActive: true,
                                StationDataSource: StationDataSource.Network
                            })
                            .ToList();
            await SendToSynchronizedSlaves<StartStreamRecordingRequest, StartStreamRecordingResponse>(
                request, ControllerName, linkedStationWithoutStreaming);

            var response = CreateSuccessResponse<StartStreamRecordingResponse>(request);
            response.FrameType = request.FrameType;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StartStreamRecordingResponse>(request, e.Message);
        } catch (OptionsValidationException e) {
            return CreateFailedResponse<StartStreamRecordingResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StartStreamRecordingResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 4, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(StopStreamRecording)}"
    ])]
    public async Task<StopStreamRecordingResponse> StopStreamRecording(StopStreamRecordingRequest request) {
        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaveStations = await dataAccessManager.LinkStationRepository.GetList();
            await SendToSynchronizedSlaves<StopStreamRecordingRequest, StopStreamRecordingResponse>(
                request, ControllerName, slaveStations);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StopStreamRecordingResponse>(request, e.Message);
        }

        if (!_isConnected) {
            return CreateFailedResponse<StopStreamRecordingResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            switch (request.FrameType) {
                case DataFrameType.IQ:
                    await _receiverService.StopIQRecording();
                    break;
                case DataFrameType.Pdw:
                    await _receiverService.StopPdwRecording();
                    break;
                case DataFrameType.Gnss:
                    await _receiverService.StopGnssRecording();
                    break;
                default:
                    return CreateFailedResponse<StopStreamRecordingResponse>(request,
                        $"Not supported stream type: {request.FrameType}");
            }

            var response = CreateSuccessResponse<StopStreamRecordingResponse>(request);
            response.FrameType = request.FrameType;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StopStreamRecordingResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StopStreamRecordingResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 14,
        RemoteCalls = [$"{nameof(ReceiverController)}.{nameof(SetupIQ)}"],
        AdditionalTimeouts = [$"{ReservedEndpointsTimeouts.ReceiverOverrideCommandTimeouts}.Get-InstantViewBandwidth-0x15"])]
    public async Task<SetupIQResponse> SetupIQ(SetupIQRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetupIQResponse>(request, "Not connected");
        }

        try {
            CheckCallerAppId(request);

            await _receiverService.SetupIQ(request.IQParameters);

            await SendToSlavesAsync<SetupIQRequest, SetupIQResponse>(request, ControllerName);

            return CreateSuccessResponse<SetupIQResponse>(request);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetupIQResponse>(request, e.Message);
        } catch (OptionsValidationException e) {
            return CreateFailedResponse<SetupIQResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetupIQResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(GetIQState)}"
    ])]
    public async Task<GetIQStateResponse> GetIQState(GetIQStateRequest request) {
        try {
            if (request.StationId != StationId) {
                var responses = await SendToSlavesAsync<GetIQStateRequest, GetIQStateResponse>(
                    request, ControllerName, stationId: request.StationId);
                return responses.Single();
            }

            if (!_isConnected) {
                return CreateFailedResponse<GetIQStateResponse>(request, "Not connected");
            }

            var result = await _receiverService.GetIQState();

            var response = CreateSuccessResponse<GetIQStateResponse>(request);
            response.IsRunning = result.GetValueOrDefault().StreamState == State.Run;
            response.WithTimestamp = result.GetValueOrDefault().Format == IQPacketFormat.WithTimeStamp;
            return response;
        } catch (OptionsValidationException e) {
            return CreateFailedResponse<GetIQStateResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetIQStateResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetFrequencyResponse> GetFrequency(GetFrequencyRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetFrequencyResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var result = await _receiverService.GetFrequency();

        var response = CreateResponse<GetFrequencyResponse>(request, result.IsSuccess, result.ErrorMessage);
        response.Frequency = result.Value;
        response.State = result.State;
        return response;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetFrequencyRangeResponse> GetFrequencyRange(GetFrequencyRangeRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetFrequencyRangeResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var result = await _controlClient.GetFrequencyRange();
        var response = CreateResponse<GetFrequencyRangeResponse>(request, result.IsSuccess, result.ErrorMessage);
        response.Range = result.Value;
        response.State = result.State;
        return response;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetFrequency)}"
    ])]
    public async Task<SetFrequencyResponse> SetFrequency(SetFrequencyRequest request) {
        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaveStations = await dataAccessManager.LinkStationRepository.GetList();
            await SendToSynchronizedSlaves<SetFrequencyRequest, SetFrequencyResponse>(
                request, ControllerName, slaveStations);

            if (!_isConnected) {
                return CreateFailedResponse<SetFrequencyResponse>(request,
                    "Receiver not connected", isTransient: _controlClient.IsConfigured);
            }

            var result = await _receiverService.SetFrequency((long)request.Frequency);

            var response = CreateResponse<SetFrequencyResponse>(request, result.IsSuccess, result.ErrorMessage);
            response.Frequency = result.Value;
            response.State = result.State;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetFrequencyResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetFrequencyResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetScanRangeResponse> GetScanRange(GetScanRangeRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetScanRangeResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var result = await _receiverService.GetScanRange();

        var response = CreateResponse<GetScanRangeResponse>(request, result.IsSuccess, result.ErrorMessage);
        response.State = result.State;
        response.ScanRange = result.Value;
        return response;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 5,
        RemoteCalls = [$"{nameof(ReceiverController)}.{nameof(SetScanRange)}"],
        AdditionalTimeouts = [$"{ReservedEndpointsTimeouts.ReceiverOverrideCommandTimeouts}.Get-InstantViewBandwidth-0x15"])]
    public async Task<SetScanRangeResponse> SetScanRange(SetScanRangeRequest request) {
        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaveStations = await dataAccessManager.LinkStationRepository.GetList();
            await SendToSynchronizedSlaves<SetScanRangeRequest, SetScanRangeResponse>(
                request, ControllerName, slaveStations);

            if (!_isConnected) {
                return CreateFailedResponse<SetScanRangeResponse>(request,
                    "Receiver not connected", isTransient: _controlClient.IsConfigured);
            }

            var res = await _receiverService.SetScanRange(request.Range, request.IsPanoramaMode);

            var response = CreateResponse<SetScanRangeResponse>(request, res.IsSuccess, res.ErrorMessage);
            response.State = res.State;
            response.ScanRange = res.Value;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetScanRangeResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetScanRangeResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ProxyModeOnRemoteCall = true, ReceiverInvokesCount = 1, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(GetDeviceInfo)}"
    ])]
    public async Task<GetDeviceInfoResponse> GetDeviceInfo(GetDeviceInfoRequest request) {
        if (request.StationId != StationId) {
            var responses = await SendToSlavesAsync<GetDeviceInfoRequest, GetDeviceInfoResponse>(
                request, ControllerName, stationId: request.StationId);
            return responses.Single();
        }

        if (!_isConnected) {
            return CreateFailedResponse<GetDeviceInfoResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var result = await _controlClient.GetDeviceInfo();
        var response = CreateResponse<GetDeviceInfoResponse>(request, result.IsSuccess, result.ErrorMessage);
        response.DeviceInfo = result.Value;
        return response;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetSpectrumParamsResponse> GetSpectrumParams(GetSpectrumParamsRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetSpectrumParamsResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var result = await _controlClient.GetSpectrumParams();

        var response = CreateResponse<GetSpectrumParamsResponse>(request, result.IsSuccess, result.ErrorMessage);
        response.SpectrumParameters = result.Value;
        response.State = result.State;
        return response;
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetSpectrumParams)}"
    ])]
    public async Task<SetSpectrumParamsResponse> SetSpectrumParams(SetSpectrumParamsRequest request) {
        try {
            if (!_isConnected) {
                return CreateFailedResponse<SetSpectrumParamsResponse>(request,
                    "Receiver not connected", isTransient: _controlClient.IsConfigured);
            }

            CheckCallerAppId(request);

            var result = await _receiverService.SetSpectrumParams(request.SpectrumParameters);

            await SendToSlavesAsync<SetSpectrumParamsRequest, SetSpectrumParamsResponse>(request, ControllerName);

            var response = CreateResponse<SetSpectrumParamsResponse>(request, result.IsSuccess, result.ErrorMessage);
            response.SpectrumParameters = result.Value;
            response.State = result.State;
            return response;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetSpectrumParamsResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetSpectrumParamsResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetSpectrumParamsResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ProxyModeOnRemoteCall = true, ReceiverInvokesCount = 1)]
    public async Task<SetAntennaAzimuthResponse> SetAntennaAzimuth(SetAntennaAzimuthRequest request) {
        try {
            CheckCallerAppId(request);

            if (request.StationId != StationId) {
                var responses = await SendToSlavesAsync<SetAntennaAzimuthRequest, SetAntennaAzimuthResponse>(
                    request, ControllerName, stationId: request.StationId);
                return responses.Single();
            }

            if (!_isConnected) {
                return CreateFailedResponse<SetAntennaAzimuthResponse>(request,
                    "Receiver not connected", isTransient: _controlClient.IsConfigured);
            }

            var res = await _controlClient.SetAntennaAzimuth(request.Azimuth);
            if (res.IsSuccess) {
                var response = CreateSuccessResponse<SetAntennaAzimuthResponse>(request);
                response.IsSuccess = res.IsSuccess;
                response.ErrorMessage = res.ErrorMessage;
                response.Azimuth = res.Value;
                return response;
            }

            var failedResponse = CreateFailedResponse<SetAntennaAzimuthResponse>(request, res.ErrorMessage);
            failedResponse.Azimuth = 0;
            return failedResponse;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetAntennaAzimuthResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetAntennaAzimuthResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetAntennaAzimuthResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ProxyModeOnRemoteCall = true, ReceiverInvokesCount = 1)]
    public async Task<GetAntennaAzimuthResponse> GetAntennaAzimuth(GetAntennaAzimuthRequest request) {
        if (request.StationId != StationId) {
            var responses =
                await SendToSlavesAsync<GetAntennaAzimuthRequest, GetAntennaAzimuthResponse>(
                request, ControllerName, stationId: request.StationId);
            return responses.Single();
        }

        if (!_isConnected) {
            return CreateFailedResponse<GetAntennaAzimuthResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var res = await _controlClient.GetAntennaAzimuth();
        if (res.IsSuccess) {
            var response = CreateSuccessResponse<GetAntennaAzimuthResponse>(request);
            response.Azimuth = res.Value;
            return response;
        }

        return CreateFailedResponse<GetAntennaAzimuthResponse>(request);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ProxyModeOnRemoteCall = true, ReceiverInvokesCount = 1)]
    public async Task<SetBacklightStateResponse> SetBacklightState(SetBacklightStateRequest request) {
        try {
            CheckCallerAppId(request);

            if (request.StationId != StationId) {
                var responses =
                    await SendToSlavesAsync<SetBacklightStateRequest, SetBacklightStateResponse>(
                        request, ControllerName, stationId: request.StationId);
                return responses.Single();
            }

            if (!_isConnected) {
                return CreateFailedResponse<SetBacklightStateResponse>(request,
                    "Receiver not connected", isTransient: _controlClient.IsConfigured);
            }

            var res = await _controlClient.SetBacklightState(request.State);

            if (res.IsSuccess) {
                var response = CreateResponse<SetBacklightStateResponse>(request, res.IsSuccess, res.ErrorMessage);
                response.State = res.Value;
                return response;
            }

            var failedResponse = CreateFailedResponse<SetBacklightStateResponse>(request, res.ErrorMessage);
            failedResponse.State = null;
            return failedResponse;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetBacklightStateResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetBacklightStateResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetBacklightStateResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ProxyModeOnRemoteCall = true, ReceiverInvokesCount = 1)]
    public async Task<GetBacklightStateResponse> GetBacklightState(GetBacklightStateRequest request) {
        if (request.StationId != StationId) {
            var responses = await SendToSlavesAsync<GetBacklightStateRequest, GetBacklightStateResponse>(
                request, ControllerName, stationId: request.StationId);
            return responses.Single();
        }

        if (!_isConnected) {
            return CreateFailedResponse<GetBacklightStateResponse>(request, "Receiver not connected",
                isTransient: _controlClient.IsConfigured);
        }

        try {
            var res = await _controlClient.GetBacklightState();
            if (res.IsSuccess) {
                var response = CreateSuccessResponse<GetBacklightStateResponse>(request);
                response.State = res.Value;
                return response;
            }

            return CreateFailedResponse<GetBacklightStateResponse>(request, res.ErrorMessage);
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<GetBacklightStateResponse>(request, e.Message, isTransient: false);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetBacklightStateResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 2)]
    public async Task<GetReceiverModeResponse> GetReceiverMode(GetReceiverModeRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetReceiverModeResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            var getReceiverMode = await _receiverService.GetReceiverMode();
            if (getReceiverMode == null) {
                return CreateFailedResponse<GetReceiverModeResponse>(request, "Error at getting receiver mode");
            }

            var scenarioDetails = getReceiverMode.Value.ReceiverMode == ReceiverMode.EsmScenario
                ? await _receiverService.GetScenarioRange()
                : null;

            var response = CreateSuccessResponse<GetReceiverModeResponse>(request);
            response.Mode = getReceiverMode.Value.ReceiverMode;
            response.IsPanoramaMode = getReceiverMode.Value.IsPanoramaMode;
            response.ScenarioId = getReceiverMode.Value.ScenarioId;
            response.ScanListLength = scenarioDetails?.ScanListLength;
            response.ScenarioRange = scenarioDetails?.ScenarioRange;
            return response;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<GetReceiverModeResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<GetReceiverModeResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetReceiverModeResponse>(request, e.Message);
        }
    }

    /// <summary>
    /// In ESMScenario mode if scenarioId \ scenario is null then we stop scanning 
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 2, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetReceiverMode)}",
        $"{nameof(ReceiverController)}.{nameof(SetFrequency)}",
        $"{nameof(DataStreamingController)}.{nameof(DataStreamingController.StartDataStreaming)}"
    ])]
    public async Task<SetReceiverModeResponse> SetReceiverMode(SetReceiverModeRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetReceiverModeResponse>(request, "Receiver not connected");
        }

        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaveStations = await dataAccessManager.LinkStationRepository.GetList();
            Scenario? scenario = null;
            if (request.ScenarioId.HasValue) {
                scenario = await dataAccessManager.ScenarioRepository
                    .GetEntity(a => a.Id == request.ScenarioId,
                        $"{nameof(Scenario.ScenarioDetails)}.{nameof(ScenarioDetail.RadarProfile)}." +
                        $"{nameof(RadarProfile.FrequencyBehaviors)}");
            } else if (request.Scenario != null) {
                scenario = request.Scenario?.ToEntity();
            }

            var setReceiverModeResponse = await _receiverService.SetReceiverMode(request.Mode,
                scenario, request.IsPanoramaSpectrumEnabled);
            if (setReceiverModeResponse == null) {
                return CreateFailedResponse<SetReceiverModeResponse>(request,
                    "Error at setting receiver mode", isTransient: _controlClient.IsConfigured);
            }

            if (!setReceiverModeResponse.IsSuccess) {
                return CreateFailedResponse<SetReceiverModeResponse>(request, setReceiverModeResponse.ErrorMessage);
            }

            var mode = setReceiverModeResponse.Value switch {
                ChannelFunction.None => ReceiverMode.Elint,
                ChannelFunction.Com => ReceiverMode.Comint,
                ChannelFunction.EsmScenario => ReceiverMode.EsmScenario,
                ChannelFunction.EsmScenarioAndSpectrum => ReceiverMode.EsmScenario,
                _ => ReceiverMode.Esm
            };

            request.ScenarioId = null;
            request.Scenario = scenario?.ToDto();
            var responses =
                await SendToSynchronizedSlaves<SetReceiverModeRequest, SetReceiverModeResponse>(
                    request, ControllerName, slaveStations);

            if (responses.Values.Any(a => a.IsSuccess)) {
                var mappings = responses
                    .Where(a => a.Value.IsSuccess)
                    .Where(a => _spectrumRepeaterService
                        .GetSourceSessionId(a.Key.ToString()).HasValue)
                    .Select(a => {
                        var clientId = _spectrumRepeaterService.GetSourceSessionId(a.Key.ToString());
                        return (a.Key, clientId!.Value);
                    }).ToDictionary(a => a.Key, b => b.Value);

                if (mappings.Count != responses.Values.Count) {
                    var diff = responses.Keys.Except(mappings.Keys).ToArray();
                    if (diff.Length != 0) {
                        _logger.LogWarning("Cannot get clientId for stations {StationId}", string.Join(',', diff));
                    }
                }

                if (request.Mode is ReceiverMode.Elint or ReceiverMode.Comint) {
                    var freq = await _receiverService.GetFrequency();
                    if (freq.IsSuccess) {
                        var setFrequencyRequest = CreateRequest<SetFrequencyRequest>(request.StationId);
                        setFrequencyRequest.Frequency = freq.Value;
                        await SendToSynchronizedSlaves<SetFrequencyRequest, SetFrequencyResponse>(
                            setFrequencyRequest, ControllerName, slaveStations, nameof(SetFrequency));
                    }

                    foreach (var mapping in mappings) {
                        var slaveStation = slaveStations.FirstOrDefault(a => a.StationId == mapping.Key);
                        if (request.Mode == ReceiverMode.Elint &&
                            slaveStation is { IsSpectrumStreamActive: true }) {
                            var slavesDataStreamingConnection = _spectrumRepeaterService.GetDataStreamingConnection();
                            if (slavesDataStreamingConnection == null) {
                                _logger.LogWarning("Cannot get listen port for data streaming");
                                continue;
                            }

                            var startDataStreamingResponse = await SendStartDataStreaming(mapping.Key,
                                slavesDataStreamingConnection.Port, slaveStation);
                            if (!startDataStreamingResponse.IsSuccess) {
                                _logger.LogWarning("Cannot start data streaming for station {StationId} reason {Reason}",
                                    mapping.Key, startDataStreamingResponse.ErrorMessage);
                            }
                        } else {
                            if (slaveStation is { IsSpectrumStreamActive: true }) {
                                var stopDataStreaming = await SendStopDataStreaming(mapping.Key);
                                if (!stopDataStreaming.IsSuccess) {
                                    _logger.LogWarning("Cannot stop data streaming for station {StationId} reason {Reason}",
                                        mapping.Key, stopDataStreaming.ErrorMessage);
                                }
                            }
                        }
                    }
                } else if (request.Mode == ReceiverMode.Esm) {
                    var rangeResponse = await _receiverService.GetScanRange();
                    if (rangeResponse.IsSuccess) {
                        var setScanRangeRequest = CreateRequest<SetScanRangeRequest>(request.StationId);
                        setScanRangeRequest.Range = rangeResponse.Value!;
                        setScanRangeRequest.IsPanoramaMode = request.IsPanoramaSpectrumEnabled;
                        await SendToSynchronizedSlaves<SetScanRangeRequest, SetScanRangeResponse>(
                            setScanRangeRequest, ControllerName, slaveStations, nameof(SetScanRange));
                    }

                    foreach (var mapping in mappings) {
                        var slaveStation = slaveStations.FirstOrDefault(a => a.StationId == mapping.Key);
                        if (slaveStation is { IsSpectrumStreamActive: true }) {
                            var stopDataStreaming = await SendStopDataStreaming(mapping.Key);
                            if (!stopDataStreaming.IsSuccess) {
                                _logger.LogWarning("Cannot stop data streaming for station {StationId} reason {Reason}",
                                    mapping.Key, stopDataStreaming.ErrorMessage);
                            }
                        }
                    }
                } else if (request.Mode == ReceiverMode.EsmScenario) {
                    foreach (var mapping in mappings) {
                        var slaveStation = slaveStations.FirstOrDefault(a => a.StationId == mapping.Key);
                        if (slaveStation is { IsSpectrumStreamActive: true }) {
                            var stopDataStreaming = await SendStopDataStreaming(mapping.Key);
                            if (!stopDataStreaming.IsSuccess) {
                                _logger.LogWarning("Cannot stop data streaming for station {StationId} reason {Reason}",
                                    mapping.Key, stopDataStreaming.ErrorMessage);
                            }
                        }
                    }
                }
            }

            var scenarioDetails = mode == ReceiverMode.EsmScenario
                ? await _receiverService.GetScenarioRange()
                : null;

            var response = CreateSuccessResponse<SetReceiverModeResponse>(request);
            response.Mode = mode;
            response.State = setReceiverModeResponse.State;
            response.ScanListLength = scenarioDetails?.ScanListLength;
            response.ScenarioRange = scenarioDetails?.ScenarioRange;
            return response;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetReceiverModeResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetReceiverModeResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetReceiverModeResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetSuppressedBandsResponse> GetSuppressedBands(GetSuppressedBandsRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetSuppressedBandsResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        var res = await _controlClient.GetSuppressedBands();
        if (res.IsSuccess) {
            var value = res.Value!.Select(x => new SuppressedBand(
                UnitsNet.Frequency.FromHertz(x.FrequencyStart.Value).Megahertz,
                UnitsNet.Frequency.FromHertz(x.FrequencyStop.Value).Megahertz)).ToArray();
            var response = CreateSuccessResponse<GetSuppressedBandsResponse>(request);
            response.SuppressedBands = value;
            return response;
        }

        return CreateFailedResponse<GetSuppressedBandsResponse>(request, res.ErrorMessage);
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 6, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetFrequency)}",
        $"{nameof(ReceiverController)}.{nameof(SetSuppressedBands)}"
    ])]
    public virtual async Task<SetSuppressedBandsResponse> SetSuppressedBands(SetSuppressedBandsRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetSuppressedBandsResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            var bands = request.Bands;
            await _controlClient.StopStreaming(CaptureMode.Continuous);

            var requestData = bands.Select(x => new DeviceSuppressedBand(
                (long)UnitsNet.Frequency.FromMegahertz(x.FrequencyStart).Hertz,
                (long)UnitsNet.Frequency.FromMegahertz(x.FrequencyStop).Hertz)).ToArray();
            var res = await _controlClient.SetSuppressedBands(requestData);


            if (!res.IsSuccess) {
                return CreateFailedResponse<SetSuppressedBandsResponse>(request, res.ErrorMessage);
            }

            var value = res.Value!.Select(x => new SuppressedBand(
                UnitsNet.Frequency.FromHertz(x.FrequencyStart.Value).Megahertz,
                UnitsNet.Frequency.FromHertz(x.FrequencyStop.Value).Megahertz)).ToArray();

            var getChannelFunctionsRes = await _controlClient.GetChannelFunctions();
            if (!getChannelFunctionsRes.IsSuccess) {
                return CreateFailedResponse<SetSuppressedBandsResponse>(request, getChannelFunctionsRes.ErrorMessage);
            }

            var channelFunctions = getChannelFunctionsRes.Value;
            if (channelFunctions is ChannelFunction.None or ChannelFunction.Com) {
                var getFrequencyRes = await _controlClient.GetFrequency();
                if (getFrequencyRes.IsSuccess) {
                    var setFrequencyRequest = CreateRequest<SetFrequencyRequest>(StationId);
                    setFrequencyRequest.Frequency = getFrequencyRes.Value;
                    await SetFrequency(setFrequencyRequest);
                } else {
                    return CreateFailedResponse<SetSuppressedBandsResponse>(request, getFrequencyRes.ErrorMessage);
                }
            } else {
                var getScanRangeRes = await _controlClient.GetScanRange();
                if (getScanRangeRes is { IsSuccess: true, Value: not null }) {
                    var setScannRequest = CreateRequest<SetScanRangeRequest>(StationId);
                    setScannRequest.Range = getScanRangeRes.Value;
                    setScannRequest.IsPanoramaMode = channelFunctions.HasFlag(ChannelFunction.PanoramaScan);
                    await SetScanRange(setScannRequest);
                } else {
                    return CreateFailedResponse<SetSuppressedBandsResponse>(request, getScanRangeRes.ErrorMessage);
                }
            }

            await _controlClient.StartStreaming(CaptureMode.Continuous);
            await SendToSlavesAsync<SetSuppressedBandsRequest, SetSuppressedBandsResponse>(request, ControllerName);

            var response = CreateSuccessResponse<SetSuppressedBandsResponse>(request);
            response.SuppressedBands = value;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetSuppressedBandsResponse>(request, e.Message);
        } catch (ConnectionException connectionException) {
            _logger.LogError(connectionException.Message);
            return CreateFailedResponse<SetSuppressedBandsResponse>(request, connectionException.Message);
        } catch (Exceptions.TimeoutException timeoutException) {
            _logger.LogError(timeoutException.Message);
            return CreateFailedResponse<SetSuppressedBandsResponse>(request, timeoutException.Message);
        } catch (CommandProcessException commandProcessException) {
            _logger.LogError(commandProcessException.Message);
            return CreateFailedResponse<SetSuppressedBandsResponse>(request, commandProcessException.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetSuppressedBandsResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 4, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetPanoramaMode)}"
    ])]
    public async Task<SetPanoramaModeResponse> SetPanoramaMode(SetPanoramaModeRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetPanoramaModeResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            var res = await _receiverService.SetPanoramaMode(
                request.IsPanoramaEnabled, request.Scenario);
            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaves = await dataAccessManager.LinkStationRepository.GetList();
            await SendToSynchronizedSlaves<SetPanoramaModeRequest, SetPanoramaModeResponse>(
                request, ControllerName, slaves);

            return CreateResponse<SetPanoramaModeResponse>(request, res.IsSuccess, res.ErrorMessage);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetPanoramaModeResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetPanoramaModeResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 23, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(SetFrequency)}",
        $"{nameof(ReceiverController)}.{nameof(SetSuppressedBands)}",
        $"{nameof(ReceiverController)}.{nameof(StartReceiver)}"
    ])]
    public virtual async Task<StartReceiverResponse> StartReceiver(StartReceiverRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<StartReceiverResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            using var dataAccessManager = _dataAccessManagerFactory.Create();
            await _pdwClient.Start();
            await _receiverService.StopStreaming();
            await SetPayloadSizes();

            if (_ipAddress10G != null) {
                await _controlClient.SetStreamingIpAddress(_ipAddress10G);
            }

            await _receiverService.RestoreReceiverMode();
            await _receiverService.StartStreaming(false);
            await _receiverService.CheckIQRecordingStatus();

            var suppressedBands = await dataAccessManager.SuppressedBandRepository.GetList();
            var setBandsRes =
                await SetSuppressedBands(new SetSuppressedBandsRequest { Bands = suppressedBands.ToArray(), AppId = AppId });
            if (!setBandsRes.IsSuccess) {
                return CreateFailedResponse<StartReceiverResponse>(request, setBandsRes.ErrorMessage);
            }

            await SendToSlavesAsync<StartReceiverRequest, StartReceiverResponse>(request, ControllerName);

            return CreateSuccessResponse<StartReceiverResponse>(request);
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StartReceiverResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StartReceiverResponse>(request, e.Message);
        } catch (Exceptions.TimeoutException ex) {
            _logger.LogError(ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
            return CreateFailedResponse<StartReceiverResponse>(request, ex.Message);
        } catch (Exception ex) {
            _logger.LogError(ex, "");
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
            return CreateFailedResponse<StartReceiverResponse>(request, ex.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 3, RemoteCalls = [
        $"{nameof(ReceiverController)}.{nameof(StopStreaming)}",
        $"{nameof(ReceiverController)}.{nameof(StopReceiver)}"
    ])]
    public async Task<StopReceiverResponse> StopReceiver(StopReceiverRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<StopReceiverResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);
            await StopStreaming(CreateRequest<StopStreamingRequest>(request.StationId));

            await _pdwClient.Stop();
            await _controlClient.StopStreaming(CaptureMode.Continuous);
            await _controlClient.Disconnect();

            _isConnected = false;
            await SendToSlavesAsync<StopReceiverRequest, StopReceiverResponse>(request, ControllerName);
            return CreateSuccessResponse<StopReceiverResponse>(request);
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StopReceiverResponse>(request, e.Message, isTransient: false);
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<StopReceiverResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<StopReceiverResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1,
        AdditionalTimeouts = [$"{ReservedEndpointsTimeouts.ReceiverOverrideCommandTimeouts}.Get-InstantViewBandwidth-0x15"])]
    public async Task<SetInstantViewBandwidthResponse> SetInstantViewBandwidth(SetInstantViewBandwidthRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetInstantViewBandwidthResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            var res = await _receiverService.SetInstantViewBandwidth(request.Bandwidth);
            using var dataAccessManager = _dataAccessManagerFactory.Create();
            var slaves = await dataAccessManager.LinkStationRepository.GetList();
            await SendToSynchronizedSlaves<SetInstantViewBandwidthRequest, SetInstantViewBandwidthResponse>(
                request, ControllerName, slaves);

            var response = CreateSuccessResponse<SetInstantViewBandwidthResponse>(request);
            response.Bandwidth = res.Value;
            response.State = res.State;
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetInstantViewBandwidthResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetInstantViewBandwidthResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(
        AdditionalTimeouts = [$"{ReservedEndpointsTimeouts.ReceiverOverrideCommandTimeouts}.Get-InstantViewBandwidth-0x15"])]
    public async Task<GetInstantViewBandwidthResponse> GetInstantViewBandwidth(GetInstantViewBandwidthRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetInstantViewBandwidthResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            var res = await _receiverService.GetInstantViewBandwidth();
            var response = CreateSuccessResponse<GetInstantViewBandwidthResponse>(request);
            response.Bandwidth = res.Value;
            response.State = res.State;
            return response;
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetInstantViewBandwidthResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<SetAgcResponse> SetAgc(SetAgcRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetAgcResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            var res = await _controlClient.SetAgc(request.Parameters);
            var response = CreateSuccessResponse<SetAgcResponse>(request);
            response.Parameters = res.Value;
            var notification = new ReceiverParametersChangedNotification {
                StationId = StationId, AgcParameters = response.Parameters
            };
            ReceiverParametersChanged?.Invoke(null, notification);

            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetAgcResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetAgcResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetAgcResponse> GetAgc(GetAgcRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetAgcResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            var res = await _controlClient.GetAgc();
            var response = CreateSuccessResponse<GetAgcResponse>(request);
            response.Parameters = res.Value;
            return response;
        } catch (NotImplementedException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<GetAgcResponse>(request, e.Message, isTransient: false);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetAgcResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<SetAttenuatorsResponse> SetAttenuators(SetAttenuatorsRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<SetAttenuatorsResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            CheckCallerAppId(request);

            var res =
                await _controlClient.SetAttenuators(request.RfAttenuator, request.IfAttenuator);
            var response = CreateSuccessResponse<SetAttenuatorsResponse>(request);
            response.RfAttenuator = res.Value.RfAttenuator;
            response.IfAttenuator = res.Value.IfAttenuator;
            var notification = new ReceiverParametersChangedNotification {
                StationId = StationId, RfAttenuator = res.Value.RfAttenuator, IfAttenuator = res.Value.IfAttenuator
            };
            ReceiverParametersChanged?.Invoke(null, notification);
            return response;
        } catch (ClientNotControllingException e) {
            _logger.LogWarning(message: e.Message);
            return CreateFailedResponse<SetAttenuatorsResponse>(request, e.Message);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<SetAttenuatorsResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata(ReceiverInvokesCount = 1)]
    public async Task<GetAttenuatorsResponse> GetAttenuators(GetAttenuatorsRequest request) {
        if (!_isConnected) {
            return CreateFailedResponse<GetAttenuatorsResponse>(request,
                "Receiver not connected", isTransient: _controlClient.IsConfigured);
        }

        try {
            var res = await _controlClient.GetAttenuators();
            var response = CreateSuccessResponse<GetAttenuatorsResponse>(request);
            response.RfAttenuator = res.Value.RfAttenuator;
            response.IfAttenuator = res.Value.IfAttenuator;
            return response;
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return CreateFailedResponse<GetAttenuatorsResponse>(request, e.Message);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [EndpointTimeoutMetadata]
    public Task<GetStationTrajectoryResponse> GetStationTrajectory(GetStationTrajectoryRequest request) {
        try {
            var response = CreateSuccessResponse<GetStationTrajectoryResponse>(request);
            response.TrajectoryBuffer = _receiverService.GetDeviceTrajectory();
            return Task.FromResult(response);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            return Task.FromResult(CreateFailedResponse<GetStationTrajectoryResponse>(request, e.Message));
        }
    }

    private async Task SetPayloadSizes() {
        var results = _generalSettings.StationType == StationType.GekataGA
            ? await SetGekataGAPayloadSizes()
            : await SetDefaultPayloadSizes();

        if (results.Any(r => !r.IsSet)) {
            var failedItems = results.Where(r => !r.IsSet).Select(r => r.Name);
            var message = $"{string.Join(",", failedItems)} payload size set incorrectly";

            _logger.LogWarning("{Message}", message);
            _messageLogger.AddMessage(MessageCategory.System, message, MessageLevel.Warn);
        }
    }

    private async Task<List<(bool IsSet, string Name)>> SetGekataGAPayloadSizes() {
        return [
            (await SetPayloadSize(_receiverSettings.PdwPayloadSize, (byte)MinervaGADataItemType.PDW), "PDW"),
            (await SetPayloadSize(_receiverSettings.IQPayloadSize, (byte)MinervaGADataItemType.DDC0_IQ), "IQ"),
            (await SetPayloadSize(_receiverSettings.IQPayloadSize, (byte)MinervaGADataItemType.DDC1_IQ), "IQ1"),
            (await SetPayloadSize(_receiverSettings.SpectrumPayloadSize, (byte)MinervaGADataItemType.Spectrum),
                "Spectrum"),

            (await SetPayloadSize(_receiverSettings.PersistencePayloadSize, (byte)MinervaGADataItemType.Persistence),
                "Persistence"),

            (await SetPayloadSize(_receiverSettings.ThresholdPayloadSize, (byte)MinervaGADataItemType.Thresholds),
                "Threshold")
        ];
    }

    private async Task<List<(bool IsSet, string Name)>> SetDefaultPayloadSizes() {
        return [
            (await SetPayloadSize(_receiverSettings.PdwPayloadSize, (byte)DataItemType.Pdw), "PDW"),
            (await SetPayloadSize(_receiverSettings.IQPayloadSize, (byte)DataItemType.IQ), "IQ"),
            (await SetPayloadSize(_receiverSettings.SpectrumPayloadSize, (byte)DataItemType.Spectrum), "Spectrum"),
            (await SetPayloadSize(_receiverSettings.PersistencePayloadSize, (byte)DataItemType.Persistence),
                "Persistence"),

            (await SetPayloadSize(_receiverSettings.ThresholdPayloadSize, (byte)DataItemType.DetectorThresholdsData),
                "Threshold")
        ];
    }

    private async Task<bool> SetPayloadSize(ushort payloadSize, byte dataItem) {
        var setResult = await _controlClient.SetPayloadSize(payloadSize, dataItem);
        return setResult.IsSuccess && setResult.Value == payloadSize;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private async Task<Dictionary<uint, TResponse>> SendToSynchronizedSlaves<TRequest, TResponse>(
        TRequest request, string controllerName, IReadOnlyList<LinkedStation> slaves,
        [CallerMemberName] string methodName = "")
        where TRequest : BaseRequest where TResponse : BaseResponse, new() {
        await _sendToSlavesLock.WaitAsync();
        try {
            var result = new Dictionary<uint, TResponse>();
            foreach (var slave in slaves) {
                if (!slave.IsSynchronized || !slave.IsActive) {
                    continue;
                }

                var responses = await SendToSlavesAsync<TRequest, TResponse>(
                    request, controllerName, methodName, stationId: slave.StationId);
                var response = responses.Single();
                response.StationId = slave.StationId;
                result.Add(slave.StationId, response);
            }

            return result;
        } finally {
            _sendToSlavesLock.Release();
        }
    }
}
#pragma warning restore IDE0079 // Remove unnecessary suppression
