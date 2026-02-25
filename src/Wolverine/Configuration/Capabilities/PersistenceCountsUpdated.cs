using Wolverine.Logging;

namespace Wolverine.Configuration.Capabilities;

public record PersistenceCountsUpdated(Uri DatabaseUri, PersistedCounts Counts);