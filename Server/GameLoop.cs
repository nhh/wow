using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Shared;

namespace Server;

public class GameLoop(LiteNetServer server, WorldDatabase db, GameObjectGroup[] groups)
{
    private readonly ParticleSystem      _particles        = new(db.ParticleCount, db.LoadParticleScript());
    private readonly PlayerSnapshotQ[]   _playerQSnapsBuf  = new PlayerSnapshotQ[64];
    private readonly ParticleSnapshot[]  _particleSnapsBuf = new ParticleSnapshot[db.ParticleCount];
    private readonly GameObjectSnapshot[] _goSnapsBuf       = new GameObjectSnapshot[groups.Sum(g => g.States.Length)];
    private readonly Session[]          _sessionsBuf      = new Session[256];
    private readonly float _moveSpeed        = db.MoveSpeed;
    private readonly float _jumpSpeed        = db.JumpSpeed;
    private readonly float _gravity          = db.Gravity;
    private readonly float _playerViewRadius = db.PlayerViewRadius;
    private readonly float _playerViewRadiusSq = db.PlayerViewRadius * db.PlayerViewRadius;
    private readonly float _drThresholdSq    = db.DRThreshold * db.DRThreshold;
    private readonly float _goViewRadiusSq   = db.GoViewRadius * db.GoViewRadius;
    private uint _tick;

    private const int NoGCBudget = 4 * 1024 * 1024;
    private const int MtuPayload = 1390; // MtuOverride(1400) minus LiteNetLib header overhead

    private struct SendState
    {
        public uint  LastSentTick;
        public float LastSentX,  LastSentZ;
        public float LastSentVX, LastSentVZ;
    }

    private readonly Dictionary<ulong, SendState> _sendState = new();
    private readonly ConcurrentQueue<uint>               _disconnected = new();

    public async Task RunAsync()
    {
        server.SessionDisconnected += _disconnected.Enqueue;

        long freq     = System.Diagnostics.Stopwatch.Frequency;
        long interval = (long)(Framing.TickDelta * freq);
        long spinHead = (long)(0.002 * freq);
        long next     = System.Diagnostics.Stopwatch.GetTimestamp() + interval;

        long statWindow  = freq * 2;
        long statNext    = System.Diagnostics.Stopwatch.GetTimestamp() + statWindow;
        long tickMaxUs   = 0;
        long tickTotalUs = 0;
        int  tickCount   = 0;

        while (true)
        {
            long t0   = System.Diagnostics.Stopwatch.GetTimestamp();
            bool noGC = GC.TryStartNoGCRegion(NoGCBudget);
            try   { Tick(); }
            finally
            {
                if (noGC && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    GC.EndNoGCRegion();
            }
            long tickUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / freq;
            if (tickUs > tickMaxUs) tickMaxUs = tickUs;
            tickTotalUs += tickUs;
            tickCount++;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= statNext)
            {
#if DEBUG
                long avgUs = tickCount > 0 ? tickTotalUs / tickCount : 0;
                Console.WriteLine($"[tick] particles={_particleSnapsBuf.Length} sessions={server.SessionCount}" +
                                  $"  avg={avgUs}µs  max={tickMaxUs}µs  budget=50000µs");
#endif
                tickMaxUs = 0; tickTotalUs = 0; tickCount = 0;
                statNext  = now + statWindow;
            }

            long sleepUntil = next - spinHead;
            if (sleepUntil > now)
                await Task.Delay(TimeSpan.FromSeconds((sleepUntil - now) / (double)freq));

            while (System.Diagnostics.Stopwatch.GetTimestamp() < next)
                Thread.SpinWait(10);

            next += interval;
        }
    }

