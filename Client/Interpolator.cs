using Shared;
using System.Diagnostics;

namespace Client;

public class Interpolator
{
    // --- Players: buffered interpolation (same approach as particles) ---
    public volatile uint MyPlayerId;
    private const double PlayerDelay = 0.1; // 2 ticks behind

    private record PlayerFrame(PlayerSnapshot[] Data, double Time);
    private readonly object       _playerLock   = new();
    private readonly PlayerFrame[] _playerBuffer = new PlayerFrame[8];
    private int _playerHead, _playerCount;

#if DEBUG
    private int _updateCount;
#endif

    public void UpdatePlayers(ReadOnlySpan<PlayerSnapshot> snaps)
    {
        double now   = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var    frame = new PlayerFrame(snaps.ToArray(), now);
        lock (_playerLock)
        {
            int slot = (_playerHead + _playerCount) % _playerBuffer.Length;
            if (_playerCount < _playerBuffer.Length)
                { _playerBuffer[slot] = frame; _playerCount++; }
            else
                { _playerBuffer[_playerHead] = frame; _playerHead = (_playerHead + 1) % _playerBuffer.Length; }
        }
#if DEBUG
        if (++_updateCount % 40 == 0)
            foreach (var s in snaps)
                Console.Error.WriteLine($"[RECV] id={s.PlayerId} X={s.X:F2} Z={s.Z:F2}");
#endif
    }

    public PlayerSnapshot[] Players
    {
        get
        {
            PlayerFrame[] frames;
            int count;
            lock (_playerLock)
            {
                count  = _playerCount;
                frames = new PlayerFrame[count];
                for (int i = 0; i < count; i++)
                    frames[i] = _playerBuffer[(_playerHead + i) % _playerBuffer.Length];
            }
            if (count == 0) return [];
            if (count == 1) return frames[0].Data;

            double renderTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - PlayerDelay;
            PlayerFrame? prev = null, cur = null;
            for (int i = 0; i < count - 1; i++)
                if (frames[i].Time <= renderTime && frames[i + 1].Time >= renderTime)
                    { prev = frames[i]; cur = frames[i + 1]; break; }

            if (prev == null)
                return renderTime <= frames[0].Time ? frames[0].Data : frames[count - 1].Data;

            float alpha = Math.Clamp((float)((renderTime - prev.Time) / (cur!.Time - prev.Time)), 0f, 1f);
            var result  = new PlayerSnapshot[cur.Data.Length];
            for (int i = 0; i < cur.Data.Length; i++)
            {
                var c = cur.Data[i];
                PlayerSnapshot p = default;
                bool found = false;
                foreach (var ps in prev.Data) if (ps.PlayerId == c.PlayerId) { p = ps; found = true; break; }
                float dyaw = c.Yaw - p.Yaw;
                dyaw -= MathF.Round(dyaw / MathF.Tau) * MathF.Tau;
                result[i] = found ? new PlayerSnapshot
                {
                    PlayerId = c.PlayerId,
                    X   = p.X + (c.X - p.X) * alpha,
                    Y   = p.Y + (c.Y - p.Y) * alpha,
                    Z   = p.Z + (c.Z - p.Z) * alpha,
                    Yaw = p.Yaw + dyaw * alpha,
                } : c;
            }
            return result;
        }
    }

    // --- Particles: buffered snapshot interpolation ---
    // Render time is always InterpolationDelay behind wall clock, so we always
    // have a prev+cur pair bracketing the render time regardless of packet jitter.
    private const double InterpolationDelay = 0.1; // 2 ticks

    private record Frame(ParticleSnapshot[] Data, double Time);

    private readonly object _lock    = new();
    private readonly Frame[] _buffer = new Frame[8];
    private int _bufHead; // index of oldest frame
    private int _bufCount;

    private long _lastSnapshotTick;
    public double SnapshotAgeMs =>
        (Stopwatch.GetTimestamp() - _lastSnapshotTick) * 1000.0 / Stopwatch.Frequency;

    public void UpdateParticles(ReadOnlySpan<ParticleSnapshot> snaps)
    {
        _lastSnapshotTick = Stopwatch.GetTimestamp();
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var frame = new Frame(snaps.ToArray(), now);
        lock (_lock)
        {
            int slot = (_bufHead + _bufCount) % _buffer.Length;
            if (_bufCount < _buffer.Length)
            {
                _buffer[slot] = frame;
                _bufCount++;
            }
            else
            {
                // overwrite oldest
                _buffer[_bufHead] = frame;
                _bufHead = (_bufHead + 1) % _buffer.Length;
            }
        }
    }

    public ParticleSnapshot[] GetParticles()
    {
        Frame[] frames;
        int count;
        lock (_lock)
        {
            count  = _bufCount;
            frames = new Frame[count];
            for (int i = 0; i < count; i++)
                frames[i] = _buffer[(_bufHead + i) % _buffer.Length];
        }

        if (count == 0) return [];
        if (count == 1) return frames[0].Data;

        double renderTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency
                            - InterpolationDelay;

        // find the two frames that bracket renderTime
        Frame? prev = null, cur = null;
        for (int i = 0; i < count - 1; i++)
        {
            if (frames[i].Time <= renderTime && frames[i + 1].Time >= renderTime)
            {
                prev = frames[i];
                cur  = frames[i + 1];
                break;
            }
        }

        // renderTime outside buffer range: clamp to edges
        if (prev == null)
            return renderTime <= frames[0].Time ? frames[0].Data : frames[count - 1].Data;

        float alpha = (float)((renderTime - prev.Time) / (cur!.Time - prev.Time));
        alpha = Math.Clamp(alpha, 0f, 1f);

        return Lerp(prev.Data, cur.Data, alpha);
    }

    private static ParticleSnapshot[] Lerp(ParticleSnapshot[] prev, ParticleSnapshot[] cur, float alpha)
    {
        int interpCount = Math.Min(prev.Length, cur.Length);
        var result = new ParticleSnapshot[cur.Length];

        for (int i = 0; i < interpCount; i++)
        {
            float dx = cur[i].X - prev[i].X;
            float dy = cur[i].Y - prev[i].Y;
            float dz = cur[i].Z - prev[i].Z;

            // large jump = wrap-around, snap to current
            if (dx * dx + dy * dy + dz * dz > 9f)
            {
                result[i] = cur[i];
                continue;
            }

            result[i] = new ParticleSnapshot
            {
                X       = prev[i].X + dx * alpha,
                Y       = prev[i].Y + dy * alpha,
                Z       = prev[i].Z + dz * alpha,
                ColorId = cur[i].ColorId,
            };
        }

        for (int i = interpCount; i < cur.Length; i++)
            result[i] = cur[i];

        return result;
    }
}
