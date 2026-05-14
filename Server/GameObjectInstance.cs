using Shared;
using Shared.Scripts;

namespace Server;

public sealed class GameObjectInstance(uint id, float x, float y, float z, float yaw,
                                       IGameObjectScript script)
{
    public uint  Id  = id;
    public float X = x, Y = y, Z = z, Yaw = yaw;

    public void Tick(uint tick, float t)
    {
        var state = new GameObjectState { Id = Id, X = X, Y = Y, Z = Z, Yaw = Yaw };
        script.Update(tick, t, ref state);
        X = state.X; Y = state.Y; Z = state.Z; Yaw = state.Yaw;
    }
}
