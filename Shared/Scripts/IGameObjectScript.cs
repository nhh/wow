namespace Shared.Scripts;

public interface IGameObjectScript
{
    void Update(uint tick, float t, ref GameObjectState state);
}
