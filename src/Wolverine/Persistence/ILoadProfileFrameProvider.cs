using System;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence;

/// <summary>
/// Opt-in extension of a persistence provider that supports named, declarative "load profiles" —
/// pre-declared include/fetch graphs selected per call site with <c>[Entity(Profile = "...")]</c>.
/// Only providers that can vary the loaded object graph (currently EF Core) implement this; when an
/// <c>[Entity]</c> carries a Profile and the resolved provider does not implement this interface,
/// <see cref="EntityAttribute"/> fails fast at codegen.
/// </summary>
public interface ILoadProfileFrameProvider
{
    /// <summary>
    /// Build the load frame for <paramref name="entityType"/> applying the named <paramref name="profile"/>.
    /// Implementations should validate the profile exists at codegen time and throw a descriptive
    /// exception if it does not, rather than silently loading nothing.
    /// </summary>
    Frame DetermineLoadFrame(IServiceContainer container, Type entityType, Variable identity, string profile);
}
