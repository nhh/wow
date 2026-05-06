using Shared;

namespace Server;

public class ParticleSystem(int count)
{
    private readonly ParticleSnapshot[] _positions = new ParticleSnapshot[count];
    public ReadOnlySpan<ParticleSnapshot> Positions => _positions;

    private const float FormationDuration = 9f;   // seconds per formation
    private const float BlendDuration     = 2f;   // crossfade window

    private enum Formation { Sphere, DoubleHelix, WaveGrid, Vortex, Galaxy }
    private const int FormationCount = 5;

    public void Update(uint tick)
    {
        float t        = tick * Framing.TickDelta;
        float cycleLen = FormationDuration * FormationCount;
        float cyclePos = t % cycleLen;
        var   form     = (Formation)(int)(cyclePos / FormationDuration);
        float formT    = cyclePos - (int)form * FormationDuration;

        float blend = 0f;
        if (formT >= FormationDuration - BlendDuration)
        {
            blend = (formT - (FormationDuration - BlendDuration)) / BlendDuration;
            blend = blend * blend * (3f - 2f * blend); // smoothstep
        }
        var nextForm = (Formation)(((int)form + 1) % FormationCount);

        for (int i = 0; i < count; i++)
        {
            GetPos(form, i, t, count, out float ax, out float ay, out float az, out byte ac);
            if (blend > 0f)
            {
                GetPos(nextForm, i, t, count, out float bx, out float by, out float bz, out byte bc);
                ax = ax + (bx - ax) * blend;
                ay = ay + (by - ay) * blend;
                az = az + (bz - az) * blend;
                if (blend > 0.5f) ac = bc;
            }
            _positions[i] = new ParticleSnapshot { X = ax, Y = ay, Z = az, ColorId = ac };
        }
    }

    private static void GetPos(Formation f, int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        switch (f)
        {
            case Formation.Sphere:     Sphere    (i, t, n, out x, out y, out z, out colorId); break;
            case Formation.DoubleHelix:DoubleHelix(i, t, n, out x, out y, out z, out colorId); break;
            case Formation.WaveGrid:   WaveGrid  (i, t, n, out x, out y, out z, out colorId); break;
            case Formation.Vortex:     Vortex    (i, t, n, out x, out y, out z, out colorId); break;
            default:                   Galaxy    (i, t, n, out x, out y, out z, out colorId); break;
        }
    }

    // --- Formation 0: Fibonacci sphere, full rotation ---
    private static void Sphere(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        float phi   = MathF.Acos(1f - 2f * (i + 0.5f) / n);
        float theta = MathF.PI * (1f + MathF.Sqrt(5f)) * i + t * 0.4f;
        float r     = 13f;
        x = r * MathF.Sin(phi) * MathF.Cos(theta);
        y = r * MathF.Cos(phi) + 13f;
        z = r * MathF.Sin(phi) * MathF.Sin(theta);
        colorId = (byte)(i * 8 / n + 1); // 8 latitude bands
    }

    // --- Formation 1: Double helix (DNA) ---
    private static void DoubleHelix(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        int   strand  = i & 1;
        float frac    = (i >> 1) / (float)(n >> 1);
        float angle   = frac * MathF.PI * 12f + t * 1.8f + strand * MathF.PI;
        float radius  = 5f + MathF.Sin(frac * MathF.PI * 4f) * 1.5f;
        x = MathF.Cos(angle) * radius;
        y = frac * 22f;
        z = MathF.Sin(angle) * radius;
        colorId = (byte)(strand == 0 ? 1 : 5); // two distinct colors
    }

    // --- Formation 2: Animated sine wave surface (grid) ---
    private static void WaveGrid(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        int cols = (int)MathF.Sqrt(n);
        int rows = n / cols;
        int row  = i / cols;
        int col  = i % cols;
        float fx = col / (float)(cols - 1);
        float fz = row / (float)Math.Max(rows - 1, 1);
        x = (fx - 0.5f) * 44f;
        z = (fz - 0.5f) * 44f;
        y = MathF.Sin(fx * MathF.PI * 5f + t * 2.5f) * 5f
          + MathF.Sin(fz * MathF.PI * 4f + t * 1.8f) * 5f
          + MathF.Sin((fx + fz) * MathF.PI * 3f + t)  * 2f + 10f;
        // color by wave height bucket
        int bucket = (int)Math.Clamp((y - 2f) / 2f, 0, 7);
        colorId = (byte)(bucket + 1);
    }

    // --- Formation 3: Vortex (wide at top, tight at bottom) ---
    private static void Vortex(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        float frac   = i / (float)n;
        float height = frac * 22f;
        float radius = 1.5f + frac * 11f;
        float speed  = 4f - frac * 2.5f; // spins faster at bottom
        float angle  = frac * MathF.PI * 24f + t * speed;
        x = MathF.Cos(angle) * radius;
        y = height;
        z = MathF.Sin(angle) * radius;
        colorId = (byte)((int)(frac * 7f) + 1); // gradient bottom→top
    }

    // --- Formation 4: Galaxy (3 spiral arms) ---
    private static void Galaxy(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        int   arm      = i % 3;
        float frac     = (i / 3) / (float)(n / 3);
        float armAngle = arm * (MathF.Tau / 3f);
        float radius   = 1.5f + frac * 19f;
        float angle    = frac * MathF.PI * 7f + armAngle + t * 0.35f;
        float scatter  = MathF.Sin(frac * 11f + t * 0.7f + arm) * 1.2f;
        x = MathF.Cos(angle) * radius + scatter;
        y = 9f + MathF.Sin(frac * MathF.PI * 3f + t + arm) * 1.5f;
        z = MathF.Sin(angle) * radius + scatter;
        colorId = (byte)(arm + 2); // arms: colors 2, 3, 4
    }
}
