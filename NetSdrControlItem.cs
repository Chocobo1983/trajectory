namespace Infozahyst.RSAAS.Server.Receiver.NetSdr;

public enum NetSdrControlItem
{
    // Not parsed (not in protocol)
    None = 0x0000,
    GetLastError = 0x0006,
    InstantViewBandwidth = 0x0015,
    ChannelFunctions = 0x0016,
    ScanRange = 0x0017,
    State = 0x0018,
    ScanList = 0x001e,
    SuppressedBands = 0x001F,
    Frequency = 0x0020,
    InputChannel = 0x0021,
    Attenuators = 0x0039,
    AGC = 0x003a,
    SpectrumParams = 0x0050,
    SpectrumState = 0x0051,
    DDCState = 0x0061,
    DDCSetup = 0x0062,
    PayloadSize = 0x00c3,
    StreamingIpAddress = 0x00c5,
    AntennaAzimuth = 0x00e0,
    Backlight = 0x02ff,
    GNSS_Status = 0x0400,
    GNSS_Control = 0x0401,
    GNSS_Position = 0x0402,
    DeviceInfo = 0x0600,
    Time = 0x0102
}
