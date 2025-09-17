using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Notifications.Interfaces;
using Infozahyst.RSAAS.Common.Transport;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Frames;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace Infozahyst.RSAAS.Client.Services.MessagingServices;

public class ReceiverMessagingService : BaseMessagingService<IReceiverMessagingNotification>, IReceiverMessagingService
{
    public event EventHandler<ConnectionChangedNotification>? ReceiverConnectionChanged;
    public event EventHandler<ReceiverParametersChangedNotification>? ReceiverParametersChanged;
    public event EventHandler<DeviceInfoUpdateNotification>? DeviceInfoUpdated;
    public event EventHandler<IQRecordingNotification>? IQRecordingStatusChanged;
    public event EventHandler<GnssRecordingNotification>? GnssRecordingStatusChanged;
    public event EventHandler<PdwRecordingNotification>? PdwRecordingStatusChanged;

    public ReceiverMessagingService(IMqttClientFactory mqttFactory, IOptions<MqttSettings> mqttSettings,
        IOptions<ServerSettings> serverSettings, IOptions<GeneralSettings> generalSettings,
        ILogger<ReceiverMessagingService> logger, IReadOnlyPolicyRegistry<string> policyRegistry,
        TraceListener traceListener)
        : base(mqttFactory, serverSettings, logger, traceListener, policyRegistry, nameof(ReceiverMessagingService),
            mqttSettings.Value.ReceiverRequestTopic, mqttSettings.Value.ReceiverResponseTopic,
            mqttSettings.Value.ReceiverNotificationTopic, generalSettings.Value.ApplicationId) {
    }

    protected override void SubscribeEvents(Func<IReceiverMessagingNotification> proxyFunc) {
        var proxy = proxyFunc();
        proxy.ReceiverConnectionChanged += (sender, data) => ReceiverConnectionChanged?.Invoke(sender, data);
        proxy.IQRecordingStatusChanged += (sender, data) => IQRecordingStatusChanged?.Invoke(sender, data);
        proxy.PdwRecordingStatusChanged += (sender, data) => PdwRecordingStatusChanged?.Invoke(sender, data);
        proxy.ReceiverParametersChanged += (sender, data) => ReceiverParametersChanged?.Invoke(sender, data);
        proxy.GnssRecordingStatusChanged += (sender, data) => GnssRecordingStatusChanged?.Invoke(sender, data);
        proxy.DeviceInfoUpdated += (sender, data) => DeviceInfoUpdated?.Invoke(sender, data);
    }

    public async Task<GetAntennaAzimuthResponse> GetAntennaAzimuth(uint? stationId = null) {
        var command = new GetAntennaAzimuthRequest();
        var response = await SendAsync<GetAntennaAzimuthRequest, GetAntennaAzimuthResponse>(command, stationId);
        return response;
    }

    public async Task<SetAntennaAzimuthResponse> SetAntennaAzimuth(float azimuth, uint? stationId = null) {
        var command = new SetAntennaAzimuthRequest { Azimuth = azimuth };
        var response = await SendAsync<SetAntennaAzimuthRequest, SetAntennaAzimuthResponse>(command, stationId);
        return response;
    }

    public async Task<GetBacklightStateResponse> GetBacklightState(uint? stationId = null) {
        var command = new GetBacklightStateRequest();
        var response = await SendAsync<GetBacklightStateRequest, GetBacklightStateResponse>(command, stationId);
        return response;
    }

    public async Task<SetBacklightStateResponse> SetBacklightState(bool state, uint? stationId = null) {
        var command = new SetBacklightStateRequest { State = state };
        var response = await SendAsync<SetBacklightStateRequest, SetBacklightStateResponse>(command, stationId);
        return response;
    }

    public async Task<GetReceiverModeResponse> GetReceiverMode(uint? stationId = null) {
        var command = new GetReceiverModeRequest();
        var response = await SendAsync<GetReceiverModeRequest, GetReceiverModeResponse>(command, stationId);
        return response;
    }

