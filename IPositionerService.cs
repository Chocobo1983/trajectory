using Infozahyst.RSAAS.Common.Dto;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Common.Enums;
using Infozahyst.RSAAS.Common.Notifications;

namespace Infozahyst.RSAAS.Server.Services.Interfaces;

public interface IPositionerService : IAsyncDisposable
{
    event EventHandler<PositionChangedNotification>? PositionChanged;
    event EventHandler<bool>? IsManualModeChanged;
    void SetManualMode(PositionSource positionSource, double longitude, double latitude, double angle);
    (double Longitude, double Latitude) GetManualCoordinates();
    double GetManualAngle();
    bool GetManualMode();
    PositionSource GetPositionSource();
    void StartRefreshPosition();
    void StopRefreshPosition();
    (double Longitude, double Latitude) GetActualCoordinates();
    double GetActualAngle();
    Task<PositionerData?> GetGnssPosition();
    void UpdateReceiverPosition(DeviceGnssInfo gnssInfo, DateTime dateTime);
}
