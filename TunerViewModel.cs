using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Mvvm;
using Esri.ArcGISRuntime.Geometry;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Client.Tools;
using Infozahyst.RSAAS.Client.Tools.Dispatcher;
using Infozahyst.RSAAS.Client.ViewModels.Interfaces;
using Infozahyst.RSAAS.Client.ViewModels.Messages;
using Infozahyst.RSAAS.Client.ViewModels.Scenarios;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using Polly.Retry;
using SciChart.Data.Model;
using UnitsNet;
using UnitsNet.Units;
using IMessenger = CommunityToolkit.Mvvm.Messaging.IMessenger;
using IsFadingSeriesEnabledChangedMessage =
    Infozahyst.RSAAS.Client.ViewModels.Messages.IsFadingSeriesEnabledChangedMessage;

namespace Infozahyst.RSAAS.Client.ViewModels;

public class TunerViewModel : BaseViewModel, ITunerViewModel
{
    private readonly ILogger<TunerViewModel> _logger;
    private readonly MessageLogger _messageLogger;
    private readonly GeneralSettings _receiverSettings;
    private readonly IMessenger _messenger;
    private readonly IReceiverMessagingService _receiverMessagingService;
    private readonly IAntennaRotatorMessagingService _antennaRotatorMessagingService;
    private readonly IDataStreamingMessagingService _dataStreamingMessagingService;
    private readonly IManualPositionMessagingService _manualPositionMessagingService;
    private readonly SpectrumSettings _spectrumSettings;
    private readonly ISpectrumReceivingClient _spectrumReceivingClient;
    private readonly IThresholdReceivingClient _thresholdReceivingClient;
    private readonly IPersistenceSpectrumReceivingClient _persistenceSpectrumReceivingClient;
    private readonly IPdwReceivingClient _pdwReceivingClient;
    private readonly ISystemControlMessagingService _systemControlMessagingService;
    private readonly IOptions<StreamingSettings> _streamingOptions;
    private readonly IDispatcherWrapper _dispatcherWrapper;
    private readonly SemaphoreSlim _setFrequencySemaphore = new(1, 1);
    private readonly IReadOnlyPolicyRegistry<string> _policyRegistry;

    private double _frequency;
    private SpectrumAveragingMode _spectrumAveragingMode;
    private byte _fftPower = 10;
    private int _averagingTime;

    private readonly Dictionary<byte, double> IQBandwidthDictionary400MHz = new() {
        { 4, 6.25 },
        { 3, 12.5 },
        { 2, 25 },
        { 1, 50 },
        { 0, 100 }
    };

    private readonly Dictionary<byte, double> IQBandwidthDictionary100MHz = new() {
        { 6, 1.5625 },
        { 5, 3.125 },
        { 4, 6.25 },
        { 3, 12.5 },
        { 2, 25 },
        { 1, 50 },
        { 0, 100 }
    };

    private readonly Dictionary<byte, double> IQBandwidthDictionary100MHzTDOAMode = new() {
        { 6, 1.5625 }, { 5, 3.125 }, { 4, 6.25 }, { 3, 12.5 }
    };

    private Dictionary<byte, double> _iQBandwidthDictionary;
    private IQRecordingMode _iQRecordingMode;

    private double _bandwidth;
    private byte _iqBandwidthKey;
    private bool _isIQRecordActive;
    private bool _isConnected;
    private double _iqFrequency;
    private double _iqFrequencyMin;
    private double _iqFrequencyMax;
    private double _iqFrequencyOffset;
    private double _frequencyMin;

    private ObservableCollection<ScenarioViewModel> _scenarios = new();
    private ScenarioViewModel? _selectedScenario;
    private Guid? _selectedScenarioId;
    private bool _isScenarioActive;
    private ScenarioDto? _currentRemoteScenario;

    private ScanRange _scanRange;
    private bool _isElintModeEnabled = true;
    private bool _isEsmModeEnabled;
    private bool _isEsmScenarioModeEnabled;
    private bool _isStreamingEnabled;
    private double _antennaRotatorAngle;
    private double _referenceLevel;
    private double _range;
    private Position? _positionerInfo;
    private double? _positionerAzimuth;
    private double? _positionerAltitudeInMeter;
    private DateTime? _positionerDateTime;
    private Position? _receiverInfo;
    private double? _receiverAltitudeInMeter;
    private DateTime? _receiverDateTime;

    private bool _isPanoramaSpectrumEnabled;
    private bool _isPanoramaWaterfallEnabled;
    private bool _isPanoramaZoomEnabled;
    private double _zoomFrequency;

    private string _antennaRotatorAngleFormat = null!;
    private bool _canBreakRotation;
    private Dictionary<int, string> _averagingTimes;

    private readonly Dictionary<SpectrumAveragingMode, Dictionary<int, string>> _spectrumModeDisplayValuesDictionary =
        new();

    private DoubleRange _zoomFrequencyLimit;
    private ReceiverMode? _receiverMode;
    private bool _isRunning;
    private double _ifAtt;
    private double _rfAtt;
    private bool _isAgcEnabled;
    private ushort _decayTime;
    private ushort _attackTime;

    private DataStreamingConnection? _spectrumConnection;
    private DataStreamingConnection? _thresholdConnection;
    private DataStreamingConnection? _persistenceSpectrumConnection;
    private DataStreamingConnection? _pdwConnection;
    private double _azimuth0;
    private double _antennaDirection;
    private bool _isAngleOperationError;
    private double _lastSavedAntennaAngle;
    private double _lastSavedAzimuth0;
    private double _positionManualX;
    private double _positionManualY;
    private DispatcherTimer? _iqRecordingTimer;
    private IQFileSize _iqFileSize;
    private TimeSpan _iqRecordingDuration;
    private bool _iqRecordWithTimestamp;
    private bool _isServerConnected;
    private bool _isAntennaRotatorPresent;
    private bool _isAntennaRotationInProcess;
    private bool _receiverIsConnected;
    private bool _isBacklightEnabled;
    private bool _isBacklightOk = true;
    private bool _isAntennaRotatorConnected;
    private bool _isPositionerPresent;

    private uint? _localStationId;
    private ReceiverMode? _setReceiverMode;
    private bool _isGettingParamsFromReceiver;
    private bool _isRotatorParked;
    private bool _isReceiverConnected;
    private bool _isReceiverAngleDifferentFromReal;
    private bool _isRotatorAngleDifferentFromRequired;
    private bool _isEnabledBreakRotation;
    private InstantViewBandwidth _instantViewBandwidth;
    private int _pulseCounter;
    private int _pulseWait;
    private int _recordTime;
    private bool _isIqTimestampEnabled;
    private Dictionary<IQRecordingMode, string> _iQRecordingModeDictionary;
    private System.Timers.Timer _iqParamsChangedTimer = null!;
    private double _azimuthManual;
    private bool _isSpectrumFadingEnabled;
    private int _fadingSeriesCount;
    private bool _isOpacityDynamic;
    private int _fadingSeriesOpacityPercent = 100;
    private bool _isFadingColorManual;
    private Color _fadingManualColor = Colors.Cyan;
    private PositionSource _currentPositionMode = PositionSource.Manual;
    private bool _isArchontTType;
    private PdwRecordingMode _pdwRecordingMode;
    private bool _isPdwRecordActive;
    private PdwFileSize _pdwFileSize;
    private bool _isUpdatingFromServer;
    private bool _allowRoundOutOfRangeIQFrequency = true;
    private bool _isGnssRecordActive;
    private bool _isControlEnabled;
    private bool _isTunerSetupInProgress;
    private CommandState _decayTimeState;
    private CommandState _attackTimeState;

    public bool IsAntennaRotatorConnected {
        get => _isAntennaRotatorConnected;
        set {
            if (Set(ref _isAntennaRotatorConnected, value)) {
                OnPropertyChanged(nameof(IsAntennaRotatorAvailable));
            }
        }
    }

    public bool IsAntennaRotatorPresent {
        get => _isAntennaRotatorPresent;
        set {
            if (Set(ref _isAntennaRotatorPresent, value)) {
                OnPropertyChanged(nameof(IsAntennaRotatorAvailable));
            }
        }
    }

    public bool IsPositionerPresent {
        get => _isPositionerPresent;
        set {
            if (Set(ref _isPositionerPresent, value)) {
                OnPropertyChanged(nameof(IsPositionerAvailable));
            }
        }
    }

    public bool IsPositionerAvailable => IsPositionerPresent && !IsIQRecordActive;

    public bool IsAntennaRotationInProcess {
        get => _isAntennaRotationInProcess;
        set {
            if (Set(ref _isAntennaRotationInProcess, value)) {
                OnPropertyChanged(nameof(IsAntennaRotatorAvailable));
            }
        }
    }

    public bool IsConnected {
        get => _isConnected;
        set => Set(ref _isConnected, value);
    }

    public double Bandwidth {
        get => _bandwidth;
        set {
            _bandwidth = value;
            FrequencyMin = value / 2;
            OnPropertyChanged();
        }
    }

    public double Frequency {
        get => Math.Truncate(_frequency);
        set {
            if (value < FrequencyMin) {
                value = FrequencyMin;
            }

            Set(ref _frequency, value);
        }
    }

    public double FrequencyMin {
        get => _frequencyMin;
        set => Set(ref _frequencyMin, value);
    }

    public ScanRange ScanRange {
        get => _scanRange;
        set => Set(ref _scanRange, value);
    }

    public ObservableCollection<ScenarioViewModel> Scenarios {
        get => _scenarios;
        set => Set(ref _scenarios, value);
    }

    public ScenarioViewModel? SelectedScenario {
        get => _selectedScenario;
        set {
            if (Set(ref _selectedScenario, value)) {
                if (value != null) {
                    _currentRemoteScenario = null;
                }

                OnPropertyChanged(nameof(IsScenarioButtonActive));
            }
        }
    }

    public bool IsScenarioButtonActive => SelectedScenario != null || IsScenarioActive;

    public static Dictionary<SpectrumAveragingMode, string> SpectrumSetupModeDictionary =>
        new() {
            { SpectrumAveragingMode.Average, "Average" },
            { SpectrumAveragingMode.PeakHold, "Peak Hold" },
            { SpectrumAveragingMode.MinHold, "Min Hold" },
            { SpectrumAveragingMode.Normal, "Normal" }
        };

    public SpectrumAveragingMode SpectrumAveragingMode {
        get => _spectrumAveragingMode;
        set => Set(ref _spectrumAveragingMode, value);
    }

    public Dictionary<IQRecordingMode, string> IQRecordingModeDictionary {
        get => _iQRecordingModeDictionary;
        set => Set(ref _iQRecordingModeDictionary, value);
    }

    public static Dictionary<IQRecordingMode, string> IQRecordingModeDictionary100MHz =>
        new() {
            { Common.Enums.IQRecordingMode.Continuous, "Continuous" },
            { Common.Enums.IQRecordingMode.TriggerOnce, "Trigger Once" },
            { Common.Enums.IQRecordingMode.TriggerCycle, "Trigger Cycle" }
        };

    public static Dictionary<IQRecordingMode, string> IQRecordingModeDictionary400MHz =>
        new() { { Common.Enums.IQRecordingMode.Continuous, "Continuous" }, };

    public IQRecordingMode IQRecordingMode {
        get => _iQRecordingMode;
        set {
            if (Set(ref _iQRecordingMode, value)) {
                OnPropertyChanged(nameof(IsTdoaIQModeEnabled));
                OnPropertyChanged(nameof(IsContinuousIqModeEnabled));
                SetIQBandwidthDictionary();
                ChangedIQParams();
            }
        }
    }

    public byte FftPower {
        get => _fftPower;
        set => Set(ref _fftPower, value);
    }

