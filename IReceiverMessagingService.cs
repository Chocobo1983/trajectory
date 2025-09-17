using System;
using System.Threading;
using System.Threading.Tasks;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications.Interfaces;
using Infozahyst.RSAAS.Core.Transport.DataStreaming.Frames;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;

namespace Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;

public interface IReceiverMessagingService : IReceiverMessagingNotification, IBaseMessagingService
{
    Task<GetAntennaAzimuthResponse> GetAntennaAzimuth(uint? stationId = null);
    Task<SetAntennaAzimuthResponse> SetAntennaAzimuth(float azimuth, uint? stationId = null);
    Task<GetBacklightStateResponse> GetBacklightState(uint? stationId = null);
    Task<SetBacklightStateResponse> SetBacklightState(bool state, uint? stationId = null);
    Task<GetReceiverModeResponse> GetReceiverMode(uint? stationId = null);
    Task<SetReceiverModeResponse> SetReceiverMode(ReceiverMode mode, bool isPanoramaSpectrumEnabled = false,
        Guid? scenarioId = null, ScenarioDto? scenario = null);
    Task<GetDeviceInfoResponse> GetDeviceInfo(uint? stationId = null, CancellationToken cancellationToken = default);
    Task<GetSuppressedBandsResponse> GetSuppressedBands();
    Task<SetSuppressedBandsResponse> SetSuppressedBands(SuppressedBand[] bands);
    Task<SetPanoramaModeResponse> SetPanoramaMode(bool isPanoramaSpectrumEnabled);
    Task<StartStreamingResponse> StartStreaming();
    Task<StopStreamingResponse> StopStreaming();
    Task<StartStreamRecordingResponse> StartStreamRecording(IQRecordingMode mode, long frequency,
        int shift, byte rateId, double bandwidth, IQFileSize fileSize, bool withTimestamp);
    Task<GetSpectrumParamsResponse> GetSpectrumParams();
    Task<GetFrequencyResponse> GetFrequency(uint? stationId = null);
    Task<GetScanRangeResponse> GetScanRange(uint? stationId = null);
    Task<SetSpectrumParamsResponse> SetSpectrumParams(SpectrumParameters spectrumParameters);
    Task<SetFrequencyResponse> SetFrequency(long frequency);
    Task<SetScanRangeResponse> SetScanRange(ScanRange range, bool isPanoramaMode);
    Task<SetInstantViewBandwidthResponse> SetInstantViewBandwidth(InstantViewBandwidth bandwidth);
    Task<GetInstantViewBandwidthResponse> GetInstantViewBandwidth();
    Task<GetAgcResponse> GetAgc();
    Task<SetAgcResponse> SetAgc(bool enable, ushort attackTime, ushort decayTime);
    Task<SetAttenuatorsResponse> SetAttenuators(double rfAttenuator, double ifAttenuator);
    Task<GetAttenuatorsResponse> GetAttenuators();
    Task<SetupIQResponse> SetupIQ(IQSetupParameters parameters);
    Task<GetStreamRecordingStateResponse> GetStreamRecordingState();
    Task<StartStreamRecordingResponse> StartStreamRecording(PdwRecordingMode mode, PdwFileSize fileSize);
    Task<StopStreamRecordingResponse> StopStreamRecording(DataFrameType frameType);
    Task<StartStreamRecordingResponse> StartStreamRecording(DataFrameType frameType);
    Task<GetStationTrajectoryResponse> GetStationTrajectory();
}
