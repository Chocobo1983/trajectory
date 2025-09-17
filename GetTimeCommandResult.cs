using System.Runtime.InteropServices;

namespace Infozahyst.RSAAS.Server.Receiver.NetSdr.Commands;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct GetTimeCommandResult : INetSdrCommand
{
    public NetSdrCommandHeader Header { get; }
    public readonly uint TimeSec;
    public readonly int TimeUsec;

    public GetTimeCommandResult() {
        byte commandLength =
            (byte)(Marshal.SizeOf<NetSdrCommandHeader>() + sizeof(uint) + sizeof(int));
        Header = new NetSdrCommandHeader(NetSdrMessageType.Set, NetSdrControlItem.Time, commandLength);
        TimeSec = 0;
        TimeUsec = 0;
    }

    public GetTimeCommandResult(uint timeSec, int timeUsec) {
        byte commandLength =
            (byte)(Marshal.SizeOf<NetSdrCommandHeader>() + sizeof(uint) + sizeof(int));
        Header = new NetSdrCommandHeader(NetSdrMessageType.Set, NetSdrControlItem.Time, commandLength);
        TimeSec = timeSec;
        TimeUsec = timeUsec;
    }
}