    public int AveragingTime {
        get => _averagingTime;
        set => Set(ref _averagingTime, value);
    }

    public int PulseCounter {
        get => _pulseCounter;
        set {
            if (Set(ref _pulseCounter, value)) {
                ChangedIQParams();
            }
        }
    }

    public int PulseWait {
        get => _pulseWait;
        set {
            if (Set(ref _pulseWait, value)) {
                ChangedIQParams();
            }
        }
    }

    public int RecordTime {
        get => _recordTime;
        set {
            if (Set(ref _recordTime, value)) {
                ChangedIQParams();
            }
        }
    }

    public bool IsIqTimestampEnabled {
        get => _isIqTimestampEnabled;
        set {
            if (Set(ref _isIqTimestampEnabled, value)) {
                ChangedIQParams();
            }
        }
    }

    public bool IsContinuousIqModeEnabled => IQRecordingMode == IQRecordingMode.Continuous;

    public bool IsTdoaIQModeEnabled => IQRecordingMode != IQRecordingMode.Continuous;

    public bool IsAveragingTimeVisible => SpectrumAveragingMode != SpectrumAveragingMode.Normal;

    public Dictionary<int, string> AveragingTimes {
        get => _averagingTimes;
        set => Set(ref _averagingTimes, value);
    }

    public double IQFrequency {
        get => Math.Truncate(_iqFrequency);
        set {
            if (!Set(ref _iqFrequency, value)) {
                return;
            }

            IQFrequencyOffset = _iqFrequency - _frequency;
            _messenger.Send(
                new IQBandwidthChangedMessage(new IQFrequencyBandwidthData(IQFrequency,
                    IQBandwidthDictionary[IQBandwidthKey])));
        }
    }

    public bool AllowRoundOutOfRangeIQFrequency {
        get => _allowRoundOutOfRangeIQFrequency;
        set => Set(ref _allowRoundOutOfRangeIQFrequency, value);
    }

    public DoubleRange ZoomFrequencyLimit {
        get => _zoomFrequencyLimit;
        set => Set(ref _zoomFrequencyLimit, value);
    }

    public double ZoomFrequency {
        get => _zoomFrequency;
        set {
            if (!Set(ref _zoomFrequency, value)) {
                return;
            }

            _messenger.Send(new ZoomFrequencyChangedMessage(_zoomFrequency));
        }
    }

    public double IQFrequencyOffset {
        get => _iqFrequencyOffset;
        set {
            if (Set(ref _iqFrequencyOffset, value)) {
                ChangedIQParams();
            }
        }
    }

    public double IQFrequencyMin {
        get => _iqFrequencyMin;
        set => Set(ref _iqFrequencyMin, value);
    }

    public double IQFrequencyMax {
        get => _iqFrequencyMax;
        set => Set(ref _iqFrequencyMax, value);
    }

    public byte IQBandwidthKey {
        get => _iqBandwidthKey;
        set {
            if (!Set(ref _iqBandwidthKey, value)) {
                return;
            }

            UpdateIQFrequencyMinMax();
            _messenger.Send(
                new IQBandwidthChangedMessage(new IQFrequencyBandwidthData(IQFrequency, IQBandwidthDictionary[value])));
            ChangedIQParams();
        }
    }

    public Dictionary<IQFileSize, string> IQFileSizeDictionary { get; set; }

    public IQFileSize IQFileSize {
        get => _iqFileSize;
        set {
            Set(ref _iqFileSize, value);
        }
    }

    public TimeSpan IQRecordingDuration {
        get => _iqRecordingDuration;
        set {
            Set(ref _iqRecordingDuration, value);
        }
    }

    public bool IQRecordWithTimestamp {
        get => _iqRecordWithTimestamp;
        set {
            Set(ref _iqRecordWithTimestamp, value);
            ChangedIQParams();
        }
    }

    public bool IsIQRecordActive {
        get => _isIQRecordActive;
        set {
            if (Set(ref _isIQRecordActive, value)) {
                OnPropertyChanged(nameof(IsPositionerAvailable));
            }
        }
    }

    public bool IsScenarioActive {
        get => _isScenarioActive;
        set {
            if (Set(ref _isScenarioActive, value)) {
                OnPropertyChanged(nameof(IsPositionerAvailable));
                OnPropertyChanged(nameof(IsScenarioButtonActive));
            }
        }
    }

    public bool IsArchontTType {
        get => _isArchontTType;
        set {
            if (Set(ref _isArchontTType, value)) {
                OnPropertyChanged(nameof(FilteredPositionSources));
            }
        }
    }

    public bool IsElintModeEnabled {
        get => _isElintModeEnabled;
        set => Set(ref _isElintModeEnabled, value);
    }

    public bool IsEsmModeEnabled {
        get => _isEsmModeEnabled;
        set => Set(ref _isEsmModeEnabled, value);
    }

    public bool IsEsmScenarioModeEnabled {
        get => _isEsmScenarioModeEnabled;
        set => Set(ref _isEsmScenarioModeEnabled, value);
    }

    public bool IsStreamingEnabled {
        get => _isStreamingEnabled;
        set => Set(ref _isStreamingEnabled, value);
    }

    public bool IsPanoramaSpectrumEnabled {
        get => _isPanoramaSpectrumEnabled;
        set {
            if (Set(ref _isPanoramaSpectrumEnabled, value)) {
                _messenger.Send(new PanoramaSpectrumChangedMessage(value));
            }
        }
    }

    public bool IsPanoramaWaterfallEnabled {
        get => _isPanoramaWaterfallEnabled;
        set {
            if (Set(ref _isPanoramaWaterfallEnabled, value)) {
                _messenger.Send(new PanoramaWaterfallChangedMessage(value));
            }
        }
    }

    public bool IsPanoramaZoomEnabled {
        get => _isPanoramaZoomEnabled;
        set {
            if (Set(ref _isPanoramaZoomEnabled, value)) {
                _messenger.Send(new PanoramaZoomChangedMessage(value));
            }
        }
    }

    public double ReferenceLevel {
        get => _referenceLevel;
        set {
            value = Math.Ceiling(value / 0.5) * 0.5;
            value = Math.Clamp(value, _spectrumSettings.MaxPowerRange.Min, _spectrumSettings.MaxPowerRange.Max);
            Set(ref _referenceLevel, value);
            Range = CheckRange(Range);
        }
    }

    public double Range {
        get => _range;
        set {
            value = Math.Ceiling(value / 0.5) * 0.5;
            value = Math.Clamp(value, _spectrumSettings.ZoomRange.Min, _spectrumSettings.ZoomRange.Max);
            value = CheckRange(value);
            Set(ref _range, value);
        }
    }

    public Dictionary<byte, double> FftPowerFrequencyDictionary {
        get {
            const int minFftPower = 7;
            const int fftPowerCount = 4;
            if (InstantViewBandwidth == InstantViewBandwidth.Bandwidth100MHz) {
                return Enumerable.Range(minFftPower, fftPowerCount)
                    .OrderByDescending(i => i)
                    .ToDictionary(i => (byte)i, i => _receiverSettings.MaxBandwidth / 1000d / Math.Pow(2, i) / 4);
            }

            return Enumerable.Range(minFftPower, fftPowerCount)
                .OrderByDescending(i => i)
                .ToDictionary(i => (byte)i, i => _receiverSettings.MaxBandwidth / 1000d / Math.Pow(2, i));
        }
    }

    public Dictionary<byte, double> IQBandwidthDictionary {
        get => _iQBandwidthDictionary;
        set => Set(ref _iQBandwidthDictionary, value);
    }

    public double FrequencyIncrement => Bandwidth / 2;

    public double AntennaRotatorAngle {
        get => _antennaRotatorAngle;
        set => Set(ref _antennaRotatorAngle, value);
    }

    public string AntennaRotatorAngleFormat {
        get => _antennaRotatorAngleFormat;
        set => Set(ref _antennaRotatorAngleFormat, value);
    }

    public bool IsAntennaRotatorAvailable =>
        !IsAntennaRotationInProcess && IsAntennaRotatorPresent && IsAntennaRotatorConnected;

    public bool CanBreakRotation {
        get => _canBreakRotation;
        set => Set(ref _canBreakRotation, value);
    }

    public bool IsEnabledBreakRotation {
        get => _isEnabledBreakRotation;
        set => Set(ref _isEnabledBreakRotation, value);
    }

    public Position? PositionerInfo {
        get => _positionerInfo;
        set {
            if (Set(ref _positionerInfo, value)) {
                OnPropertyChanged(nameof(CurrentAutoPosition));
            }
        }
    }

    public Position? ReceiverInfo {
        get => _receiverInfo;
        set {
            if (Set(ref _receiverInfo, value)) {
                OnPropertyChanged(nameof(CurrentAutoPosition));
            }
        }
    }

    public Position? CurrentAutoPosition {
        get {
            if (CurrentPositionMode == PositionSource.Receiver) {
                return ReceiverInfo;
            }

            if (CurrentPositionMode == PositionSource.Positioner) {
                return PositionerInfo;
            }

            return null;
        }
    }

    public double? PositionerAltitudeInMeter {
        get => _positionerAltitudeInMeter;
        set {
            if (Set(ref _positionerAltitudeInMeter, value)) {
                OnPropertyChanged(nameof(CurrentAutoAltitude));
            }
        }
    }

    public double? ReceiverAltitudeInMeter {
        get => _receiverAltitudeInMeter;
        set {
            if (Set(ref _receiverAltitudeInMeter, value)) {
                OnPropertyChanged(nameof(CurrentAutoAltitude));
            }
        }
    }

    public double? CurrentAutoAltitude {
        get {
            if (CurrentPositionMode == PositionSource.Receiver) {
                return ReceiverAltitudeInMeter;
            }

            if (CurrentPositionMode == PositionSource.Positioner) {
                return PositionerAltitudeInMeter;
            }

            return null;
        }
    }

    public double? PositionerAzimuth {
        get => _positionerAzimuth;
        set => Set(ref _positionerAzimuth, value);
    }

    public double AzimuthManual {
        get => _azimuthManual;
        set => Set(ref _azimuthManual, value);
    }

    public DateTime? PositionerDateTime {
        get => _positionerDateTime;
        set {
            if (Set(ref _positionerDateTime, value)) {
                OnPropertyChanged(nameof(CurrentAutoDateTime));
            }
        }
    }

    public DateTime? ReceiverDateTime {
        get => _receiverDateTime;
        set {
            if (Set(ref _receiverDateTime, value)) {
                OnPropertyChanged(nameof(CurrentAutoDateTime));
            }
        }
    }

    public DateTime? CurrentAutoDateTime {
        get {
            if (CurrentPositionMode == PositionSource.Receiver) {
                return ReceiverDateTime;
            }

            if (CurrentPositionMode == PositionSource.Positioner) {
                return PositionerDateTime;
            }

            return null;
        }
    }

    public ReceiverMode? ReceiverMode {
        get => _receiverMode;
        set {
            if (!Set(ref _receiverMode, value)) {
                return;
            }

            _messenger.Send(new ReceiverModeChangedMessage(value));
        }
    }

