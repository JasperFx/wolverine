using System.Buffers;
using Newtonsoft.Json;

namespace Wolverine.Runtime.Serialization;

internal class JsonArrayPool<T> : IArrayPool<T>
{
    private readonly ArrayPool<T> _inner;

    public JsonArrayPool(ArrayPool<T> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public T[] Rent(int minimumLength)
    {
        return _inner.Rent(minimumLength);
    }

#pragma warning disable CS8767
    public void Return(T[] array)
#pragma warning restore CS8767
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        _inner.Return(array);
    }
}