using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Infozahyst.RSAAS.ReceiverEmulator.Control;
using Infozahyst.RSAAS.ReceiverEmulator.Control.Commands;
using Infozahyst.RSAAS.ReceiverEmulator.Control.Enums;
using Infozahyst.RSAAS.ReceiverEmulator.Control.Models;
using Infozahyst.RSAAS.ReceiverEmulator.Settings;
using Infozahyst.RSAAS.ReceiverEmulator.Spectrum;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infozahyst.RSAAS.ReceiverEmulator.Services;

public class ControlService
{
    private readonly ILogger<ControlService> _logger;
    private readonly EmulatorSettings _settings;
    private readonly IPdwStreamingService _pdwStreamingService;
    private readonly IIQStreamingService _iqStreamingService;
    private readonly IPersistenceSpectrumStreamingService _persistenceSpectrumStreamingService;
    private readonly IThresholdStreamingService _thresholdStreamingService;
    private readonly ISpectrumStreamingService _spectrumStreamingService;
    private const ulong FreqMinLimit = 7_200_000_000;
    private const ulong FreqMaxLimit = 12_500_000_000;

    private float _azimuth;
    private long _frequency = 7_200_000_000;
    private NetSdrChannel _channel = NetSdrChannel.Channel0;
    private ChannelFunction _channelFunctions = ChannelFunction.None;
    private ScanRange _scanRange = new() { FreqFrom = 7_000_000_000, FreqTo = 7_400_000_000 };

    private SpectrumParameters _spectrumParameters = new() {
        Mode = SpectrumAveragingMode.Average,
        Time = 0,
        RBW = 10,
        MaxPower = -200,
        MinPower = -1200
    };

    private bool _backlightState;
    private InstantViewBandwidth _bandwidth = InstantViewBandwidth.Bandwidth400MHz;
    private (int Shift, byte Rate, byte Format, State State) _ddcState;
    private (IQTDOAModeId Mode, int PulseCounter, int PulseWait, int RecordTime) _ddcSetup;
    private string _ip = "127.0.0.1";
    private int _port;
    private byte _spectrumState;
    private (StreamingState State, CaptureMode Mode) _streamingState =
        (StreamingState.Stop, CaptureMode.Continuous);
    private DeviceSuppressedBand[] _suppressedBands = Array.Empty<DeviceSuppressedBand>();
    private ScanListItem[] _scanListItems = Array.Empty<ScanListItem>();
    private bool _isEnabledAgc;
    private (float RfAttenuator, float IfAttenuator) _attenuators;
    private ushort _agcAttackTime;
    private ushort _agcDecayTime;

    public ControlService(ILogger<ControlService> logger,
        IOptions<EmulatorSettings> settings,
        IPdwStreamingService pdwStreamingService,
        IIQStreamingService iqStreamingService,
        IThresholdStreamingService thresholdStreamingService,
        IPersistenceSpectrumStreamingService persistenceSpectrumStreamingService,
        ISpectrumStreamingService spectrumStreamingService) {
        _logger = logger;
        _settings = settings.Value;
        _pdwStreamingService = pdwStreamingService;
        _iqStreamingService = iqStreamingService;
        _thresholdStreamingService = thresholdStreamingService;
        _persistenceSpectrumStreamingService = persistenceSpectrumStreamingService;
        _spectrumStreamingService = spectrumStreamingService;
    }

