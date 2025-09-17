using Infozahyst.RSAAS.Common.Models;
using Infozahyst.RSAAS.Core.Transport.MQTT.Commands;

namespace Infozahyst.RSAAS.Common.Commands;

public class GetStationTrajectoryResponse : BaseResponse
{
    public TrajectoryPoint[]? TrajectoryBuffer { get; set; }
}

