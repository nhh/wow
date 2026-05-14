using System.Runtime.InteropServices;

namespace Shared;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerSnapshot   // 20 bytes — interpolation / rendering only, not wire
{
    public uint  PlayerId;
    public float X, Y, Z, Yaw;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlayerSnapshotQ  // 11 bytes — wire format (quantized)
{
    public uint  PlayerId; // 4
    public short X, Y, Z;  // 6  — fixed-point ×100, ±327.68 m, 1 cm resolution
    public byte  Yaw;      // 1  — 256 steps = 1.4° resolution

    public PlayerSnapshot Decode() => new()
    {
        PlayerId = PlayerId,
        X   = X / 100f,
        Y   = Y / 100f,
        Z   = Z / 100f,
        Yaw = Yaw / 256f * MathF.Tau,
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ParticleSnapshot  // 13 bytes
{
    public float X, Y, Z;
    public byte  ColorId;   // 0 = default blue, 1-6 = highlight palette
}

// Runtime / interpolation type — client + script interface
public struct GameObjectState
{
    public uint  Id;
    public float X, Y, Z, Yaw;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameObjectSnapshot  // 11 bytes — wire format (quantized, same layout as PlayerSnapshotQ)
{
    public uint  Id;      // 4
    public short X, Y, Z; // 6  — fixed-point ×100, ±327.68 m, 1 cm resolution
    public byte  Yaw;     // 1  — 256 steps

    public GameObjectState Decode() => new()
    {
        Id  = Id,
        X   = X / 100f,
        Y   = Y / 100f,
        Z   = Z / 100f,
        Yaw = Yaw / 256f * MathF.Tau,
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WelcomeMsg   // 4 bytes
{
    public uint PlayerId;
}

public static class DatagramWriter
{
    public static int Write<T>(Span<byte> buf, Opcode op, ReadOnlySpan<T> items)
        where T : unmanaged
    {
        int structSize = Marshal.SizeOf<T>();
        BitConverter.TryWriteBytes(buf,      (ushort)op);
        BitConverter.TryWriteBytes(buf[2..], (ushort)items.Length);
        MemoryMarshal.AsBytes(items).CopyTo(buf[4..]);
        return 4 + items.Length * structSize;
    }

    public static ReadOnlySpan<T> Read<T>(ReadOnlySpan<byte> buf)
        where T : unmanaged
    {
        int count = BitConverter.ToUInt16(buf[2..]);
        return MemoryMarshal.Cast<byte, T>(buf[4..(4 + count * Marshal.SizeOf<T>())]);
    }
}
