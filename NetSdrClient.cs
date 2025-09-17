using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Interfaces;
using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Server.DataStream;
using Infozahyst.RSAAS.Server.Exceptions;
using Infozahyst.RSAAS.Server.Models;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Commands;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Enums;
using Infozahyst.RSAAS.Server.Receiver.NetSdr.Logger;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Polly;
using Polly.Retry;
using TimeoutException = System.TimeoutException;

namespace Infozahyst.RSAAS.Server.Receiver.NetSdr;

public abstract class NetSdrClient : IControlClient
{
    private const int TaskTimeOut = 1;
    protected const short Dbm10 = 10;
#pragma warning disable IDE0300 // Simplify collection initialization
    private readonly ReadOnlyMemory<byte> _nakResponse = new([0x02, 0x00]);
#pragma warning restore IDE0300 // Simplify collection initialization
    protected readonly ILogger<NetSdrClient> _logger;
    protected readonly IFeatureManager _featureManager;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _commandTimeout;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly IPEndPoint? _endPoint;
    private readonly string _noConnectionMessage;
    protected readonly IOptions<ReceiverSettings> _receiverOptions;
    private readonly IDataClientFactory _dataClientFactory;
    private readonly int _headerSize = Marshal.SizeOf<NetSdrCommandHeader>();
    private readonly IndicatorAggregator _indicatorAggregator;
    private ITcpClient? _client;
    private Task? _listenForDataTask;
    private CancellationTokenSource? _connectCts;
    private readonly TimeSpan _commandQueueTimeout;

    private readonly ConcurrentStack<NetSdrCommandHeader> _activeCommands = new();
    private event ResponseReceivedAction<NetSdrCommandHeader, byte>? ResponseReceived;
    public event Action<GetFrequencyCommandResult>? UpdatedFrequencyReceived;
    public event Action<GetGnssPositionCommandResult>? UpdatedGnssPositionReceived;
    public event Action<SetStreamingIpAddressCommand>? UpdatedStreamingIpAddressReceived;
    public event Action<GetStreamingStateCommandResult>? UpdatedStreamingStateReceived;
    public event Action<GetDeviceInfoCommandResult>? UpdatedDeviceInfoReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConfigured { get; set; }

    public virtual bool IsAntennaAzimuthSupported => true;

    public NetSdrClient(IOptions<ReceiverSettings> receiverOptions, IDataClientFactory dataClientFactory,
        IndicatorAggregator indicatorAggregator, ILogger<NetSdrClient> logger, IRetryProvider retryProvider,
        IFeatureManager featureManager) {
        _receiverOptions = receiverOptions;
        _dataClientFactory = dataClientFactory;
        _indicatorAggregator = indicatorAggregator;
        _logger = logger;
        _featureManager = featureManager;
        _retryPolicy = retryProvider.GetOrAddDynamicPolicy<AsyncRetryPolicy>($"{nameof(NetSdrClient)}",
            receiverOptions.Value.CommandRetryCount, receiverOptions.Value.CommandRetryInterval);
        var receiverSettings = receiverOptions.Value;
        if (IPAddress.TryParse(receiverSettings.IpAddress, out var ipAddress)) {
            _endPoint = new IPEndPoint(ipAddress, receiverOptions.Value.ControlPort);
            IsConfigured = true;
        } else {
            IsConfigured = false;
        }

        _connectionTimeout = receiverSettings.ConnectionTimeout;
        _commandTimeout = receiverSettings.DefaultCommandTimeout;
        _commandQueueTimeout = receiverSettings.CommandQueueTimeout;
        _noConnectionMessage = $"Can't connect to receiver {_endPoint?.Address}:{_endPoint?.Port}";
    }

    #region Commands

