using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using DevExpress.Data.Filtering;
using DevExpress.Mvvm;
using DevExpress.Xpf.Docking;
using DevExpress.Xpf.Docking.Base;
using DevExpress.Xpf.LayoutControl;
using Infozahyst.RSAAS.Client.Models.Indicators;
using Infozahyst.RSAAS.Client.Services;
using Infozahyst.RSAAS.Client.Services.MessagingServices.Interfaces;
using Infozahyst.RSAAS.Client.Settings;
using Infozahyst.RSAAS.Client.Tools;
using Infozahyst.RSAAS.Client.Tools.Dispatcher;
using Infozahyst.RSAAS.Client.ViewModels.Interfaces;
using Infozahyst.RSAAS.Client.ViewModels.Messages;
using Infozahyst.RSAAS.Common.Commands;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Common.Tools;
using Infozahyst.RSAAS.Core.Tools;
using Infozahyst.RSAAS.Core.Transport.MQTT;
using Infozahyst.RSAAS.Core.Transport.MQTT.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using Polly.Retry;
using SciChart.Charting.Common.Extensions;
using SciChart.Core.Extensions;
using Dock = System.Windows.Controls.Dock;
using IMessenger = CommunityToolkit.Mvvm.Messaging.IMessenger;

namespace Infozahyst.RSAAS.Client.ViewModels;

public class Message
{
    public Guid Id { get; }
    public DateTime Date { get; }
    public string? Text { get; }
    public MessageLevel Level { get; }
    public MessageCategory Category { get; }

    public Message(DateTime date, string? text, MessageLevel level, MessageCategory category) {
        Id = Guid.NewGuid();
        Date = date;
        Text = text;
        Level = level;
        Category = category;
    }
}

public class ShellViewModel : BaseViewModel, ISupportServices
{
    private const string DocumentationFilePathKey = "Documentation";
    private bool _isDebugVisible;
    private bool _isInfoVisible;
    private bool _isWarnVisible;
    private bool _isErrorVisible;
    private Message? _selectedMessage;
    private CriteriaOperator? _fixedFilter;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly MessageLogger _messageLogger;
    private readonly ITunerViewModel _tunerViewModel;
    private bool _isRunning;
    private ReceiverMode? _receiverMode;
    private readonly IReceiverMessagingService _messagingService;
    private readonly ISystemControlMessagingService _systemControlMessagingService;
    private readonly ILinkStationMessagingService _linkStationMessagingService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IFilePathLocalizationService _filePathLocalizationService;
    private readonly IReadOnlyPolicyRegistry<string> _policyRegistry;
    private readonly IAntennaRotatorMessagingService _antennaRotatorMessagingService;
    private readonly IEnumerable<IBaseMessagingService> _messagingServices;
    private readonly GeneralSettings _generalSettings;
    private IDialogService DialogService => ServiceContainer.GetService<IDialogService>();
    private IServiceContainer? _serviceContainer;
    private readonly IMessenger _messenger;
    private readonly ILogger<ShellViewModel> _logger;
    private bool _isPositionerAvailable;
    private uint _localStationId;
    private Task _initializeTask;
    private CancellationTokenSource _cancellationTokenSource = new();
    private readonly IDispatcherWrapper _dispatcher;
    private string _version;
    private StationType _stationType;
    private GetStationInfoResponse _localStationInfo;
    private bool _isControlEnabled;

    IServiceContainer ISupportServices.ServiceContainer { get { return ServiceContainer; } }

    protected IServiceContainer ServiceContainer {
        get { return _serviceContainer ??= new ServiceContainer(this); }
    }

