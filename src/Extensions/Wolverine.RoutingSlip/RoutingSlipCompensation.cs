using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip;

/// <inheritdoc />
public sealed record RoutingSlipCompensation(Guid ExecutionId, Uri DestinationUri) : IRoutingSlipCompensation;