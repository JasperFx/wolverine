

using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

public record MessageStore(Uri Uri, bool SupportsDeadLetterQueueAdmin, DatabaseDescriptor Database);