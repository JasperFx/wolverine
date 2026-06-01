using JasperFx.CodeGeneration.Frames;

namespace Wolverine.Configuration;

/// <summary>
/// Implemented by codegen frames whose work resolves a "should-be-singleton-per-message" instance
/// (e.g. an outbox-enrolled <c>IDocumentSession</c>) that must also be seen by any service-located
/// dependency. When a chain falls back to service location, Wolverine collects a scoping frame from
/// every <see cref="IRequireScopingFrame"/> in the chain and emits it immediately after the child
/// service-location scope is created, so service location returns the already-resolved instance
/// rather than a duplicate. See GH-3001.
/// </summary>
public interface IRequireScopingFrame
{
    /// <summary>
    /// Build the frame that primes the service-location scope with this frame's already-resolved
    /// instance. Returns null when, for the current configuration, no priming is required.
    /// </summary>
    SyncFrame? BuildScopingFrame();
}
