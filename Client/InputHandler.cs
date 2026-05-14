using Silk.NET.Input;
using Shared;

namespace Client;

public class InputHandler
{
    private readonly IKeyboard     _kb;
    private readonly IMouse        _mouse;
    private readonly LiteNetClient _client;
    private readonly Action        _onExit;
    private readonly Interpolator  _interp;

    private bool  _posInitialized;
    private uint  _tick;
    private bool  _wasSpacePressed;

    private const float ReconcileHardSnap  = 3f;    // hard snap only for large divergence
    private const float ReconcileBlendRate = 15f;   // correction speed: 15× error per second
    private const float ReconcileDeadzone  = 0.02f; // below 2cm: don't correct
    private const float MoveSpeed          = 5f;    // Stage 4: replaced by SConfigSnapshot
    private const float Gravity           = 20f;
    private const float JumpSpeed         = 9f;

    public float Yaw        { get; private set; }
    public float Pitch      { get; private set; }
    public float LocalX     { get; private set; }
    public float LocalY     { get; private set; }
    public float LocalZ     { get; private set; }
    public float VelocityY  { get; private set; }

    private System.Numerics.Vector2 _lastMousePos;
    private bool _firstMouseEvent = true;

    private const float Sensitivity = 0.001f;
    private const float PitchLimit  = 1.45f;

    public InputHandler(IInputContext ctx, LiteNetClient client, Action onExit, Interpolator interp)
    {
        _client = client;
        _onExit = onExit;
        _interp = interp;
        _kb     = ctx.Keyboards[0];
        _mouse  = ctx.Mice[0];

        _mouse.Cursor.CursorMode = CursorMode.Disabled;
        _mouse.MouseMove += OnMouseMove;
    }

    private void OnMouseMove(IMouse _, System.Numerics.Vector2 pos)
    {
        if (_firstMouseEvent) { _lastMousePos = pos; _firstMouseEvent = false; return; }
        var delta = pos - _lastMousePos;
        _lastMousePos = pos;
        Yaw   -= delta.X * Sensitivity;
        Pitch -= delta.Y * Sensitivity;
        Pitch  = Math.Clamp(Pitch, -PitchLimit, PitchLimit);
    }

    public void Poll(double dt)
    {
        if (_kb.IsKeyPressed(Key.Escape)) { _onExit(); return; }

        float fwd = 0, str = 0;
        if (_kb.IsKeyPressed(Key.W)) fwd += 1;
        if (_kb.IsKeyPressed(Key.S)) fwd -= 1;
        if (_kb.IsKeyPressed(Key.A)) str += 1;
        if (_kb.IsKeyPressed(Key.D)) str -= 1;

        // Jump: edge-trigger on spacebar press while grounded
        bool spaceNow = _kb.IsKeyPressed(Key.Space);
        bool jump     = spaceNow && !_wasSpacePressed && LocalY <= 0f;
        _wasSpacePressed = spaceNow;

        // XZ prediction (mirrors server ApplyInput)
        float dx = fwd * MathF.Sin(Yaw) + str * MathF.Cos(Yaw);
        float dz = fwd * MathF.Cos(Yaw) - str * MathF.Sin(Yaw);
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0) { dx /= len; dz /= len; }
        LocalX += dx * MoveSpeed * (float)dt;
        LocalZ += dz * MoveSpeed * (float)dt;

        // Y prediction (mirrors server gravity/jump logic)
        if (jump) VelocityY = JumpSpeed;
        VelocityY -= Gravity * (float)dt;
        LocalY    += VelocityY * (float)dt;
        if (LocalY < 0f) { LocalY = 0f; VelocityY = 0f; }

        _client.SendInput(new CInputState
        {
            Tick     = _tick++,
            Forward  = fwd > 0,
            Backward = fwd < 0,
            Left     = str > 0,
            Right    = str < 0,
            Yaw      = Yaw,
            Jump     = jump,
        });

        // XZ reconciliation against latest server snapshot (no interpolation delay)
        if (_interp.TryGetLatestSelf(out var server))
        {
            if (!_posInitialized)
            {
                LocalX = server.X;
                LocalZ = server.Z;
                _posInitialized = true;
            }
            else
            {
                float ex = LocalX - server.X, ez = LocalZ - server.Z;
                float driftSq = ex * ex + ez * ez;
                if (driftSq > ReconcileHardSnap * ReconcileHardSnap)
                {
                    LocalX = server.X;
                    LocalZ = server.Z;
                }
                else if (driftSq > ReconcileDeadzone * ReconcileDeadzone)
                {
                    float alpha = Math.Min(1f, ReconcileBlendRate * (float)dt);
                    LocalX += (server.X - LocalX) * alpha;
                    LocalZ += (server.Z - LocalZ) * alpha;
                }
            }
        }
    }
}
