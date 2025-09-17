using Infozahyst.RSAAS.Common.Collections;
using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Server.Receiver.NetSdr;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;

namespace Infozahyst.RSAAS.Server.Services.Interfaces;

public interface IReceiverService : IAsyncDisposable
{
    event EventHandler<IQSetupParameters>? IQSetupChanged;
    event EventHandler<IQRecordingNotification>? IQRecordingStatusChanged;
    event EventHandler<ReceiverParametersChangedNotification>? ReceiverParametersChanged;
    event EventHandler<PdwRecordingNotification>? PdwRecordingStatusChanged;
    event EventHandler<GnssRecordingNotification>? GnssRecordingStatusChanged;
    event EventHandler<DeviceInfoUpdateNotification>? DeviceInfoUpdated;
    
    bool IsIQRecordingActive { get; }
    bool IsPdwRecordingActive { get; }
    bool IsGnssRecordingActive { get; }

    Task<NetSdrResponse<ChannelFunction>?> SetReceiverMode(ReceiverMode receiverMode,
        Scenario? scenario = null, bool isPanoramaSpectrumEnabled = false,
        bool isPdwStreamControlRequired = true);

    Task<(ReceiverMode ReceiverMode, bool IsPanoramaMode, Guid? ScenarioId)?> GetReceiverMode();
    Task<(int? ScanListLength, ScanRange? ScenarioRange)?> GetScenarioRange();
    Task StopIQRecording(bool resetStateAndSetup = true, bool sendNotification = true);

    Task StartIQRecording(IQRecordingParameters parameters, uint stationId,
        IReadOnlyList<LinkedStation>? linkedStationList);

    Task<NetSdrResponse<InstantViewBandwidth>> SetInstantViewBandwidth(InstantViewBandwidth instantViewBandwidth);
    Task<NetSdrResponse<InstantViewBandwidth>> GetInstantViewBandwidth();
    Task<NetSdrResponse<long>> SetFrequency(long frequency);
    Task<NetSdrResponse<long>> GetFrequency();
    Task<NetSdrResponse<ScanRange>> SetScanRange(ScanRange scanRange, bool isPanoramaMode);
    Task<NetSdrResponse<ScanRange>> GetScanRange();
    Task<NetSdrResponse<ChannelFunction>> SetPanoramaMode(bool isPanoramaEnabled, ScenarioDto? scenario);
    Task StartStreaming(bool isPdwStreamControlRequired = true);
    Task StopStreaming();
    Task<NetSdrResponse<ChannelFunction>> GetChannelFunctions();
    Task<NetSdrResponse<SpectrumParameters>> SetSpectrumParams(SpectrumParameters spectrumParameters);
    Task RestoreReceiverMode(bool isPdwStreamControlRequired = false);
    Task StopPdwRecording(bool sendNotification = true);
    Task StartPdwRecording(PdwRecordingParameters pdwRecordingParameters, uint stationId);
    Task SetupIQ(IQSetupParameters parameters);
    Task<NetSdrResponse<bool>> StartIQStreaming(int shift, byte rateId, IQPacketFormat format);
    Task StopIQStreaming();
    Task<(int Shift, byte RateId, IQPacketFormat Format, State StreamState)?> GetIQState();
    Task<IQSetupParameters?> GetIQSetup();
    Task StartGnssRecording();
    Task StopGnssRecording();
    Task CheckIQRecordingStatus();
    Task StartMonitoringDevice();
    Task StopMonitoringDevice();
    TrajectoryPoint[] GetDeviceTrajectory();
}
