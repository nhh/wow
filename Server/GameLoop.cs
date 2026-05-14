using System.Buffers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Shared;

namespace Server;

public class GameLoop(LiteNetServer server, WorldDatabase db)
{
    private readonly ParticleSystem _particles   = new(db.ParticleCount, db.LoadParticleScript());
    private readonly PlayerSnapshotQ[]  _playerQSnapsBuf  = new PlayerSnapshotQ[64];
    private readonly ParticleSnapshot[] _particleSnapsBuf = new ParticleSnapshot[db.ParticleCount];
    private readonly int   _particleCount      = db.ParticleCount;
    private readonly float _moveSpeed          = db.MoveSpeed;
    private readonly float _jumpSpeed          = db.JumpSpeed;
    private readonly float _gravity            = db.Gravity;
    private readonly float _playerViewRadius   = db.PlayerViewRadius;
    private readonly float _drThreshold        = db.DRThreshold;
    private uint _tick;

    private const int NoGCBudget = 4 * 1024 * 1024;

    public async Task RunAsync()
    {
        long freq     = System.Diagnostics.Stopwatch.Frequency;
        long interval = (long)(Framing.TickDelta * freq);
        long spinHead = (long)(0.002 * freq);
        long next     = System.Diagnostics.Stopwatch.GetTimestamp() + interval;

        long   statWindow  = freq * 2; // log every 2 seconds
        long   statNext    = System.Diagnostics.Stopwatch.GetTimestamp() + statWindow;
        long   tickMaxUs   = 0;
        long   tickTotalUs = 0;
        int    tickCount   = 0;

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
                Console.WriteLine($"[tick] particles={_particleCount} sessions={server.Sessions.Count}" +
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


    private struct SendState
    {
        public uint  LastSentTick;
        public float LastSentX,  LastSentZ;
        public float LastSentVX, LastSentVZ;
    }

    private readonly Dictionary<(uint, uint), SendState> _sendState = new();

    private void Tick()
    {
        server.PollEvents();

        _tick++;
        var sessions = server.Sessions;

        foreach (var s in sessions)
        {
            s.VelocityX = 0;
            s.VelocityZ = 0;
            CInputState? latest = null;
            bool anyJump = false;
            while (s.TryDequeueInput() is { } input) { latest = input; anyJump |= input.Jump; }
            if (latest is not null) ApplyInput(s, latest, anyJump);
        }

        _particles.Update(_tick);

        int playerSnapSize   = Marshal.SizeOf<PlayerSnapshotQ>();
        int particleSnapSize = Marshal.SizeOf<ParticleSnapshot>();

        foreach (var observer in sessions)
        {
            // ── Players: radius + FOV-bias + LOD + dead reckoning ─────────────
            int playerCount = 0;
            foreach (var entity in sessions)
            {
                float dx      = entity.X - observer.X;
                float dz      = entity.Z - observer.Z;
                float rawDist = MathF.Sqrt(dx * dx + dz * dz);

                // FOV-bias: entities behind observer get inflated effective distance
                float effectDist;
                if (rawDist < 0.001f)
                {
                    effectDist = 0f; // same position — always include
                }
                else
                {
                    float dot       = MathF.Sin(observer.Yaw) * (dx / rawDist)
                                    + MathF.Cos(observer.Yaw) * (dz / rawDist);
                    float fovFactor = (dot + 1f) / 2f;         // [0,1]: 1=front, 0=behind
                    effectDist      = rawDist / (0.3f + 0.7f * fovFactor);
                }

                if (effectDist > _playerViewRadius) continue;

                float score    = 1f - effectDist / _playerViewRadius;
                int   interval = score > 0.7f ? 1 : score > 0.3f ? 4 : 20;

                var  key     = (observer.PlayerId, entity.PlayerId);
                _sendState.TryGetValue(key, out var state);
                uint elapsed = _tick - state.LastSentTick;

                if (elapsed < (uint)interval) continue;        // LOD gate

                if (interval > 1 && state.LastSentTick > 0)
                {
                    float predX = state.LastSentX + state.LastSentVX * elapsed * Framing.TickDelta;
                    float predZ = state.LastSentZ + state.LastSentVZ * elapsed * Framing.TickDelta;
                    float errSq = (entity.X - predX) * (entity.X - predX)
                                + (entity.Z - predZ) * (entity.Z - predZ);
                    if (errSq < _drThreshold * _drThreshold) continue;
                }

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
                _sendState[key] = new SendState
                {
                    LastSentTick = _tick,
                    LastSentX    = entity.X,         LastSentZ  = entity.Z,
                    LastSentVX   = entity.VelocityX, LastSentVZ = entity.VelocityZ,
                };
            }

            // ── Particles: no culling — array index must be stable for client Lerp ──
            // Any per-observer culling shifts indices and breaks index-based interpolation.
            // 100 particles × 13 bytes = 1300 bytes/tick — negligible bandwidth cost.
            int particleCount = _particleCount;
            _particles.Positions.CopyTo(_particleSnapsBuf.AsSpan(0, particleCount));

            // ── Coalescing: one packet per observer per tick ───────────────────
            int worldLen    = playerCount   > 0 ? 4 + playerCount   * playerSnapSize   : 0;
            int particleLen = particleCount > 0 ? 4 + particleCount * particleSnapSize : 0;
            int totalLen    = worldLen + particleLen;
            if (totalLen == 0) continue;

            byte[] coalesced = ArrayPool<byte>.Shared.Rent(totalLen);
            try
            {
                int off = 0;
                if (playerCount > 0)
                    off += DatagramWriter.Write<PlayerSnapshotQ>(coalesced.AsSpan(), Opcode.SWorldSnapshot,
                        _playerQSnapsBuf.AsSpan(0, playerCount));
                if (particleCount > 0)
                    DatagramWriter.Write<ParticleSnapshot>(coalesced.AsSpan(off), Opcode.SParticleSnapshot,
                        _particleSnapsBuf.AsSpan(0, particleCount));
                observer.SendSnapshot(coalesced, totalLen);
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
