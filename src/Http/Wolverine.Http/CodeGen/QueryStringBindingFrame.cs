using System.Reflection;
using JasperFx;
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
        variable = default;
        if (!parameter.HasAttribute<FromQueryAttribute>())
        {
            return false;
        }

        if (parameter.ParameterType.IsSimple()) return false;
        if (parameter.ParameterType.IsNullable() && parameter.ParameterType.GetInnerTypeFromNullable().IsSimple()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<DateTime>()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<DateTimeOffset>()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<DateOnly>()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<TimeOnly>()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<TimeSpan>()) return false;
        if (parameter.ParameterType.IsTypeOrNullableOf<Guid>()) return false;

        chain.ComplexQueryStringType = parameter.ParameterType;

        variable = new QueryStringBindingFrame(parameter.ParameterType, chain).Variable;
        return true;
    }
}

internal class QueryStringBindingFrame : SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly List<Variable> _parameters = [];
    private readonly List<IGeneratesCode> _props = [];

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

        // Here's the limitation, either it's all ctor args, or all settable props
        if (!_constructor.GetParameters().Any())
        {
            foreach (var propertyInfo in queryType.GetProperties().Where(x => x.CanWrite))
            {
                var queryStringName = propertyInfo.Name;
                if (propertyInfo.TryGetAttribute<FromQueryAttribute>(out var att))
                {
                    queryStringName = att.Name;
                }
                
                var queryStringVariable =
                    chain.TryFindOrCreateQuerystringValue(propertyInfo.PropertyType, queryStringName);

                if (queryStringVariable.Creator is IReadHttpFrame frame)
                {
                    frame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _props.Add(frame);
                }
            }
        }
    }

    public Variable Variable { get; }

    public record SettableProperty(PropertyInfo Property, Variable Variable);

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Binding QueryString values to the argument marked with [FromQuery]");
        var arguments = _parameters.Select(x => x.Usage).Join(", ");

        writer.Write($"var {Variable.Usage} = new {Variable.VariableType.FullNameInCode()}({arguments});");

        foreach (var frame in _props)
        {
            frame.GenerateCode(method, writer);
        }
        
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var parameter in _parameters)
        {
            yield return parameter;
        }
    }
}