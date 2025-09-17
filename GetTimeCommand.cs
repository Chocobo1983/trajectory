using System.Runtime.InteropServices;

namespace Infozahyst.RSAAS.Server.Receiver.NetSdr.Commands;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct GetTimeCommand : INetSdrCommand
{
    private const byte MessageLength = 4;

    public NetSdrCommandHeader Header { get; }

    public GetTimeCommand() {
        Header = new NetSdrCommandHeader(NetSdrMessageType.Get, NetSdrControlItem.Time, MessageLength);
    }
}
