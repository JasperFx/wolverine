using System.Diagnostics;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// Use this when you absolutely have to keep a number of Frames together
/// and not allowing the topological sort to break them up
/// </summary>
public class LoadEntityFrameBlock : Frame
{
    private readonly Frame[] _guardFrames;

    public LoadEntityFrameBlock(Variable entity, params Frame[] guardFrames) : base(entity.Creator!.IsAsync || guardFrames.Any(x => x.IsAsync))
    {
        _guardFrames = guardFrames;
        Mirror = new Variable(entity.VariableType, entity.Usage, this);
        Creator = entity.Creator;
    }

    public void AlsoMirrorAsTheCreator(Variable variable)
    {
        // Seems goofy, but adds it to the creates
        new Variable(variable.VariableType, variable.Usage, this);
    }

    public Frame Creator { get; }

    public Variable Mirror { get; }

    public override IEnumerable<Variable> Creates => [Mirror];

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Creator.Next == this || Creator.Next != null)
        {
            // Creator has been handled elsewhere (e.g. by batching) —
            // only render the guard frames
            if (_guardFrames.Length > 0)
            {
                for (int i = 1; i < _guardFrames.Length; i++)
                {
                    _guardFrames[i - 1].Next = _guardFrames[i];
                }

                _guardFrames[0].GenerateCode(method, writer);
            }
        }
        else
        {
            var previous = Creator;
            foreach (var next in _guardFrames)
            {
                previous.Next = next;
                previous = next;
            }

            Creator.GenerateCode(method, writer);
        }

        Next?.GenerateCode(method, writer);
    }
    
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        return Creator
            .FindVariables(chain)
            .Concat(_guardFrames.SelectMany(x => x.FindVariables(chain))).Distinct();
    }

    public override bool CanReturnTask()
    {
        if (_guardFrames.Any()) return _guardFrames.Last().CanReturnTask();

        return Creator.CanReturnTask();
    }
}

/// <summary>
/// Apply this on a message handler method, an HTTP endpoint method, or any "before" middleware method parameter
/// to direct Wolverine to use a known persistence strategy to resolve the entity from the request or message
/// </summary>
public class EntityAttribute : WolverineParameterAttribute, IDataRequirement
{
    private OnMissing? _onMissing;
    private bool? _maybeSoftDeleted;

    public EntityAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public EntityAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    /// Is the existence of this entity required for the rest of the handler action or HTTP endpoint
    /// execution to continue? Default is true.
    /// </summary>
    public bool Required { get; set; } = true;

    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    /// <summary>
    /// Should Wolverine consider soft-deleted entities to be missing if deleted. I.e., if an entity
    /// can be found, but is marked as deleted, is this considered a "good" entity and the message handling
    /// or HTTP execution should continue?
    ///
    ///     If the document is soft-deleted, whether the endpoint should receive the document (<c>true</c>) or NULL (
    ///     <c>false</c>).
    ///     Set it to <c>false</c> and combine it with <see cref="Required" /> so a 404 will be returned for soft-deleted
    ///     documents.
    /// </summary>
    public bool MaybeSoftDeleted
    {
        get => _maybeSoftDeleted ?? true;
        set => _maybeSoftDeleted = value;
    }

    /// <summary>
    /// Load this entity by calling a <c>Load</c> / <c>LoadAsync</c> method on this type instead of
    /// going to the application's configured persistence. Use it for anything Wolverine has no
    /// persistence provider for — an object store like S3 or Azure Blob Storage, a cache, an HTTP
    /// API, a legacy repository — while keeping the <see cref="Required" />, <see cref="OnMissing" />
    /// and <see cref="MissingMessage" /> handling this attribute already gives you.
    /// <para>
    /// The loader's method parameters are resolved out of the surrounding chain exactly like a
    /// handler method's, so it can take its own services, the <c>TenantId</c>, route arguments,
    /// message members and the <see cref="CancellationToken" />. That is what lets a loader address
    /// something by more than one value — an object key built from a tenant and an id, say — where
    /// the identity convention only ever finds one.
    /// </para>
    /// <para>
    /// A loader-backed entity is read-only as far as Wolverine is concerned: it never takes part in
    /// a unit of work, and <see cref="MaybeSoftDeleted" /> does not apply because only the loader
    /// knows what deleted means for its source. <see cref="MissingMessage" /> is used verbatim, so a
    /// <c>{Id}</c> placeholder is only substituted when the chain happens to expose an identity.
    /// </para>
    /// </summary>
    public Type? Loader { get; set; }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        // Resolve unset properties from global defaults
        var entityDefaults = container.GetInstance<WolverineOptions>().EntityDefaults;
        _onMissing ??= entityDefaults.OnMissing;
        _maybeSoftDeleted ??= entityDefaults.MaybeSoftDeleted;

