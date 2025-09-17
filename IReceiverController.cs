using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Notifications.Interfaces;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;

namespace Infozahyst.RSAAS.Server.Controllers.Interfaces;

[TopicName("receiver")]
public interface IReceiverController : IBaseController, IReceiverMessagingNotification
{
    Task<StartStreamingResponse> StartStreaming(StartStreamingRequest request);
    Task<StopStreamingResponse> StopStreaming(StopStreamingRequest request);
    Task<StartStreamRecordingResponse> StartStreamRecording(StartStreamRecordingRequest request);
    Task<StopStreamRecordingResponse> StopStreamRecording(StopStreamRecordingRequest request);
    Task<GetFrequencyResponse> GetFrequency(GetFrequencyRequest request);
    Task<GetFrequencyRangeResponse> GetFrequencyRange(GetFrequencyRangeRequest request);
    Task<SetFrequencyResponse> SetFrequency(SetFrequencyRequest request);
    Task<GetScanRangeResponse> GetScanRange(GetScanRangeRequest request);
    Task<SetScanRangeResponse> SetScanRange(SetScanRangeRequest request);
    Task<GetDeviceInfoResponse> GetDeviceInfo(GetDeviceInfoRequest request);
    Task<GetSpectrumParamsResponse> GetSpectrumParams(GetSpectrumParamsRequest request);
    Task<SetSpectrumParamsResponse> SetSpectrumParams(SetSpectrumParamsRequest request);
    Task<SetAntennaAzimuthResponse> SetAntennaAzimuth(SetAntennaAzimuthRequest request);
    Task<GetAntennaAzimuthResponse> GetAntennaAzimuth(GetAntennaAzimuthRequest request);
    Task<SetBacklightStateResponse> SetBacklightState(SetBacklightStateRequest stateRequest);
    Task<GetBacklightStateResponse> GetBacklightState(GetBacklightStateRequest stateRequest);
    Task<GetStreamRecordingStateResponse> GetStreamRecordingState(GetStreamRecordingStateRequest request);
    Task<GetReceiverModeResponse> GetReceiverMode(GetReceiverModeRequest request);
    Task<SetReceiverModeResponse> SetReceiverMode(SetReceiverModeRequest request);
    Task<GetSuppressedBandsResponse> GetSuppressedBands(GetSuppressedBandsRequest request);
    Task<SetSuppressedBandsResponse> SetSuppressedBands(SetSuppressedBandsRequest request);
    Task<SetPanoramaModeResponse> SetPanoramaMode(SetPanoramaModeRequest request);
    Task<StartReceiverResponse> StartReceiver(StartReceiverRequest request);
    Task<StopReceiverResponse> StopReceiver(StopReceiverRequest request);
    Task<GetStationTrajectoryResponse> GetStationTrajectory(GetStationTrajectoryRequest request);
}
