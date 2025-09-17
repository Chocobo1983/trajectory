using System;
using System.Collections.Generic;
using System.Drawing;

namespace Infozahyst.RSAAS.Client.Settings;

public class MapSettings
{
    public string? ServiceUri { get; set; } = "http://localhost:8686/wmts/1.0.0/WMTSCapabilities.xml";
    public string? LayerId { get; set; } = "google_sat_hybrid";
    public double InfinityDirectionOfArrivalSectorAngle { get; set; } = 60;
    public double AntennaSectorAngle { get; set; } = 60;
    public int ClustersDirectionCount { get; set; } = 5;
    public TimeSpan DirectionsCleanupInterval { get; set; } = TimeSpan.FromSeconds(10);
    public Color DirectionColor { get; set; } = Color.Aqua;
    public double DirectionLineWidth { get; set; } = 2d;
    public double IdentifyObjectTolerance { get; set; } = 5d;
    public double RunCompareAngleTolerance { get; set; } = 0.2;
    public string MeasurementToolMainColor { get; set; } = "#FFFF00";
    public string MeasurementToolLabelBackgroundColor { get; set; } = "#A0404040";
    public double ReviewSectorAngle { get; set; } = 70;
    public double BearingDistanceKm { get; set; } = 800;
    public Color PtoaPointColor { get; set; } = Color.White;
    public double PtoaPointSize { get; set; } = 10;
    public Color PtoaEllipseColor { get; set; } = Color.White;
    public List<int> ObjectSizes { get; set; } = [];
    public int DefaultObjectSize { get; set; } = 64;
    public double OverviewMapScaleFactor { get; set; } = 10;
    public double OverviewMapWidth { get; set; } = 200;
    public double OverviewMapHeight { get; set; } = 200;
    public int TrajectoryBufferCapacity { get; set; } = 100;
}
