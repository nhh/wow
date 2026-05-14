namespace Shared.Scripts;

public interface IGameObjectScript
{
    void Update(uint tick, float t, ref GameObjectState state);

    // Bulk variant — override for index-aware scripts (particles, formations).
    // Default calls Update per element, which is correct for scripts that treat
    // all instances identically (e.g. SpinningBox).
    virtual void UpdateBulk(uint tick, float t, Span<GameObjectState> states)
    {
        for (int i = 0; i < states.Length; i++)
            Update(tick, t, ref states[i]);
    }
}
