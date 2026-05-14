using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Implemented by integration packages (currently
/// <c>Wolverine.CritterWatch.Http</c>) that surface non-Wolverine
/// ASP.NET Core endpoints — Minimal API, MVC actions, Razor Pages,
/// SignalR hub methods — as <see cref="AspNetEndpointDescriptor"/>
/// snapshots for CritterWatch.
/// </summary>
/// <remarks>
/// Pure-Wolverine worker hosts (no ASP.NET Core stack loaded) won't
/// register any source. The descriptors are eagerly built once at
/// host startup (HTTP graph is static across process lifetime) and
/// cached; this interface just exposes the cached collection so
/// <c>ServiceCapabilities.ReadFrom</c> can fold them into its snapshot
/// without re-running discovery on every emit.
/// </remarks>
public interface IAspNetEndpointDescriptorSource
{
    IReadOnlyList<AspNetEndpointDescriptor> Endpoints { get; }
}
