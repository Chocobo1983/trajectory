using System.Net;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Server.DataStream;
using Infozahyst.RSAAS.Server.Models;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Commands;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Tools;
using MathNet.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;

namespace Infozahyst.RSAAS.Server.Receiver.NetSdr;

/// <summary>
/// description:
/// https://confluence.infozahyst.com/pages/viewpage.action?pageId=269226935#id-%D0%A0%D0%B0%D1%81%D1%88%D0%B8%D1%80%D0%B5%D0%BD%D0%BD%D1%8B%D0%B9%D0%BF%D1%80%D0%BE%D1%82%D0%BE%D0%BA%D0%BE%D0%BBNetSDR%D0%B4%D0%BB%D1%8F%D1%83%D0%BF%D1%80%D0%B0%D0%B2%D0%BB%D0%B5%D0%BD%D0%B8%D1%8F%D0%BF%D1%80%D0%B8%D0%B5%D0%BC%D0%BD%D0%B8%D0%BA%D0%BE%D0%BC%D0%A0%D0%A2%D0%A0-%D0%A3%D0%B2%D0%B5%D0%B4%D0%BE%D0%BC%D0%BB%D0%B5%D0%BD%D0%B8%D1%8F
/// </summary>
public class MinervaControlClient(
    IOptions<ReceiverSettings> receiverOptions,
    IDataClientFactory dataClientFactory,
    IndicatorAggregator indicatorAggregator,
    ILogger<MinervaControlClient> logger,
    IRetryProvider retryProvider,
    IFeatureManager featureManager)
    : NetSdrClient(receiverOptions, dataClientFactory, indicatorAggregator, logger, retryProvider, featureManager)
{
    
    public override async Task<NetSdrResponse<bool>> SetDDCState(int shift, byte rateId, State state, IQPacketFormat format) {
        try {
            var command = new SetDDCStateCommand(shift, rateId, state, format);
            await SendCommand<SetDDCStateCommand, SetDDCStateCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(true);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<GetDDCStateCommandResult>> GetDDCState() {
        try {
            var command = new GetDDCStateCommand();
            var result = await SendCommand<GetDDCStateCommand, GetDDCStateCommandResult>(command);

            return NetSdrResponse<GetDDCStateCommandResult>.CreateSuccess(result);
        } catch (Exception e) {
            return NetSdrResponse<GetDDCStateCommandResult>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<GetTimeCommandResult>> GetTime() {
        try {
            var command = new GetTimeCommand();
            var res = await SendCommand<GetTimeCommand, GetTimeCommandResult>(command);
            return NetSdrResponse<GetTimeCommandResult>.CreateSuccess(res);
        } catch (Exception e) {
            return NetSdrResponse<GetTimeCommandResult>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<bool>> SetDDCSetup(IQTDOAModeId mode, int pulseCounter, int pulseWait, int recordTime) {
        try {
            var command = new SetDDCSetupCommand(mode, pulseCounter, pulseWait, recordTime);
            await SendCommand<SetDDCSetupCommand, SetDDCSetupCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(true);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<GetDDCSetupCommandResult>> GetDDCSetup() {
        try {
            var command = new GetDDCSetupCommand();
            var result = await SendCommand<GetDDCSetupCommand, GetDDCSetupCommandResult>(command);

            return NetSdrResponse<GetDDCSetupCommandResult>.CreateSuccess(result);
        } catch (Exception e) {
            return NetSdrResponse<GetDDCSetupCommandResult>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<long>> SetFrequency(long frequency) {
        try {
            var command = new SetFrequencyCommand(NetSdrChannel.Channel0, frequency);
            var res = await SendCommand<SetFrequencyCommand, SetFrequencyCommand>(command);

            return new NetSdrResponse<long> {
                State = res.Freq.Value == frequency ? CommandState.Success : CommandState.Warning,
                Value = res.Freq.Value
            };
        } catch (Exception e) {
            return NetSdrResponse<long>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ushort>> SetPayloadSize(ushort payloadSize, byte dataItem) {
        try {
            var command = new SetPayloadSizeCommand(dataItem, payloadSize);
            var res = await SendCommand<SetPayloadSizeCommand, SetPayloadSizeCommand>(command);

            return NetSdrResponse<ushort>.CreateSuccess(res.PayloadSize);
        } catch (Exception e) {
            return NetSdrResponse<ushort>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<SpectrumParameters>> SetSpectrumParams(SpectrumParameters parameters) {
        try {
            var command = new SetSpectrumParamsCommand(
                parameters.Mode,
                parameters.RBW,
                parameters.Time,
                (short)(parameters.MinPower * Dbm10),
                (short)(parameters.MaxPower * Dbm10));
            var res = await SendCommand<SetSpectrumParamsCommand, SetSpectrumParamsCommand>(command);

            var newParameters = new SpectrumParameters {
                Mode = (SpectrumAveragingMode)res.Mode,
                Time = res.Time,
                RBW = res.FftPow2,
                MinPower = (double)res.MinPower / Dbm10,
                MaxPower = (double)res.MaxPower / Dbm10
            };

            bool isCorrect = parameters.Mode == newParameters.Mode &&
                             parameters.Time == newParameters.Time &&
                             parameters.RBW == newParameters.RBW &&
                             parameters.MaxPower.AlmostEqual(newParameters.MaxPower, 1) &&
                             parameters.MinPower.AlmostEqual(newParameters.MinPower, 1);

            return new NetSdrResponse<SpectrumParameters> {
                State = isCorrect ? CommandState.Success : CommandState.Warning,
                Value = newParameters
            };
        } catch (Exception e) {
            return NetSdrResponse<SpectrumParameters>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<float>> GetAntennaAzimuth() {
        try {
            var command = new GetAntennaAzimuthCommand();
            var res = await SendCommand<GetAntennaAzimuthCommand, GetAntennaAzimuthCommandResult>(command);

            return NetSdrResponse<float>.CreateSuccess(res.Azimuth);
        } catch (Exception e) {
            return NetSdrResponse<float>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<bool>> GetBacklightState() {
        try {
            var command = new GetBacklightStateCommand();
            var res = await SendCommand<GetBacklightStateCommand, SetBacklightStateCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(res.BacklightState == 1);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<bool>> SetBacklightState(bool state) {
        try {
            var command = new SetBacklightStateCommand(state);
            var res = await SendCommand<SetBacklightStateCommand, SetBacklightStateCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(res.BacklightState == 1);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ChannelFunction>> GetChannelFunctions() {
        try {
            var command = new GetChannelFunctionsCommand(NetSdrChannel.Channel0);
            var res = await SendCommand<GetChannelFunctionsCommand, GetChannelFunctionsCommandResult>(command);

            return NetSdrResponse<ChannelFunction>.CreateSuccess(res.Functions);
        } catch (Exception e) {
            return NetSdrResponse<ChannelFunction>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<float>> SetAntennaAzimuth(float azimuth) {
        try {
            var command = new SetAntennaAzimuthCommand(azimuth);
            var res = await SendCommand<SetAntennaAzimuthCommand, SetAntennaAzimuthCommand>(command);

            return NetSdrResponse<float>.CreateSuccess(res.Azimuth);
        } catch (Exception e) {
            return NetSdrResponse<float>.CreateError(e.Message);
        }
    }
    
    public override async Task<NetSdrResponse<DeviceInfo>> GetDeviceInfo() {
        try {
            var command = new GetDeviceInfoCommand();
            var support16 = await _featureManager.IsEnabledAsync(FeatureFlags.SupportNetSdr16);
            DeviceInfo res;
            if (support16) {
                var result = await SendCommand<GetDeviceInfoCommand, GetDeviceInfoCommandResult16>(command);
                res = result.DeviceInfo.To15();
            } else {
                var result = await SendCommand<GetDeviceInfoCommand, GetDeviceInfoCommandResult>(command);
                res = result.DeviceInfo;
            }

            return NetSdrResponse<DeviceInfo>.CreateSuccess(res);
        } catch (Exception e) {
            return NetSdrResponse<DeviceInfo>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<long>> GetFrequency() {
        try {
            var command = new GetFrequencyCommand(NetSdrChannel.Channel0);
            var res = await SendCommand<GetFrequencyCommand, GetFrequencyCommandResult>(command);

            return NetSdrResponse<long>.CreateSuccess(res.Freq.Value);
        } catch (Exception e) {
            return NetSdrResponse<long>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<SpectrumParameters>> GetSpectrumParams() {
        try {
            var command = new GetSpectrumParamsCommand();
            var res = await SendCommand<GetSpectrumParamsCommand, GetSpectrumParamsCommandResult>(command);

            var parameters = new SpectrumParameters {
                Mode = (SpectrumAveragingMode)res.Mode,
                RBW = res.FftPow2,
                Time = res.Time,
                MinPower = (short)(res.MinPower / Dbm10),
                MaxPower = (short)(res.MaxPower / Dbm10)
            };

            return NetSdrResponse<SpectrumParameters>.CreateSuccess(parameters);
        } catch (Exception e) {
            return NetSdrResponse<SpectrumParameters>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<GnssPosition>> GetGnssPosition() {
        try {
            var command = new GetGnssPositionCommand();
            var res = await SendCommand<GetGnssPositionCommand, GetGnssPositionCommandResult>(command);

            var position = new GnssPosition {
                Longitude = res.Longitude,
                Latitude = res.Latitude,
                HeightAboveSeaLevel = res.HeightAboveSeaLevel,
                TimeOfWeek = res.TimeOfWeek,
                HorizontalAccuracy = res.HorizontalAccuracy,
                VerticalAccuracy = res.VerticalAccuracy
            };

            return NetSdrResponse<GnssPosition>.CreateSuccess(position);
        } catch (Exception e) {
            return NetSdrResponse<GnssPosition>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ScanRange>> GetScanRange() {
        try {
            var command = new GetScanRangeCommand(0);
            var res = await SendCommand<GetScanRangeCommand, GetScanRangeCommandResult>(command);
            var scanRange = new ScanRange {
                FreqFrom = UnitsNet.Frequency.FromHertz(res.FreqFrom.Value).Megahertz,
                FreqTo = UnitsNet.Frequency.FromHertz(res.FreqTo.Value).Megahertz
            };

            return NetSdrResponse<ScanRange>.CreateSuccess(scanRange);
        } catch (Exception e) {
            return NetSdrResponse<ScanRange>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<DeviceSuppressedBand[]>> GetSuppressedBands() {
        try {
            var command = new GetSuppressedBandsCommand(NetSdrChannel.Channel0);
            var (_, data) =
                await SendCommand<GetSuppressedBandsCommand, GetSuppressedBandsCommandResult, DeviceSuppressedBand>(
                    command);
            return NetSdrResponse<DeviceSuppressedBand[]>.CreateSuccess(data);
        } catch (Exception e) {
            return NetSdrResponse<DeviceSuppressedBand[]>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ScanRange>> GetFrequencyRange() {
        try {
            var command = new GetFrequencyRangeCommand(0);
            var res = await SendCommand<GetFrequencyRangeCommand, GetFrequencyRangeCommandResponse>(command);
            var scanRange = new ScanRange {
                FreqFrom = UnitsNet.Frequency.FromHertz(res.FreqFrom.Value).Megahertz,
                FreqTo = UnitsNet.Frequency.FromHertz(res.FreqTo.Value).Megahertz
            };

            return NetSdrResponse<ScanRange>.CreateSuccess(scanRange);
        } catch (Exception e) {
            return NetSdrResponse<ScanRange>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ChannelFunction>> SetChannelFunctions(ChannelFunction functions) {
        try {
            var command = new SetChannelFunctionsCommand(NetSdrChannel.Channel0, functions);
            var res = await SendCommand<SetChannelFunctionsCommand, SetChannelFunctionsCommand>(command);

            return NetSdrResponse<ChannelFunction>.CreateSuccess(res.Functions);
        } catch (Exception e) {
            return NetSdrResponse<ChannelFunction>.CreateError(e.Message,
                e.InnerException is NetSdrNakReceivedException);
        }
    }

    public override async Task<NetSdrResponse<ScanRange>> SetScanRange(ScanRange range) {
        try {
            var from = (long)UnitsNet.Frequency.FromMegahertz(range.FreqFrom).Hertz;
            var to = (long)UnitsNet.Frequency.FromMegahertz(range.FreqTo).Hertz;
            var command = new SetScanRangeCommand(0, from, to);
            var res = await SendCommand<SetScanRangeCommand, SetScanRangeCommand>(command);

            return NetSdrResponse<ScanRange>.CreateSuccess(new ScanRange {
                FreqFrom = UnitsNet.Frequency.FromHertz(res.FreqFrom.Value).Megahertz,
                FreqTo = UnitsNet.Frequency.FromHertz(res.FreqTo.Value).Megahertz
            });
        } catch (Exception e) {
            return NetSdrResponse<ScanRange>.CreateError(e.Message);
        }
    }

    public async Task<GnssStatus> GetGnssStatus() {
        var command = new GetGnssStatusCommand();
        var res = await SendCommand<GetGnssStatusCommand, GetGnssStatusCommandResult>(command);

        return new GnssStatus {
            Fix = res.Fix,
            SatCount = res.SatCount,
            State = res.State,
            TimeOfWeek = res.TimeOfWeek
        };
    }

    public async Task<int> GetGnssControl() {
        var command = new GetGnssControlCommand();
        var res = await SendCommand<GetGnssControlCommand, GetGnssControlCommandResult>(command);
        return res.MaskRaw;
    }

    public async Task<int> SetGnssControl(int mask) {
        var command = new SetGnssControlCommand(mask);
        var res = await SendCommand<SetGnssControlCommand, SetGnssControlCommand>(command);
        return (int)res.MaskRaw;
    }

    public override async Task<NetSdrResponse<bool>> SetStreamingIpAddress(IPAddress ipAddress) {
        try {
            var command = new SetStreamingIpAddressCommand(ipAddress);
            await SendCommand<SetStreamingIpAddressCommand, SetStreamingIpAddressCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(true);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<bool>> SetSpectrumState(SpectrumType spectrumType, bool enabled) {
        try {
            var command = new SetSpectrumStateCommand(spectrumType, enabled);
            var result = await SendCommand<SetSpectrumStateCommand, SetSpectrumStateCommand>(command);

            return NetSdrResponse<bool>.CreateSuccess(result.SpectrumState);
        } catch (Exception e) {
            return NetSdrResponse<bool>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<DeviceSuppressedBand[]>> SetSuppressedBands(DeviceSuppressedBand[] bands) {
        try {
            var command = new SetSuppressedBandsCommand(NetSdrChannel.Channel0, (ushort)bands.Length);
            var (_, data) =
                await SendCommand<SetSuppressedBandsCommand, SetSuppressedBandsCommand, DeviceSuppressedBand>(
                    command, bands);
            return NetSdrResponse<DeviceSuppressedBand[]>.CreateSuccess(data);
        } catch (Exception e) {
            return NetSdrResponse<DeviceSuppressedBand[]>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<GetStreamingStateCommandResult>> GetStreamingState() {
        try {
            var command = new GetStreamingStateCommand();
            var result = await SendCommand<GetStreamingStateCommand, GetStreamingStateCommandResult>(command);
            return NetSdrResponse<GetStreamingStateCommandResult>.CreateSuccess(result);
        } catch (Exception e) {
            return NetSdrResponse<GetStreamingStateCommandResult>.CreateError(e.Message);
        }
    }

    public override async Task StartStreaming(CaptureMode captureMode) {
        var command = new SetStreamingStateCommand(StreamingState.Start, captureMode);
        await SendCommand<SetStreamingStateCommand, SetStreamingStateCommand>(command);
    }

    public override async Task StopStreaming(CaptureMode captureMode) {
        var command = new SetStreamingStateCommand(StreamingState.Stop, captureMode);
        await SendCommand<SetStreamingStateCommand, SetStreamingStateCommand>(command);
    }

    public override async Task<NetSdrResponse<InstantViewBandwidth>> SetInstantViewBandwidth(InstantViewBandwidth bandwidth) {
        try {
            var command = new SetInstantViewBandwidthCommand(NetSdrChannel.Channel0, bandwidth);
            var res = await SendCommand<SetInstantViewBandwidthCommand, SetInstantViewBandwidthCommand>(command);
            return NetSdrResponse<InstantViewBandwidth>.CreateSuccess(res.Bandwidth);
        } catch (Exception e) {
            return NetSdrResponse<InstantViewBandwidth>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<InstantViewBandwidth>> GetInstantViewBandwidth() {
        try {
            var command = new GetInstantViewBandwidthCommand(NetSdrChannel.Channel0);
            var res = await SendCommand<GetInstantViewBandwidthCommand, GetInstantViewBandwidthCommandResult>(command);
            return NetSdrResponse<InstantViewBandwidth>.CreateSuccess(res.Bandwidth);
        } catch (Exception e) {
            return NetSdrResponse<InstantViewBandwidth>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<AgcParameters>> SetAgc(AgcParameters parameters) {
        try {
            var command = new SetAgcCommand(parameters.IsEnabled, parameters.AttackTime, parameters.DecayTime);
            var res = await SendCommand<SetAgcCommand, SetAgcCommand>(command);
            return NetSdrResponse<AgcParameters>.CreateSuccess(new AgcParameters(res.IsEnabled, res.AttackTime,
                res.DecayTime));
        } catch (Exception e) {
            return NetSdrResponse<AgcParameters>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<AgcParameters>> GetAgc() {
        try {
            var command = new GetAgcCommand();
            var res = await SendCommand<GetAgcCommand, GetAgcCommandResult>(command);
            return NetSdrResponse<AgcParameters>.CreateSuccess(new AgcParameters(res.IsEnabled, res.AttackTime,
                res.DecayTime));
        } catch (Exception e) {
            return NetSdrResponse<AgcParameters>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> SetAttenuators(float rfAttenuator,
        float ifAttenuator) {
        try {
            var command = new SetAttenuatorsCommand(rfAttenuator, ifAttenuator);
            var result = await SendCommand<SetAttenuatorsCommand, SetAttenuatorsCommand>(command);
            return NetSdrResponse<(float, float)>.CreateSuccess((result.RfAttenuatorValue, result.IfAttenuatorValue));
        } catch (Exception e) {
            return NetSdrResponse<(float, float)>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> GetAttenuators() {
        try {
            var command = new GetAttenuatorsCommand();
            var result = await SendCommand<GetAttenuatorsCommand, GetAttenuatorsCommandResult>(command);
            return NetSdrResponse<(float, float)>.CreateSuccess((result.RfAttenuatorValue, result.IfAttenuatorValue));
        } catch (Exception e) {
            return NetSdrResponse<(float, float)>.CreateError(e.Message);
        }
    }
    
    public override async Task<NetSdrResponse<ScanListItem[]>> SetScanList(ScanListItem[] items) {
        try {
            var command = new SetScanListCommand(NetSdrChannel.Channel0, (ushort)items.Length);
            var result = await SendCommand<SetScanListCommand, SetScanListCommandResult, ScanListItem>(command, items);
            return NetSdrResponse<ScanListItem[]>.CreateSuccess(result.Data);
        } catch (Exception e) {
            return NetSdrResponse<ScanListItem[]>.CreateError(e.Message);
        }
    }

    public override async Task<NetSdrResponse<ScanListItem[]>> GetScanList() {
        try {
            var command = new GetScanListCommand();
            var result = await SendCommand<GetScanListCommand, GetScanListCommandResult, ScanListItem>(command);
            return NetSdrResponse<ScanListItem[]>.CreateSuccess(result.Data);
        } catch (Exception e) {
            return NetSdrResponse<ScanListItem[]>.CreateError(e.Message);
        }
    }
}

