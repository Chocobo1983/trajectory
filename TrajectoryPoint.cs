using System;

namespace Infozahyst.RSAAS.Common.Models;

public class TrajectoryPoint(DateTime dateTime, float longitude, float latitude)
{
    public DateTime DateTime { get; set; } = dateTime;
    public float Latitude { get; set; } = latitude;
    public float Longitude { get; set; } = longitude;
}

