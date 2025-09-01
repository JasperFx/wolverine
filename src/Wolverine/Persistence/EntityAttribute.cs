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
    private readonly Frame _creator;

    public LoadEntityFrameBlock(Variable entity, params Frame[] guardFrames) : base(entity.Creator.IsAsync || guardFrames.Any(x => x.IsAsync))
    {
        _guardFrames = guardFrames;
        Mirror = new Variable(entity.VariableType, entity.Usage, this);
        _creator = entity.Creator;
    }

    public Variable Mirror { get; }

    public override IEnumerable<Variable> Creates => [Mirror];

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // The [WriteAggregate] somehow causes this
        if (_creator.Next == this || _creator.Next != null)
        {
            for (int i = 1; i < _guardFrames.Length; i++)
            {
                _guardFrames[i - 1].Next = _guardFrames[i];
            }
            
            _guardFrames[0].GenerateCode(method, writer);
        }
        else
        {
            var previous = _creator;
            foreach (var next in _guardFrames)
            {
                previous.Next = next;
                previous = next;
            }
        
            _creator.GenerateCode(method, writer);
        }

        Next?.GenerateCode(method, writer);
    }
    
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        return _creator
            .FindVariables(chain)
            .Concat(_guardFrames.SelectMany(x => x.FindVariables(chain))).Distinct();
    }

    public override bool CanReturnTask()
    {
        if (_guardFrames.Any()) return _guardFrames.Last().CanReturnTask();

        return _creator.CanReturnTask();
    }
}

/// <summary>
/// Apply this on a message handler method, an HTTP endpoint method, or any "before" middleware method parameter
/// to direct Wolverine to use a known persistence strategy to resolve the entity from the request or message
/// </summary>
public class EntityAttribute : WolverineParameterAttribute, IDataRequirement
{
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

    public string MissingMessage { get; set; }
    public OnMissing OnMissing { get; set; } = OnMissing.Simple404;
    
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
    public bool MaybeSoftDeleted { get; set; } = true;

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
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
        
        if (MaybeSoftDeleted is false)
        {
            var softDeleteFrames = provider.DetermineFrameToNullOutMaybeSoftDeleted(entity);
            chain.Middleware.AddRange(softDeleteFrames);
        }
        
        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(entity, identity, this);
            
            var block = new LoadEntityFrameBlock(entity, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }

        chain.Middleware.Add(frame);
        return entity;
    }
}