    public async Task<SetReceiverModeResponse> SetReceiverMode(ReceiverMode mode,
        bool isPanoramaSpectrumEnabled = false, Guid? scenarioId = null, ScenarioDto? scenario = null) {
        var command = new SetReceiverModeRequest {
            Mode = mode, IsPanoramaSpectrumEnabled = isPanoramaSpectrumEnabled, ScenarioId = scenarioId, Scenario = scenario
        };
        var response = await SendAsync<SetReceiverModeRequest, SetReceiverModeResponse>(command);
        return response;
    }

    public async Task<GetDeviceInfoResponse> GetDeviceInfo(uint? stationId = null,
        CancellationToken cancellationToken = default) {
        var command = new GetDeviceInfoRequest();
        var response =
            await SendAsync<GetDeviceInfoRequest, GetDeviceInfoResponse>(command, stationId,
                cancellationToken: cancellationToken);
        return response;
    }

    public async Task<GetSuppressedBandsResponse> GetSuppressedBands() {
        var command = new GetSuppressedBandsRequest();
        var response = await SendAsync<GetSuppressedBandsRequest, GetSuppressedBandsResponse>(command);
        return response;
    }

    public async Task<SetSuppressedBandsResponse> SetSuppressedBands(SuppressedBand[] bands) {
        var command = new SetSuppressedBandsRequest { Bands = bands };
        var response = await SendAsync<SetSuppressedBandsRequest, SetSuppressedBandsResponse>(command);
        return response;
    }

    public async Task<SetPanoramaModeResponse> SetPanoramaMode(bool isPanoramaSpectrumEnabled) {
        var command = new SetPanoramaModeRequest { IsPanoramaEnabled = isPanoramaSpectrumEnabled };
        var response = await SendAsync<SetPanoramaModeRequest, SetPanoramaModeResponse>(command);
        return response;
    }

    public async Task<StartStreamingResponse> StartStreaming() {
        var command = new StartStreamingRequest();
        var response = await SendAsync<StartStreamingRequest, StartStreamingResponse>(command);
        return response;
    }

    public async Task<StopStreamingResponse> StopStreaming() {
        var command = new StopStreamingRequest();
        var response = await SendAsync<StopStreamingRequest, StopStreamingResponse>(command);
        return response;
    }

    public async Task<StartStreamRecordingResponse> StartStreamRecording(IQRecordingMode mode, long frequency, 
        int shift, byte rateId, double bandwidth, IQFileSize fileSize, bool withTimestamp) {
        var command = new StartStreamRecordingRequest(DataFrameType.IQ) {
            IQParameters = new IQRecordingParameters {
                Frequency = (ulong)frequency,
                Shift = shift,
                RateId = rateId,
                Bandwidth = (uint)UnitsNet.Frequency.FromMegahertz(bandwidth).Hertz,
                FileSize = fileSize,
                WithTimestamp = withTimestamp,
                RecordingMode = mode
            }
        };

        return await SendAsync<StartStreamRecordingRequest, StartStreamRecordingResponse>(command);
    }

    public async Task<GetStreamRecordingStateResponse> GetStreamRecordingState() {
        var command = new GetStreamRecordingStateRequest();
        return await SendAsync<GetStreamRecordingStateRequest, GetStreamRecordingStateResponse>(command);
    }

    public async Task<StartStreamRecordingResponse> StartStreamRecording(PdwRecordingMode mode,
        PdwFileSize fileSize) {
        var command = new StartStreamRecordingRequest(DataFrameType.Pdw) {
            PdwParameters = 
                new PdwRecordingParameters { FileSize = fileSize, Mode = mode },
        };
        return await SendAsync<StartStreamRecordingRequest, StartStreamRecordingResponse>(command);
    }

    public async Task<StartStreamRecordingResponse> StartStreamRecording(DataFrameType frameType) {
        var command = new StartStreamRecordingRequest(frameType);
        return await SendAsync<StartStreamRecordingRequest, StartStreamRecordingResponse>(command);
    }
    
    public async Task<StopStreamRecordingResponse> StopStreamRecording(DataFrameType frameType) {
        var command = new StopStreamRecordingRequest { FrameType = frameType };
        return await SendAsync<StopStreamRecordingRequest, StopStreamRecordingResponse>(command);
    }

