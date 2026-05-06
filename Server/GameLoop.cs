using Shared;
using System.Threading;

namespace Server;

public class GameLoop(LiteNetServer server, int particleCount = Framing.ParticleCount)
{
    private readonly ParticleSystem _particles = new(particleCount);
    private uint _tick;

    public async Task RunAsync()
    {
        long freq     = System.Diagnostics.Stopwatch.Frequency;
        long interval = (long)(Framing.TickDelta * freq);
        long spinHead = (long)(0.002 * freq);
        long next     = System.Diagnostics.Stopwatch.GetTimestamp() + interval;

        while (true)
        {
            Tick();

            long sleepUntil = next - spinHead;
            long now        = System.Diagnostics.Stopwatch.GetTimestamp();
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

        var playerSnaps = new PlayerSnapshot[sessions.Count];
        for (int i = 0; i < sessions.Count; i++)
            playerSnaps[i] = new PlayerSnapshot
            {
                PlayerId = sessions[i].PlayerId,
                X        = sessions[i].X,
                Y        = sessions[i].Y,
                Z        = sessions[i].Z,
                Yaw      = sessions[i].Yaw,
            };

        var worldBuf = new byte[Framing.DatagramHeaderSize +
                                playerSnaps.Length * System.Runtime.InteropServices.Marshal.SizeOf<PlayerSnapshot>()];
        DatagramWriter.Write<PlayerSnapshot>(worldBuf, Opcode.SWorldSnapshot, playerSnaps);

        var allParticles = _particles.Positions;
        var particleBuf  = new byte[Framing.DatagramHeaderSize +
                                    allParticles.Length * System.Runtime.InteropServices.Marshal.SizeOf<ParticleSnapshot>()];
        DatagramWriter.Write<ParticleSnapshot>(particleBuf, Opcode.SParticleSnapshot, allParticles);

        foreach (var s in sessions)
        {
            s.SendSnapshot(worldBuf);
            s.SendSnapshot(particleBuf);
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
