using System.Diagnostics;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Codegen;

public class ServiceCollectionServerVariableSource : IServiceVariableSource
{
    private readonly ServiceContainer _services;
    private bool _usesScopedContainerDirectly;
    private readonly List<StandInVariable> _standins = new();
    private readonly List<InjectedSingleton> _fields = new();
    private Variable _scoped = new ScopedContainerCreation().Scoped;
    

    public const string UsingScopedContainerDirectly = @"Using the scoped provider service location approach
because at least one dependency is directly using IServiceProvider or has an opaque, scoped or transient Lambda registration";

    public ServiceCollectionServerVariableSource(IServiceContainer services)
    {
        _services = (ServiceContainer?)services;
    }

    public bool Matches(Type type)
    {
        return _services.CouldResolve(type);
    }
    
    public bool TryFindKeyedService(Type type, string key, out Variable? variable)
    {
        variable = default;
        
        var descriptor = _services.RegistrationsFor(type).Where(x => x.IsKeyedService)
            .FirstOrDefault(x => Equals(x.ServiceKey, key));

        if (descriptor == null)
        {
            return false;
        }

        var plan = _services.PlanFor(descriptor, []);

        variable = createVariableForPlan(type, plan);
        return variable != null;
    }

    public Variable Create(Type type)
    {
        if (type == typeof(IServiceProvider))
        {
            _usesScopedContainerDirectly = true;
            return new ScopedContainerCreation().Scoped;
        }

        var plan = _services.FindDefault(type, new());
        return createVariableForPlan(type, plan);
    }

    private Variable createVariableForPlan(Type type, ServicePlan? plan)
    {
        if (plan is InvalidPlan)
        {
            throw new NotSupportedException($"Cannot build service type {type.FullNameInCode()} in any way");
        }

        if (plan is null)
        {
            throw new NotSupportedException($"Unable to create a service variable for type {type.FullNameInCode()}");
        }
        
        if (plan.Lifetime == ServiceLifetime.Singleton)
        {
            var field = _fields.FirstOrDefault(x => x.Descriptor == plan.Descriptor);
            if (field == null)
            {
                field = new InjectedSingleton(plan.Descriptor);
                _fields.Add(field);
            }

            return field;
        }

        var standin = new StandInVariable(plan);
        _standins.Add(standin);

        return standin;
    }

    public void ReplaceVariables(IMethodVariables method)
    {
        if (_usesScopedContainerDirectly || _standins.Any(x => x.Plan.RequiresServiceProvider(method)))
        {
            useServiceProvider(method);
        }
        else
        {
            useInlineConstruction(method);
        }
    }

    public void StartNewType()
    {
        StartNewMethod();
        _fields.Clear();
    }

    public void StartNewMethod()
    {
        _scoped = new ScopedContainerCreation().Scoped;
        _standins.Clear();
    }

    private void useServiceProvider(IMethodVariables method)
    {
        var written = false;
        foreach (var standin in _standins)
        {
            var frame = new GetServiceFromScopedContainerFrame(_scoped, standin.VariableType);
            var variable = frame.Variable;

            // Write description of why this had to use the nested container
            if (standin.Plan.RequiresServiceProvider(method))
            {
                var comment = standin.Plan.WhyRequireServiceProvider(method);

                if (_usesScopedContainerDirectly && !written)
                {
                    comment += Environment.NewLine;
                    comment += UsingScopedContainerDirectly;

                    written = true;
                }

                frame.MultiLineComment(comment);
            }
            else if (_usesScopedContainerDirectly && !written)
            {
                frame.MultiLineComment(UsingScopedContainerDirectly);
                written = true;
            }

            standin.UseInner(variable);
        }

        var duplicates = _standins.GroupBy(x => x.Usage).Where(x => x.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            var usage = 0;
            foreach (var standinVariable in duplicate) standinVariable.OverrideName(standinVariable.Usage + ++usage);
        }
    }
    
    private void useInlineConstruction(IMethodVariables method)
    {
        // THIS NEEDS TO BE SCOPED PER METHOD!!!
        var variables = new ServiceVariables(method, _fields);
        foreach (var standin in _standins)
        {
            var variable = variables.Resolve(standin.Plan);
            standin.UseInner(variable);
        }

        foreach (var singleton in variables.OfType<InjectedSingleton>())
        {
            singleton.IsOnlyOne = !_services.HasMultiplesOf(singleton.VariableType);
        }

        variables.MakeNamesUnique();
    }
}