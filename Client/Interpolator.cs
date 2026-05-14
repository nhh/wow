using Shared;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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

    private readonly Dictionary<uint, PlayerSnapshot>   _prevPlayerLookup = new(64);
    private readonly Dictionary<uint, GameObjectState>  _prevGoLookup     = new(16);

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

    private PlayerSnapshot[] LerpPlayers(PlayerSnapshot[] prev, PlayerSnapshot[] cur, float alpha)
    {
        _prevPlayerLookup.Clear();
        foreach (var p in prev) _prevPlayerLookup[p.PlayerId] = p;

        var result = new PlayerSnapshot[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            var c = cur[i];
            if (!_prevPlayerLookup.TryGetValue(c.PlayerId, out var p))
            {
                result[i] = c;
                continue;
            }
            float dyaw = c.Yaw - p.Yaw;
            dyaw -= MathF.Round(dyaw / MathF.Tau) * MathF.Tau;
            result[i] = new PlayerSnapshot
            {
                PlayerId = c.PlayerId,
                X   = p.X + (c.X - p.X) * alpha,
                Y   = p.Y + (c.Y - p.Y) * alpha,
                Z   = p.Z + (c.Z - p.Z) * alpha,
                Yaw = p.Yaw + dyaw * alpha,
            };
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ParticleSnapshot[] LerpParticles(ParticleSnapshot[] prev, ParticleSnapshot[] cur, float alpha)
    {
        if (prev.Length != cur.Length) return cur;
        var result = new ParticleSnapshot[cur.Length];

        var vAlpha     = Vector128.Create(alpha);
        var vMaxDistSq = Vector128.Create(9f);

        int i = 0;
        for (int end = cur.Length - 3; i <= end; i += 4)
        {
            var pX = Vector128.Create(prev[i].X, prev[i+1].X, prev[i+2].X, prev[i+3].X);
            var pY = Vector128.Create(prev[i].Y, prev[i+1].Y, prev[i+2].Y, prev[i+3].Y);
            var pZ = Vector128.Create(prev[i].Z, prev[i+1].Z, prev[i+2].Z, prev[i+3].Z);
            var cX = Vector128.Create(cur[i].X,  cur[i+1].X,  cur[i+2].X,  cur[i+3].X);
            var cY = Vector128.Create(cur[i].Y,  cur[i+1].Y,  cur[i+2].Y,  cur[i+3].Y);
            var cZ = Vector128.Create(cur[i].Z,  cur[i+1].Z,  cur[i+2].Z,  cur[i+3].Z);

            var dx   = cX - pX;
            var dy   = cY - pY;
            var dz   = cZ - pZ;
            var mask = Vector128.GreaterThan(dx*dx + dy*dy + dz*dz, vMaxDistSq);

            var rX = Vector128.ConditionalSelect(mask, cX, pX + dx * vAlpha);
            var rY = Vector128.ConditionalSelect(mask, cY, pY + dy * vAlpha);
            var rZ = Vector128.ConditionalSelect(mask, cZ, pZ + dz * vAlpha);

            result[i]   = new ParticleSnapshot { X = rX[0], Y = rY[0], Z = rZ[0], ColorId = cur[i].ColorId };
            result[i+1] = new ParticleSnapshot { X = rX[1], Y = rY[1], Z = rZ[1], ColorId = cur[i+1].ColorId };
            result[i+2] = new ParticleSnapshot { X = rX[2], Y = rY[2], Z = rZ[2], ColorId = cur[i+2].ColorId };
            result[i+3] = new ParticleSnapshot { X = rX[3], Y = rY[3], Z = rZ[3], ColorId = cur[i+3].ColorId };
        }

        for (; i < cur.Length; i++)
        {
            float dx = cur[i].X - prev[i].X;
            float dy = cur[i].Y - prev[i].Y;
            float dz = cur[i].Z - prev[i].Z;
            if (dx*dx + dy*dy + dz*dz > 9f) { result[i] = cur[i]; continue; }
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

    private GameObjectState[] LerpGameObjects(GameObjectState[] prev, GameObjectState[] cur, float alpha)
    {
        _prevGoLookup.Clear();
        foreach (var p in prev) _prevGoLookup[p.Id] = p;

        var result = new GameObjectState[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            var c = cur[i];
            if (!_prevGoLookup.TryGetValue(c.Id, out var p))
            {
                result[i] = c;
                continue;
            }
            float dyaw = c.Yaw - p.Yaw;
            dyaw -= MathF.Round(dyaw / MathF.Tau) * MathF.Tau;
            result[i] = new GameObjectState
            {
                Id  = c.Id,
                X   = p.X + (c.X - p.X) * alpha,
                Y   = p.Y + (c.Y - p.Y) * alpha,
                Z   = p.Z + (c.Z - p.Z) * alpha,
                Yaw = p.Yaw + dyaw * alpha,
            };
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
