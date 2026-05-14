using Shared;
using Shared.Scripts;

namespace Server;

public class FallbackParticleScript : IParticleScript
{
    private const float FormationDuration = 9f;
    private const float BlendDuration     = 2f;
    private const int   FormationCount    = 5;

    public void Update(uint tick, float t, Span<ParticleSnapshot> positions)
    {
        int   n        = positions.Length;
        float cycleLen = FormationDuration * FormationCount;
        float cyclePos = t % cycleLen;
        int   formIdx  = (int)(cyclePos / FormationDuration);
        float formT    = cyclePos - formIdx * FormationDuration;

        float blend = 0f;
        if (formT >= FormationDuration - BlendDuration)
            blend = Smoothstep((formT - (FormationDuration - BlendDuration)) / BlendDuration);

        int nextIdx = (formIdx + 1) % FormationCount;

        for (int i = 0; i < n; i++)
        {
            GetPos(formIdx, i, t, n, out float ax, out float ay, out float az, out byte ac);
            if (blend > 0f)
            {
                GetPos(nextIdx, i, t, n, out float bx, out float by, out float bz, out byte bc);
                ax = ax + (bx - ax) * blend;
                ay = ay + (by - ay) * blend;
                az = az + (bz - az) * blend;
                if (blend > 0.5f) ac = bc;
            }
            positions[i] = new ParticleSnapshot { X = ax, Y = ay, Z = az, ColorId = ac };
        }
    }

    private static float Smoothstep(float x) => x * x * (3f - 2f * x);

    private static void GetPos(int f, int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        switch (f)
        {
            case 0: Sphere    (i, t, n, out x, out y, out z, out colorId); break;
            case 1: DoubleHelix(i, t, n, out x, out y, out z, out colorId); break;
            case 2: WaveGrid  (i, t, n, out x, out y, out z, out colorId); break;
            case 3: Vortex    (i, t, n, out x, out y, out z, out colorId); break;
            default: Galaxy   (i, t, n, out x, out y, out z, out colorId); break;
        }
    }

    private static void Sphere(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        float phi   = MathF.Acos(1f - 2f * (i + 0.5f) / n);
        float theta = MathF.PI * (1f + MathF.Sqrt(5f)) * i + t * 0.4f;
        float r     = 13f;
        x = r * MathF.Sin(phi) * MathF.Cos(theta);
        y = r * MathF.Cos(phi) + 13f;
        z = r * MathF.Sin(phi) * MathF.Sin(theta);
        colorId = (byte)(i * 8 / n + 1);
    }

    private static void DoubleHelix(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        int   strand = i & 1;
        float frac   = (i >> 1) / (float)(n >> 1);
        float angle  = frac * MathF.PI * 12f + t * 1.8f + strand * MathF.PI;
        float radius = 5f + MathF.Sin(frac * MathF.PI * 4f) * 1.5f;
        x = MathF.Cos(angle) * radius;
        y = frac * 22f;
        z = MathF.Sin(angle) * radius;
        colorId = (byte)(strand == 0 ? 1 : 5);
    }

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
        colorId = (byte)(Math.Clamp((int)((y - 2f) / 2f), 0, 7) + 1);
    }

    private static void Vortex(int i, float t, int n,
        out float x, out float y, out float z, out byte colorId)
    {
        float frac   = i / (float)n;
        float height = frac * 22f;
        float radius = 1.5f + frac * 11f;
        float speed  = 4f - frac * 2.5f;
        float angle  = frac * MathF.PI * 24f + t * speed;
        x = MathF.Cos(angle) * radius;
        y = height;
        z = MathF.Sin(angle) * radius;
        colorId = (byte)((int)(frac * 7f) + 1);
    }

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
        colorId = (byte)(arm + 2);
    }
}
