namespace Wolverine.Runtime.Serialization;

internal static class BinaryWriterExtensions
{
    public static void WriteProp(this BinaryWriter writer, ref int count, string key, string? value)
    {
        if (value == null)
        {
            return;
        }

        writer.Write(key);
        writer.Write(value);

        count++;
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, int value)
    {
        if (value > 0)
        {
            writer.Write(key);
            writer.Write(value.ToString());

            count++;
        }
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, Guid value)
    {
        if (value != Guid.Empty)
        {
            writer.Write(key);
            writer.Write(value.ToString());

            count++;
        }
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, bool value)
    {
        if (value)
        {
            writer.Write(key);
            writer.Write(value.ToString());

            count++;
        }
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, DateTime? value)
    {
        if (value.HasValue)
        {
            writer.Write(key);
            writer.Write(value.Value.ToString("o"));

            count++;
        }
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writer.Write(key);
            writer.Write(value.Value.ToString("o"));

            count++;
        }
    }

    public static void WriteProp(this BinaryWriter writer, ref int count, string key, Uri? value)
    {
        if (value == null)
        {
            return;
        }

        writer.Write(key);
        writer.Write(value.ToString());

        count++;
    }
}