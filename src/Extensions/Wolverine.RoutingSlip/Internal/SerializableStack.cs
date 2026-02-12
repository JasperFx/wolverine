using System.Text.Json.Serialization;

/// <summary>
/// Stack implementation safe for ANY serializer without custom converters.
/// Serializes as plain array/list in bottom-to-top order: [bottom, ..., top].
/// Works with JSON, XML, binary, protobuf, MessagePack — no configuration needed.
/// </summary>
/// <typeparam name="T">Element type</typeparam>
public sealed class SerializableStack<T>
{
    /// <summary>
    /// Elements stored bottom-to-top. Serialized as-is by ANY serializer.
    /// Index 0 = bottom, Last index = top.
    /// </summary>
    public List<T> Items { get; set; } = [];

    /// <summary>
    /// Pushes element to the top of the stack.
    /// </summary>
    public void Push(T item) => Items.Add(item);

    /// <summary>
    /// Removes and returns the top element.
    /// </summary>
    /// <exception cref="InvalidOperationException">When stack is empty</exception>
    public T Pop()
    {
        if (Items.Count == 0)
            throw new InvalidOperationException("Stack is empty");
        
        var item = Items[^1];
        Items.RemoveAt(Items.Count - 1);
        return item;
    }

    /// <summary>
    /// Attempts to pop without exception.
    /// </summary>
    public bool TryPop(out T result)
    {
        if (Items.Count == 0)
        {
            result = default!;
            return false;
        }

        result = Items[^1];
        Items.RemoveAt(Items.Count - 1);
        return true;
    }

    /// <summary>
    /// Returns top element without removal.
    /// </summary>
    public T Peek()
    {
        if (Items.Count == 0)
            throw new InvalidOperationException("Stack is empty");
        
        return Items[^1];
    }

    /// <summary>
    /// Number of elements in the stack.
    /// </summary>
    [JsonIgnore] // Optional: exclude from JSON if desired
    public int Count => Items.Count;

    /// <summary>
    /// Checks if stack is empty.
    /// </summary>
    [JsonIgnore] // Optional
    public bool IsEmpty => Items.Count == 0;
}