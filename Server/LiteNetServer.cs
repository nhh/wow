using LiteNetLib;
using MemoryPack;
using Shared;

namespace Server;

public class LiteNetServer : INetEventListener
{
    private readonly NetManager    _manager;
    private readonly List<Session> _sessions = [];
    private readonly Lock          _sessionsLock = new();
    private uint                   _nextId = 1;

    public LiteNetServer()
    {
        _manager = new NetManager(this) { MtuOverride = 1400 };
        _manager.Start(Framing.Port);
#if DEBUG
        Console.WriteLine($"Listening on :{Framing.Port}");
#endif
    }

    public event Action<uint>? SessionDisconnected;

    public int CopySessionsTo(Session[] buffer)
    {
        lock (_sessionsLock)
        {
            int count = Math.Min(_sessions.Count, buffer.Length);
            for (int i = 0; i < count; i++) buffer[i] = _sessions[i];
            return count;
        }
    }

    public int SessionCount { get { lock (_sessionsLock) return _sessions.Count; } }

    public void PollEvents() => _manager.PollEvents();

    // ── INetEventListener ──────────────────────────────────────────────────

    public void OnConnectionRequest(ConnectionRequest request)
        => request.AcceptIfKey("mmorpg");

    public void OnPeerConnected(NetPeer peer)
    {
        var session = new Session(peer, _nextId++);
        peer.Tag = session;
        lock (_sessionsLock) _sessions.Add(session);
#if DEBUG
        Console.WriteLine($"Session {session.PlayerId} connected");
#endif
        session.SendWelcome();
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (peer.Tag is not Session s) return;
        lock (_sessionsLock) _sessions.Remove(s);
        SessionDisconnected?.Invoke(s.PlayerId);
#if DEBUG
        Console.WriteLine($"Session {s.PlayerId} disconnected: {info.Reason}");
#endif
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader,
                                 byte channel, DeliveryMethod method)
    {
        if (peer.Tag is not Session session) { reader.Recycle(); return; }

        var bytes = reader.GetRemainingBytes();
        if (bytes.Length < 2) { reader.Recycle(); return; }

        var op = (Opcode)BitConverter.ToUInt16(bytes, 0);
        if (op == Opcode.CInputState)
        {
            var input = MemoryPackSerializer.Deserialize<CInputState>(bytes.AsSpan(2));
            if (input is not null) session.EnqueueInput(input);
        }
        reader.Recycle();
    }

    public void OnNetworkError(System.Net.IPEndPoint ep, System.Net.Sockets.SocketError err)
    {
#if DEBUG
        Console.WriteLine($"Network error {ep}: {err}");
#endif
    }

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint ep,
        NetPacketReader reader, UnconnectedMessageType type) { }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
}