    public async Task<SetupIQResponse> SetupIQ(IQSetupParameters parameters) {
        var command = new SetupIQRequest {
            IQParameters = parameters
        };
        var response = await SendAsync<SetupIQRequest, SetupIQResponse>(command);
        return response;
    }

    public async Task<GetSpectrumParamsResponse> GetSpectrumParams() {
        var command = new GetSpectrumParamsRequest();
        var response = await SendAsync<GetSpectrumParamsRequest, GetSpectrumParamsResponse>(command);
        return response;
    }

    public async Task<GetFrequencyResponse> GetFrequency(uint? stationId = null) {
        var command = new GetFrequencyRequest();
        var response = await SendAsync<GetFrequencyRequest, GetFrequencyResponse>(command, stationId);
        return response;
    }

    public async Task<GetScanRangeResponse> GetScanRange(uint? stationId = null) {
        var command = new GetScanRangeRequest();
        var response = await SendAsync<GetScanRangeRequest, GetScanRangeResponse>(command, stationId);
        return response;
    }

    public async Task<SetSpectrumParamsResponse> SetSpectrumParams(SpectrumParameters spectrumParameters) {
        var command = new SetSpectrumParamsRequest { SpectrumParameters = spectrumParameters };
        var response = await SendAsync<SetSpectrumParamsRequest, SetSpectrumParamsResponse>(command);
        return response;
    }

    public async Task<SetFrequencyResponse> SetFrequency(long frequency) {
        var command = new SetFrequencyRequest { Frequency = frequency };
        var response = await SendAsync<SetFrequencyRequest, SetFrequencyResponse>(command);
        return response;
    }

    public async Task<SetScanRangeResponse> SetScanRange(ScanRange range, bool isPanoramaMode) {
        var command = new SetScanRangeRequest { IsPanoramaMode = isPanoramaMode, Range = range };
        var response = await SendAsync<SetScanRangeRequest, SetScanRangeResponse>(command);
        return response;
    }

    public async Task<GetInstantViewBandwidthResponse> GetInstantViewBandwidth() {
        var command = new GetInstantViewBandwidthRequest();
        var response = await SendAsync<GetInstantViewBandwidthRequest, GetInstantViewBandwidthResponse>(command);
        return response;
    }

    public async Task<SetInstantViewBandwidthResponse> SetInstantViewBandwidth(InstantViewBandwidth bandwidth) {
        var command = new SetInstantViewBandwidthRequest { Bandwidth = bandwidth };
        var response = await SendAsync<SetInstantViewBandwidthRequest, SetInstantViewBandwidthResponse>(command);
        return response;
    }

    public async Task<SetAgcResponse> SetAgc(bool enable, ushort attackTime, ushort decayTime) {
        var command = new SetAgcRequest(new AgcParameters(enable, attackTime, decayTime));
        var response = await SendAsync<SetAgcRequest, SetAgcResponse>(command);
        return response;
    }

    public async Task<GetAgcResponse> GetAgc() {
        var command = new GetAgcRequest();
        var response = await SendAsync<GetAgcRequest, GetAgcResponse>(command);
        return response;
    }

    public async Task<SetAttenuatorsResponse> SetAttenuators(double rfAttenuator, double ifAttenuator) {
        var command = new SetAttenuatorsRequest {
            RfAttenuator = (float)rfAttenuator, IfAttenuator = (float)ifAttenuator
        };
        var response = await SendAsync<SetAttenuatorsRequest, SetAttenuatorsResponse>(command);
        return response;
    }

    public async Task<GetAttenuatorsResponse> GetAttenuators() {
        var command = new GetAttenuatorsRequest();
        var response = await SendAsync<GetAttenuatorsRequest, GetAttenuatorsResponse>(command);
        return response;
    }

    public async Task<GetStationTrajectoryResponse> GetStationTrajectory() {
        var command = new GetStationTrajectoryRequest();
        var response = await SendAsync<GetStationTrajectoryRequest, GetStationTrajectoryResponse>(command);
        return response;
    }
}