        var loaderType = Loader ?? (entityDefaults.TryFindLoader(parameter.ParameterType, out var registered)
            ? registered
            : null);

        if (loaderType != null)
        {
            return modifyWithLoader(loaderType, chain, parameter, container);
        }

        if (!rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out var provider))
        {
            throw new InvalidOperationException("Could not determine a matching persistence service for entity " +
                                                parameter.ParameterType.FullNameInCode());

        }

        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = provider.DetermineSagaIdType(parameter.ParameterType, container);

        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        if (identity.Creator != null)
        {
            chain.Middleware.Add(identity.Creator);
        }

        var frame = provider.DetermineLoadFrame(container, parameter.ParameterType, identity);

        var entity = frame.Creates.First(x => x.VariableType == parameter.ParameterType);
        entity.OverrideName(parameter.Name!);

        if (MaybeSoftDeleted is false)
        {
            var softDeleteFrames = provider.DetermineFrameToNullOutMaybeSoftDeleted(entity);
            chain.Middleware.AddRange(softDeleteFrames);
        }

        Variable returnVariable;
        if (chain.IsDataRequired(this))
        {
            var otherFrames = chain.AddStopConditionIfNull(entity, identity, this);

            var block = new LoadEntityFrameBlock(entity, otherFrames);
            chain.Middleware.Add(block);

            returnVariable = block.Mirror;
        }
        else
        {
            chain.Middleware.Add(frame);
            returnVariable = entity;
        }

        // Store deferred assignment for middleware methods added later (Before/After)
        StoreDeferredMiddlewareVariable(chain, parameter.Name!, returnVariable);

        return returnVariable;
    }

    /// <summary>
    /// The loader path. Deliberately the same shape as the persistence-provider path above it: build
    /// the frame that creates the entity, then hang the same missing-data guard off it, so
    /// Required/OnMissing/MissingMessage behave identically no matter where the entity came from.
    /// </summary>
    private Variable modifyWithLoader(Type loaderType, IChain chain, ParameterInfo parameter,
        IServiceContainer container)
    {
        var plan = EntityLoaderPlan.For(loaderType, parameter.ParameterType);
        var (call, preamble) = plan.BuildFrames(chain, container);

        foreach (var frame in preamble)
        {
            chain.Middleware.Add(frame);
        }

        var entity = call.Creates.First(x => x.VariableType == parameter.ParameterType);
        entity.OverrideName(parameter.Name!);

        Variable returnVariable;
        if (Required)
        {
            // No identity variable: a loader addresses its source however it likes, and its
            // parameters are not necessarily one id. The guard frames handle that.
            var guardFrames = chain.AddStopConditionIfNull(entity, null, this);

            var block = new LoadEntityFrameBlock(entity, guardFrames);
            chain.Middleware.Add(block);

            returnVariable = block.Mirror;
        }
        else
        {
            chain.Middleware.Add(call);
            returnVariable = entity;
        }

        StoreDeferredMiddlewareVariable(chain, parameter.Name!, returnVariable);

        return returnVariable;
    }

    internal static void StoreDeferredMiddlewareVariable(IChain chain, string parameterName, Variable variable)
    {
        const string key = "DeferredMiddlewareVariables";
        if (!chain.Tags.TryGetValue(key, out var raw))
        {
            raw = new List<(string Name, Variable Variable)>();
            chain.Tags[key] = raw;
        }
        ((List<(string Name, Variable Variable)>)raw).Add((parameterName, variable));
    }
}