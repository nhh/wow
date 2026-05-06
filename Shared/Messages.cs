using MemoryPack;

namespace Shared;

[MemoryPackable]
public partial class CInputState
{
    public uint  Tick     { get; init; }
    public bool  Forward  { get; init; }
    public bool  Backward { get; init; }
    public bool  Left     { get; init; }
    public bool  Right    { get; init; }
    public float Yaw      { get; init; }
    public bool  Jump     { get; init; }
}

[MemoryPackable]
[MemoryPackUnion(100, typeof(SPlayerJoined))]
[MemoryPackUnion(101, typeof(SPlayerLeft))]
public abstract partial class ServerEvent { }

[MemoryPackable]
public partial class SPlayerJoined : ServerEvent
{
    public uint  PlayerId { get; init; }
    public float X        { get; init; }
    public float Y        { get; init; }
    public float Z        { get; init; }
}

[MemoryPackable]
public partial class SPlayerLeft : ServerEvent
{
    public uint PlayerId { get; init; }
}