    public bool IsRunning {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    public bool IsDebugVisible {
        get => _isDebugVisible;
        set {
            if (Set(ref _isDebugVisible, value)) {
                UpdateFixedFilter();
            }
        }
    }

    public bool IsInfoVisible {
        get => _isInfoVisible;
        set {
            if (Set(ref _isInfoVisible, value)) {
                UpdateFixedFilter();
            }
        }
    }

    public bool IsWarnVisible {
        get => _isWarnVisible;
        set {
            if (Set(ref _isWarnVisible, value)) {
                UpdateFixedFilter();
            }
        }
    }

    public bool IsErrorVisible {
        get => _isErrorVisible;
        set {
            if (Set(ref _isErrorVisible, value)) {
                UpdateFixedFilter();
            }
        }
    }

    public Message? SelectedMessage {
        get => _selectedMessage;
        set => Set(ref _selectedMessage, value);
    }

    public CriteriaOperator? FixedFilter {
        get => _fixedFilter;
        set => Set(ref _fixedFilter, value);
    }

    public ReceiverMode? ReceiverMode {
        get => _receiverMode;
        set => Set(ref _receiverMode, value);
    }

    public bool IsControlEnabled {
        get => _isControlEnabled;
        set => Set(ref _isControlEnabled, value);
    }

    public string Title => $"RSAAS {_version} [{_stationType} #{_localStationId}]";

    public ReceiverIndicator ReceiverIndicator { get; }
    public PositionerIndicator PositionerIndicator { get; } = new();
    public RotatorIndicator RotatorIndicator { get; } = new();
    public SystemIndicator SystemIndicator { get; } = new();
    public LogIndicator LogIndicator { get; } = new();

    public DelegateCommand<DockOperationCompletedEventArgs> DockOperationCompletedCommand { get; }
    public DelegateCommand ClearMessagesCommand { get; }
    public IAsyncCommand RunCommand { get; }
    public IAsyncCommand StopCommand { get; }
    public ICommand<CancelEventArgs> CloseCommand { get; }
    public DelegateCommand OpenDocumentationCommand { get; }
    public IAsyncCommand LoadedCommand { get; set; }
    public IAsyncCommand ToggleControlCommand { get; set; }

    public ShellViewModel(MessageLogger messageLogger, ITunerViewModel tunerViewModel,
        ISettingsViewModel settingsViewModel, IOptions<GeneralSettings> generalOptions,
        IReceiverMessagingService messagingService, ISystemControlMessagingService systemControlMessagingService,
        IAntennaRotatorMessagingService antennaRotatorMessagingService,
        IEnumerable<IBaseMessagingService> messagingServices, IMessenger messenger,
        ILogger<ShellViewModel> logger, IFilePathLocalizationService filePathLocalizationService,
        IReadOnlyPolicyRegistry<string> policyRegistry, ILinkStationMessagingService linkStationMessagingService,
        IServiceProvider serviceProvider, IDispatcherWrapper? dispatcherWrapper = null) {
        IsInfoVisible = true;
        IsWarnVisible = true;
        IsErrorVisible = true;
        _messageLogger = messageLogger;
        _tunerViewModel = tunerViewModel;
        _messagingService = messagingService;
        _systemControlMessagingService = systemControlMessagingService;
        _linkStationMessagingService = linkStationMessagingService;
        _serviceProvider = serviceProvider;
        _messenger = messenger;
        _logger = logger;
        _filePathLocalizationService = filePathLocalizationService;
        _policyRegistry = policyRegistry;
        _antennaRotatorMessagingService = antennaRotatorMessagingService;
        _messagingServices = messagingServices;
        _dispatcher = dispatcherWrapper ?? new DispatcherWrapper(Application.Current.Dispatcher);
        _generalSettings = generalOptions.Value;

        ReceiverIndicator = new ReceiverIndicator(_messenger, generalOptions, dispatcherWrapper);
        DockOperationCompletedCommand = new DelegateCommand<DockOperationCompletedEventArgs>(OnDockOperationCompleted);
        ClearMessagesCommand = new DelegateCommand(ClearMessages);
        RunCommand = new AsyncCommand(RunCommandMethod);
        CloseCommand = new AsyncCommand<CancelEventArgs>(Close);
        OpenDocumentationCommand = new DelegateCommand(OpenDocumentation);
        StopCommand = new AsyncCommand(Stop);
        LoadedCommand = new AsyncCommand(async () => await RunCommand.ExecuteAsync(null));
        ToggleControlCommand = new AsyncCommand(() => UpdateControlStatus(true));
#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
        settingsViewModel.Resetting += async () => await StopCommand.ExecuteAsync(null);
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
      
        _messenger.Register<ReceiverModeChangedMessage>(this, (_, m) => ReceiverMode = m.Value);
        _messenger.Register<PositionerAvailabilityChangedMessage>(this, 
            (_, m) => _isPositionerAvailable = m.Value);

        _systemControlMessagingService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _systemControlMessagingService.IndicatorsChanged += SystemControlMessagingServiceOnIndicatorsChanged;
        _systemControlMessagingService.EndpointsTimeoutsChanged +=
            OnSystemControlMessagingServiceOnEndpointsTimeoutsChanged;
        _linkStationMessagingService.ControlClientChanged += OnControlClientChanged;
        _messagingService.DeviceInfoUpdated += OnDeviceInfoUpdated;
    }
    
    private void OnControlClientChanged(object? sender, ControlClientChangedNotification e) {
        if (e.Force) {
            // current client controlling or slave controlled by local station
            var isMessageShouldBeIgnored = e.ControllingAppId == _generalSettings.ApplicationId ||
                                           e.ControlClientStationId == _localStationId;
            if (!isMessageShouldBeIgnored) {
                _messageLogger.AddMessage(MessageCategory.System,
                    $"Control client of station #{e.StationId} changed to: ip - {e.ControlClientIpAddress}, station ID - " +
                    $"{e.ControlClientStationId?.ToString() ?? "controlled by client"}",
                    MessageLevel.Warn);
            }
        }

        if (e.StationId != _localStationId) {
            return;
        }

        var isCurrentClientControlling = e.ControllingAppId == _generalSettings.ApplicationId;
        if (!isCurrentClientControlling) {
            _dispatcher.Invoke(() => {
                IsControlEnabled = false;
                SystemIndicator.ControlClientStationId = e.ControlClientStationId;
                SystemIndicator.ControlClientIp = e.ControlClientIpAddress;
            });

            _messenger.Send(new ControlStatusChangedMessage(IsControlEnabled));
        }
    }

    private async Task UpdateControlStatus(bool force = false) {
        try {
            var serverAddress = _messenger.Send(new GetServerIpAddressRequestMessage());
            var ip = await SelfAddressHelper.GetLocalIpAddress(serverAddress.Response ?? "");
            var response = await _linkStationMessagingService.SetClientApp(ip, force);
            await RefreshControlClientInfo();
            await _dispatcher.InvokeAsync(() => IsControlEnabled = response.IsSuccess);
            _messenger.Send(new ControlStatusChangedMessage(IsControlEnabled));
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private async Task RefreshControlClientInfo() {
        try {
            var response = await _linkStationMessagingService.GetClientApp();
            if (response.IsSuccess) {
                await _dispatcher.InvokeAsync(() => {
                    SystemIndicator.ControlClientStationId = response.ControlClientStationId;
                    SystemIndicator.ControlClientIp = response.ControlClientIpAddress;
                });
            }
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }
    }

    private Task RunCommandMethod() {
        var messagingServices = _serviceProvider.GetServices<IBaseMessagingService>();
        _ = Task.Run(async () => {
            await Task.WhenAll(messagingServices.Select(a => a.Connect()));
        });

        return Task.CompletedTask;
    }

    private void OnSystemControlMessagingServiceOnEndpointsTimeoutsChanged(object? sender,
        EndpointsTimeoutsChangedNotification e) {
        Task.Run(async () => {
            var result = await _systemControlMessagingService.GetStationInfo();
            if (result.IsSuccess) {
                LoadEndpointsTimeouts(result.EndpointsTimeouts);
            }
        });
    }

    private async Task OnConnectionStatusChanged(bool isConnected) {
        if (isConnected) {
            if (_initializeTask is not { IsCompleted: false }) {
                _initializeTask = Task.Run(Initialize)
                    .ContinueWith(task => {
                        _logger.LogError(task.Exception, "Failed initialize after connection status changed");
                    }, TaskContinuationOptions.OnlyOnFaulted);
            }
        } else {
            ChangeIsRunningFlag(isConnected);
            UpdateConnectionIndicators(isConnected);
            _messenger.Send(new ServerConnectionStateChangedMessage(isConnected));
            await TasksHelper.SafeComplete(_cancellationTokenSource, _initializeTask);
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
        }
    }

    private void SystemControlMessagingServiceOnIndicatorsChanged(object? sender, IndicatorAggregateNotification? e) {
        if (e == null) {
            return;
        }

        _dispatcher.Invoke(() => {
            ReceiverIndicator.Connection = e.ReceiverStatus.ConnectionStatus.ToIndicatorStatus();
            ReceiverIndicator.Control = e.ReceiverStatus.CommandStatus;

            PositionerIndicator.Connection = e.PositionerStatus.ConnectionStatus.ToIndicatorStatus();
            PositionerIndicator.Control = e.PositionerStatus.CommandStatus;
            if (PositionerIndicator.Connection is IndicatorStatus.None or IndicatorStatus.Error) {
                PositionerIndicator.Gnss = null;
            }

            RotatorIndicator.Connection = e.RotatorStatus.ConnectionStatus.ToIndicatorStatus();
            RotatorIndicator.Control = e.RotatorStatus.CommandStatus;
        });

        _messenger.Send(new RotatorIndicatorChangedMessage(RotatorIndicator));
        _messenger.Send(new PositionerIndicatorChangedMessage(PositionerIndicator));
        _messenger.Send(new ReceiverConnectionChangedMessage(ReceiverIndicator.Connection == IndicatorStatus.Ok));
    }

    private async Task Close(CancelEventArgs e) {
        bool confirmParkRotator = false;
        if (!_tunerViewModel.IsRotatorParked && _localStationInfo.StationType != StationType.ArchontT) {
            if (ConfirmParkRotator()) {
                confirmParkRotator = true;
                e.Cancel = true;
            }
        }

        if (ConfirmClosing()) {
            if (confirmParkRotator) {
                // Send command to server without waiting for the result
                _ = Task.Run(() => _antennaRotatorMessagingService.ParkAntennaRotator());
            }

            if (_tunerViewModel.IsIQRecordActive) {
                _tunerViewModel.IsIQRecordActive = false;
                await _tunerViewModel.StartStopIQCommand.ExecuteAsync(null!);
            }

            Application.Current.Shutdown();
        } else {
            e.Cancel = true;
            if (confirmParkRotator) {
                // Send command and wait for the result
                await _tunerViewModel.ParkRotatorCommand.ExecuteAsync(null);
            }
        }
    }

    private bool ConfirmParkRotator() {
        var result = DialogService.ShowDialog(
            dialogButtons: MessageButton.YesNo,
            title: "Do you want to park the rotator?",
            viewModel: null
        );

        return result == MessageResult.Yes;
    }

    private bool ConfirmClosing() {
        var result = DialogService.ShowDialog(
            dialogButtons: MessageButton.OKCancel,
            title: "Are you sure you want to exit?",
            viewModel: null
        );

        return result == MessageResult.OK;
    }

    private void OpenDocumentation() {
        var filename = _filePathLocalizationService.GetStringOrDefault(DocumentationFilePathKey);
        if (filename.IsNullOrEmpty()) {
            return;
        }

        using Process fileOpener = new Process();

        fileOpener.StartInfo.FileName = "explorer";
        fileOpener.StartInfo.Arguments = "\"" + filename + "\"";
        fileOpener.Start();
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void OnDeviceInfoUpdated(object? sender, DeviceInfoUpdateNotification notification) {
        _messenger.Send(
            new DeviceInfoReceivedMessage((notification.DeviceInfo, _localStationId, notification.DateTime)));
        var synthesizerStatus = notification.DeviceInfo.SynthesizerInfo.Status;
        var gnssStatus = notification.DeviceInfo.GnssInfo.Status;

        await _dispatcher.InvokeAsync(() => {
            ReceiverIndicator.Synthesizer = synthesizerStatus;
            if (_isPositionerAvailable && PositionerIndicator.Connection != IndicatorStatus.None &&
                PositionerIndicator.Connection != IndicatorStatus.Error) {
                PositionerIndicator.Gnss = gnssStatus;
            }
        });
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    private void UpdateConnectionIndicators(bool isConnected) {
        _dispatcher.Invoke(() => {
            SystemIndicator.ServerConnection = isConnected ? IndicatorStatus.Ok : IndicatorStatus.Error;
        });
        
        if (!isConnected) {
            _dispatcher.InvokeAsync(() => {
                ReceiverIndicator.Connection = IndicatorStatus.Error;
                PositionerIndicator.Connection = IndicatorStatus.Error;
                RotatorIndicator.Connection = IndicatorStatus.Error;
            });
        }
    }

    private void ChangeIsRunningFlag(bool state) {
        _dispatcher.Invoke(() => IsRunning = state);
        _messenger.Send(new StatusChangedMessage(IsRunning));
    }

    private async Task Initialize() {
        _version = GetAssemblyVersion();
        var cancellationToken = _cancellationTokenSource.Token;
        var isConnected = _systemControlMessagingService.IsConnected;
        var policy = _policyRegistry.Get<AsyncRetryPolicy<bool>>(PolicyNameConstants
            .WaitAndRetryForeverByBooleanResultAsync);

        GetStationInfoResponse? stationInfoResponse = null;
        await policy.ExecuteAsync(async token => {
            stationInfoResponse = await _systemControlMessagingService.GetStationInfo(token);
            return stationInfoResponse.IsSuccess;
        }, cancellationToken);

        if (stationInfoResponse != null) {
            _localStationId = stationInfoResponse.StationId;
            _stationType = stationInfoResponse.StationType;
            _localStationInfo = stationInfoResponse;
            await _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(Title)));
            LoadEndpointsTimeouts(stationInfoResponse.EndpointsTimeouts);
            _messenger.Send(new LocalStationInfoChangedMessage(stationInfoResponse));
            UpdateLocalStationId(stationInfoResponse.StationId);
        }

        ReceiverIndicator.Initialize();
        UpdateConnectionIndicators(isConnected);
        
        GetIndicatorsResponse? indicators = null;
        await policy.ExecuteAsync(async token => {
            indicators = await _systemControlMessagingService.GetIndicators(_localStationId, token);
            return indicators.IsSuccess;
        }, cancellationToken);

        if (indicators != null) {
            SystemControlMessagingServiceOnIndicatorsChanged(null, indicators.Notification);
        }

        await UpdateControlStatus();

        _messenger.Send(new ServerConnectionStateChangedMessage(isConnected));
        ChangeIsRunningFlag(true);
        _messageLogger.MessageReceived -= OnMessageReceived;
        _messageLogger.MessageReceived += OnMessageReceived;
    }

    private void LoadEndpointsTimeouts(EndpointTimeoutInfo[] timeouts) {
        foreach (var messagingService in _messagingServices) {
            messagingService.LoadEndpointsTimeouts(timeouts);
        }
    }

    private Task Stop() {
        ReceiverMode = null;

        ChangeIsRunningFlag(false);
        return Task.CompletedTask;
    }

    private void OnMessageReceived(MessageLog log) {
        _dispatcher.InvokeAsync(() => AddMessage(log.Category, log.Text, log.Level, log.Counters),
            DispatcherPriority.Background);
    }

    private void AddMessage(MessageCategory category, string? text, MessageLevel level,
        Dictionary<CounterName, string>? counters = null) {
        if (category == MessageCategory.Counters) {
            if (counters != null &&
                counters.TryGetValue(CounterName.PdwReceivingSpeed, out string? receivingSpeedString)) {
                SystemIndicator.PdwReceivingSpeed = ulong.Parse(receivingSpeedString);
            }

            if (counters != null &&
                counters.TryGetValue(CounterName.PdwTotalReceived, out string? totalReceivedString)) {
                SystemIndicator.TotalPdwReceived = ulong.Parse(totalReceivedString);
            }

            return;
        }

        LogIndicator.AddMessage(new Message(DateTime.Now, text, level, category));
    }

    private void ClearMessages() {
        LogIndicator.Clear();
    }

    private void UpdateFixedFilter() {
        var group = new GroupOperator(GroupOperatorType.And);
        var levelGroup = new GroupOperator(GroupOperatorType.Or);

        if (IsDebugVisible) {
            levelGroup.Operands.Add(new BinaryOperator(nameof(Message.Level), nameof(MessageLevel.Debug)));
        }

        if (IsInfoVisible) {
            levelGroup.Operands.Add(new BinaryOperator(nameof(Message.Level), nameof(MessageLevel.Info)));
        }

        if (IsWarnVisible) {
            levelGroup.Operands.Add(new BinaryOperator(nameof(Message.Level), nameof(MessageLevel.Warn)));
        }

        if (IsErrorVisible) {
            levelGroup.Operands.Add(new BinaryOperator(nameof(Message.Level), nameof(MessageLevel.Error)));
        }

        if (levelGroup.Operands.IsNullOrEmptyList()) {
            levelGroup.Operands.Add(new BinaryOperator("", ""));
        }

        group.Operands.Add(levelGroup);
        FixedFilter = group;
    }

    private void OnDockOperationCompleted(DockOperationCompletedEventArgs eventArgs) {
        if (eventArgs.Item is not LayoutPanel layoutPanel) {
            return;
        }

        var layoutControl = (layoutPanel.Content as UserControl)?.FindVisualChild<LayoutControl>();
        var dockPosition = (layoutPanel.Parent as AutoHideGroup)?.DockType;
        var dockLayoutManager = eventArgs.Source as DockLayoutManager;

        if (dockLayoutManager != null) {
            layoutPanel.TabCaptionTemplate = (DataTemplate)dockLayoutManager.Resources[
                dockPosition == Dock.Left
                    ? "RotatedTabCaptionTemplate"
                    : "DefaultTabCaptionTemplate"
            ];
        }
    }

    private string GetAssemblyVersion() {
        var assembly = Assembly.GetExecutingAssembly();
        var fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
        string version = fileVersionInfo.FileVersion!;
        return version;
    }
    
    private void UpdateLocalStationId(uint localStationId) {
        var messagingServices = _serviceProvider!.GetServices<IBaseMessagingService>();
        foreach (var messagingService in messagingServices) {
            messagingService.ChangeLocalStationId(localStationId);
        }
    }
}