    public abstract Task<NetSdrResponse<float>> SetAntennaAzimuth(float azimuth);
    public abstract Task<NetSdrResponse<bool>> SetDDCState(int shift, byte rateId, State state, IQPacketFormat format);
    public abstract Task<NetSdrResponse<long>> SetFrequency(long frequency);
    public abstract Task<NetSdrResponse<ushort>> SetPayloadSize(ushort payloadSize, byte dataItem);
    public abstract Task<NetSdrResponse<SpectrumParameters>> SetSpectrumParams(SpectrumParameters parameters);
    public abstract Task<NetSdrResponse<float>> GetAntennaAzimuth();
    public abstract Task<NetSdrResponse<bool>> GetBacklightState();
    public abstract Task<NetSdrResponse<bool>> SetBacklightState(bool state);
    public abstract Task<NetSdrResponse<ChannelFunction>> GetChannelFunctions();
    public abstract Task<NetSdrResponse<DeviceInfo>> GetDeviceInfo();
    public abstract Task<NetSdrResponse<long>> GetFrequency();
    public abstract Task<NetSdrResponse<SpectrumParameters>> GetSpectrumParams();
    public abstract Task<NetSdrResponse<GnssPosition>> GetGnssPosition();
    public abstract Task<NetSdrResponse<ScanRange>> GetScanRange();
    public abstract Task<NetSdrResponse<DeviceSuppressedBand[]>> GetSuppressedBands();
    public abstract Task<NetSdrResponse<ChannelFunction>> SetChannelFunctions(ChannelFunction functions);
    public abstract Task<NetSdrResponse<ScanRange>> SetScanRange(ScanRange range);
    public abstract Task<NetSdrResponse<bool>> SetStreamingIpAddress(IPAddress ipAddress);
    public abstract Task<NetSdrResponse<bool>> SetSpectrumState(SpectrumType spectrumType, bool enabled);
    public abstract Task<NetSdrResponse<DeviceSuppressedBand[]>> SetSuppressedBands(DeviceSuppressedBand[] bands);
    public abstract Task<NetSdrResponse<ScanRange>> GetFrequencyRange();
    public abstract Task<NetSdrResponse<InstantViewBandwidth>> SetInstantViewBandwidth(InstantViewBandwidth bandwidth);
    public abstract Task<NetSdrResponse<InstantViewBandwidth>> GetInstantViewBandwidth();
    public abstract Task<NetSdrResponse<AgcParameters>> SetAgc(AgcParameters parameters);
    public abstract Task<NetSdrResponse<AgcParameters>> GetAgc();

    public abstract Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> SetAttenuators(float rfAttenuator,
        float ifAttenuator);

    public abstract Task<NetSdrResponse<(float RfAttenuator, float IfAttenuator)>> GetAttenuators();
    public abstract Task StartStreaming(CaptureMode captureMode);
    public abstract Task StopStreaming(CaptureMode captureMode);

    public abstract Task<NetSdrResponse<bool>> SetDDCSetup(IQTDOAModeId mode, int pulseCounter = 10, int pulseWait = 20,
        int recordTime = 1000);

    public abstract Task<NetSdrResponse<GetStreamingStateCommandResult>> GetStreamingState();
    public abstract Task<NetSdrResponse<ScanListItem[]>> SetScanList(ScanListItem[] items);
    public abstract Task<NetSdrResponse<ScanListItem[]>> GetScanList();
    public abstract Task<NetSdrResponse<GetDDCSetupCommandResult>> GetDDCSetup();

    public abstract Task<NetSdrResponse<GetDDCStateCommandResult>> GetDDCState();

    // Do not remove this method!
    public virtual async Task<NetSdrResponse<GetLastErrorCommandResult>> GetLastError() {
        try {
            var command = new GetLastErrorCommand();
            var result = await SendCommand<GetLastErrorCommand, GetLastErrorCommandResult>(command);
            return NetSdrResponse<GetLastErrorCommandResult>.CreateSuccess(result);
        } catch (Exception e) {
            return NetSdrResponse<GetLastErrorCommandResult>.CreateError(e.Message);
        }
    }

    public abstract Task<NetSdrResponse<GetTimeCommandResult>> GetTime();
    #endregion

