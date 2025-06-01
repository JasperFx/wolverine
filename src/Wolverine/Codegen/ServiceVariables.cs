using System.Collections;
using System.Reflection;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

internal class ServiceVariables : IEnumerable<Variable>, IMethodVariables
{
    private readonly List<Variable> _all = new();
    private readonly Dictionary<ServiceDescriptor, Variable> _tracking = new();
    private readonly Lazy<Variable> _serviceProvider;

    public ServiceVariables()
    {
        Method = this;
        _serviceProvider = new(() => new ScopedContainerCreation().Scoped);
    }

    public ServiceVariables(IMethodVariables method, IList<InjectedSingleton> fields)
    {
        Method = method;
        _all.AddRange(fields);

        foreach (var field in fields) _tracking[field.Descriptor] = field;

        _serviceProvider = new(() =>
        {
            var inner = method.TryFindVariable(typeof(IServiceProvider), VariableSource.NotServices);
            return inner ?? new ScopedContainerCreation().Scoped;
        });
    }

    public int VariableSequence { get; set; }

    public IMethodVariables Method { get; }
    public Variable ServiceProvider => _serviceProvider.Value;

    public IEnumerator<Variable> GetEnumerator()
    {
        return _all.GetEnumerator();
    }

    Variable IMethodVariables.FindVariable(Type type)
    {
        if (type == typeof(IServiceProvider)) return _serviceProvider.Value;
        return null;
    }

    public Variable FindVariable(ParameterInfo parameter)
    {
        return null;
    }

    Variable IMethodVariables.FindVariableByName(Type dependency, string name)
    {
        return null;
    }

    bool IMethodVariables.TryFindVariableByName(Type dependency, string name, out Variable variable)
    {
        variable = default;
        return false;
    }

    Variable IMethodVariables.TryFindVariable(Type type, VariableSource source)
    {
        if (type == typeof(IServiceProvider)) return _serviceProvider.Value;
        return null;
    }

    public Variable Resolve(ServicePlan plan)
    {
        if (_tracking.TryGetValue(plan.Descriptor, out var variable))
        {
            return variable;
        }

        var fromOutside = Method.TryFindVariable(plan.ServiceType, VariableSource.NotServices);
        if (fromOutside != null && !(fromOutside is StandInVariable))
        {
            _all.Add(fromOutside);
            _tracking[plan.Descriptor] = fromOutside;

            return fromOutside;
        }

        variable = plan.CreateVariable(this);
        _all.Add(variable);

        // Don't track it for possible reuse if it's transient
        if (plan.Lifetime == ServiceLifetime.Scoped)
        {
            _tracking[plan.Descriptor] = variable;
        }

        return variable;
    }

    public void MakeNamesUnique()
    {
        var duplicateGroups = _all.GroupBy(x => x.Usage).Where(x => x.Count() > 1).ToArray();
        foreach (var group in duplicateGroups)
        {
            var i = 0;
            foreach (var variable in group) variable.OverrideName(variable.Usage + ++i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}