    public ReadOnlyMemory<byte> SetAntennaAzimuth(SetAntennaAzimuthCommand command) {
        _azimuth = command.Azimuth;
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetDDCState(SetDDCStateCommand command) {
        _ddcState = new(command.Shift, command.GetRate, command.GetFormat, command.State);
        if (command.State == State.Run) {
            _iqStreamingService.StartStreaming(legacyFormat: command.GetFormat == 0);
        } else {
            _iqStreamingService.StopStreaming();
        }

        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetFrequency(SetFrequencyCommand command) {
        _frequency = command.Freq.Value;
        _channel = command.Channel;
        _thresholdStreamingService.SetFrequency(_frequency);
        _persistenceSpectrumStreamingService.SetFrequency(_frequency);
        _spectrumStreamingService.SetFrequency(_frequency);
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetPayloadSize(SetPayloadSizeCommand command) {
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetSpectrumParams(SetSpectrumParamsCommand command) {
        Log(command, command);
        _spectrumParameters = new SpectrumParameters {
            Time = command.Time,
            MaxPower = command.MaxPower,
            MinPower = command.MinPower,
            Mode = (SpectrumAveragingMode)command.Mode,
            RBW = command.FftPow2
        };
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetAntennaAzimuth(GetAntennaAzimuthCommand command) {
        var response = new GetAntennaAzimuthCommandResult(_azimuth);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetBacklightState(GetBacklightStateCommand command) {
        var response = new GetBacklightStateCommandResult(_backlightState);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetBacklightState(SetBacklightStateCommand command) {
        _backlightState = command.BacklightState == 0x01;
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetChannelFunctions(GetChannelFunctionsCommand command) {
        var response = new GetChannelFunctionsCommandResult(_channel, _channelFunctions);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetDeviceInfo(GetDeviceInfoCommand command) {
        var response = new GetDeviceInfoCommandResult(new DeviceInfo {
            AppInfo = new DeviceAppInfo(),
            DriverInfo = new DeviceDriverInfo(),
            FpgaInfo = new DeviceFpgaInfo(),
            FrequencyInfo = new DeviceFrequencyInfo(),
            GeneralInfo = new DeviceGeneralInfo("1.6.0", 0),
            GnssInfo = new DeviceGnssInfo(),
            IQInfo = new DeviceIQInfo(),
            NetstackInfo = new DeviceNetstackInfo(),
            PdwCalibrationInfo = new DevicePdwCalibrationInfo(),
            PersistenceSpectrumInfo = new DevicePersistenceSpectrumInfo(),
            SupervisorInfo = new DeviceSupervisorInfo(),
            SynthesizerInfo = new DeviceSynthesizerInfo(),
            TunerSpectrumInfo = new DeviceTunerSpectrumInfo()
        });
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetFrequency(GetFrequencyCommand command) {
        var response = new GetFrequencyCommandResult(_channel, _frequency);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetSpectrumParams(GetSpectrumParamsCommand command) {
        var response = new GetSpectrumParamsCommandResult(_spectrumParameters.Mode,
            _spectrumParameters.RBW, _spectrumParameters.Time,
            (short)_spectrumParameters.MinPower, (short)_spectrumParameters.MaxPower);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetGnssPosition(GetGnssPositionCommand command) {
        var response = new GetGnssPositionCommandResult(0, 0, 0, 0, 0, 0);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetScanRange(GetScanRangeCommand command) {
        var response = new GetScanRangeCommandResult(_channel, (long)_scanRange.FreqFrom, (long)_scanRange.FreqTo);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetSuppressedBands(GetSuppressedBandsCommand command) {
        var response = new GetSuppressedBandsCommandResult(_channel, (ushort)_suppressedBands.Length);
        Log(command, response, _suppressedBands);
        return TransformResponse(response, _suppressedBands);
    }

    public ReadOnlyMemory<byte> SetSuppressedBands(SetSuppressedBandsCommand command,
        ReadOnlyMemory<byte> suppressedBandsBytes) {
        if (command.Count > 0) {
            var responseSize = Unsafe.SizeOf<SetSuppressedBandsCommand>();
            var dataItemSize = Unsafe.SizeOf<DeviceSuppressedBand>();
            var suppressedBands = new DeviceSuppressedBand[command.Count];
            for (int i = 0; i < command.Count; i++) {
                var startIndex = responseSize + dataItemSize * i;
                suppressedBands[i] = MemoryMarshal.AsRef<DeviceSuppressedBand>(suppressedBandsBytes.Span[startIndex..]);
            }

            _suppressedBands = suppressedBands;
        }

        Log(command, command);
        return TransformResponse(command, _suppressedBands);
    }

    public ReadOnlyMemory<byte> SetChannelFunctions(SetChannelFunctionsCommand command) {
        _channelFunctions = command.Functions;
        _channel = command.Channel;
        _thresholdStreamingService.SetChannelFunctions(_channelFunctions);
        _spectrumStreamingService.SetChannelFunctions(_channelFunctions);
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetScanRange(SetScanRangeCommand command) {
        _channel = command.Channel;
        var bandwidth = _bandwidth == InstantViewBandwidth.Bandwidth400MHz ? 400000000 : 100000000;
        var freqTo = command.FreqTo.Value - command.FreqFrom.Value < bandwidth
            ? command.FreqFrom.Value + bandwidth
            : command.FreqTo.Value;
        _scanRange = new ScanRange { FreqFrom = command.FreqFrom.Value, FreqTo = freqTo };
        _thresholdStreamingService.SetScanRange(_scanRange);
        _spectrumStreamingService.SetScanRange(_scanRange);
        Log(command, command);
        return TransformResponse(
            new SetScanRangeCommand(_channel, (long)_scanRange.FreqFrom, (long)_scanRange.FreqTo));
    }

    public ReadOnlyMemory<byte> SetStreamingIpAddress(SetStreamingIpAddressCommand command) {
        _ip = new IPAddress(BitConverter.GetBytes(command.IpAddress).Reverse().ToArray()).ToString();
        _port = command.Port;
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> SetSpectrumState(SetSpectrumStateCommand command) {
        _spectrumState = command.State;

        if (command.Mask is (byte)SpectrumType.All or (byte)SpectrumType.Threshold) {
            if (_spectrumState == 0x02) {
                _ = Task.Run(async () => await _thresholdStreamingService.StartStreaming(_ip));
            } else if (_spectrumState == 0x01) {
                _ = Task.Run(_thresholdStreamingService.StopStreaming);
            }
        }

        if (command.Mask is (byte)SpectrumType.All or (byte)SpectrumType.Persistence) {
            if (command.State == 2) {
                _ = Task.Run(async () => await _persistenceSpectrumStreamingService.StartStreaming(_ip));
            } else {
                _ = Task.Run(async () => await _persistenceSpectrumStreamingService.StopStreaming());
            }
        }

        if (command.Mask is (byte)SpectrumType.All or (byte)SpectrumType.Spectrum) {
            _ = _spectrumState == 2
                ? Task.Run(async () => await _spectrumStreamingService.StartStreaming(_ip))
                : Task.Run(async () => await _spectrumStreamingService.StopStreaming());
        }

        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetFrequencyRange(GetFrequencyRangeCommand command) {
        var response =
            new GetFrequencyRangeCommandResult(_channel, (long)FreqMinLimit, (long)FreqMaxLimit, 0, 1);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetInstantViewBandwidth(SetInstantViewBandwidthCommand command) {
        _bandwidth = command.Bandwidth;
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetInstantViewBandwidth(GetInstantViewBandwidthCommand command) {
        var response = new GetInstantViewBandwidthCommandResult(_channel, _bandwidth);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetAgc(SetAgcCommand command) {
        _isEnabledAgc = command.IsEnabled;
        _agcAttackTime = command.AttackTime;
        _agcDecayTime = command.DecayTime;
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetAgc(GetAgcCommand command) {
        var response = new GetAgcCommandResult((byte)(_isEnabledAgc ? 0x01 : 0x00), _agcAttackTime, _agcDecayTime);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetAttenuators(SetAttenuatorsCommand command) {
        _attenuators = new ValueTuple<float, float>(command.RfAttenuatorValue, command.IfAttenuatorValue);
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetAttenuators(GetAttenuatorsCommand command) {
        var response = new GetAttenuatorsCommandResult(
            (byte)(_attenuators.RfAttenuator * 4), (byte)(_attenuators.IfAttenuator * 4));
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetDDCSetup(SetDDCSetupCommand command) {
        _ddcSetup = new ValueTuple<IQTDOAModeId, int, int, int>(
            command.ModeId, command.WaitPulses, command.WaitTime, command.FrameTime);
        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetStreamingState(GetStreamingStateCommand command) {
        var response = new GetStreamingStateCommandResult(_streamingState.State);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetStreamingState(SetStreamingStateCommand command) {
        _streamingState = (command.State, command.CaptureMode);
        if (_streamingState.State == StreamingState.Start) {
            _ = Task.Run(async () => await _pdwStreamingService.StartStreaming(_ip));
        } else {
            _ = Task.Run(async () => await _pdwStreamingService.StopStreaming());
        }

        Log(command, command);
        return TransformResponse(command);
    }

    public ReadOnlyMemory<byte> GetDDCSetup(GetDDCSetupCommand command) {
        var response = new GetDDCSetupCommandResult(_ddcSetup.Mode, _ddcSetup.PulseCounter, _ddcSetup.PulseWait,
            _ddcSetup.RecordTime);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> GetDDCState(GetDDCStateCommand command) {
        var response = new GetDDCStateCommandResult(_ddcState.Shift, _ddcState.Rate, _ddcState.State,
            (IQPacketFormat)_ddcState.Format);
        Log(command, response);
        return TransformResponse(response);
    }

    public ReadOnlyMemory<byte> SetScanList(SetScanListCommand command, ReadOnlyMemory<byte> itemsBytes) {
        if (command.Count > 0) {
            var requestSize = Unsafe.SizeOf<SetScanListCommand>();
            var dataItemSize = Unsafe.SizeOf<ScanListItem>();
            var scanListItems = new ScanListItem[command.Count];
            for (int i = 0; i < command.Count; i++) {
                var startIndex = requestSize + dataItemSize * i;
                scanListItems[i] = MemoryMarshal.AsRef<ScanListItem>(itemsBytes.Span[startIndex..]);
            }

            _scanListItems = scanListItems;
        }

        var count = _scanListItems.Length;
        var response = new SetScanListCommandResult(NetSdrChannel.Channel0, (ushort)count);
        Log(command, response, _scanListItems);
        return TransformResponse(response, _scanListItems);
    }

    public ReadOnlyMemory<byte> GetScanList(GetScanListCommand command) {
        var response = new GetScanListCommandResult(NetSdrChannel.Channel0, (ushort)_scanListItems.Length);
        Log(command, response, _scanListItems);
        return TransformResponse(response, _scanListItems);
    }

    private void Log(INetSdrCommand request, INetSdrCommand response, object? data = null,
        [CallerMemberName] string method = "") {
        _logger.LogInformation("{Method} Request {Command}", method, request.Serialize());
        _logger.LogInformation("{Method} Response {Command}", method, response.Serialize());
        if (data != null) {
            _logger.LogInformation("{Method} Data {@Data}", method, data);
        }
    }

    private ReadOnlyMemory<byte> TransformResponse<T>(T response) where T : struct, INetSdrCommand {
        return TransformResponse<T, byte>(response, []);
    }

    private ReadOnlyMemory<byte> TransformResponse<T, TData>(T response, TData[] data)
        where T : struct, INetSdrCommand
        where TData : struct {

        var declaredFullSize = (int)response.Header.FullSize;

        using var memory = MemoryPool<byte>.Shared.Rent(declaredFullSize > 0 ? declaredFullSize : ushort.MaxValue);

        MemoryMarshal.Write(memory.Memory.Span, in response);

        var actualStructSize = Unsafe.SizeOf<T>();

        if (data.Length > 0) {
            var dataItemSize = Unsafe.SizeOf<TData>();
            for (int i = 0; i < data.Length; i++) {
                var startIndex = actualStructSize + dataItemSize * i;
                MemoryMarshal.Write(memory.Memory.Span[startIndex..], in data[i]);
            }
        }

        if (declaredFullSize > 0) {

            if (actualStructSize < declaredFullSize) {
                memory.Memory.Span[actualStructSize..declaredFullSize].Clear();
            }
            return memory.Memory.Slice(0, declaredFullSize);
        }

        var totalSize = actualStructSize + (data.Length * Unsafe.SizeOf<TData>());
        return memory.Memory.Slice(0, totalSize);
    }

    public ReadOnlyMemory<byte> GetTime(GetTimeCommand command) {
        var seconds = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = new GetTimeCommandResult(seconds, 0);
        Log(command, response);
        return TransformResponse(response);
    }
}
