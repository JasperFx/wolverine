using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class FromQueryAttributeUsage : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.HasAttribute<FromQueryAttribute>() && !parameter.ParameterType.IsSimple())
        {
            variable = new QueryStringBindingFrame(parameter.ParameterType, chain).Variable;
            return true;
        }

        variable = default;
        return false;
    }
}

internal class QueryStringBindingFrame : SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly List<Variable> _parameters = new();
    private readonly List<SettableProperty> _props = new();

    public QueryStringBindingFrame(Type queryType, HttpChain chain)
    {
        Variable = new Variable(queryType, this);

        var constructors = queryType.GetConstructors();
        if (constructors.Length > 1)
            throw new ArgumentOutOfRangeException(nameof(queryType),
                $"Wolverine can only bind a query string to a type with only one public constructor. {queryType.FullNameInCode()} has multiple constructors");

        _constructor = constructors.Single();
        foreach (var parameter in _constructor.GetParameters())
        {
            var queryStringVariable = chain.TryFindOrCreateQuerystringValue(parameter);
            _parameters.Add(queryStringVariable);
        }

        // foreach (var propertyInfo in queryType.GetProperties().Where(x => x.CanWrite))
        // {
        //     var queryStringVariable =
        //         chain.TryFindOrCreateQuerystringValue(propertyInfo.PropertyType, propertyInfo.Name);
        //     _props.Add(new SettableProperty(propertyInfo, queryStringVariable));
        // }
    }

    public Variable Variable { get; }

    public record SettableProperty(PropertyInfo Property, Variable Variable);

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var arguments = _parameters.Select(x => x.Usage).Join(", ");
        var props = _props.Select(x => $"{x.Property.Name} = {x.Variable.Usage}").Join(", ");
        
        writer.Write($"var {Variable.Usage} = new {Variable.VariableType.FullNameInCode()}({arguments}){{{props}}};");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var parameter in _parameters)
        {
            yield return parameter;
        }

        // foreach (var prop in _props)
        // {
        //     yield return prop.Variable;
        // }
    }
}