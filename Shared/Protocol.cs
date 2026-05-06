namespace Shared;

public enum Opcode : ushort
{
    CInputState       = 1,
    SWelcome          = 10,
    SPlayerJoined     = 100,
    SPlayerLeft       = 101,
    SWorldSnapshot    = 200,
    SParticleSnapshot = 201,
}

public static class Framing
{
    public const int   Port             = 7777;
    public const int   DatagramHeaderSize = sizeof(ushort) + sizeof(ushort);
    public const float TickRate         = 20f;
    public const float TickDelta        = 1f / TickRate;
    public const float MoveSpeed        = 5f;
    public const int   ParticleCount    = 1000;
    public const float WorldRadius      = 30f;
}
