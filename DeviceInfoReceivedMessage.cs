using System;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Infozahyst.RSAAS.Common.Dto.NetSdr;

namespace Infozahyst.RSAAS.Client.ViewModels.Messages;

public class DeviceInfoReceivedMessage((DeviceInfo DeviceInfo, uint StationId, DateTime DateTime) value)
    : ValueChangedMessage<(DeviceInfo DeviceInfo, uint StationId, DateTime DateTime)>(value);
