using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;

internal class AsParamatersAttributeUsage : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        variable = default;
        if (!parameter.HasAttribute<AsParametersAttribute>())
        {
            return false;
        }

        if (IsClassOrNullableClassNotCollection(parameter.ParameterType))
        {
            chain.RequestType = parameter.ParameterType;
            chain.IsFormData = true;
            variable = new AsParametersBindingFrame(parameter.ParameterType, chain).Variable;
            return true;
        }

        return false;
    }

    private bool IsClassOrNullableClassNotCollection(Type type)
    {
        return (
            type.IsClass ||
            (type.IsNullable() && type.GetInnerTypeFromNullable().IsClass)
        ) && !type.IsEnumerable();
    }
}

internal class AsParametersBindingFrame : SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly List<IReadFormFrame> _formprops = new();
    private readonly List<Variable> _parameters = new();
    private readonly List<IReadQueryStringFrame> _queryprops = new();

    public AsParametersBindingFrame(Type queryType, HttpChain chain)
    {
        Variable = new Variable(queryType, this);

        var constructors = queryType.GetConstructors();
        if (constructors.Length != 1 && constructors.Single().GetParameters().Any())
        {
            throw new ArgumentOutOfRangeException(nameof(queryType),
                $"Wolverine can only bind AsParamaters values to a type with a public parameterless constructor. {queryType.FullNameInCode()} has a constructor");
        }

        foreach (var propertyInfo in queryType.GetProperties().Where(x => x.CanWrite))
        {
            if (propertyInfo.TryGetAttribute<FromFormAttribute>(out var fatt))
            {
                var formName = fatt.Name ?? propertyInfo.Name;
                var formValueVariable =
                    chain.TryFindOrCreateFormValue(propertyInfo.PropertyType, propertyInfo.Name, formName);
                if (formValueVariable.Creator is IReadFormFrame frame)
                {
                    frame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _formprops.Add(frame);
                }
            }
            else if (propertyInfo.TryGetAttribute<FromQueryAttribute>(out var qatt))
            {
                var queryStringName = qatt.Name ?? propertyInfo.Name;
                var queryStringVariable =
                    chain.TryFindOrCreateQuerystringValue(propertyInfo.PropertyType, queryStringName);

                if (queryStringVariable.Creator is IReadQueryStringFrame frame)
                {
                    frame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _queryprops.Add(frame);
                }
            }
            else if (propertyInfo.TryGetAttribute<FromRouteAttribute>(out var ratt))
            {
                throw new NotImplementedException("FromRoute is not supported yet");
            }
            else if (propertyInfo.TryGetAttribute<FromHeaderAttribute>(out var hatt))
            {
                throw new NotImplementedException("FromHeader is not supported yet");
            }
            else if (propertyInfo.TryGetAttribute<FromBodyAttribute>(out var batt))
            {
                throw new NotImplementedException("FromBody is not supported yet");
            }
            else if (propertyInfo.TryGetAttribute<FromServicesAttribute>(out var satt))
            {
                throw new NotImplementedException("FromServices is not supported yet");
            }
        }
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Binding Form & Querystring values to the argument marked with [AsParameters]");
        var arguments = _parameters.Select(x => x.Usage).Join(", ");

        writer.Write($"var {Variable.Usage} = new {Variable.VariableType.FullNameInCode()}({arguments});");

        foreach (var frame in _formprops) frame.GenerateCode(method, writer);
        foreach (var frame in _queryprops) frame.GenerateCode(method, writer);

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var parameter in _parameters) yield return parameter;
    }
}