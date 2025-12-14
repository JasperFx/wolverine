namespace Wolverine.Runtime;

/// <summary>
/// Just a place holder for the real message
/// </summary>
public class PlaceHolder : ISerializable
{
    public byte[] Write()
    {
        return [];
    }

    public static object Read(byte[] bytes)
    {
        return new PlaceHolder();
    }
}