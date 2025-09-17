using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Messaging;
using Infozahyst.RSAAS.Common.Notifications;
using Infozahyst.RSAAS.Server.Exceptions;
using Infozahyst.RSAAS.Server.Positioner;
using Infozahyst.RSAAS.Server.Services.Interfaces;
using Infozahyst.RSAAS.Server.Settings;
using Infozahyst.RSAAS.Server.Settings.Providers.Db;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TimeoutException = System.TimeoutException;

namespace Infozahyst.RSAAS.Server.Services;

public class PositionerService : IPositionerService
{
    private readonly IPositionerClient _positionerClient;
    private readonly ISettingsStorage _settingsStorage;
    private readonly IOptionsMonitor<PositionerSettings> _positionerSettings;
    private readonly ILogger<PositionerService> _logger;
    private readonly IMessageLogger _messageLogger;
    private readonly TimeSpan _refreshPositionInterval;
    private Timer? _refreshPositionTimer;

    private readonly bool _isPositionerAvailable;
    private readonly uint _stationId;
    private Position? _currentPosition;
    private double? _currentPositionAzimuth;
    private DateTime _currentDateTime;

    private bool IsManualMode =>
        _positionerSettings.CurrentValue.PositionSource == PositionSource.Manual;

    private PositionSource CurrentPositionSource => _positionerSettings.CurrentValue.PositionSource;

    public event EventHandler<PositionChangedNotification>? PositionChanged;
    public event EventHandler<bool>? IsManualModeChanged;

    public PositionerService(IPositionerClient positionerClient, ISettingsStorage settingsStorage,
        IOptions<GeneralSettings> generalSettings, IOptionsMonitor<PositionerSettings> positionerSettings,
        ILogger<PositionerService> logger, IMessageLogger messageLogger) {
        _positionerClient = positionerClient;
        _settingsStorage = settingsStorage;
        _positionerSettings = positionerSettings;
        _logger = logger;
        _messageLogger = messageLogger;
        _stationId = generalSettings.Value.StationId;
        _refreshPositionInterval = generalSettings.Value.RefreshPositionInterval;
        _isPositionerAvailable = !string.IsNullOrWhiteSpace(positionerSettings.CurrentValue.IpAddress)
                                 && generalSettings.Value.StationType != StationType.ArchontT;

        if (!_isPositionerAvailable && _positionerSettings.CurrentValue.PositionSource == PositionSource.Positioner) {
            SetManualMode(
                generalSettings.Value.StationType == StationType.ArchontT
                    ? PositionSource.Receiver
                    : PositionSource.Manual, 0, 0, 0);
        }
    }

    public void UpdateReceiverPosition(DeviceGnssInfo gnssInfo, DateTime dateTime) {
        if (CurrentPositionSource != PositionSource.Receiver) {
            return;
        }

        _currentPosition = new Position(gnssInfo.Latitude, gnssInfo.Longitude) { Altitude = (int)gnssInfo.Altitude };
        _currentDateTime = dateTime;
        var azimuth = CurrentPositionSource == PositionSource.Positioner ? _currentPositionAzimuth ?? 0 : 0;
        var positionData = new PositionerData {
            Position = _currentPosition,
            Azimuth = azimuth,
            DateTime = _currentDateTime,
            PositionSource = CurrentPositionSource
        };

        SendNotification(positionData);
    }

    public void SetManualMode(PositionSource positionSource, double longitude, double latitude, double angle) {
        var isManualMode = positionSource == PositionSource.Manual;
        if (isManualMode) {
            _settingsStorage.Save(() => _positionerSettings.CurrentValue.Manual.Latitude, latitude);
            _settingsStorage.Save(() => _positionerSettings.CurrentValue.Manual.Longitude, longitude);
            _settingsStorage.Save(() => _positionerSettings.CurrentValue.ManualAzimuth, angle);
        }

        _settingsStorage.Save(() => _positionerSettings.CurrentValue.PositionSource, positionSource);

        if (isManualMode) {
            var position = new Position(latitude, longitude);
            SendNotification(position, angle, DateTime.UtcNow, positionSource);
        }

        IsManualModeChanged?.Invoke(this, isManualMode);
    }

