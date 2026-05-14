namespace Shared;

public enum Opcode : ushort
{
    CInputState       = 1,
    SWelcome          = 10,
    SPlayerJoined     = 100,
    SPlayerLeft       = 101,
    SWorldSnapshot      = 200,
    SGameObjectSnapshot = 202,
}

public static class Framing
{
    public const int   Port             = 7777;
    public const int   DatagramHeaderSize = sizeof(ushort) + sizeof(ushort);
    public const float TickRate         = 20f;
    public const float TickDelta        = 1f / TickRate;
}