    public virtual async Task<NetSdrResponse<bool>> Connect() {
        if (_endPoint == null) {
            return new NetSdrResponse<bool> { State = CommandState.Error, Value = false };
        }

        CancellationTokenSource? connectTimeoutCts = null;
        try {
            await Disconnect();

            _connectCts = new CancellationTokenSource();

            _client = _dataClientFactory.CreateClient(new IPEndPoint(_endPoint.Address, _endPoint.Port),
                categoryName: nameof(NetSdrClient));
            _client.ConnectionStatusChanged += TcpDataClientConnectionStatusChanged;

            connectTimeoutCts = new CancellationTokenSource(_connectionTimeout);
            var result = await _client.ConnectAsync(connectTimeoutCts.Token);

            _listenForDataTask = Task.Factory.StartNew(async () => await ListenForData(_connectCts.Token),
                _connectCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            return result
                ? NetSdrResponse<bool>.CreateSuccess(result)
                : NetSdrResponse<bool>.CreateError("Failed to connect");
        } catch (Exception) {
            return NetSdrResponse<bool>.CreateError(_noConnectionMessage);
        } finally {
            connectTimeoutCts?.Dispose();
        }
    }

    public virtual async Task Disconnect() {
        try {
            try {
                if (_connectCts != null) {
                    await _connectCts.CancelAsync();
                }

                if (_listenForDataTask != null) {
                    await _listenForDataTask.WaitAsync(TimeSpan.FromSeconds(TaskTimeOut));
                }
            } catch (OperationCanceledException) {
            } catch (TimeoutException) { }

            if (_client != null) {
                ConnectionStatusChanged?.Invoke(this, false);
                _client.ConnectionStatusChanged -= TcpDataClientConnectionStatusChanged;
                await _client.Disconnect();
            }
        } finally {
            _client = null;
            _connectCts?.Dispose();
            _connectCts = null;
        }
    }

    public async ValueTask DisposeAsync() {
        _semaphore.Dispose();
        if (_client != null) {
            await _client!.DisposeAsync();
        }

        _connectCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<TResponse> SendCommand<TRequest, TResponse>(TRequest command)
        where TRequest : struct, INetSdrCommand
        where TResponse : struct, INetSdrCommand {
        var result = await SendCommand<TRequest, TResponse, Null>(command);
        return result.Item1;
    }

    protected async Task<(TResponse Response, TDataItem[] Data)> SendCommand<TRequest, TResponse, TDataItem>(
        TRequest command, TDataItem[]? data = null)
        where TRequest : struct, INetSdrCommand
        where TResponse : struct, INetSdrCommand
        where TDataItem : struct, ISerializable {
        (TResponse Response, TDataItem[] Data) result;
        var isSet = command.Header.Type == NetSdrMessageType.Set;
        var commandName =
            $"{command.Header.Type}-{command.Header.ControlItem}-0x{(ushort)command.Header.ControlItem:X}";
        try {
            result = await _retryPolicy.ExecuteAsync(
                async context => {
                    int retryCount =
                        (context.TryGetValue("retrycount", out var retryObject) && retryObject is int count)
                            ? count
                            : 0;
                    return await SendCommandInner<TRequest, TResponse, TDataItem>(commandName, command, data,
                        retryCount);
                }, new Context());
            _indicatorAggregator.UpdateReceiverCommandStatus(commandName, CommandStatus.Ok);
        } catch {
            _indicatorAggregator.UpdateReceiverCommandStatus(commandName,
                isSet ? CommandStatus.Warn : CommandStatus.Error);
            throw;
        }

        return result;
    }

    protected virtual async Task<(TResponse Response, TDataItem[] Data)> SendCommandInner<TRequest, TResponse,
        TDataItem>(
        string commandName, TRequest command, TDataItem[]? data = null, int? retryCount = null)
        where TRequest : struct, INetSdrCommand
        where TResponse : struct, INetSdrCommand
        where TDataItem : struct {
        if (_client is null or { Connected: false }) {
            throw new ConnectionException(_noConnectionMessage);
        }

        var retryLogPrefix = retryCount.GetValueOrDefault(0) == 0 ? "" : $"[Retry {retryCount}] ";

        if (!await _semaphore.WaitAsync(_commandQueueTimeout)) {
            var message =
                $"{retryLogPrefix}{commandName}: timeout while waiting in command queue [{_commandQueueTimeout}]";
            _logger.LogWarning(message);
            throw new Exceptions.TimeoutException(message);
        }

        var isSet = command.Header.Type == NetSdrMessageType.Set;
        var method = $"{command.Header.Type}{command.Header.ControlItem}";

        try {
            _logger.LogInformation("{Prefix}Request - {RequestData}", retryLogPrefix,
                NetSdrLogger.CreateCommandLogData(command));
            foreach (var item in data ?? []) {
                _logger.LogInformation("Request data-item - {Data}",
                    NetSdrLogger.CreateDataItemLogData(item, command.Header));
            }

            _activeCommands.Push(new TResponse().Header);

            var requestSize = Marshal.SizeOf<TRequest>();
            var responseSize = Marshal.SizeOf<TResponse>();
            int dataItemSize = Marshal.SizeOf<TDataItem>();

            var completionSource =
                new TaskCompletionSource<(TResponse, TDataItem[])>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var commandTimeout = _commandTimeout;
            if (_receiverOptions.Value.OverrideCommandTimeouts.TryGetValue(commandName, out var timeSpan)) {
                commandTimeout = timeSpan;
            }

            using var cancellationTokenSource = new CancellationTokenSource(commandTimeout);
            var cancellationToken = cancellationTokenSource.Token;

            cancellationToken.Register(() => {
                var timeoutException =
                    new TimeoutException($"{retryLogPrefix}{commandName}: timeout [{commandTimeout}]");
                completionSource.TrySetException(timeoutException);
            });

            Task OnDataReceived(NetSdrCommandHeader header, Memory<byte> memory, bool nakReceived) {
                if (nakReceived) {
                    completionSource.TrySetException(new NetSdrNakReceivedException());
                    return Task.CompletedTask;
                }

                if (header.FullSize < responseSize) {
                    completionSource.TrySetException(new Exception(
                        $"Invalid response for command 0x{(ushort)command.Header.ControlItem:X}-{command.Header.Type}."));
                }

                var res = MemoryMarshal.AsRef<TResponse>(memory.Span[..responseSize]);
                _logger.LogInformation("{Prefix}Response - {ResponseData}", retryLogPrefix,
                    NetSdrLogger.CreateCommandLogData(res, command.Header.Type));
                if (header.FullSize - responseSize == 0) {
                    completionSource.TrySetResult((res, Array.Empty<TDataItem>()));
                }

                var resData = new TDataItem[(header.FullSize - responseSize) / dataItemSize];

                for (int i = 0; i < resData.Length; i++) {
                    var startIndex = responseSize + dataItemSize * i;
                    resData[i] = MemoryMarshal.AsRef<TDataItem>(memory.Span[startIndex..]);
                    _logger.LogInformation("Response data-item - {Data}",
                        NetSdrLogger.CreateDataItemLogData(resData[i], command.Header, command.Header.Type));
                }

                completionSource.TrySetResult((res, resData));
                return Task.CompletedTask;
            }

            using var memory = MemoryPool<byte>.Shared.Rent(ushort.MaxValue);
            MemoryMarshal.Write(memory.Memory.Span, in command);
            var totalSize = requestSize;

            if (data != null) {
                for (int i = 0; i < data.Length; i++) {
                    var startIndex = requestSize + dataItemSize * i;
                    MemoryMarshal.Write(memory.Memory.Span[startIndex..], in data[i]);
                    totalSize += dataItemSize;
                }
            }

            (TResponse, TDataItem[]) res;
            ResponseReceived += OnDataReceived;
            try {
                await _client.SendAsync(memory.Memory[..totalSize], cancellationToken);

                res = await completionSource.Task;
            } finally {
                ResponseReceived -= OnDataReceived;
            }

            _indicatorAggregator.UpdateReceiverCommandStatus(method, CommandStatus.Ok);
            return res;
        } catch (TimeoutException timeoutException) {
            var status = isSet ? CommandStatus.Warn : CommandStatus.Error;
            _indicatorAggregator.UpdateReceiverCommandStatus(method, status);
            _logger.LogWarning("{Message}", timeoutException.Message);
            throw new Exceptions.TimeoutException(timeoutException.Message, timeoutException);
        } catch (Exception ex) {
            var status = isSet ? CommandStatus.Warn : CommandStatus.Error;
            _indicatorAggregator.UpdateReceiverCommandStatus(method, status);
            _logger.LogError(exception: ex, message: "{Prefix}{CommandName}: ERROR", retryLogPrefix, commandName);
            throw new CommandProcessException(commandName, ex.Message, ex);
        } finally {
            _activeCommands.TryPop(out NetSdrCommandHeader _);
            _semaphore.Release();
        }
    }

    private async Task ListenForData(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            try {
                if (_client is not { Connected: true }) {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var stream = _client.GetStream();
                var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
                await ReadPipeAsync(reader, cancellationToken);
            } catch (OperationCanceledException) {
                _logger.LogInformation("Listening from stream was cancelled. No more data will processed");
                break;
            } catch (InvalidOperationException) {
                _logger.LogWarning(_noConnectionMessage);
            } catch (Exception ex) {
                if (ex.InnerException is SocketException {
                        SocketErrorCode: SocketError.TimedOut
                        or SocketError.ConnectionReset
                        or SocketError.ConnectionAborted
                    }) {
                    continue;
                }

                _logger.LogError(exception: ex, message: "");
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private void TcpDataClientConnectionStatusChanged(object? sender, bool e) {
        _logger.LogInformation("Connection changed state: {State}", e ? "connected" : "disconnected");
        ConnectionStatusChanged?.Invoke(sender, e);
    }

    private static bool VerifyHeader(NetSdrCommandHeader actual, NetSdrCommandHeader expected) {
        if (actual.Type == expected.Type && actual.ControlItem == expected.ControlItem) {
            return true;
        }

        return false;
    }

    private async Task ReadPipeAsync(PipeReader reader, CancellationToken cancellationToken) {
        try {
            ReadResult result;
            do {
                result = await ReadReplyFromPipe(reader, NotificationOrDataArrived, cancellationToken);
                if (result.IsCompleted && IsTcpCloseWait(_endPoint)) {
                    _client?.Close();
                    break;
                }
            } while (!(result.IsCompleted || result.IsCanceled));
        } finally {
            await reader.CompleteAsync();
        }
    }

    private async Task NotificationOrDataArrived(NetSdrCommandHeader currentHeader, Memory<byte> memory,
        bool isNakReceived) {
        var isResponse = _activeCommands.TryPeek(out NetSdrCommandHeader expectedHeader) &&
                         VerifyHeader(currentHeader, expectedHeader);

        _logger.LogTrace("Expected: {@Expected} actual: {@Actual}", expectedHeader, currentHeader);

        if ((isResponse || isNakReceived) && ResponseReceived != null) {
            await ResponseReceived(currentHeader, memory, isNakReceived);
        } else {
            InvokeNotifications(currentHeader, memory);
        }
    }

    private async Task<ReadResult> ReadReplyFromPipe(PipeReader reader,
        ResponseReceivedAction<NetSdrCommandHeader, byte> notificationOrDataArrived,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(reader, nameof(reader));

        var result = await reader.ReadAsync(cancellationToken);
        if (Debugger.IsAttached) {
            Debug.WriteLine($"Read: {String.Concat(result.Buffer.First.Span.ToArray().Select(a => $"{a:X}"))}");
        }

        var buffer = result.Buffer;
        var header = new NetSdrCommandHeader(NetSdrMessageType.Get, NetSdrControlItem.None, 0);

        var nakReceived = false;
        while (TryReadHeader(ref buffer, ref header, ref nakReceived)) {
            cancellationToken.ThrowIfCancellationRequested();

            if (nakReceived) {
                await notificationOrDataArrived.InvokeAsync(header, Memory<byte>.Empty, nakReceived);

                buffer = buffer.Slice(_nakResponse.Length);
                if (!buffer.IsEmpty) {
                    continue;
                }

                break;
            }

            using var memory = MemoryPool<byte>.Shared.Rent((int)header.FullSize);

            if (header.FullSize - _headerSize == 0) {
                var newBuffer = buffer.Slice(_headerSize);
                newBuffer.Slice(0, _headerSize).CopyTo(memory.Memory.Span);
                await notificationOrDataArrived.InvokeAsync(header, Memory<byte>.Empty, nakReceived);
                buffer = newBuffer;
                continue;
            }

            while (!result.IsCompleted) {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryReadPayload(ref buffer, memory.Memory.Span[..(int)header.FullSize])) {
                    await notificationOrDataArrived.InvokeAsync(
                        header, memory.Memory[..(int)header.FullSize], nakReceived);
                    break;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                result = await reader.ReadAsync(cancellationToken);
                if (Debugger.IsAttached) {
                    Debug.WriteLine($"Read: {String.Concat(result.Buffer.First.Span.ToArray().Select(a => $"{a:X}"))}");
                }

                buffer = result.Buffer;
            }
        }

        reader.AdvanceTo(buffer.Start, buffer.End);
        return result;
    }

    private static bool TryReadPayload(ref ReadOnlySequence<byte> buffer, Span<byte> payloadBuffer) {
        if (buffer.Length < payloadBuffer.Length) {
            return false;
        }

        buffer.Slice(buffer.Start, payloadBuffer.Length).CopyTo(payloadBuffer);
        buffer = buffer.Slice(payloadBuffer.Length);

        return true;
    }

    private bool TryReadHeader(ref ReadOnlySequence<byte> buffer, ref NetSdrCommandHeader header,
        ref bool nakReceived) {
        var position = buffer.Start;
        buffer.TryGet(ref position, out var readOnlyMemory);

        if (readOnlyMemory.IsEmpty)
            return false;

        if (readOnlyMemory[..2].Span.SequenceEqual(_nakResponse.Span)) {
            nakReceived = true;
            return true;
        }

        if (buffer.IsSingleSegment) {
            nakReceived = false;
            header = MemoryMarshal.Read<NetSdrCommandHeader>(buffer.FirstSpan[.._headerSize]);
            return true;
        }

        nakReceived = false;
        var headerSpan = MemoryMarshal.CreateSpan(ref header, 1);
        var headerSpanBytes = MemoryMarshal.Cast<NetSdrCommandHeader, byte>(headerSpan);
        buffer.Slice(buffer.Start, _headerSize).CopyTo(headerSpanBytes);
        return true;
    }

    private void InvokeNotifications(NetSdrCommandHeader header, Memory<byte> data) {
        if (header is { ControlItem: NetSdrControlItem.Frequency, Type: NetSdrMessageType.Get }) {
            UpdatedFrequencyReceived?.Invoke(MemoryMarshal.AsRef<GetFrequencyCommandResult>(data.Span));
        }

        if (header is { ControlItem: NetSdrControlItem.GNSS_Position, Type: NetSdrMessageType.Get }) {
            UpdatedGnssPositionReceived?.Invoke(MemoryMarshal.AsRef<GetGnssPositionCommandResult>(data.Span));
        }

        if (header is { ControlItem: NetSdrControlItem.StreamingIpAddress, Type: NetSdrMessageType.Set }) {
            UpdatedStreamingIpAddressReceived?.Invoke(MemoryMarshal.AsRef<SetStreamingIpAddressCommand>(data.Span));
        }

        if (header is { ControlItem: NetSdrControlItem.State, Type: NetSdrMessageType.Set }) {
            UpdatedStreamingStateReceived?.Invoke(MemoryMarshal.AsRef<GetStreamingStateCommandResult>(data.Span));
        }

        if (header is { ControlItem: NetSdrControlItem.DeviceInfo, Type: NetSdrMessageType.Get }) {
            UpdatedDeviceInfoReceived?.Invoke(MemoryMarshal.AsRef<GetDeviceInfoCommandResult>(data.Span));
        }
    }

    private bool IsTcpCloseWait(IPEndPoint? endPoint) {
        IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
        TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
        var connectionInfo = connections.FirstOrDefault(x => x.RemoteEndPoint.Equals(endPoint));
        var result = connectionInfo is { State: TcpState.CloseWait };
        return result;
    }
}
