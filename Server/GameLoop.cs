using System.Buffers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Shared;

namespace Server;

public class GameLoop(LiteNetServer server, int particleCount = Framing.ParticleCount)
{
    private readonly ParticleSystem _particles   = new(particleCount);
    private readonly byte[]         _particleBuf = new byte[Framing.DatagramHeaderSize +
                                                            particleCount * Marshal.SizeOf<ParticleSnapshot>()];
    private readonly PlayerSnapshot[] _playerSnapsBuf = new PlayerSnapshot[64];
    private uint _tick;

    private const int NoGCBudget = 4 * 1024 * 1024; // 4 MB

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
                Console.WriteLine($"[tick] particles={particleCount} sessions={server.Sessions.Count}" +
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

        _tick++;
        var sessions = server.Sessions;

        foreach (var s in sessions)
        {
            CInputState? latest = null;
            bool anyJump = false;
            while (s.TryDequeueInput() is { } input) { latest = input; anyJump |= input.Jump; }
            if (latest is not null) ApplyInput(s, latest, anyJump);
        }

        _particles.Update(_tick);

        int playerCount = sessions.Count;
        int playerSnapSize = Marshal.SizeOf<PlayerSnapshot>();
        for (int i = 0; i < playerCount; i++)
            _playerSnapsBuf[i] = new PlayerSnapshot
            {
                PlayerId = sessions[i].PlayerId,
                X        = sessions[i].X,
                Y        = sessions[i].Y,
                Z        = sessions[i].Z,
                Yaw      = sessions[i].Yaw,
            };

        int worldBufSize = Framing.DatagramHeaderSize + playerCount * playerSnapSize;
        byte[] worldBuf = ArrayPool<byte>.Shared.Rent(worldBufSize);
        try
        {
            DatagramWriter.Write<PlayerSnapshot>(worldBuf, Opcode.SWorldSnapshot,
                _playerSnapsBuf.AsSpan(0, playerCount));

            DatagramWriter.Write<ParticleSnapshot>(_particleBuf, Opcode.SParticleSnapshot,
                _particles.Positions);

            foreach (var s in sessions)
            {
                s.SendSnapshot(worldBuf, worldBufSize);
                s.SendSnapshot(_particleBuf);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(worldBuf);
        }
    }

    private const float Gravity    = 20f;
    private const float JumpSpeed  = 9f;

    private static void ApplyInput(Session s, CInputState input, bool jump)
    {
        float fwd = 0, str = 0;
        if (input.Forward)  fwd += 1;
        if (input.Backward) fwd -= 1;
        if (input.Left)     str += 1;
        if (input.Right)    str -= 1;

        float yaw = input.Yaw;
        float dx = fwd * MathF.Sin(yaw) + str * MathF.Cos(yaw);
        float dz = fwd * MathF.Cos(yaw) - str * MathF.Sin(yaw);

        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0) { dx /= len; dz /= len; }

        s.X   += dx * Framing.MoveSpeed * Framing.TickDelta;
        s.Z   += dz * Framing.MoveSpeed * Framing.TickDelta;
        s.Yaw  = input.Yaw;

        if (jump && s.Y <= 0f) s.VelocityY = JumpSpeed;
        s.VelocityY -= Gravity * Framing.TickDelta;
        s.Y         += s.VelocityY * Framing.TickDelta;
        if (s.Y < 0f) { s.Y = 0f; s.VelocityY = 0f; }
    }
}
