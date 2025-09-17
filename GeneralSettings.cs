using Infozahyst.RSAAS.Common.Enums;

namespace Infozahyst.RSAAS.Server.Settings;

public class GeneralSettings
{
    public StationType StationType { get; set; } = StationType.ArchontA;
    public int SocketReceiveBufferSize { get; set; } = ushort.MaxValue;
    public string CsvDelimiter { get; set; } = ";";
    public TimeSpan RefreshPositionInterval { get; set; }
    public uint StationId { get; set; }
    public TimeSpan SocketKeepAliveTime { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SocketKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int TcpKeepAliveRetryCount { get; set; } = 1;
    public int SocketConnectionRetryInterval { get; set; } = 100;
    public string? WorkingFilePath { get; set; } = "./";
    public TimeSpan CodeExecutionTimeout { get;set; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan GnssRecordingInterval { get; set; } = TimeSpan.FromMilliseconds(5);
    public Guid ApplicationId { get; set; } = Guid.NewGuid();
    public int TrajectoryBufferCapacity { get; set; } = 100;
    public TimeSpan ReceiverMonitoringInterval = TimeSpan.FromSeconds(15);
}
