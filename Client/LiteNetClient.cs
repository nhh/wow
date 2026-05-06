using LiteNetLib;
using MemoryPack;
using Shared;

namespace Client;

public class LiteNetClient : INetEventListener
{
    private readonly NetManager  _manager;
    private readonly Interpolator _interp;
    private NetPeer?             _server;

    public LiteNetClient(Interpolator interp)
    {
        _interp  = interp;
        _manager = new NetManager(this);
        _manager.Start();
    }

    public void Connect(string host, int port)
        => _manager.Connect(host, port, "mmorpg");

    public void PollEvents() => _manager.PollEvents();

    public void SendInput(CInputState input)
    {
        if (_server is null) return;
        var payload = MemoryPackSerializer.Serialize(input);
        var packet  = new byte[2 + payload.Length];
        BitConverter.TryWriteBytes(packet, (ushort)Opcode.CInputState);
        payload.CopyTo(packet, 2);
        _server.Send(packet, DeliveryMethod.Unreliable);
    }

    // ── INetEventListener ──────────────────────────────────────────────────

    public void OnPeerConnected(NetPeer peer)
    {
        _server = peer;
        Console.Error.WriteLine("Connected to server");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        _server = null;
        Console.Error.WriteLine($"Disconnected: {info.Reason}");
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader,
                                 byte channel, DeliveryMethod method)
    {
        var bytes = reader.GetRemainingBytes();
        if (bytes.Length < 2) { reader.Recycle(); return; }

        var span = bytes.AsSpan();
        var op   = (Opcode)BitConverter.ToUInt16(span);
        switch (op)
        {
            case Opcode.SWelcome:
                _interp.MyPlayerId = DatagramWriter.Read<WelcomeMsg>(span)[0].PlayerId;
                Console.Error.WriteLine($"Welcome — PlayerId={_interp.MyPlayerId}");
                break;
            case Opcode.SWorldSnapshot:
                _interp.UpdatePlayers(DatagramWriter.Read<PlayerSnapshot>(span));
                break;
            case Opcode.SParticleSnapshot:
                _interp.UpdateParticles(DatagramWriter.Read<ParticleSnapshot>(span));
                break;
        }
        reader.Recycle();
    }

    public void OnConnectionRequest(ConnectionRequest request) { }
    public void OnNetworkError(System.Net.IPEndPoint ep, System.Net.Sockets.SocketError err)
        => Console.Error.WriteLine($"Network error {ep}: {err}");
    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint ep,
        NetPacketReader reader, UnconnectedMessageType type) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
