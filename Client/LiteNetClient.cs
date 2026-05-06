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
#if DEBUG
        Console.Error.WriteLine("Connected to server");
#endif
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        _server = null;
#if DEBUG
        Console.Error.WriteLine($"Disconnected: {info.Reason}");
#endif
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
#if DEBUG
                Console.Error.WriteLine($"Welcome — PlayerId={_interp.MyPlayerId}");
#endif
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
    {
#if DEBUG
        Console.Error.WriteLine($"Network error {ep}: {err}");
#endif
    }
    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint ep,
        NetPacketReader reader, UnconnectedMessageType type) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
