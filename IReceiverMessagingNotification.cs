using System;

namespace Infozahyst.RSAAS.Common.Notifications.Interfaces;

public interface IReceiverMessagingNotification
{
    event EventHandler<ConnectionChangedNotification>? ReceiverConnectionChanged;
    event EventHandler<IQRecordingNotification>? IQRecordingStatusChanged;
    event EventHandler<PdwRecordingNotification>? PdwRecordingStatusChanged;
    event EventHandler<GnssRecordingNotification>? GnssRecordingStatusChanged;
    event EventHandler<ReceiverParametersChangedNotification>? ReceiverParametersChanged;
    event EventHandler<DeviceInfoUpdateNotification>? DeviceInfoUpdated;
}
