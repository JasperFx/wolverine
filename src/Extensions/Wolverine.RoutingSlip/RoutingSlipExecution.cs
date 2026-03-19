using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip;

/// <inheritdoc />
public sealed record RoutingSlipExecution(string Name, Uri DestinationUri) : IRoutingSlipExecution;