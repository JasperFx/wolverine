using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// Thrown when a type named as an <see cref="EntityAttribute.Loader"/> cannot be used to load
/// the entity type it was named for.
/// </summary>
public class InvalidEntityLoaderException : Exception
{
    public InvalidEntityLoaderException(Type loaderType, Type entityType, string reason)
        : base(
            $"Type {loaderType.FullNameInCode()} cannot be used as the [Entity] loader for {entityType.FullNameInCode()}. {reason}")
    {
    }
}

/// <summary>
/// Resolves the single <c>Load</c> / <c>LoadAsync</c> method on a user-supplied loader type and
/// builds the code generation frames that call it. Any parameter of that method is resolved out of
/// the surrounding chain exactly like a handler or middleware method parameter, so a loader can take
/// its own services, the <c>TenantId</c>, route arguments, message members and the
/// <see cref="CancellationToken"/>.
/// </summary>
internal sealed class EntityLoaderPlan
{
    /// <summary>
    /// The method names a loader may use. Deliberately the same two names Wolverine already
    /// recognizes for "load the data this handler needs" so there is one convention to learn.
    /// </summary>
    private static readonly string[] _methodNames = ["Load", "LoadAsync"];

    private EntityLoaderPlan(Type loaderType, Type entityType, MethodInfo method)
    {
        LoaderType = loaderType;
        EntityType = entityType;
        Method = method;
    }

    public Type LoaderType { get; }
    public Type EntityType { get; }
    public MethodInfo Method { get; }

    // The loader type is supplied by the user through [Entity(Loader = typeof(...))] or
    // EntityDefaults.LoadWith(...), so it is statically rooted by that call site. The reflective
    // walk only runs under Dynamic codegen, which is intentionally not AOT-clean — same position as
    // FromQuerySpecificationAttribute. See docs/guide/aot.md.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Loader type is statically rooted by the [Entity(Loader = typeof(...))] / LoadWith call site. Dynamic codegen path. See AOT guide.")]
    public static EntityLoaderPlan For(Type loaderType, Type entityType)
    {
        ArgumentNullException.ThrowIfNull(loaderType);
        ArgumentNullException.ThrowIfNull(entityType);

        if (!loaderType.IsPublic && !loaderType.IsVisible)
        {
            throw new InvalidEntityLoaderException(loaderType, entityType, "The loader type must be public.");
        }

        var candidates = loaderType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.FlattenHierarchy)
            .Where(x => _methodNames.Contains(x.Name, StringComparer.OrdinalIgnoreCase))
            .Where(x => !x.IsGenericMethodDefinition)
            .Where(x => ReturnedEntityType(x) == entityType)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidEntityLoaderException(loaderType, entityType,
                $"Expected exactly one public 'Load' or 'LoadAsync' method returning {entityType.FullNameInCode()}, Task<{entityType.NameInCode()}> or ValueTask<{entityType.NameInCode()}>, but found none.");
        }

        if (candidates.Length > 1)
        {
            var signatures = candidates.Select(Describe).Join(", ");
            throw new InvalidEntityLoaderException(loaderType, entityType,
                $"Found {candidates.Length} candidate 'Load'/'LoadAsync' methods returning {entityType.FullNameInCode()}: {signatures}. Leave exactly one so Wolverine does not have to guess.");
        }

        return new EntityLoaderPlan(loaderType, entityType, candidates[0]);
    }

    private static string Describe(MethodInfo method)
    {
        var parameters = method.GetParameters().Select(x => x.ParameterType.NameInCode()).Join(", ");
        return $"{method.Name}({parameters})";
    }

    /// <summary>
    /// The entity type this method hands back, unwrapping <see cref="Task{T}" /> and
    /// <see cref="ValueTask{T}" />, or null when the method returns nothing usable.
    /// </summary>
    private static Type? ReturnedEntityType(MethodInfo method)
    {
        var returnType = method.ReturnType;

        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return null;
        }

        if (returnType.IsGenericType)
        {
            var openType = returnType.GetGenericTypeDefinition();
            if (openType == typeof(Task<>) || openType == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }

    /// <summary>
    /// The call to the loader, plus whatever has to happen first for its target to exist: nothing for
    /// a static loader class, nothing for one the container can resolve, and a constructor frame for
    /// a plain unregistered class — which is how an instance loader gets its own dependencies the
    /// same way a handler does.
    /// </summary>
    public (MethodCall Call, Frame[] Preamble) BuildFrames(IChain chain, IServiceContainer container)
    {
        var call = new MethodCall(LoaderType, Method);

        bindArguments(chain, call);

        if (Method.IsStatic)
        {
            return (call, []);
        }

        // An interface or abstract type has to come out of the container, and so should a concrete
        // type the application has already registered — building that one here would quietly ignore
        // its registration's factory and lifetime. Leaving the call's target unset makes Wolverine
        // resolve it as a service, which is what lets an existing store abstraction be named
        // directly as the loader.
        if (LoaderType.IsInterface || LoaderType.IsAbstract)
        {
            if (!container.HasRegistrationFor(LoaderType))
            {
                throw new InvalidEntityLoaderException(LoaderType, EntityType,
                    "An interface or abstract loader has to be registered in the application's service container.");
            }

            return (call, []);
        }

        if (container.HasRegistrationFor(LoaderType))
        {
            return (call, []);
        }

        return (call, container.TryCreateConstructorFrames([call]).ToArray());
    }

    /// <summary>
    /// Bind each loader parameter to a value the chain already exposes under that name — a message
    /// member, a route argument, a query string value, a header — and leave the rest for Wolverine's
    /// normal resolution, which is what supplies the loader's own services, the <c>TenantId</c>, the
    /// <see cref="CancellationToken" /> and so on.
    /// <para>
    /// Name-first is what makes a key of more than one value expressible at all: a <c>string id</c>
    /// parameter has to come from the message or the route, and no container can be asked for it. It
    /// is also safe — a chain only ever offers route and query string values that its own endpoint
    /// declares, so this cannot invent a binding.
    /// </para>
    /// </summary>
    private void bindArguments(IChain chain, MethodCall call)
    {
        var parameters = Method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.Name == null)
            {
                continue;
            }

            if (chain.TryFindVariable(parameter.Name, ValueSource.Anything, parameter.ParameterType,
                    out var variable))
            {
                call.Arguments[i] = variable;
            }
        }
    }
}
