namespace Wolverine;

/// <summary>
/// Marks a message type as being able to read and write itself
/// to and from a byte array
/// </summary>
public interface ISerializable
{
    byte[] Write();
#pragma warning disable CA2252
    static abstract object Read(byte[] bytes);
#pragma warning restore CA2252
}

