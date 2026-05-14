namespace Shared.Scripts;

public interface IParticleScript
{
    void Update(uint tick, float t, Span<ParticleSnapshot> positions);
}