    private void Tick()
    {
        server.PollEvents();

        // Purge sendState entries for disconnected players (rare, so ToList alloc is fine)
        while (_disconnected.TryDequeue(out uint id))
        {
            foreach (var key in _sendState.Keys.ToList())
                if ((uint)(key >> 32) == id || (uint)key == id)
                    _sendState.Remove(key);
        }

        _tick++;
        int sessionCount = server.CopySessionsTo(_sessionsBuf);
        var sessions     = _sessionsBuf.AsSpan(0, sessionCount);

        foreach (var s in sessions)
        {
            s.VelocityX = 0;
            s.VelocityZ = 0;
            CInputState? latest = null;
            bool anyJump = false;
            while (s.TryDequeueInput() is { } input) { latest = input; anyJump |= input.Jump; }
            if (latest is not null) ApplyInput(s, latest, anyJump);
        }

        float t = _tick * Framing.TickDelta;
        _particles.Update(_tick);
        foreach (var group in groups)
            group.Tick(_tick, t);

        int playerSnapSize   = Marshal.SizeOf<PlayerSnapshotQ>();
        int particleSnapSize = Marshal.SizeOf<ParticleSnapshot>();
        int goSnapSize       = Marshal.SizeOf<GameObjectSnapshot>();
        int particleCount    = _particleSnapsBuf.Length;
        _particles.Positions.CopyTo(_particleSnapsBuf);

        foreach (var observer in sessions)
        {
            float observerSin = MathF.Sin(observer.Yaw);
            float observerCos = MathF.Cos(observer.Yaw);
            int playerCount = 0;

            foreach (var entity in sessions)
            {
                bool isSelf = entity.PlayerId == observer.PlayerId;

                if (!isSelf)
                {
                    float dx     = entity.X - observer.X;
                    float dz     = entity.Z - observer.Z;
                    float distSq = dx * dx + dz * dz;

                    if (distSq > _playerViewRadiusSq && distSq > 0.000001f) continue;

                    float effectDist;
                    if (distSq < 0.000001f)
                    {
                        effectDist = 0f;
                    }
                    else
                    {
                        float rawDist   = MathF.Sqrt(distSq);
                        float dot       = observerSin * (dx / rawDist) + observerCos * (dz / rawDist);
                        float fovFactor = (dot + 1f) / 2f;
                        effectDist      = rawDist / (0.3f + 0.7f * fovFactor);
                    }

                    if (effectDist > _playerViewRadius) continue;

                    float score    = 1f - effectDist / _playerViewRadius;
                    int   interval = score > 0.7f ? 1 : score > 0.3f ? 4 : 20;

                    ulong key = (ulong)observer.PlayerId << 32 | entity.PlayerId;
                    _sendState.TryGetValue(key, out var state);
                    uint elapsed = _tick - state.LastSentTick;

                    if (elapsed < (uint)interval) continue;

                    if (interval > 1 && state.LastSentTick > 0)
                    {
                        float predX = state.LastSentX + state.LastSentVX * elapsed * Framing.TickDelta;
                        float predZ = state.LastSentZ + state.LastSentVZ * elapsed * Framing.TickDelta;
                        float errSq = (entity.X - predX) * (entity.X - predX)
                                    + (entity.Z - predZ) * (entity.Z - predZ);
                        if (errSq < _drThresholdSq) continue;
                    }
                }
                // isSelf: always send — client needs own position every tick for reconciliation

                float yawNorm = entity.Yaw % MathF.Tau;
                if (yawNorm < 0f) yawNorm += MathF.Tau;

                _playerQSnapsBuf[playerCount++] = new PlayerSnapshotQ
                {
                    PlayerId = entity.PlayerId,
                    X        = (short)(entity.X * 100f),
                    Y        = (short)(entity.Y * 100f),
                    Z        = (short)(entity.Z * 100f),
                    Yaw      = (byte)(yawNorm / MathF.Tau * 256f),
                };
                _sendState[(ulong)observer.PlayerId << 32 | entity.PlayerId] = new SendState
                {
                    LastSentTick = _tick,
                    LastSentX    = entity.X,         LastSentZ  = entity.Z,
                    LastSentVX   = entity.VelocityX, LastSentVZ = entity.VelocityZ,
                };
            }

            // Game objects: per-observer distance cull (go_view_radius from game_config).
            // Particles have no IDs so indices must be stable — no culling there.
            int goVisibleCount = 0;
            foreach (var group in groups)
                foreach (ref var go in group.States.AsSpan())
                {
                    float goDx = go.X - observer.X;
                    float goDz = go.Z - observer.Z;
                    if (goDx * goDx + goDz * goDz > _goViewRadiusSq) continue;
                    float yawNorm = go.Yaw % MathF.Tau;
                    if (yawNorm < 0f) yawNorm += MathF.Tau;
                    _goSnapsBuf[goVisibleCount++] = new GameObjectSnapshot
                    {
                        Id  = go.Id,
                        X   = (short)(go.X * 100f),
                        Y   = (short)(go.Y * 100f),
                        Z   = (short)(go.Z * 100f),
                        Yaw = (byte)(yawNorm / MathF.Tau * 256f),
                    };
                }

            int worldLen    = playerCount      > 0 ? 4 + playerCount      * playerSnapSize   : 0;
            int particleLen = particleCount    > 0 ? 4 + particleCount    * particleSnapSize : 0;
            int goLen       = goVisibleCount   > 0 ? 4 + goVisibleCount   * goSnapSize       : 0;
            int totalLen    = worldLen + particleLen + goLen;
            if (totalLen == 0) continue;

            byte[] coalesced = ArrayPool<byte>.Shared.Rent(totalLen);
            try
            {
                int off = 0;
                if (playerCount > 0)
                    off += DatagramWriter.Write<PlayerSnapshotQ>(coalesced.AsSpan(), Opcode.SWorldSnapshot,
                        _playerQSnapsBuf.AsSpan(0, playerCount));
                if (particleCount > 0)
                    off += DatagramWriter.Write<ParticleSnapshot>(coalesced.AsSpan(off), Opcode.SParticleSnapshot,
                        _particleSnapsBuf.AsSpan(0, particleCount));
                if (goVisibleCount > 0)
                    DatagramWriter.Write<GameObjectSnapshot>(coalesced.AsSpan(off), Opcode.SGameObjectSnapshot,
                        _goSnapsBuf.AsSpan(0, goVisibleCount));
                if (totalLen <= MtuPayload)
                {
                    observer.SendSnapshot(coalesced, totalLen);
                }
                else
                {
                    Span<(int off, int len)> segs =
                    [
                        (0,                      worldLen),
                        (worldLen,               particleLen),
                        (worldLen + particleLen, goLen),
                    ];
                    int pktStart = -1, pktLen = 0;
                    foreach (var (segOff, segLen) in segs)
                    {
                        if (segLen == 0) continue;
                        if (pktStart < 0) pktStart = segOff;
                        if (pktLen > 0 && pktLen + segLen > MtuPayload)
                        {
                            observer.SendSnapshot(coalesced, pktStart, pktLen);
                            pktStart = segOff;
                            pktLen   = 0;
                        }
                        pktLen += segLen;
                    }
                    if (pktLen > 0) observer.SendSnapshot(coalesced, pktStart, pktLen);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(coalesced); }
        }
    }

    private void ApplyInput(Session s, CInputState input, bool jump)
    {
        float fwd = 0, str = 0;
        if (input.Forward)  fwd += 1;
        if (input.Backward) fwd -= 1;
        if (input.Left)     str += 1;
        if (input.Right)    str -= 1;

        float yaw = input.Yaw;
        float dx  = fwd * MathF.Sin(yaw) + str * MathF.Cos(yaw);
        float dz  = fwd * MathF.Cos(yaw) - str * MathF.Sin(yaw);

        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0) { dx /= len; dz /= len; }

        s.X        += dx * _moveSpeed * Framing.TickDelta;
        s.Z        += dz * _moveSpeed * Framing.TickDelta;
        s.Yaw       = input.Yaw;
        s.VelocityX = dx * _moveSpeed;
        s.VelocityZ = dz * _moveSpeed;

        if (jump && s.Y <= 0f) s.VelocityY = _jumpSpeed;
        s.VelocityY -= _gravity * Framing.TickDelta;
        s.Y         += s.VelocityY * Framing.TickDelta;
        if (s.Y < 0f) { s.Y = 0f; s.VelocityY = 0f; }
    }
}
