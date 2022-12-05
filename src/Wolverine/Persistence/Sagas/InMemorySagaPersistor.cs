using System;
using System.Collections.Concurrent;
using JasperFx.CodeGeneration;

namespace Wolverine.Persistence.Sagas;

public class InMemorySagaPersistor
{
    private readonly ConcurrentDictionary<string, object> _data = new();

    public static string ToKey(Type documentType, object id)
    {
        return documentType.FullName + "/" + id;
    }


    public T? Load<T>(object id) where T : class
    {
        var key = ToKey(typeof(T), id);


        if (_data.TryGetValue(key, out var value))
        {
            return value as T;
        }


        return null;
    }

    public void Store<T>(T document)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var id = typeof(T).GetProperty("Id")?.GetValue(document);
        if (id == null)
        {
            throw new InvalidOperationException(
                $"Type {typeof(T).FullNameInCode()} does not have a public Id property");
        }

        var key = ToKey(typeof(T), id);
        _data[key] = document;
    }

    public void Delete<T>(object id)
    {
        var key = ToKey(typeof(T), id);
        _data.TryRemove(key, out _);
    }
}