using Shared;

namespace Server;

public class ParticleSystem(int maxCount)
{
    private readonly ParticleSnapshot[] _positions = new ParticleSnapshot[maxCount];
    private int _activeCount;

    public int Count => _activeCount;

    // Returns only the active slice — bandwidth varies with count
    public ReadOnlySpan<ParticleSnapshot> Positions => _positions.AsSpan(0, _activeCount);

    public void Update(uint tick)
    {
        float t = tick * Framing.TickDelta;

        // count pulses gently between 60% and 100%
        _activeCount = (int)(maxCount * (0.6f + 0.4f * (0.5f + 0.5f * MathF.Sin(t * 0.08f))));

        // color slot changes every 5 s
        uint colorSlot = tick / 100;
        byte colorId   = (byte)(colorSlot % 6 + 1);

        // 3 tornado columns at fixed world positions
        float[] cx = [  0f, -14f,  14f ];
        float[] cz = [ 18f,  -9f,  -9f ];

        int maxPerStream = Math.Max(1, maxCount / 3);

        for (int i = 0; i < _activeCount; i++)
        {
            int   stream   = i % 3;
            float progress = (float)(i / 3) / maxPerStream;

            float fallSpeed = 1.2f + stream * 0.1f;
            float yPhase    = progress * 20f;
            float y = 20f - (t * fallSpeed + yPhase) % 20f;  // all descend

            // tornado funnel: wide at bottom (y≈0), narrow at top (y≈20)
            float funnel = 0.3f + (1f - y / 20f) * 1.8f;   // radius 0.3..2.1 u

            // spin faster than the calm rings; each tornado slightly different
            float angle = progress * MathF.Tau * 2f + t * (0.5f + stream * 0.07f);

            bool highlighted = ((uint)i * 2654435761u ^ colorSlot) % 10u == 0;
            _positions[i] = new ParticleSnapshot
            {
                X       = cx[stream] + MathF.Sin(angle) * funnel,
                Y       = y,
                Z       = cz[stream] + MathF.Cos(angle) * funnel,
                ColorId = highlighted ? colorId : (byte)0,
            };
        }
    }
}
