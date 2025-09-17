using System.Net;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Server.Models;
using Infozahyst.RSAAS.Server.Receiver.NetSdr;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Commands;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;

namespace Infozahyst.RSAAS.Server.Receiver;

public interface IControlClient : IAsyncDisposable
{
    event EventHandler<bool> ConnectionStatusChanged;
    event Action<GetFrequencyCommandResult>? UpdatedFrequencyReceived;
    event Action<GetGnssPositionCommandResult>? UpdatedGnssPositionReceived;
    event Action<SetStreamingIpAddressCommand>? UpdatedStreamingIpAddressReceived;
    event Action<GetStreamingStateCommandResult>? UpdatedStreamingStateReceived;
    event Action<GetDeviceInfoCommandResult>? UpdatedDeviceInfoReceived;
    bool IsConfigured { get; set; }
    bool IsAntennaAzimuthSupported { get; }

    Task<NetSdrResponse<bool>> Connect();
    Task Disconnect();
    Task<NetSdrResponse<float>> SetAntennaAzimuth(float azimuth);
    Task<NetSdrResponse<bool>> SetDDCState(int shift, byte rateId, State state, IQPacketFormat format);
    Task<NetSdrResponse<long>> SetFrequency(long frequency);
    Task<NetSdrResponse<ushort>> SetPayloadSize(ushort payloadSize, byte dataItem);
    Task<NetSdrResponse<SpectrumParameters>> SetSpectrumParams(SpectrumParameters parameters);
    Task<NetSdrResponse<float>> GetAntennaAzimuth();
    Task<NetSdrResponse<bool>> GetBacklightState();
    Task<NetSdrResponse<bool>> SetBacklightState(bool state);
    Task<NetSdrResponse<ChannelFunction>> GetChannelFunctions();
    Task<NetSdrResponse<DeviceInfo>> GetDeviceInfo();
    Task<NetSdrResponse<long>> GetFrequency();
    Task<NetSdrResponse<SpectrumParameters>> GetSpectrumParams();
    Task<NetSdrResponse<GnssPosition>> GetGnssPosition();
    Task<NetSdrResponse<ScanRange>> GetScanRange();
    Task<NetSdrResponse<DeviceSuppressedBand[]>> GetSuppressedBands();
    Task<NetSdrResponse<ChannelFunction>> SetChannelFunctions(ChannelFunction functions);
    Task<NetSdrResponse<ScanRange>> SetScanRange(ScanRange range);
    Task<NetSdrResponse<bool>> SetStreamingIpAddress(IPAddress ipAddress);
    Task<NetSdrResponse<bool>> SetSpectrumState(SpectrumType spectrumType, bool enabled);
    Task<NetSdrResponse<DeviceSuppressedBand[]>> SetSuppressedBands(DeviceSuppressedBand[] bands);
    Task<NetSdrResponse<ScanRange>> GetFrequencyRange();
    Task<NetSdrResponse<InstantViewBandwidth>> SetInstantViewBandwidth(InstantViewBandwidth bandwidth);
    Task<NetSdrResponse<InstantViewBandwidth>> GetInstantViewBandwidth();
    Task<NetSdrResponse<AgcParameters>> SetAgc(AgcParameters parameters);
    Task<NetSdrResponse<AgcParameters>> GetAgc();
    Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> SetAttenuators(float rfAttenuator, float ifAttenuator);
    Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> GetAttenuators();
    Task StartStreaming(CaptureMode captureMode);
    Task StopStreaming(CaptureMode captureMode);
    Task<NetSdrResponse<bool>> SetDDCSetup(IQTDOAModeId mode, int pulseCounter = 10, int pulseWait = 20, int recordTime = 1000);
    Task<NetSdrResponse<GetStreamingStateCommandResult>> GetStreamingState();
    Task<NetSdrResponse<ScanListItem[]>> SetScanList(ScanListItem[] items);
    Task<NetSdrResponse<ScanListItem[]>> GetScanList();
    Task<NetSdrResponse<GetDDCSetupCommandResult>> GetDDCSetup();
    Task<NetSdrResponse<GetDDCStateCommandResult>> GetDDCState(); 
    Task<NetSdrResponse<GetTimeCommandResult>> GetTime();
}
