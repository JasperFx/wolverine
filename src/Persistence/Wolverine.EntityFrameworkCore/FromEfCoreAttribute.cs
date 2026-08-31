using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Load this parameter through EF Core by its primary key, exactly as <see cref="EntityAttribute" /> does, but
///     always through EF Core rather than through whichever registered persistence provider happens to claim the
///     type first — plus the two loading options that only mean something to EF Core.
/// </summary>
/// <remarks>
///     <para>
///         Every <see cref="EntityAttribute" /> option applies unchanged —
///         <see cref="EntityAttribute.Required" />, <see cref="EntityAttribute.OnMissing" />,
///         <see cref="EntityAttribute.MissingMessage" />, the <c>ArgumentName</c> constructor and the
///         <c>FromRoute</c> / <c>FromHeader</c> / <c>FromClaim</c> / <c>FromMethod</c> value sources — because this
///         attribute inherits the one implementation of them all.
///     </para>
///     <para>
///         With neither <see cref="AsNoTracking" /> nor an include path set, the emitted load is the same
///         <c>DbContext.FindAsync</c> that <c>[Entity]</c> emits, which can answer straight from the change tracker.
///         Setting either one switches to a <c>Set&lt;T&gt;()</c> query, because <c>FindAsync</c> supports neither.
///     </para>
/// </remarks>
public class FromEfCoreAttribute : ExplicitEntityAttribute
{
    public FromEfCoreAttribute()
    {
    }

    /// <param name="argumentName">
    ///     The name of the member on the incoming message, route argument, header, or claim that carries the
    ///     entity's identity, when it is not <c>Id</c> or <c>{EntityType}Id</c>.
    /// </param>
    public FromEfCoreAttribute(string argumentName) : base(argumentName)
    {
    }

    /// <summary>
    ///     Load the entity without EF Core change tracking. Faster and cheaper for a read-only handler or endpoint,
    ///     but be aware that mutating a no-tracking entity will NOT be picked up by the transactional middleware's
    ///     <c>SaveChangesAsync</c>.
    /// </summary>
    public bool AsNoTracking { get; set; }

    /// <summary>
    ///     A single navigation path to eagerly load with the entity. Dotted paths chain, so
    ///     <c>Include = "Orders.Items"</c> is EF Core's <c>Include(x =&gt; x.Orders).ThenInclude(x =&gt; x.Items)</c>.
    /// </summary>
    /// <remarks>
    ///     A string rather than a lambda because attribute arguments must be compile-time constants. Every path is
    ///     walked against the EF Core model during code generation, so a typo is a bootstrapping error naming the
    ///     valid alternatives rather than a runtime failure on the first message handled.
    /// </remarks>
    public string? Include { get; set; }

    /// <summary>
    ///     Several navigation paths to eagerly load with the entity, each with the same dotted semantics as
    ///     <see cref="Include" />. Combines with <see cref="Include" /> rather than replacing it.
    /// </summary>
    public string[] Includes { get; set; } = [];

    protected override Type providerType => typeof(EFCorePersistenceFrameProvider);

    protected override string toolName => "EF Core";

    protected override string integrationCall =>
        "services.AddDbContextWithWolverineIntegration<YourDbContext>(/* ... */)";

    protected override string cannotPersistRemedy(Type entityType)
    {
        return $"No registered DbContext maps {entityType.FullNameInCode()}. Map it in a DbContext's OnModelCreating (or expose it as a DbSet), or use the attribute for the store that really holds it.";
    }

    /// <summary>
    ///     Every include path this attribute asked for, in declaration order, with <see cref="Include" /> first.
    /// </summary>
    internal string[] AllIncludes()
    {
        var all = new List<string>();
        if (Include.IsNotEmpty())
        {
            all.Add(Include!);
        }

        all.AddRange(Includes.Where(x => x.IsNotEmpty()));

        return all.ToArray();
    }

    protected override Frame determineLoadFrame(IPersistenceFrameProvider provider, IServiceContainer container,
        ParameterInfo parameter, Variable identity)
    {
        var includes = AllIncludes();
        if (!AsNoTracking && includes.Length == 0)
        {
            // Nothing EF-specific was asked for, so keep the cheaper FindAsync load
            return base.determineLoadFrame(provider, container, parameter, identity);
        }

        // tryFindProvider is sealed on ExplicitEntityAttribute and matched this on providerType, so the cast
        // cannot fail
        var efCore = (EFCorePersistenceFrameProvider)provider;

        return efCore.DetermineLoadFrameWithQueryOptions(container, parameter.ParameterType, identity, includes,
            AsNoTracking, describeUsage(parameter));
    }
}
