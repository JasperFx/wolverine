using System;
using System.Collections.Generic;

namespace Wolverine.Util;

internal static class DictionaryWriterExtensions
{
    public static void WriteProp(this IDictionary<string, object> writer, string key, string? value)
    {
        if (value == null)
        {
            return;
        }

        writer.Add(key, value);
    }


    public static void WriteProp(this IDictionary<string, object> writer, string key, int value)
    {
        if (value > 0)
        {
            writer.Add(key, value.ToString());
        }
    }

    public static void WriteProp(this IDictionary<string, object> writer, string key, Guid value)
    {
        if (value != Guid.Empty)
        {
            writer.Add(key, value.ToString());
        }
    }

    public static void WriteProp(this IDictionary<string, object> writer, string key, bool value)
    {
        if (value)
        {
            writer.Add(key, "true");
        }
    }

    public static void WriteProp(this IDictionary<string, object> writer, string key, DateTime? value)
    {
        if (value.HasValue)
        {
            writer.Add(key, value.Value.ToString("o"));
        }
    }

    public static void WriteProp(this IDictionary<string, object> writer, string key, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writer.Add(key, value.Value.ToString("o"));
        }
    }

    public static void WriteProp(this IDictionary<string, object> writer, string key, Uri? value)
    {
        if (value == null)
        {
            return;
        }

        writer.Add(key, value.ToString());
    }
}
