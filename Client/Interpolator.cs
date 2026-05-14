using Shared;
using System.Diagnostics;

namespace Client;

public class Interpolator
{
    private const double InterpolationDelay = 0.1; // 2 ticks behind

    public volatile uint MyPlayerId;

    private readonly InterpolationBuffer<PlayerSnapshot>   _players     = new();
    private readonly InterpolationBuffer<ParticleSnapshot> _particles   = new();
    private readonly InterpolationBuffer<GameObjectState>  _gameObjects = new();

    private long _lastSnapshotTick;
    public double SnapshotAgeMs =>
        (Stopwatch.GetTimestamp() - _lastSnapshotTick) * 1000.0 / Stopwatch.Frequency;

    // Latest received own-player snapshot — no interpolation delay, used for reconciliation
    private PlayerSnapshot _latestSelf;
    private bool           _hasSelf;
    private readonly object _selfLock = new();

    public bool TryGetLatestSelf(out PlayerSnapshot snap)
    {
        lock (_selfLock) { snap = _latestSelf; return _hasSelf; }
    }

#if DEBUG
    private int _updateCount;
#endif

    public void UpdatePlayers(ReadOnlySpan<PlayerSnapshot> snaps)
    {
        _players.Push(snaps);
        uint myId = MyPlayerId;
        if (myId != 0)
            foreach (var s in snaps)
                if (s.PlayerId == myId)
                {
                    lock (_selfLock) { _latestSelf = s; _hasSelf = true; }
                    break;
                }
#if DEBUG
        if (++_updateCount % 40 == 0)
            foreach (var s in snaps)
                Console.Error.WriteLine($"[RECV] id={s.PlayerId} X={s.X:F2} Z={s.Z:F2}");
#endif
    }

    public void UpdateParticles(ReadOnlySpan<ParticleSnapshot> snaps)
    {
        _lastSnapshotTick = Stopwatch.GetTimestamp();
        _particles.Push(snaps);
    }

    public void UpdateGameObjects(GameObjectState[] snaps) => _gameObjects.Push(snaps);

    public PlayerSnapshot[] Players
    {
        get
        {
            var (prev, cur, alpha) = _players.GetBracket(InterpolationDelay);
            if (cur is null) return [];
            if (prev is null) return cur;
            return LerpPlayers(prev, cur, alpha);
        }
    }

    public ParticleSnapshot[] GetParticles()
    {
        var (prev, cur, alpha) = _particles.GetBracket(InterpolationDelay);
        if (cur is null) return [];
        if (prev is null) return cur;
        return LerpParticles(prev, cur, alpha);
    }

    public GameObjectState[] GetGameObjects()
    {
        var (prev, cur, alpha) = _gameObjects.GetBracket(InterpolationDelay);
        if (cur is null) return [];
        if (prev is null) return cur;
        return LerpGameObjects(prev, cur, alpha);
    }

    private static PlayerSnapshot[] LerpPlayers(PlayerSnapshot[] prev, PlayerSnapshot[] cur, float alpha)
    {
        var result = new PlayerSnapshot[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            var  c     = cur[i];
            bool found = false;
            PlayerSnapshot p = default;
            foreach (var ps in prev) if (ps.PlayerId == c.PlayerId) { p = ps; found = true; break; }
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

    private static ParticleSnapshot[] LerpParticles(ParticleSnapshot[] prev, ParticleSnapshot[] cur, float alpha)
    {
        if (prev.Length != cur.Length) return cur;
        var result = new ParticleSnapshot[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            float dx = cur[i].X - prev[i].X;
            float dy = cur[i].Y - prev[i].Y;
            float dz = cur[i].Z - prev[i].Z;
            if (dx * dx + dy * dy + dz * dz > 9f) { result[i] = cur[i]; continue; }
            result[i] = new ParticleSnapshot
            {
                X       = prev[i].X + dx * alpha,
                Y       = prev[i].Y + dy * alpha,
                Z       = prev[i].Z + dz * alpha,
                ColorId = cur[i].ColorId,
            };
        }
        return result;
    }

    private static GameObjectState[] LerpGameObjects(GameObjectState[] prev, GameObjectState[] cur, float alpha)
    {
        var result = new GameObjectState[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            var  c     = cur[i];
            bool found = false;
            GameObjectState p = default;
            foreach (var ps in prev) if (ps.Id == c.Id) { p = ps; found = true; break; }
            float dyaw = c.Yaw - p.Yaw;
            dyaw -= MathF.Round(dyaw / MathF.Tau) * MathF.Tau;
            result[i] = found ? new GameObjectState
            {
                Id  = c.Id,
                X   = p.X + (c.X - p.X) * alpha,
                Y   = p.Y + (c.Y - p.Y) * alpha,
                Z   = p.Z + (c.Z - p.Z) * alpha,
                Yaw = p.Yaw + dyaw * alpha,
            } : c;
        }
        return result;
    }

    private sealed class InterpolationBuffer<T>
    {
        private record Frame(T[] Data, double Time);

        private readonly object  _lock = new();
        private readonly Frame[] _buf  = new Frame[8];
        private int _head, _count;

        public void Push(ReadOnlySpan<T> snaps)
        {
            double now   = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var    frame = new Frame(snaps.ToArray(), now);
            lock (_lock)
            {
                int slot = (_head + _count) % _buf.Length;
                if (_count < _buf.Length) { _buf[slot] = frame; _count++; }
                else                      { _buf[_head] = frame; _head = (_head + 1) % _buf.Length; }
            }
        }

        public (T[]? prev, T[]? cur, float alpha) GetBracket(double delay)
        {
            Frame[] frames;
            int count;
            lock (_lock)
            {
                count  = _count;
                frames = new Frame[count];
                for (int i = 0; i < count; i++)
                    frames[i] = _buf[(_head + i) % _buf.Length];
            }
            if (count == 0) return (null, null, 0f);
            if (count == 1) return (null, frames[0].Data, 0f);

            double renderTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - delay;
            for (int i = 0; i < count - 1; i++)
            {
                if (frames[i].Time <= renderTime && frames[i + 1].Time >= renderTime)
                {
                    float alpha = Math.Clamp(
                        (float)((renderTime - frames[i].Time) / (frames[i + 1].Time - frames[i].Time)),
                        0f, 1f);
                    return (frames[i].Data, frames[i + 1].Data, alpha);
                }
            }
            return (null, renderTime <= frames[0].Time ? frames[0].Data : frames[count - 1].Data, 0f);
        }
    }
}
