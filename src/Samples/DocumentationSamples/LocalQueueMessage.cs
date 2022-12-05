using Wolverine.Attributes;

namespace DocumentationSamples;

#region sample_local_queue_routed_message

[LocalQueue("important")]
public class ImportanceMessage
{
}

#endregion