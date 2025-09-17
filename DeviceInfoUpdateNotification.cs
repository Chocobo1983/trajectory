using System;
using Infozahyst.RSAAS.Common.Dto.NetSdr;
using Infozahyst.RSAAS.Core.Transport.MQTT;

namespace Infozahyst.RSAAS.Common.Notifications;

public class DeviceInfoUpdateNotification : BaseNotification
{
    public DeviceInfo DeviceInfo { get; set; }

    public DateTime DateTime { get; set; }
   
    public DeviceInfoUpdateNotification(uint stationId, DeviceInfo deviceInfo, DateTime dateTime) {
        StationId = stationId;
        DeviceInfo = deviceInfo;
        DateTime = dateTime;
    }
}

