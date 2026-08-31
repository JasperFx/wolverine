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

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        // Resolve unset properties from global defaults
        var entityDefaults = container.GetInstance<WolverineOptions>().EntityDefaults;
        _onMissing ??= entityDefaults.OnMissing;
        _maybeSoftDeleted ??= entityDefaults.MaybeSoftDeleted;

        if (!tryFindProvider(rules, container, parameter, out var provider))
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

        var frame = determineLoadFrame(provider, container, parameter, identity);

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
    ///     Chooses the <see cref="IPersistenceFrameProvider" /> that will build this parameter's load frame.
    ///     This is the <b>only</b> provider-specific decision in <see cref="Modify" />, which is exactly why it is a
    ///     hook rather than something a subclass reimplements: the explicit per-provider attributes
    ///     (<c>[FromMarten]</c>, <c>[FromEfCore]</c>, ...) inherit every other behavior — identity discovery,
    ///     <see cref="Required" />, <see cref="OnMissing" />, <see cref="MissingMessage" />,
    ///     <see cref="MaybeSoftDeleted" />, the stop-condition frames and the deferred middleware variable — by
    ///     construction rather than by copying <see cref="Modify" /> and keeping the copies in step.
    /// </summary>
    /// <remarks>
    ///     Takes the whole <see cref="ParameterInfo" /> rather than just the entity type so an overriding attribute
    ///     can name the offending parameter and its declaring method in the exception it throws — see
    ///     <see cref="WolverineParameterAttribute.DescribeMember" />. An override is free to throw with a specific
    ///     diagnostic instead of returning false; returning false yields the generic "no matching persistence
    ///     service" message from <see cref="Modify" />.
    /// </remarks>
    protected virtual bool tryFindProvider(GenerationRules rules, IServiceContainer container,
        ParameterInfo parameter, out IPersistenceFrameProvider provider)
    {
        return rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out provider);
    }

    /// <summary>
    ///     Builds the frame that actually reads the entity. Overridden by provider-specific subclasses that expose
    ///     extra loading options their provider alone understands — <c>[FromEfCore(AsNoTracking = true)]</c> and its
    ///     <c>Include</c> paths, for instance, cannot be expressed through <c>DbContext.FindAsync</c> and need a
    ///     different query shape.
    /// </summary>
    protected virtual Frame determineLoadFrame(IPersistenceFrameProvider provider, IServiceContainer container,
        ParameterInfo parameter, Variable identity)
    {
        return provider.DetermineLoadFrame(container, parameter.ParameterType, identity);
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