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
            variable = new AsParametersBindingFrame(parameter.ParameterType, chain, container).Variable;
            return true;
        }

        return false;
    }

    // TODO -- move this to an extension method in JasperFx. Could be useful in other places
    private bool IsClassOrNullableClassNotCollection(Type type)
    {
        return (
            type.IsClass ||
            (type.IsNullable() && type.GetInnerTypeFromNullable().IsClass)
        ) && !type.IsEnumerable();
    }
}

// TODO -- this should be in JasperFx longer term
internal class AssignPropertyFrame : SyncFrame, IGeneratesCode
{
    private readonly Variable _target;
    private readonly PropertyInfo _property;
    private readonly Variable _value;

    public AssignPropertyFrame(Variable target, PropertyInfo property, Variable value)
    {
        _target = target;
        _property = property;
        _value = value;
        
        uses.Add(target);
        uses.Add(value);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_target.Usage}.{_property.Name} = {_value.Usage};");
        Next?.GenerateCode(method, writer);
    }
}

internal class AsParametersBindingFrame : SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly List<IGeneratesCode> _props = [];
    private readonly List<Variable> _parameters = [];
    private readonly List<Variable> _dependencies = [];
    
    private bool _hasForms = false;
    private bool _hasJsonBody = false;

    public AsParametersBindingFrame(Type queryType, HttpChain chain, IServiceContainer container)
    {
        Variable = new Variable(queryType, this);

        var constructors = queryType.GetConstructors();
        if (constructors.Length != 1 && constructors.Single().GetParameters().Any())
        {
            throw new ArgumentOutOfRangeException(nameof(queryType),
                $"Wolverine can only bind AsParamaters values to a type with a public parameterless constructor. {queryType.FullNameInCode()} has a constructor");
        }

        foreach (var propertyInfo in queryType.GetProperties().Where(x => x is { CanWrite: true, IsSpecialName: false }))
        {
            if (tryCreateFrame(propertyInfo, chain, container, out var variable))
            {
                if (variable?.Creator is IReadHttpFrame frame)
                {
                    frame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _props.Add(frame);
                }
                else
                {
                    _props.Add(new AssignPropertyFrame(Variable, propertyInfo, variable));
                    _dependencies.Add(variable);
                }
            }
        }

        if (_hasJsonBody && _hasForms)
        {
            throw new InvalidOperationException(
                $"{queryType.FullNameInCode()} cannot be decorated with [AsParameters] because it uses both [FromForm] and [FromBody] binding. You can only use one or the other option");
        }
    }

    private bool tryCreateFrame(PropertyInfo propertyInfo, HttpChain chain, IServiceContainer container, out Variable? variable)
    {
        variable = default;

        
        if (propertyInfo.TryGetAttribute<FromFormAttribute>(out var fatt))
        {
            _hasForms = true;
            var formName = fatt.Name ?? propertyInfo.Name;
            variable = chain.TryFindOrCreateFormValue(propertyInfo.PropertyType, propertyInfo.Name, formName);
            return true;
        }

        if (propertyInfo.TryGetAttribute<FromQueryAttribute>(out var qatt))
        {
            var queryStringName = qatt.Name ?? propertyInfo.Name;
            variable =
                chain.TryFindOrCreateQuerystringValue(propertyInfo.PropertyType, queryStringName);

            return true;
        }

        if (propertyInfo.TryGetAttribute<FromRouteAttribute>(out var ratt))
        {
            var routeArgumentName = ratt.Name ?? propertyInfo.Name;
            if (chain.FindRouteVariable(propertyInfo.PropertyType, routeArgumentName, out variable))
            {
                return true;
            }

            throw new InvalidOperationException(
                $"Unable to find a route argument '{routeArgumentName}' specified on property {Variable.VariableType.FullNameInCode()}.{propertyInfo.Name}");
        }
        else if (propertyInfo.TryGetAttribute<FromHeaderAttribute>(out var hatt))
        {
            variable = chain.GetOrCreateHeaderVariable(hatt, propertyInfo);
            return true;
        }
        else if (propertyInfo.TryGetAttribute<FromBodyAttribute>(out var batt))
        {
            _hasJsonBody = true;
            chain.RequestType = propertyInfo.PropertyType;
            variable = chain.BuildJsonDeserializationVariable();
            
            chain.IsFormData = false;
            return true;
        }
        else if (propertyInfo.TryGetAttribute<FromServicesAttribute>(out var satt))
        {
            variable = container.GetInstance<IServiceVariableSource>().Create(propertyInfo.PropertyType);
            return true;
        }

        return false;
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Binding Form & Querystring values to the argument marked with [AsParameters]");
        var arguments = _parameters.Select(x => x.Usage).Join(", ");

        writer.Write($"var {Variable.Usage} = new {Variable.VariableType.FullNameInCode()}({arguments});");

        foreach (var frame in _props) frame.GenerateCode(method, writer);

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var parameter in _parameters) yield return parameter;
        foreach (var parameter in _dependencies) yield return parameter;
    }
}