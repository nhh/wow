using System.Runtime.InteropServices;
using System.Threading.Channels;
using LiteNetLib;
using Shared;

namespace Server;

public class Session(NetPeer peer, uint id)
{
    public uint  PlayerId => id;
    public float X         = ((id - 1) % 8) * 4f - 14f;
    public float Y         = 0;
    public float Z         = ((id - 1) / 8) * 6f;
    public float Yaw       = 0;
    public float VelocityY = 0;

    private readonly Channel<CInputState> _inputs =
        Channel.CreateBounded<CInputState>(64);

    public CInputState? TryDequeueInput()
    {
        _inputs.Reader.TryRead(out var input);
        return input;
    }

    public bool EnqueueInput(CInputState input) => _inputs.Writer.TryWrite(input);

    public void SendSnapshot(byte[] data)
        => peer.Send(data, DeliveryMethod.ReliableOrdered);

    public void SendSnapshot(byte[] data, int length)
        => peer.Send(data, 0, length, DeliveryMethod.ReliableOrdered);

    public void SendWelcome()
    {
        var welcome = new byte[Framing.DatagramHeaderSize + Marshal.SizeOf<WelcomeMsg>()];
        DatagramWriter.Write<WelcomeMsg>(welcome, Opcode.SWelcome,
            [new WelcomeMsg { PlayerId = id }]);
        peer.Send(welcome, DeliveryMethod.ReliableOrdered);
    }
}