    public bool IsRunning {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    public bool IsServerConnected {
        get => _isServerConnected;
        set {
            if (Set(ref _isServerConnected, value)) {
                OnPropertyChanged(nameof(IsUiAvailable));
            }
        }
    }

    public bool IsReceiverConnected {
        get => _isReceiverConnected;
        set {
            if (Set(ref _isReceiverConnected, value)) {
                OnPropertyChanged(nameof(IsUiAvailable));
            }
        }
    }

    public bool IsUiAvailable => IsServerConnected && IsReceiverConnected && IsControlEnabled;

    public double Azimuth0 {
        get => _azimuth0;
        set => Set(ref _azimuth0, value);
    }

    public double AntennaDirection {
        get => _antennaDirection;
        set => Set(ref _antennaDirection, value);
    }

    public bool IsAngleOperationError {
        get => _isAngleOperationError;
        set => Set(ref _isAngleOperationError, value);
    }

    public bool IsReceiverAngleDifferentFromReal {
        get => _isReceiverAngleDifferentFromReal;
        set => Set(ref _isReceiverAngleDifferentFromReal, value);
    }

    public bool IsRotatorAngleDifferentFromRequired {
        get => _isRotatorAngleDifferentFromRequired;
        set => Set(ref _isRotatorAngleDifferentFromRequired, value);
    }

    public bool IsRotatorParked {
        get => _isRotatorParked;
        set => Set(ref _isRotatorParked, value);
    }

    public PositionSource CurrentPositionMode {
        get => _currentPositionMode;
        set {
            if (Set(ref _currentPositionMode, value)) {
                OnPropertyChanged(nameof(IsTunerPositionManual));
                OnPropertyChanged(nameof(IsTunerPositionReceiver));
                OnPropertyChanged(nameof(CurrentAutoAltitude));
                OnPropertyChanged(nameof(CurrentAutoPosition));
                OnPropertyChanged(nameof(CurrentAutoDateTime));
            }
        }
    }

    public bool IsTunerPositionManual => CurrentPositionMode == PositionSource.Manual;

    public bool IsTunerPositionReceiver => CurrentPositionMode == PositionSource.Receiver;

    public double PositionManualX {
        get => _positionManualX;
        set => Set(ref _positionManualX, value);
    }

    public double PositionManualY {
        get => _positionManualY;
        set => Set(ref _positionManualY, value);
    }

    public bool IsBacklightEnabled {
        get => _isBacklightEnabled;
        set => Set(ref _isBacklightEnabled, value);
    }

    public bool IsBacklightOk {
        get => _isBacklightOk;
        set => Set(ref _isBacklightOk, value);
    }

    public InstantViewBandwidth InstantViewBandwidth {
        get => _instantViewBandwidth;
        set {
            if (Set(ref _instantViewBandwidth, value)) {
                OnPropertyChanged(nameof(FftPowerFrequencyDictionary));
            }
        }
    }

    public Dictionary<InstantViewBandwidth, double> BandwidthDictionary {
        get {
            return new Dictionary<InstantViewBandwidth, double> {
                { InstantViewBandwidth.Bandwidth100MHz, 100 }, { InstantViewBandwidth.Bandwidth400MHz, 400 }
            };
        }
    }

    public double IfAtt {
        get => _ifAtt;
        set {
            value = Math.Ceiling(value / 0.5) * 0.5;
            Set(ref _ifAtt, value);
        }
    }

    public double RfAtt {
        get => _rfAtt;
        set {
            value = Math.Ceiling(value / 0.5) * 0.5;
            Set(ref _rfAtt, value);
        }
    }

    public bool IsAgcEnabled {
        get => _isAgcEnabled;
        set => Set(ref _isAgcEnabled, value);
    }

    public ushort AttackTime {
        get => _attackTime;
        set => Set(ref _attackTime, value);
    }

    public ushort DecayTime {
        get => _decayTime;
        set => Set(ref _decayTime, value);
    }

    #region SpectrumFading

    public bool IsSpectrumFadingEnabled {
        get => _isSpectrumFadingEnabled;
        set {
            if (Set(ref _isSpectrumFadingEnabled, value)) {
                _messenger.Send(new IsFadingSeriesEnabledChangedMessage(value));
            }
        }
    }

    public int MaxFadingSeriesCount { get; init; }

    public int FadingSeriesCount {
        get => _fadingSeriesCount;
        set {
            if (Set(ref _fadingSeriesCount, value)) {
                _messenger.Send(new FadingSeriesCountChangedMessage(value));
            }
        }
    }

    public bool IsOpacityDynamic {
        get => _isOpacityDynamic;
        set {
            if (Set(ref _isOpacityDynamic, value)) {
                _messenger.Send(new FadingSeriesOpacityParametersChangedMessage((value, FadingSeriesOpacityPercent)));
            }
        }
    }

    public int FadingSeriesOpacityPercent {
        get => _fadingSeriesOpacityPercent;
        set {
            if (Set(ref _fadingSeriesOpacityPercent, value)) {
                _messenger.Send(
                    new FadingSeriesOpacityParametersChangedMessage((IsOpacityDynamic, FadingSeriesOpacityPercent)));
            }
        }
    }

    public bool IsFadingColorManual {
        get => _isFadingColorManual;
        set {
            if (Set(ref _isFadingColorManual, value)) {
                _messenger.Send(new FadingSeriesColorParametersChangedMessage((value, FadingManualColor)));
            }
        }
    }

    public Color FadingManualColor {
        get => _fadingManualColor;
        set {
            if (Set(ref _fadingManualColor, value)) {
                _messenger.Send(
                    new FadingSeriesColorParametersChangedMessage((IsFadingColorManual, FadingManualColor)));
            }
        }
    }

    #endregion

    public IEnumerable<PositionSource> FilteredPositionSources {
        get {
            return Enum.GetValues(typeof(PositionSource))
                .Cast<PositionSource>()
                .Where(pos => !ShouldDisable(pos));
        }
    }

    public PdwRecordingMode PdwRecordingMode {
        get => _pdwRecordingMode;
        set => Set(ref _pdwRecordingMode, value);
    }

    public bool IsPdwRecordActive {
        get => _isPdwRecordActive;
        set => Set(ref _isPdwRecordActive, value);
    }

    public PdwFileSize PdwFileSize {
        get => _pdwFileSize;
        set { Set(ref _pdwFileSize, value); }
    }

    public bool IsGnssRecordActive {
        get => _isGnssRecordActive;
        set => Set(ref _isGnssRecordActive, value);
    }

    public bool IsControlEnabled {
        get => _isControlEnabled;
        set {
            if (Set(ref _isControlEnabled, value)) {
                OnPropertyChanged(nameof(IsUiAvailable));
            }
        }
    }

    public bool IsTunerSetupInProgress {
        get => _isTunerSetupInProgress;
        set => Set(ref _isTunerSetupInProgress, value);
    }

    public CommandState AttackTimeState {
        get => _attackTimeState;
        set => Set(ref _attackTimeState, value);
    }

    public CommandState DecayTimeState {
        get => _decayTimeState;
        set => Set(ref _decayTimeState, value);
    }
    
    public Dictionary<PdwFileSize, string> PdwFileSizeDictionary { get; set; }

    public float MaxIfAttenuation => _receiverSettings.MaxIfAttenuation;

    public float MaxRfAttenuation => _receiverSettings.MaxRfAttenuation;

    public ushort MaxDecayTime => _receiverSettings.MaxDecayTime;

    public ushort MaxAttackTime => _receiverSettings.MaxAttackTime;

    public AsyncCommandWithState<double> SetFrequencyCommand { get; }
    public AsyncCommandWithState SetScanRangeCommand { get; }
    public AsyncCommandWithState SetupTunerSpectrumCommand { get; }
    public AsyncCommandWithState RunCommand { get; }
    public AsyncCommandWithState StopCommand { get; }
    public AsyncCommandWithState StartStopSpectrumCommand { get; }
    public AsyncCommandWithState StartStopIQCommand { get; }
    public AsyncCommand SetPanoramaSpectrumCommand { get; }
    public AsyncCommandWithState SetReceiverModeCommand { get; }
    public AsyncCommand SetAzimuthManualCommand { get; }
    public AsyncCommand SetPositionCommand { get; }
    public AsyncCommand GetBacklightCommand { get; }
    public AsyncCommand SetBacklightCommand { get; }
    public AsyncCommand BreakRotationCommand { get; }
    public AsyncCommand ParkRotatorCommand { get; }
    public AsyncCommand SetAntennaDirectionCommand { get; }
    public AsyncCommand SetAntennaRotatorAngleCommand { get; }
    public AsyncCommand SetAzimuth0Command { get; }
    public AsyncCommand StartStopScenarioCommand { get; }
    public AsyncCommand SetSpectrumAveragingModeCommand { get; }
    public AsyncCommandWithState SetInstantViewBandwidthCommand { get; }
    public AsyncCommandWithState SetAttenuationModeCommand { get; }
    public AsyncCommandWithState SetAttenuationCommand { get; }
    public AsyncCommandWithState StartStopPdwRecordingCommand { get; }
    public AsyncCommandWithState StartStopGnssRecordingCommand { get; }

    public TunerViewModel(IOptions<GeneralSettings> generalSettings, ILogger<TunerViewModel> logger,
        MessageLogger messageLogger, IMessenger messenger, IOptions<SpectrumSettings> spectrumOptions,
        IPositionerMessagingService positionerMessagingService, IReceiverMessagingService receiverMessagingService,
        ISpectrumReceivingClient spectrumReceivingClient, IThresholdReceivingClient thresholdReceivingClient,
        IAntennaRotatorMessagingService antennaRotatorMessagingService,
        IDataStreamingMessagingService dataStreamingMessagingService,
        IManualPositionMessagingService manualPositionMessagingService,
        IPersistenceSpectrumReceivingClient persistenceSpectrumReceivingClient, IPdwReceivingClient pdwReceivingClient,
        ISystemControlMessagingService systemControlMessagingService, IReadOnlyPolicyRegistry<string> policyRegistry,
    IOptions<IQRecordingSettings> iqRecordingOptions, IOptions<StreamingSettings> streamingOptions,
        IDispatcherWrapper? dispatcherWrapper = null) {
        ArgumentNullException.ThrowIfNull(generalSettings);

        _iqParamsChangedTimer = new System.Timers.Timer(TimeSpan.FromSeconds(1));
        _iqParamsChangedTimer.Elapsed += OnTimerChangedIQParams;

        _logger = logger;
        _messageLogger = messageLogger;
        _receiverSettings = generalSettings.Value;
        _messenger = messenger;
        _receiverMessagingService = receiverMessagingService;
        _spectrumSettings = spectrumOptions.Value;
        MaxFadingSeriesCount = _spectrumSettings.MaxFadingSeriesCount;
        FadingSeriesCount = _spectrumSettings.InitialFadingSeriesCount;
        _spectrumReceivingClient = spectrumReceivingClient;
        _thresholdReceivingClient = thresholdReceivingClient;
        _antennaRotatorMessagingService = antennaRotatorMessagingService;
        _persistenceSpectrumReceivingClient = persistenceSpectrumReceivingClient;
        _dataStreamingMessagingService = dataStreamingMessagingService;
        _pdwReceivingClient = pdwReceivingClient;
        _manualPositionMessagingService = manualPositionMessagingService;
        _systemControlMessagingService = systemControlMessagingService;
        _policyRegistry = policyRegistry;
        _streamingOptions = streamingOptions;
        _dispatcherWrapper = dispatcherWrapper ?? new DispatcherWrapper(Application.Current.Dispatcher);

        RunCommand = new AsyncCommandWithState(Run);
        StopCommand = new AsyncCommandWithState(Stop);
        SetFrequencyCommand = new AsyncCommandWithState<double>(SetFrequency);
        SetScanRangeCommand = new AsyncCommandWithState(SetScanRange);
        SetupTunerSpectrumCommand = new AsyncCommandWithState(SetupTunerSpectrum);
        StartStopSpectrumCommand = new AsyncCommandWithState(StartStopStreaming);
        StartStopIQCommand = new AsyncCommandWithState(StartStopIQRecording);
        SetAntennaRotatorAngleCommand = new AsyncCommand(SetAntennaRotatorAngle);
        BreakRotationCommand = new AsyncCommand(BreakRotation);
        SetPanoramaSpectrumCommand = new AsyncCommand(SetPanoramaSpectrum);
        SetReceiverModeCommand = new AsyncCommandWithState(SetReceiverMode);
        SetAntennaDirectionCommand = new AsyncCommand(SetAntennaDirection);
        ParkRotatorCommand = new AsyncCommand(ParkRotator);
        GetBacklightCommand = new AsyncCommand(GetBacklight);
        SetBacklightCommand = new AsyncCommand(SetBacklight);
        SetSpectrumAveragingModeCommand = new AsyncCommand(OnSpectrumAveragingModeChanged);
        SetAzimuth0Command = new AsyncCommand(SetAzimuth0);
        SetPositionCommand = new AsyncCommand(SetPosition);
        SetAzimuthManualCommand = new AsyncCommand(SetAzimuthManual);
        SetInstantViewBandwidthCommand =
            new AsyncCommandWithState(SetInstantViewBandwidth, () => !_isUpdatingFromServer);
        SetAttenuationCommand = new AsyncCommandWithState(SetAttenuation);
        SetAttenuationModeCommand = new AsyncCommandWithState(SetAttenuationMode);
        StartStopScenarioCommand = new AsyncCommand(StartStopScenario);
        StartStopPdwRecordingCommand = new AsyncCommandWithState(StartStopPdwRecording);
        StartStopGnssRecordingCommand = new AsyncCommandWithState(StartStopGnssRecording);

        _iQBandwidthDictionary = IQBandwidthDictionary400MHz;
        _iQRecordingModeDictionary = IQRecordingModeDictionary400MHz;
        Bandwidth = UnitsNet.Frequency.FromHertz(_receiverSettings.Bandwidth).Megahertz;
        Frequency = FrequencyMin;
        IQBandwidthKey = 2;
        IQFrequency = FrequencyMin;
        ZoomFrequency = FrequencyMin;
        _scanRange = new ScanRange();
        _zoomFrequencyLimit = new DoubleRange();

        var settings = iqRecordingOptions.Value;
        PulseCounter = settings.PulseCounterDefault;
        PulseWait = settings.PulseWaitDefault;
        RecordTime = settings.RecordTimeDefault;

        SetupAveragingTimesDictionaries();
        _averagingTimes = _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode.Normal];
        _spectrumAveragingMode = SpectrumAveragingMode.Normal;

        AntennaRotatorAngleFormat = $"000.{new string('0', generalSettings.Value.AngleDecimalPlaces)}";

        IsPanoramaSpectrumEnabled = true;

#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
        _receiverMessagingService.ReceiverParametersChanged +=
            async (_, notification) => await OnReceiverParametersChanged(notification);
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
        IQFileSizeDictionary = new Dictionary<IQFileSize, string> {
            { IQFileSize.Unlimited, "Unlimited" },
            { IQFileSize.FourGB, "4GB" },
            { IQFileSize.EightGB, "8GB" },
            { IQFileSize.SixteenGB, "16GB" },
            { IQFileSize.ThirtyTwoGB, "32GB" },
            { IQFileSize.SixtyFourGB, "64GB" }
        };
        PdwFileSizeDictionary = new Dictionary<PdwFileSize, string> {
            { PdwFileSize.Unlimited, "Unlimited" },
            { PdwFileSize._100MB, "100MB" },
            { PdwFileSize._500MB, "500MB" },
            { PdwFileSize._1GB, "1GB" },
            { PdwFileSize._2GB, "2GB" },
            { PdwFileSize._4GB, "4GB" }
        };

        AttackTime = _receiverSettings.DefaultAttackTime;
        DecayTime = _receiverSettings.DefaultDecayTime;

        IQFileSize = IQFileSize.SixteenGB;
        PdwFileSize = PdwFileSize._1GB;
        _iqRecordingTimer =
            new DispatcherTimer(DispatcherPriority.Normal, _dispatcherWrapper.Dispatcher) {
                Interval = TimeSpan.FromSeconds(1)
            };
        _iqRecordingTimer.Tick += IQRecordingTimerTick;
        positionerMessagingService.PositionChanged += PositionChanged;
        _systemControlMessagingService.AnglesParametersChanged += OnAnglesDataReceived;
        _systemControlMessagingService.ReceiverAngleStateChanged += ReceiverAngleStateChanged;
        _systemControlMessagingService.RotatorParkedStateChanged += RotatorParkedStateChanged;
        _receiverMessagingService.IQRecordingStatusChanged += OnIQRecordingStatusChanged;
        _receiverMessagingService.PdwRecordingStatusChanged += OnPdwRecordingStatusChanged;
        _receiverMessagingService.GnssRecordingStatusChanged += OnGnssRecordingStatusChanged;
        RegisterMessages(messenger);
        InstantViewBandwidth = InstantViewBandwidth.Bandwidth400MHz;
        _ = Initialize(settings);
    }

