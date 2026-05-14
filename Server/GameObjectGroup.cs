using Shared;
using Shared.Scripts;

namespace Server;

public sealed class GameObjectGroup(IGameObjectScript script, GameObjectState[] states)
{
    public readonly GameObjectState[] States = states;

    public void Tick(uint tick, float t) => script.UpdateBulk(tick, t, States);
}
