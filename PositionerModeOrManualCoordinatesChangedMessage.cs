using CommunityToolkit.Mvvm.Messaging.Messages;
using Esri.ArcGISRuntime.Geometry;
using Infozahyst.RSAAS.Common.Enums;

namespace Infozahyst.RSAAS.Client.ViewModels.Messages;

public class PositionerModeOrManualCoordinatesChangedMessage((PositionSource, MapPoint, uint) value)
    : ValueChangedMessage<(PositionSource, MapPoint, uint)>(value);
