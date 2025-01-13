using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
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
internal class LoadEntityFrameBlock : Frame
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
        var previous = _creator;
        foreach (var next in _guardFrames)
        {
            previous.Next = next;
            previous = next;
        }
        
        _creator.GenerateCode(method, writer);

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
public class EntityAttribute : WolverineParameterAttribute
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
        
        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(entity);
            
            var block = new LoadEntityFrameBlock(entity, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }

        chain.Middleware.Add(frame);
        return entity;
    }

    private bool tryFindIdentityVariable(IChain chain, ParameterInfo parameter, Type idType, out Variable variable)
    {
        if (ArgumentName.IsNotEmpty())
        {
            if (chain.TryFindVariable(ArgumentName, ValueSource, idType, out variable))
            {
                return true;
            }
        }
        
        if (chain.TryFindVariable(parameter.ParameterType.Name + "Id", ValueSource, idType, out variable))
        {
            return true;
        }
        
        if (chain.TryFindVariable("Id", ValueSource, idType, out variable))
        {
            return true;
        }

        variable = default;
        return false;
    }
}