    private bool ShouldDisable(PositionSource positionSource) {
        return positionSource == PositionSource.Positioner && IsArchontTType;
    }

    private async Task SetAzimuthManual() {
        await _manualPositionMessagingService.SetManualPositionState(CurrentPositionMode, PositionManualX,
            PositionManualY, AzimuthManual);
    }

    private Task Initialize(IQRecordingSettings settings) {
        IsIqTimestampEnabled = settings.IqTimestampDefault;
        return Task.CompletedTask;
    }

    private async Task SetInstantViewBandwidth() {
        var response = await _receiverMessagingService.SetInstantViewBandwidth(InstantViewBandwidth);
        await _dispatcherWrapper.InvokeAsync(() => {
            SetInstantViewBandwidthCommand.State = response.State;
            if (!response.IsSuccess) {
                SetInstantViewBandwidthCommand.State = CommandState.Error;
                if (response.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        response.ErrorMessage ?? "Error at setting bandwidth",
                        MessageLevel.Error);
                }
            } else {
                InstantViewBandwidth = response.Bandwidth;
                Bandwidth = BandwidthDictionary[InstantViewBandwidth];
                SetAveragingModeParameters();
            }
        });

        if (response.IsSuccess) {
            _messenger.Send(new BandwidthChangedMessage(Bandwidth));
            SetIQBandwidthDictionary();
        }
    }

    private void SetIQBandwidthDictionary() {
        _dispatcherWrapper.Invoke(() => {
            var previousKey = IQBandwidthKey;

            IQRecordingModeDictionary = InstantViewBandwidth switch {
                InstantViewBandwidth.Bandwidth100MHz => IQRecordingModeDictionary100MHz,
                InstantViewBandwidth.Bandwidth400MHz => IQRecordingModeDictionary400MHz,
                _ => throw new ArgumentOutOfRangeException()
            };

            if (IsTdoaIQModeEnabled) {
                IQBandwidthDictionary = IQBandwidthDictionary100MHzTDOAMode;
                IQBandwidthKey = Math.Max(IQBandwidthDictionary.Keys.Min(), previousKey);
                IsIqTimestampEnabled = true;
            } else {
                IQBandwidthDictionary = InstantViewBandwidth switch {
                    InstantViewBandwidth.Bandwidth100MHz => IQBandwidthDictionary100MHz,
                    InstantViewBandwidth.Bandwidth400MHz => IQBandwidthDictionary400MHz,
                    _ => throw new ArgumentOutOfRangeException()
                };
            }

            if (InstantViewBandwidth == InstantViewBandwidth.Bandwidth400MHz) {
                if (previousKey > IQBandwidthDictionary.Keys.Max()) {
                    IQBandwidthKey = IQBandwidthDictionary.Keys.Min();
                }

                IQRecordingMode = IQRecordingModeDictionary.Keys.Min();
            }
        });
    }

    private async Task SetAttenuationMode() {
        AttackTime = _receiverSettings.DefaultAttackTime;
        var decayTime = DecayTime;
        var isAgcEnabled = IsAgcEnabled;
        var response = await _receiverMessagingService.SetAgc(IsAgcEnabled, AttackTime, DecayTime);
        await _dispatcherWrapper.InvokeAsync(() => {
            SetAttenuationModeCommand.State = DetermineCommandState(
                response.IsSuccess,
                response.Parameters?.IsEnabled != isAgcEnabled);

            AttackTimeState = DetermineCommandState(
                response.IsSuccess,
                response.Parameters?.AttackTime != _receiverSettings.DefaultAttackTime);

            DecayTimeState = DetermineCommandState(
                response.IsSuccess,
                response.Parameters?.DecayTime != decayTime);

            if (response.Parameters is not null) {
                IsAgcEnabled = response.Parameters.IsEnabled;
                AttackTime = response.Parameters.AttackTime;
                DecayTime = response.Parameters.DecayTime;
            }

            if (response is { IsSuccess: false, IsTransient: true }) {
                _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage ?? "Error at setting AGC",
                    MessageLevel.Error);
            }
        });

        if (response is { IsSuccess: true, Parameters.IsEnabled: false }) {
            await GetAttenuation();
        }
    }

    private CommandState DetermineCommandState(bool isSuccess, bool hasMismatch) {
        return isSuccess
            ? hasMismatch ? CommandState.Warning : CommandState.Success
            : CommandState.Error;
    }

    private async Task<bool> GetAttenuationMode() {
        var response = await _receiverMessagingService.GetAgc();
        await _dispatcherWrapper.InvokeAsync(() => {
            SetAttenuationModeCommand.State = response.IsSuccess ? CommandState.Success : CommandState.Error;
            if (response is { Parameters: not null }) {
                IsAgcEnabled = response.Parameters.IsEnabled;
                AttackTime = response.Parameters.AttackTime;
                DecayTime = response.Parameters.DecayTime;
            }
            if (response is { IsSuccess: false}) {
                _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage ?? "Error at getting AGC",
                    MessageLevel.Error);
            }
        });

        return response.IsSuccess || !response.IsTransient;
    }

    private async Task SetAttenuation() {
        var response = await _receiverMessagingService.SetAttenuators(RfAtt, IfAtt);
        await _dispatcherWrapper.InvokeAsync(() => {
            SetAttenuationCommand.State = response.IsSuccess ? CommandState.Success : CommandState.Error;
            if (!response.IsSuccess) {
                if (response.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        response.ErrorMessage ?? "Error at setting attenuators",
                        MessageLevel.Error);
                }
            } else {
                IfAtt = response.IfAttenuator;
                RfAtt = response.RfAttenuator;
            }
        });
    }

    private async Task<bool> GetAttenuation() {
        var response = await _receiverMessagingService.GetAttenuators();
        await _dispatcherWrapper.InvokeAsync(() => {
            SetAttenuationCommand.State = response.IsSuccess ? CommandState.Success : CommandState.Error;
            if (!response.IsSuccess) {
                if (response.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        response.ErrorMessage ?? "Error at getting attenuators",
                        MessageLevel.Error);
                }
            } else {
                IfAtt = response.IfAttenuator;
                RfAtt = response.RfAttenuator;
            }
        });

        return response.IsSuccess;
    }

    private void RotatorParkedStateChanged(object? sender, RotatorParkedStateChangedNotification e) {
        if (e.StationId == _localStationId) {
            _dispatcherWrapper.Invoke(() => {
                IsRotatorParked = e.IsAntennaRotatorParked;
            });
        }
    }

    private void ReceiverAngleStateChanged(object? sender, ReceiverAngleStateChangedNotification e) {
        if (e.StationId == _localStationId) {
            _dispatcherWrapper.Invoke(() => {
                IsAngleOperationError = e.IsErrorDuringOperation;
            });
        }
    }

    private void OnAnglesDataReceived(object? sender, AnglesParametersChangedNotification notification) {
        if (notification.StationId == _localStationId && !IsArchontTType) {
            SetAnglesParameters(notification.State);
        }
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void ReceiverConnectionChanged(object? sender, ConnectionChangedNotification e) {
        try {
            if (e.StationId != _localStationId) {
                return;
            }

            if (_receiverIsConnected == e.IsConnected) {
                return;
            }

            _receiverIsConnected = e.IsConnected;
            if (!_receiverIsConnected) {
                return;
            }

            await UpdateReceiverParameters();
        } catch (Exception exception) {
            _logger.LogError(exception: exception, message: "");
            _messageLogger.AddMessage(MessageCategory.System, exception.Message, MessageLevel.Error);
        }
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    private async Task<bool> UpdateReceiverParameters() {
        var response = await _receiverMessagingService.GetReceiverMode();
        var state = response.IsSuccess switch {
            true => ReceiverMode != null && ReceiverMode != response.Mode ? CommandState.Warning : CommandState.Success,
            _ => CommandState.Error
        };
        _dispatcherWrapper.Invoke(() => SetReceiverModeCommand.State = state);

        if (response.IsSuccess) {
            _setReceiverMode = response.Mode;
            _dispatcherWrapper.Invoke(() => {
                ReceiverMode = response.Mode;
                if (response.ScenarioId.HasValue) {
                    _selectedScenarioId = response.ScenarioId;
                    if (Scenarios.Any()) {
                        SelectedScenario = Scenarios.FirstOrDefault(a => a.Id == _selectedScenarioId);
                    }

                    IsScenarioActive = true;
                }
            });

            _messenger.Send(new EsmScenarioFrameCountChangedMessage((response.ScanListLength, response.ScenarioRange)));
            SetModeEnabledParameters(response.Mode);
        }

        var result = await FillReceiverParams(SelectedScenario?.CurrentScenarioDto);
        return response.IsSuccess && result;
    }

    private async Task SetReceiverMode() {
        if (ReceiverMode is null || ReceiverMode == _setReceiverMode) {
            return;
        }

        try {
            _dispatcherWrapper.Invoke(() => IsTunerSetupInProgress = true);
            var response = await _receiverMessagingService.SetReceiverMode(ReceiverMode.Value,
                IsPanoramaSpectrumEnabled, null, _currentRemoteScenario);
            _dispatcherWrapper.Invoke(() => SetReceiverModeCommand.State = response.State);

            if (!response.IsSuccess) {
                _dispatcherWrapper.Invoke(() => {
                    ReceiverMode = _setReceiverMode;
                    SetReceiverModeCommand.State = CommandState.Error;
                });
                _messageLogger.AddMessage(MessageCategory.System,
                    response.ErrorMessage != null
                        ? $"Failed to set receiver mode: {response.ErrorMessage}"
                        : "Failed to set receiver mode", MessageLevel.Error);
                return;
            }

            _setReceiverMode = response.Mode;
            ReceiverMode = response.Mode;
            _messenger.Send(new EsmScenarioFrameCountChangedMessage((response.ScanListLength, response.ScenarioRange)));
        } finally {
            SetModeEnabledParameters(ReceiverMode);
            await FillReceiverParams(SelectedScenario?.CurrentScenarioDto);
            _dispatcherWrapper.Invoke(() => IsTunerSetupInProgress = false);
        }
    }

    private void IQRecordingTimerTick(object? sender, EventArgs e) {
        IQRecordingDuration = IQRecordingDuration.Add(TimeSpan.FromSeconds(1));
    }

    private void SetupAveragingTimesDictionaries() {
        _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode.Average] = new Dictionary<int, string> {
            { 1, "1" },
            { 3, "3" },
            { 10, "10" },
            { 30, "30" },
            { 100, "100" },
            { 300, "300" },
            { 1000, "1000" }
        };

        var peakOrMinHold = new Dictionary<int, string> {
            { 0, "0" },
            { 1, "0.1" },
            { 3, "0.3" },
            { 10, "1" },
            { 30, "3" },
            { 100, "10" },
            { 300, "30" },
            { 1000, "100" }
        };

        _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode.PeakHold] = peakOrMinHold;
        _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode.MinHold] = peakOrMinHold;
        _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode.Normal] = new Dictionary<int, string>();
    }

    private async Task OnSpectrumAveragingModeChanged() {
        if (SetAveragingModeParameters()) {
            return;
        }

        await SetupTunerSpectrum();
    }

    private bool SetAveragingModeParameters() {
        _spectrumModeDisplayValuesDictionary.TryGetValue(SpectrumAveragingMode, out var dict);
        if (dict == null) {
            _logger.LogError("TunerViewModel - No dictionary found for averaging mode: {value}", SpectrumAveragingMode);
            return true;
        }

        AveragingTimes = dict;
        AveragingTime = AveragingTimes.Keys.FirstOrDefault();

        if (SpectrumAveragingMode == SpectrumAveragingMode.Normal) {
            AveragingTime = 0;
        }

        OnPropertyChanged(nameof(IsAveragingTimeVisible));
        return false;
    }

    private void PositionChanged(object? sender, PositionChangedNotification? data) {
        if (data == null || data.StationId != _localStationId) {
            return;
        }

        _dispatcherWrapper.Invoke(() => {
            if (data.Data.PositionSource == PositionSource.Positioner) {
                PositionerInfo = data.Data.Position;
                PositionerAzimuth = data.Data.Azimuth;
                PositionerDateTime = data.Data.DateTime;
                PositionerAltitudeInMeter =
                    Math.Round(UnitConverter.Convert(data.Data.Position.Altitude, LengthUnit.Millimeter,
                        LengthUnit.Meter));
            } else if (data.Data.PositionSource == PositionSource.Receiver) {
                ReceiverAltitudeInMeter = data.Data.Position.Altitude;
                ReceiverInfo = data.Data.Position;
                ReceiverDateTime = data.Data.DateTime;
            } else {
                PositionManualX = data.Data.Position.Longitude;
                PositionManualY = data.Data.Position.Latitude;
                AzimuthManual = data.Data.Azimuth;
            }
        });
    }

    private async Task SetAntennaRotatorAngle() {
        if (IsArchontTType) {
            return;
        }

        if (!IsAntennaRotatorAvailable) {
            return;
        }

        _dispatcherWrapper.Invoke(() => {
            IsAntennaRotationInProcess = true;
            CanBreakRotation = IsAntennaRotatorPresent && IsAntennaRotatorConnected;
            IsEnabledBreakRotation = true;
        });

        try {
            var res = await _antennaRotatorMessagingService.SetAngle(AntennaRotatorAngle);

            if (!res.IsSuccess) {
                throw new Exception(res.ErrorMessage);
            }

            _dispatcherWrapper.Invoke(() => {
                IsReceiverAngleDifferentFromReal = false;
                IsRotatorAngleDifferentFromRequired = !res.IsEqualsInputAngle;
            });
        } catch (Exception e) {
            _dispatcherWrapper.Invoke(() => {
                IsAngleOperationError = true;
            });
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        } finally {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = false;
                CanBreakRotation = false;
                IsEnabledBreakRotation = false;
            });
        }
    }

    private async Task SetAzimuth0() {
        if (IsArchontTType) {
            return;
        }

        _dispatcherWrapper.Invoke(() => {
            IsAntennaRotationInProcess = true;
        });

        try {
            var res = await _systemControlMessagingService.SetAzimuth0(Azimuth0);

            if (!res.IsSuccess) {
                throw new Exception(res.ErrorMessage);
            }

            var receiverAngleStateResponse = await _systemControlMessagingService.GetReceiverAngleState();
            if (receiverAngleStateResponse.IsSuccess) {
                _dispatcherWrapper.Invoke(() => {
                    IsReceiverAngleDifferentFromReal = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                    IsRotatorAngleDifferentFromRequired = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                });
            }
        } catch (Exception e) {
            _dispatcherWrapper.Invoke(() => {
                Azimuth0 = _lastSavedAzimuth0;
                IsAngleOperationError = true;
            });
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        } finally {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = false;
            });
        }
    }

    private void SetAnglesParameters(AzimuthParameters deviceAnglesState) {
        _dispatcherWrapper.Invoke(() => {
            if (IsAntennaRotatorPresent) {
                AntennaRotatorAngle = deviceAnglesState.AntennaRotatorAngle.GetValueOrDefault();
            }

            if (deviceAnglesState.AntennaDirection != null) {
                AntennaDirection = deviceAnglesState.AntennaDirection.Value;
                _lastSavedAntennaAngle = deviceAnglesState.AntennaDirection.Value;
                if (_localStationId != null) {
                    _messenger.Send(new AntennaDirectionUpdatedMessage(
                        new AntennaDirectionUpdatedData(AntennaDirection, _localStationId.Value)));
                }
            }

            if (deviceAnglesState.Azimuth0 != null) {
                Azimuth0 = deviceAnglesState.Azimuth0.Value;
                _lastSavedAzimuth0 = deviceAnglesState.Azimuth0.Value;
            }
        });
    }

    private async Task BreakRotation() {
        if (IsArchontTType) {
            return;
        }

        try {
            var res = await _antennaRotatorMessagingService.BreakRotation();
            _dispatcherWrapper.Invoke(() => IsEnabledBreakRotation = false);
            if (!res.IsSuccess) {
                throw new Exception(res.ErrorMessage);
            }
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private async Task ParkRotator() {
        if (IsArchontTType) {
            return;
        }

        if (!IsAntennaRotatorAvailable) {
            return;
        }

        try {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = true;
                CanBreakRotation = true;
            });
            var res = await _antennaRotatorMessagingService.ParkAntennaRotator();
            if (res.IsSuccess) {
                AntennaRotatorAngle = res.Angle;
                _dispatcherWrapper.Invoke(() => {
                    IsReceiverAngleDifferentFromReal = false;
                    IsRotatorAngleDifferentFromRequired = !res.IsEqualsParkingAngle;
                });
            } else {
                throw new Exception(res.ErrorMessage);
            }
        } catch (Exception e) {
            _dispatcherWrapper.Invoke(() => {
                IsAngleOperationError = true;
            });
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        } finally {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = false;
                CanBreakRotation = false;
            });
        }
    }

    private async Task SetAntennaDirection() {
        if (IsArchontTType) {
            return;
        }

        try {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = true;
                CanBreakRotation = IsAntennaRotatorPresent && IsAntennaRotatorConnected;
                IsEnabledBreakRotation = true;
            });
            var res = await _systemControlMessagingService.SetAntennaDirection(AntennaDirection);
            if (res is { IsSuccess: true, SetReceiverDirection: not null }) {
                _dispatcherWrapper.Invoke(() => AntennaDirection = res.SetReceiverDirection.Value);
                _lastSavedAntennaAngle = AntennaDirection;
            } else {
                throw new Exception(res.ErrorMessage);
            }

            var receiverAngleStateResponse = await _systemControlMessagingService.GetReceiverAngleState();
            if (receiverAngleStateResponse.IsSuccess) {
                _dispatcherWrapper.Invoke(() => {
                    IsReceiverAngleDifferentFromReal = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                    IsRotatorAngleDifferentFromRequired = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                });
            }
        } catch (Exception e) {
            _dispatcherWrapper.Invoke(() => {
                AntennaDirection = _lastSavedAntennaAngle;
                IsAngleOperationError = true;
            });
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        } finally {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = false;
                CanBreakRotation = false;
                IsEnabledBreakRotation = false;
            });
        }
    }

    private async Task<bool> GetAntennaParameters() {
        if (IsArchontTType) {
            SetAnglesParameters(new AzimuthParameters { AntennaDirection = 0, AntennaRotatorAngle = 0, Azimuth0 = 0 });
            return true;
        }

        _dispatcherWrapper.Invoke(() => {
            IsAntennaRotationInProcess = true;
        });

        try {
            var azimuthParametersResponse = await _systemControlMessagingService.GetAzimuthParameters();
            if (azimuthParametersResponse is { IsSuccess: true, AzimuthParameters: not null }) {
                SetAnglesParameters(azimuthParametersResponse.AzimuthParameters);
            } else {
                if (!string.IsNullOrWhiteSpace(azimuthParametersResponse.ErrorMessage) && azimuthParametersResponse.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System, azimuthParametersResponse.ErrorMessage,
                        MessageLevel.Error);
                }

                return azimuthParametersResponse.IsSuccess || !azimuthParametersResponse.IsTransient;
            }

            var rotatorParkedResponse = await _systemControlMessagingService.GetRotatorParkedState();
            var receiverAngleStateResponse = await _systemControlMessagingService.GetReceiverAngleState();
            if (rotatorParkedResponse.IsSuccess && receiverAngleStateResponse.IsSuccess) {
                _dispatcherWrapper.Invoke(() => {
                    IsReceiverAngleDifferentFromReal = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                    IsRotatorAngleDifferentFromRequired = !receiverAngleStateResponse.IsRealAngleEqualsReceiverAngle;
                    IsRotatorParked = rotatorParkedResponse.IsAntennaRotatorParked;
                });
            } else {
                if (!string.IsNullOrWhiteSpace(rotatorParkedResponse.ErrorMessage) ||
                    !string.IsNullOrWhiteSpace(receiverAngleStateResponse.ErrorMessage)) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        rotatorParkedResponse.ErrorMessage ?? azimuthParametersResponse.ErrorMessage ?? "",
                        MessageLevel.Error);
                }
            }

            return azimuthParametersResponse.IsSuccess && rotatorParkedResponse.IsSuccess && receiverAngleStateResponse.IsSuccess;
        } catch (Exception e) {
            _dispatcherWrapper.Invoke(() => {
                IsAngleOperationError = true;
            });
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
            return false;
        } finally {
            _dispatcherWrapper.Invoke(() => {
                IsAntennaRotationInProcess = false;
            });
        }
    }

    private void RegisterMessages(IMessenger messenger) {
        messenger.Register<SpectrumFrequencyDoubleClickedMessage>(this, (_, m) => {
            if (IsIQRecordActive) {
                return;
            }

            if (IsElintModeEnabled) {
                SetFrequencyCommand.Execute(m.Value);
            } else {
                ZoomFrequency = Math.Clamp((int)m.Value, ZoomFrequencyLimit.Min, ZoomFrequencyLimit.Max);
            }
        });

        messenger.Register<ServerConnectionStateChangedMessage>(this, (_, m) => OnServerConnectionChanged(m.Value));
        messenger.Register<StatusChangedMessage>(this, (_, m) => OnStatusChanged(m.Value));
#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
        messenger.Register<PdwStreamingChangedMessage>(this, async (_, m) => await StartStopPdwStreaming(m.Value));
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
        messenger.Register<AntennaRotatorAvailabilityChangedMessage>(this,
            (_, m) => { IsAntennaRotatorPresent = m.Value; });
        messenger.Register<PositionerAvailabilityChangedMessage>(this,
            (_, m) => OnPositionerAvailabilityChanged(m.Value));
        messenger.Register<RotatorIndicatorChangedMessage>(this,
            (_, m) => IsAntennaRotatorConnected = m.Value.Connection == IndicatorStatus.Ok);
        messenger.Register<PositionerIndicatorChangedMessage>(this,
            (_, _) => SetPositionMode());
        messenger.Register<LocalStationInfoChangedMessage>(this, (_, m) => {
            _localStationId = m.Value.StationId;
            _dispatcherWrapper.InvokeAsync(() => IsArchontTType = m.Value.StationType == StationType.ArchontT);
        });
        messenger.Register<IsInReconnectionStateChangedMessage>(this, (_, m) => OnIsInReconnectionChanged(m.Value));
        messenger.Register<ReceiverConnectionChangedMessage>(this, (_, m) => {
            OnReceiverConnectionChanged(m.Value);
        });
        messenger.Register<EsmScenarioRangeChanged>(this,
            (_, m) => _dispatcherWrapper.Invoke(() => SetZoomFrequencyLimit(m.Value)));

        messenger.Register<TunerViewModel, ParkRotatorRequestMessage>(this, (r, m) => {
            m.Reply(Task.Run(async () => {
                await r.ParkRotator();
                return !r.IsRotatorAngleDifferentFromRequired;
            }));
        });

        messenger.Register<ScenariosLoadedMessage>(this,
            (_, m) => _dispatcherWrapper.Invoke(() => {
                Scenarios = m.Value;
                if (_selectedScenarioId.HasValue) {
                    SelectedScenario = m.Value.FirstOrDefault(a => a.Id == _selectedScenarioId);
                }
            }));

        _messenger.Register<ControlStatusChangedMessage>(this,
            (_, m) => _dispatcherWrapper.Invoke(() => IsControlEnabled = m.Value));
        }

    private void OnReceiverConnectionChanged(bool isReceiverConnected) {
        _dispatcherWrapper.Invoke(() => IsReceiverConnected = isReceiverConnected);
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void OnIsInReconnectionChanged(bool isInReconnection) {
        try {
            if (isInReconnection) {
                await CheckAndStopStreaming();
            }
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    private void OnPositionerAvailabilityChanged(bool isAvailable) {
        IsPositionerPresent = isAvailable;
        SetPositionMode();
    }

    private void SetPositionMode() {
        // todo probably change to positioner
        if (CurrentPositionMode == PositionSource.Positioner && !IsPositionerAvailable) {
            CurrentPositionMode = PositionSource.Manual;
        }

        if (IsPositionerAvailable) {
            GetBacklightCommand.Execute(null);
        }
    }

    private void OnServerConnectionChanged(bool isConnected) {
        _dispatcherWrapper.Invoke(() => IsServerConnected = isConnected);
    }

    private void OnStatusChanged(bool isRunning) {
        if (IsRunning == isRunning) {
            return;
        }

        _ = Task.Run(async () => {
            _dispatcherWrapper.Invoke(() => IsRunning = isRunning);

            if (isRunning) {
                try {
                    var policy = _policyRegistry.Get<AsyncRetryPolicy<bool>>(
                        PolicyNameConstants.WaitAndRetryLimit3ByBooleanResultAsync);
                    await policy.ExecuteAsync(async () => {
                        var result1 = await UpdateReceiverParameters();
                        var result2 = await Run();
                        var result3 = await UpdateManualPositionParameters();
                        var result4 = await GetStreamRecordingStates();
                        return result1 && result2 && result3 && result4;
                    });
                } catch (Exception e) {
                    _dispatcherWrapper.Invoke(() => IsRunning = false);
                    _logger.LogError(exception: e, message: "Failed update tuner card on reconnection");
                    _messageLogger.AddMessage(MessageCategory.System, $"Failed update tuner card on reconnection {e.Message}" , MessageLevel.Error);
                }
            } else {
                await Stop();
                _dispatcherWrapper.Invoke(() => {
                    ReceiverMode = null;
                });
            }
        });
    }

    private async Task<bool> GetStreamRecordingStates() {
        var res = await _receiverMessagingService.GetStreamRecordingState();
        if (!res.IsSuccess) {
            return false;
        }

        if (!res.IsIQRecordingActive) {
            StopIQRecordingTimer();
        }

        _dispatcherWrapper.Invoke(() => {
            IsIQRecordActive = res.IsIQRecordingActive;
            IsPdwRecordActive = res.IsPdwRecordingActive;
            IsGnssRecordActive = res.IsGnssRecordingActive;
        });

        return true;
    }

    private async Task<bool> UpdateManualPositionParameters() {
        var currentState = await _manualPositionMessagingService.GetManualPositionState();
        if (currentState.IsSuccess) {
            _dispatcherWrapper.Invoke(() => {
                CurrentPositionMode = currentState.PositionSource;
                PositionManualX = currentState.ManualLongitude ?? 0;
                PositionManualY = currentState.ManualLatitude ?? 0;
                AzimuthManual = currentState.ManualAngle ?? 0;
            });

            if (_localStationId != null) {
                var receiverPoint = IsTunerPositionManual
                    ? new MapPoint(PositionManualX, PositionManualY, SpatialReferences.Wgs84)
                    : new MapPoint(CurrentAutoPosition?.Longitude ?? 0, CurrentAutoPosition?.Latitude ?? 0,
                        SpatialReferences.Wgs84);
                //todo send angle
                _messenger.Send(
                    new PositionerModeOrManualCoordinatesChangedMessage(new ValueTuple<PositionSource, MapPoint, uint>(
                        CurrentPositionMode,
                        receiverPoint, _localStationId.Value)));
            }
        }

        return currentState.IsSuccess;
    }

    private double CheckRange(double range) {
        return ReferenceLevel - range < _spectrumSettings.MinPowerRange.Min
            ? ReferenceLevel - _spectrumSettings.MinPowerRange.Min
            : range;
    }

    private async Task OnReceiverParametersChanged(ReceiverParametersChangedNotification notification) {
        if (notification.StationId != _localStationId) {
            return;
        }

        if (notification.Frequency.HasValue || notification.ScanRange != null) {
            await GetInstantViewBandwidth();
            await FillSpectrumParams();
            await _dispatcherWrapper.InvokeAsync(() => {
                if (notification.Frequency.HasValue) {
                    UpdateFrequencyAndIQ((long)notification.Frequency.Value);
                } else if (notification.ScanRange != null) {
                    ScanRange = notification.ScanRange;
                    SetZoomFrequencyLimit();
                    _messenger.Send(new ScanRangeChangedMessage(ScanRange));
                }
            });

            return;
        }

        if (notification.InstantViewBandwidth.HasValue) {
            try {
                _isUpdatingFromServer = true;
                _dispatcherWrapper.Invoke(() => {
                    InstantViewBandwidth = notification.InstantViewBandwidth.Value;
                    Bandwidth = BandwidthDictionary[InstantViewBandwidth];
                    SetAveragingModeParameters();
                });

                _messenger.Send(new BandwidthChangedMessage(Bandwidth));
            } finally {
                _isUpdatingFromServer = false;
            }

            return;
        }

        if (notification.SpectrumParameters != null) {
            SetLocalSpectrumParams(notification.SpectrumParameters);
            return;
        }

        if (notification.Mode.HasValue && !IsTunerSetupInProgress) {
            _dispatcherWrapper.Invoke(() => {
                _setReceiverMode = notification.Mode;
                ReceiverMode = notification.Mode;
            });

            SetModeEnabledParameters(notification.Mode);
            await FillReceiverParams(notification.Scenario);
        }

        if (notification.ScanListLength.HasValue) {
            _messenger.Send(
                new EsmScenarioFrameCountChangedMessage((notification.ScanListLength, notification.ScenarioRange)));
        }

        if (notification.AgcParameters != null) {
            await _dispatcherWrapper.InvokeAsync(() => {
                IsAgcEnabled = notification.AgcParameters.IsEnabled;
                AttackTime = notification.AgcParameters.AttackTime;
                DecayTime = notification.AgcParameters.DecayTime;
            });
            if (!IsAgcEnabled) {
                await GetAttenuation();
            }
        }

        if (notification is { RfAttenuator: not null, IfAttenuator: not null }) {
            _dispatcherWrapper.Invoke(() => {
                IfAtt = notification.IfAttenuator.Value;
                RfAtt = notification.RfAttenuator.Value;
            });
        }
    }

    private void SetModeEnabledParameters(ReceiverMode? mode) {
        _dispatcherWrapper.Invoke(() => {
            IsElintModeEnabled = mode is Common.Enums.ReceiverMode.Elint or Common.Enums.ReceiverMode.Comint;
            IsEsmModeEnabled = mode == Common.Enums.ReceiverMode.Esm;
            IsEsmScenarioModeEnabled = mode == Common.Enums.ReceiverMode.EsmScenario;
        });
    }

    private void UpdateIQFrequencyMinMax() {
        IQFrequencyMin = Math.Floor(_frequency - Bandwidth / 2d + Math.Ceiling(IQBandwidthDictionary[IQBandwidthKey] / 2d));
        IQFrequencyMax = Math.Max(IQFrequencyMin,
            _frequency + Bandwidth / 2d - Math.Ceiling(IQBandwidthDictionary[IQBandwidthKey] / 2d));
    }

    private async Task<string> GetLocalIpAddress() {
        var serverAddress = _messenger.Send(new GetServerIpAddressRequestMessage());
        if (serverAddress.Response is null) {
            return string.Empty;
        }

        return await SelfAddressHelper.GetLocalIpAddress(serverAddress.Response);
    }

    private async Task StartStopPdwStreaming(bool isPdwEnabled) {
        try {
            if (isPdwEnabled) {
                var localIpAddress = await GetLocalIpAddress();
                _streamingOptions.Value.Protocols.TryGetValue(DataFrameType.Spectrogram, out var protocol);
                _pdwConnection = await _pdwReceivingClient.StartListening();
                await StartDataStreaming(_pdwConnection, localIpAddress);
            } else {
                await _pdwReceivingClient.StopListening();
                await StopDataStreaming(_pdwConnection);
            }
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private async Task<bool> StartStopStreaming() {
        _messenger.Send(new StreamingStateChangedMessage(IsStreamingEnabled));

        if (IsStreamingEnabled) {
            return await StartStreaming();
        } else {
            return await StopStreaming();
        }
    }

    private async Task<bool> StartStreaming() {
        var localIpAddress = await GetLocalIpAddress();
        _spectrumConnection = await _spectrumReceivingClient.StartListening();
        var result1 = await StartDataStreaming(_spectrumConnection, localIpAddress);
        _thresholdConnection = await _thresholdReceivingClient.StartListening();
        var result2 = await StartDataStreaming(_thresholdConnection, localIpAddress);
        _persistenceSpectrumConnection = await _persistenceSpectrumReceivingClient.StartListening();
        var result3 = await StartDataStreaming(_persistenceSpectrumConnection, localIpAddress);
        return result1 && result2 && result3;
    }

    private async Task<bool> StopStreaming() {
        await _spectrumReceivingClient.StopListening();
        var result1 = await StopDataStreaming(_spectrumConnection);
        await _thresholdReceivingClient.StopListening();
        var result2 = await StopDataStreaming(_thresholdConnection);
        await _persistenceSpectrumReceivingClient.StopListening();
        var result3 = await StopDataStreaming(_persistenceSpectrumConnection);
        return result1 && result2 && result3;
    }

    private async Task<bool> StopDataStreaming(DataStreamingConnection? connection) {
        if (connection != null) {
            var result = await _dataStreamingMessagingService.StopDataStreaming(connection);
            return result.IsSuccess;
        }

        return false;
    }

    private async Task<bool> StartDataStreaming(DataStreamingConnection? connection, string localIpAddress) {
        if (connection != null) {
            var result = await _dataStreamingMessagingService.StartDataStreaming(connection, localIpAddress);
            return result.IsSuccess;
        }

        return false;
    }

    private async Task SetPanoramaSpectrum() {
        await _receiverMessagingService.SetPanoramaMode(IsPanoramaSpectrumEnabled);
    }

    private async Task StartStopIQRecording() {
        if (IsIQRecordActive) {
            var offsetInHz = (int)Math.Ceiling(UnitsNet.Frequency.FromMegahertz(IQFrequencyOffset).Hertz);
            var frequencyInHz = (long)UnitsNet.Frequency.FromMegahertz(IQFrequency).Hertz;
            var setupResponse = await _receiverMessagingService.SetupIQ(CreateIQSetupParameters());
            if (setupResponse is { IsSuccess: false, ErrorMessage: not null }) {
                _messageLogger.AddMessage(MessageCategory.System, $"IQ setup failed: {setupResponse.ErrorMessage}",
                    MessageLevel.Error);
                IsIQRecordActive = false;
                return;
            }

            var response = await _receiverMessagingService.StartStreamRecording(
                IQRecordingMode, frequencyInHz, offsetInHz, IQBandwidthKey, IQBandwidthDictionary[IQBandwidthKey],
                IQFileSize, IsIqTimestampEnabled);
            if (response is { IsSuccess: false }) {
                IsIQRecordActive = false;
                _messageLogger.AddMessage(MessageCategory.System,
                    $"Error while starting IQ recording: {response.ErrorMessage}", MessageLevel.Error);
            }
        } else {
            var stopResponse = await _receiverMessagingService.StopStreamRecording(DataFrameType.IQ);
            if (!stopResponse.IsSuccess) {
                _messageLogger.AddMessage(MessageCategory.System,
                    $"Error while stopping IQ recording: {stopResponse.ErrorMessage}", MessageLevel.Error);

            } else {
                StopIQRecordingTimer();
            }
        }
    }

    private async Task StartStopPdwRecording() {
        if (IsPdwRecordActive) {
            var response = await _receiverMessagingService.StartStreamRecording(PdwRecordingMode, PdwFileSize);
            if (response is { IsSuccess: false }) {
                IsPdwRecordActive = false;
                if (response is { IsTransient: true, ErrorMessage: not null }) {
                    _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
                }
            }
        } else {
            await _receiverMessagingService.StopStreamRecording(DataFrameType.Pdw);
        }
    }

    private async Task StartStopGnssRecording() {
        if (IsGnssRecordActive) {
            var response = await _receiverMessagingService.StartStreamRecording(DataFrameType.Gnss);
            if (response is { IsSuccess: false }) {
                IsGnssRecordActive = false;
                if (response is { IsTransient: true, ErrorMessage: not null }) {
                    _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
                }
            }
        } else {
            await _receiverMessagingService.StopStreamRecording(DataFrameType.Gnss);
        }
    }

    private async Task StartStopScenario() {
        if (ReceiverMode == null) {
            return;
        }

        if (IsScenarioActive) {
            var response =
                await _receiverMessagingService.SetReceiverMode(ReceiverMode.Value, scenarioId: SelectedScenario?.Id,
                    isPanoramaSpectrumEnabled: IsPanoramaSpectrumEnabled);
            if (!response.IsSuccess) {
                _logger.LogError("Error at starting scenario: {Message}", response.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage ?? "Error at starting scenario",
                    MessageLevel.Error);
                IsScenarioActive = false;
                _currentRemoteScenario = null;
            }

            _messenger.Send(new EsmScenarioFrameCountChangedMessage((response.ScanListLength, response.ScenarioRange)));
        } else {
            _currentRemoteScenario = null;
            var response =
                await _receiverMessagingService.SetReceiverMode(ReceiverMode.Value, scenarioId: null,
                    isPanoramaSpectrumEnabled: IsPanoramaSpectrumEnabled);
            if (!response.IsSuccess) {
                _logger.LogError("Error at stopping scenario: {Message}", response.ErrorMessage);
                _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage ?? "Error at stopping scenario",
                    MessageLevel.Error);
            }

            _messenger.Send(new EsmScenarioFrameCountChangedMessage((response.ScanListLength, response.ScenarioRange)));
        }
    }

    private void OnIQRecordingStatusChanged(object? sender, IQRecordingNotification notification) {
        if (notification.StationId != _localStationId) {
            return;
        }

        _dispatcherWrapper.Invoke(() => IsIQRecordActive = notification.IsRecordingStarted);
        if (IsIQRecordActive) {
            _iqRecordingTimer?.Start();
            if (notification is { RecordingParameters: not null, SetupParameters: not null }) {
                _dispatcherWrapper.Invoke(() => {
                    IQFrequency = UnitsNet.Frequency.FromHertz((long)notification.RecordingParameters.Frequency).Megahertz;
                    IQRecordingMode = notification.RecordingParameters.RecordingMode;
                    IQBandwidthKey = notification.RecordingParameters.RateId;
                    IQFileSize = notification.RecordingParameters.FileSize;
                    IsIqTimestampEnabled = notification.RecordingParameters.WithTimestamp;
                    PulseCounter = notification.SetupParameters.PulseCounter;
                    PulseWait = notification.SetupParameters.PulseWait;
                    RecordTime = notification.SetupParameters.RecordTime;
                });
            }
        } else {
            StopIQRecordingTimer();
        }
    }

    private void OnPdwRecordingStatusChanged(object? sender, PdwRecordingNotification notification) {
        if (notification.StationId != _localStationId) {
            return;
        }

        _dispatcherWrapper.Invoke(() => {
            IsPdwRecordActive = notification.IsRecordingStarted;
            if (IsPdwRecordActive && notification.PdwParameters != null) {
                PdwFileSize = notification.PdwParameters.FileSize;
                PdwRecordingMode = notification.PdwParameters.Mode;
            }
        });
    }

    private void OnGnssRecordingStatusChanged(object? sender, GnssRecordingNotification notification) {
        if (notification.StationId != _localStationId) {
            return;
        }

        _dispatcherWrapper.Invoke(() => {
            IsGnssRecordActive = notification.IsRecordingStarted;
        });
    }

    private void StopIQRecordingTimer() {
        _iqRecordingTimer?.Stop();
        _dispatcherWrapper.Invoke(() => IQRecordingDuration = TimeSpan.Zero);
    }

    private async Task Stop() {
        await _dispatcherWrapper.InvokeAsync(() => {
            SetFrequencyCommand.State = CommandState.Undefined;
            SetScanRangeCommand.State = CommandState.Undefined;
            SetupTunerSpectrumCommand.State = CommandState.Undefined;
            SetInstantViewBandwidthCommand.State = CommandState.Undefined;
            SetReceiverModeCommand.State = CommandState.Undefined;
            SetAttenuationCommand.State = CommandState.Undefined;
            SetAttenuationModeCommand.State = CommandState.Undefined;
            IsConnected = false;
        });

        _messenger.Send(new ConnectionStateChangedMessage(IsConnected));
    }

    private async Task CheckAndStopStreaming() {
        if (IsStreamingEnabled) {
            IsStreamingEnabled = false;
            await StartStopStreaming();
        }
    }

    private async Task<bool> Run() {
        _receiverMessagingService.ReceiverConnectionChanged -= ReceiverConnectionChanged;
        _receiverMessagingService.ReceiverConnectionChanged += ReceiverConnectionChanged;

        await _dispatcherWrapper.InvokeAsync(() => { IsConnected = true; });

        IsStreamingEnabled = true;
        var result1 = await StartStopStreaming();

        var result2 = await GetAntennaParameters();
        _messenger.Send(new ConnectionStateChangedMessage(IsConnected));
        _messenger.Send(
            new IQBandwidthChangedMessage(new IQFrequencyBandwidthData(IQFrequency,
                IQBandwidthDictionary[IQBandwidthKey])));

        return result1 && result2;
    }

    private void UpdateFrequencyAndIQ(long frequency) {
        try {
            AllowRoundOutOfRangeIQFrequency = false;
            var freqMhz = UnitsNet.Frequency.FromHertz(frequency).Megahertz;
            Frequency = freqMhz;
            var offsetSave = IQFrequencyOffset;
            UpdateIQFrequencyMinMax();
            IQFrequency = freqMhz + offsetSave;
            _messenger.Send(new FrequencyChangedMessage(Frequency, _localStationId ?? 0));

        } finally {
            AllowRoundOutOfRangeIQFrequency = true;
        }
    }

    private async Task<bool> FillReceiverParams(ScenarioDto? scenario) {
        var result1 = await GetInstantViewBandwidth();
        var result2 = await FillSpectrumParams();
        var result3 = await GetAttenuationMode();
        var result4 = await GetAttenuation();

        bool result5 = false, result6 = false;
        if (IsElintModeEnabled) {
            result5 = await GetFrequencyFromReceiver();
        } else if (IsEsmModeEnabled) {
            result6 = await GetScanRangeFromReceiver();
        } else if (IsEsmScenarioModeEnabled) {
            _dispatcherWrapper.Invoke(() => {
                if (scenario != null) {
                    var existedScenario = Scenarios.FirstOrDefault(a => a.Id == scenario.Id);
                    existedScenario?.Update(scenario);

                    if (existedScenario == null) {
                        _currentRemoteScenario = scenario;
                    }

                    SelectedScenario = existedScenario;
                    IsScenarioActive = true;
                } else {
                    IsScenarioActive = false;
                    _currentRemoteScenario = null;
                }
            });
        }

        return result1 && result2 && result3 && result4 && (result5 || result6);
    }

    private async Task<bool> FillSpectrumParams() {
        var spectrumResponse = await _receiverMessagingService.GetSpectrumParams();
        if (!spectrumResponse.IsSuccess || spectrumResponse.SpectrumParameters is null) {
            _logger.LogError("{Message}", spectrumResponse.ErrorMessage);
            if (spectrumResponse.IsTransient) {
                _messageLogger.AddMessage(MessageCategory.System,
                    spectrumResponse.ErrorMessage ?? "Error while getting spectrum params", MessageLevel.Error);
            }

            return false;
        }

        SetLocalSpectrumParams(spectrumResponse.SpectrumParameters);

        _dispatcherWrapper.Invoke(() => {
            SetupTunerSpectrumCommand.State = spectrumResponse.State;
        });

        return true;
    }

    private async Task<bool> GetInstantViewBandwidth() {
        var response = await _receiverMessagingService.GetInstantViewBandwidth();
        await _dispatcherWrapper.InvokeAsync(() => {
            SetInstantViewBandwidthCommand.State = response.State;
            if (!response.IsSuccess) {
                if (response.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        response.ErrorMessage ?? "Error while getting bandwidth", MessageLevel.Error);
                }
            } else if (InstantViewBandwidth != response.Bandwidth) {
                InstantViewBandwidth = response.Bandwidth;
                Bandwidth = BandwidthDictionary[InstantViewBandwidth];
                SetAveragingModeParameters();
            }
        });
        _messenger.Send(new BandwidthChangedMessage(Bandwidth));
        SetIQBandwidthDictionary();

        return response.IsSuccess;
    }

    private void SetLocalSpectrumParams(SpectrumParameters spectrumParameters) {
        _isGettingParamsFromReceiver = true;
        try {
            _dispatcherWrapper.Invoke(() => {
                ReferenceLevel = spectrumParameters.MaxPower;
                Range = ReferenceLevel - spectrumParameters.MinPower;
                _messenger.Send(new SpectrumRangeChanged(new DoubleRange(
                    spectrumParameters.MinPower,
                    spectrumParameters.MaxPower)));

                if (SpectrumAveragingMode != spectrumParameters.Mode) {
                    SpectrumAveragingMode = spectrumParameters.Mode;
                    SetAveragingModeParameters();
                }

                AveragingTimes = _spectrumModeDisplayValuesDictionary[SpectrumAveragingMode];
                AveragingTime = spectrumParameters.Time;
                FftPower = spectrumParameters.RBW;

                _messenger.Send(new FftPowerChangedMessage(FftPower));
                _messenger.Send(new SpectrumRangeChanged(new DoubleRange(spectrumParameters.MinPower,
                    spectrumParameters.MaxPower)));
            });
        } finally {
            _isGettingParamsFromReceiver = false;
        }
    }

    private async Task<bool> GetFrequencyFromReceiver() {
        var frequencyResponse = await _receiverMessagingService.GetFrequency();
        await _dispatcherWrapper.InvokeAsync(() => {
            SetFrequencyCommand.State = frequencyResponse.State;

            if (frequencyResponse.IsSuccess) {
                UpdateFrequencyAndIQ((long)frequencyResponse.Frequency!);
            } else {
                _logger.LogError("{Message}", frequencyResponse.ErrorMessage);
                if (frequencyResponse.IsTransient) {
                    _messageLogger.AddMessage(MessageCategory.System,
                        frequencyResponse.ErrorMessage ?? "Error while getting frequency", MessageLevel.Error);
                }
            }
        });

        return frequencyResponse.IsSuccess;
    }

    private async Task<bool> GetScanRangeFromReceiver() {
        var scanRangeResponse = await _receiverMessagingService.GetScanRange();
        if (scanRangeResponse.IsSuccess) {
            await _dispatcherWrapper.InvokeAsync(() => {
                SetScanRangeCommand.State = scanRangeResponse.State;
                SetupTunerSpectrumCommand.State = scanRangeResponse.State;
                ScanRange = scanRangeResponse.ScanRange!;
                SetZoomFrequencyLimit();
            });
            _messenger.Send(new ScanRangeChangedMessage(ScanRange));
        } else {
            _logger.LogError("{Message}", scanRangeResponse.ErrorMessage);
            if (scanRangeResponse.IsTransient) {
                _messageLogger.AddMessage(MessageCategory.System,
                    scanRangeResponse.ErrorMessage ?? "Error while getting scan range", MessageLevel.Error);
            }
        }

        return scanRangeResponse.IsSuccess;
    }

    private void SetZoomFrequencyLimit(ScanRange? esmScenarioRange = null) {
        var min = Math.Floor(esmScenarioRange?.FreqFrom ?? ScanRange.FreqFrom);
        var max = Math.Ceiling(Math.Max(esmScenarioRange?.FreqTo ?? ScanRange.FreqTo, min + Bandwidth));
        ZoomFrequencyLimit = new DoubleRange(min + Bandwidth / 2, max - Bandwidth / 2);
    }

    private async Task SetFrequency(double frequency) {
        await _setFrequencySemaphore.WaitAsync();
        try {
            Frequency = frequency;
            var freqInHz = (long)UnitsNet.Frequency.FromMegahertz(Frequency).Hertz;
            var response = await _receiverMessagingService.SetFrequency(freqInHz);
            await _dispatcherWrapper.InvokeAsync(() => {
                SetFrequencyCommand.State = response.State;
                if (response.IsSuccess) {
                    UpdateFrequencyAndIQ((long)response.Frequency!);
                } else {
                    SetFrequencyCommand.State = CommandState.Error;
                    _logger.LogError("{Message}", response.ErrorMessage);
                    if (response is { ErrorMessage: not null, IsTransient: true }) {
                        _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
                    }
                }
            });
        } finally {
            _setFrequencySemaphore.Release();
        }
    }

    private async Task SetScanRange() {
        var response = await _receiverMessagingService.SetScanRange(ScanRange, IsPanoramaSpectrumEnabled);

        await _dispatcherWrapper.InvokeAsync(() => {
            if (response.IsSuccess) {
                SetScanRangeCommand.State = response.State;
                ScanRange = response.ScanRange!;
                SetZoomFrequencyLimit();
            } else {
                SetScanRangeCommand.State = CommandState.Error;
                _logger.LogError("{Message}", response.ErrorMessage);
                if (response is { ErrorMessage: not null, IsTransient: true }) {
                    _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
                }
            }
        });

        _messenger.Send(new ScanRangeChangedMessage(ScanRange));
    }

    private async Task SetupTunerSpectrum() {
        if (_isGettingParamsFromReceiver || !IsControlEnabled) {
            return;
        }

        var maxPower = ReferenceLevel;
        var minPower = ReferenceLevel - Range;
        var response = await _receiverMessagingService.SetSpectrumParams(new SpectrumParameters {
            Mode = SpectrumAveragingMode,
            RBW = FftPower,
            Time = (ushort)AveragingTime,
            MinPower = minPower,
            MaxPower = maxPower,
        });

        _dispatcherWrapper.Invoke(() => {
            SetupTunerSpectrumCommand.State = response.State;

            if (response.IsSuccess) {
                ReferenceLevel = response.SpectrumParameters!.MaxPower;
                Range = ReferenceLevel - response.SpectrumParameters.MinPower;
                AveragingTime = response.SpectrumParameters.Time;
                SpectrumAveragingMode = response.SpectrumParameters.Mode;

                if (FftPower != response.SpectrumParameters.RBW) {
                    FftPower = response.SpectrumParameters.RBW;
                    _messenger.Send(new FftPowerChangedMessage(FftPower));
                }

                _messenger.Send(new SpectrumRangeChanged(new DoubleRange(response.SpectrumParameters.MinPower,
                    response.SpectrumParameters.MaxPower)));
            } else {
                SetupTunerSpectrumCommand.State = CommandState.Error;
                _logger.LogError("{Message}", response.ErrorMessage);
                if (response is { ErrorMessage: not null}) {
                    _messageLogger.AddMessage(MessageCategory.System, response.ErrorMessage, MessageLevel.Error);
                }
            }
        });
    }

    private async Task SetPosition() {
        var receiverPoint = IsTunerPositionManual
            ? new MapPoint(PositionManualX, PositionManualY, SpatialReferences.Wgs84)
            : new MapPoint(CurrentAutoPosition?.Longitude ?? 0, CurrentAutoPosition?.Latitude ?? 0,
                SpatialReferences.Wgs84);
        if (_localStationId != null) {
            //todo send angle
            _messenger.Send(
                new PositionerModeOrManualCoordinatesChangedMessage(new ValueTuple<PositionSource, MapPoint, uint>(
                    CurrentPositionMode, receiverPoint, _localStationId.Value)));

            if (CurrentPositionMode == PositionSource.Receiver) {
                _messenger.Send(new AntennaDirectionUpdatedMessage(
                    new AntennaDirectionUpdatedData(0, _localStationId.Value)));
            }
        }

        await _manualPositionMessagingService.SetManualPositionState(CurrentPositionMode, PositionManualX,
            PositionManualY, AzimuthManual);
    }

    private async Task SetBacklight() {
        var response = await _receiverMessagingService.SetBacklightState(_isBacklightEnabled);
        IsBacklightOk = response.IsSuccess;
        if (response.IsSuccess) {
            if (response.State != null) {
                IsBacklightEnabled = (bool)response.State;
            }
        } else {
            _messageLogger.AddMessage(MessageCategory.System,
                $"Set backlight failed {response.ErrorMessage}", MessageLevel.Error);
        }
    }

    private async Task GetBacklight() {
        var response = await _receiverMessagingService.GetBacklightState();
        IsBacklightOk = response.IsSuccess;
        if (response.IsSuccess) {
            if (response.State != null) {
                IsBacklightEnabled = (bool)response.State;
            }
        } else {
            _messageLogger.AddMessage(MessageCategory.System,
                $"Get backlight failed {response.ErrorMessage}", MessageLevel.Error);
        }
    }

    private IQSetupParameters CreateIQSetupParameters() {
        var offsetInHz = (int)UnitsNet.Frequency.FromMegahertz(IQFrequencyOffset).Hertz;
        var notification = new IQSetupParameters {
            Mode = IQRecordingMode,
            PulseCounter = PulseCounter,
            PulseWait = PulseWait,
            RecordTime = RecordTime,
            Shift = offsetInHz,
            RateId = IQBandwidthKey,
            WithTimestamp = IsIqTimestampEnabled
        };
        return notification;
    }

    private void ChangedIQParams() {
        if (IsRunning) {
            _iqParamsChangedTimer.Stop();
            _iqParamsChangedTimer.Start();
        }
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void OnTimerChangedIQParams(object? obj, ElapsedEventArgs args) {
        try {
            _iqParamsChangedTimer.Stop();
            if (!IsControlEnabled) {
                return;
            }

            var parameters = CreateIQSetupParameters();
            await _receiverMessagingService.SetupIQ(parameters);
        } catch (Exception e) {
            _logger.LogError(e, "Failed to send SetupIQ command");
        }
    }
#pragma warning restore VSTHRD100 // Avoid async void methods
}
