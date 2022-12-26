namespace Wolverine.Util;

internal static class StreamExtensions
{
    public static async Task<byte[]> ReadBytesAsync(this Stream stream, long length)
    {
        var buffer = new byte[length];
        var totalRead = 0;
        int current;
        do
        {
            current = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead).ConfigureAwait(false);
            totalRead += current;
        } while (totalRead < length && current > 0);

        return buffer;
    }

    public static async Task<bool> ReadExpectedBufferAsync(this Stream stream, byte[] expected)
    {
        try
        {
            var bytes = await stream.ReadBytesAsync(expected.Length).ConfigureAwait(false);
            return expected.SequenceEqual(bytes);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Task SendBufferAsync(this Stream stream, byte[] buffer)
    {
        return stream.WriteAsync(buffer, 0, buffer.Length);
    }
}