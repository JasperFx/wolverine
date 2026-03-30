namespace Wolverine.SourceGeneration
{
    /// <summary>
    /// Metadata about a discovered message type.
    /// </summary>
    internal sealed class MessageTypeInfo
    {
        public MessageTypeInfo(string fullName, string alias)
        {
            FullName = fullName;
            Alias = alias;
        }

        public string FullName { get; }
        public string Alias { get; }
    }
}
