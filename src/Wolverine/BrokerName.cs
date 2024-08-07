namespace Wolverine;

/// <summary>
/// Just identifies a named broker for the case of needing to connect a single
/// Wolverine application to multiple message brokers of the same type
/// </summary>
/// <param name="Name"></param>
public record BrokerName(string Name);