    public (double Longitude, double Latitude) GetManualCoordinates() {
        return (_positionerSettings.CurrentValue.Manual.Longitude,
            _positionerSettings.CurrentValue.Manual.Latitude);
    }

    public double GetManualAngle() {
        return _positionerSettings.CurrentValue.ManualAzimuth;
    }

    public (double Longitude, double Latitude) GetActualCoordinates() {
        if (IsManualMode) {
            return GetManualCoordinates();
        }

        return (_currentPosition?.Longitude ?? 0, _currentPosition?.Latitude ?? 0);
    }

    public double GetActualAngle() {
        return IsManualMode ? GetManualAngle() : _currentPositionAzimuth.GetValueOrDefault();
    }

    public bool GetManualMode() {
        return IsManualMode;
    }

    public PositionSource GetPositionSource() {
        return _positionerSettings.CurrentValue.PositionSource;
    }

    public void StartRefreshPosition() {
        if (_refreshPositionTimer == null) {
            _refreshPositionTimer = new Timer(RefreshPosition);
            _refreshPositionTimer.Change(TimeSpan.Zero, _refreshPositionInterval);
        }
    }

    public void StopRefreshPosition() {
        if (_refreshPositionTimer != null) {
            _refreshPositionTimer.Dispose();
            _refreshPositionTimer = null;
        }
    }

    private void SendNotification(Position position, double azimuth, DateTime dateTime, PositionSource positionSource) {
        var data = new PositionerData {
            Position = position,
            Azimuth = azimuth,
            DateTime = dateTime,
            PositionSource = positionSource
        };
        PositionChanged?.Invoke(this, new PositionChangedNotification(_stationId, data));
    }

    private void SendNotification(PositionerData data) {
        PositionChanged?.Invoke(this, new PositionChangedNotification(_stationId, data));
    }

#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void RefreshPosition(object? sender) {
        if (CurrentPositionSource == PositionSource.Receiver) {
            return;
        }

        var positionData = await GetGnssPosition();
        if (positionData != null) {
            SendNotification(positionData);
        }
    }
#pragma warning restore VSTHRD100 // Avoid async void methods

    public async Task<PositionerData?> GetGnssPosition() {
        PositionerData? positionerData = null;
        if (IsManualMode) {
            return null;
        }

        try {
            if (CurrentPositionSource == PositionSource.Positioner) {
                if (!_isPositionerAvailable) {
                    return null;
                }

                _currentPositionAzimuth = await _positionerClient.GetAzimuth();
                var positionerPosition = await _positionerClient.GetPosition();
                _currentPosition = positionerPosition.ToPosition();
                _currentDateTime = await _positionerClient.GetDateTime();
            } 

            var azimuth = CurrentPositionSource == PositionSource.Positioner ? _currentPositionAzimuth ?? 0 : 0;
            if (_currentPosition != null) {
                positionerData = new PositionerData {
                    Position = _currentPosition,
                    Azimuth = azimuth,
                    DateTime = _currentDateTime,
                    PositionSource = CurrentPositionSource
                };
            }
        } catch (Exception ex) when (ex is ConnectionException or TimeoutException) {
            _logger.LogError("Error at getting positioner angle: {Error}", ex.Message);
            _messageLogger.AddMessage(MessageCategory.System, ex.Message, MessageLevel.Error);
        } catch (Exception e) {
            _logger.LogError(exception: e, message: "");
            _messageLogger.AddMessage(MessageCategory.System, e.Message, MessageLevel.Error);
        }

        return positionerData;
    }

    public async ValueTask DisposeAsync() {
        if (_refreshPositionTimer != null) {
            await _refreshPositionTimer.DisposeAsync();
        }
    }
}
