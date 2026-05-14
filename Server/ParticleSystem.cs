using Shared;
using Shared.Scripts;

namespace Server;

public class ParticleSystem(int count, IParticleScript script)
{
    private readonly ParticleSnapshot[] _positions = new ParticleSnapshot[count];
    public ReadOnlySpan<ParticleSnapshot> Positions => _positions;

    public void Update(uint tick)
    {
        float t = tick * Framing.TickDelta;
        script.Update(tick, t, _positions);
    }
}
