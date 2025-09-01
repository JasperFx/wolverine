using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;


internal class FromFormAttributeUsage : IParameterStrategy
{
    private List<Type> _simpleTypes = new()
    {
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(bool),
        typeof(DateTime),
        typeof(DateTimeOffset),
        typeof(TimeSpan),
        typeof(DateOnly),
        typeof(TimeOnly)
    }; 

    

    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
         variable = default;
        if (!parameter.HasAttribute<FromFormAttribute>())
        {
            return false;
        }
        variable = chain.TryFindOrCreateFormValue(parameter);
        if(variable != null){
            return true;
        }
       
       if(IsClassOrNullableClassNotCollection(parameter.ParameterType)){
            chain.RequestType = parameter.ParameterType;
            chain.IsFormData = true;
            variable = new FormBindingFrame(parameter.ParameterType, chain).Variable;
            return true;
       }

        return false;
    }

    private bool IsClassOrNullableClassNotCollection(Type type){
        return (
                type.IsClass || 
                type.IsNullable() && type.GetInnerTypeFromNullable().IsClass
                ) && !type.IsEnumerable();
    }
}

internal class FormBindingFrame : SyncFrame
{
    private readonly ConstructorInfo _constructor;
    private readonly List<Variable> _parameters = new();
    private readonly List<IReadHttpFrame> _props = new();
    public Variable Variable { get; }
    public FormBindingFrame(Type queryType, HttpChain chain){
        Variable = new Variable(queryType, this);

        var constructors = queryType.GetConstructors();
        if (constructors.Length > 1)
            throw new ArgumentOutOfRangeException(nameof(queryType),
                $"Wolverine can only bind form values to a type with only one public constructor. {queryType.FullNameInCode()} has multiple constructors");

        _constructor = constructors.Single();
        foreach (var parameter in _constructor.GetParameters())
        {
            var formValueVariable = chain.TryFindOrCreateFormValue(parameter);
            _parameters.Add(formValueVariable);
        }

        if (!_constructor.GetParameters().Any())
        {
            foreach (var propertyInfo in queryType.GetProperties().Where(x => x.CanWrite))
            {
                var formName = propertyInfo.Name;
                if (propertyInfo.TryGetAttribute<FromFormAttribute>(out var att))
                {
                    formName = att.Name;
                }
                var formValueVariable =
                    chain.TryFindOrCreateFormValue(propertyInfo.PropertyType, propertyInfo.Name, formName);

                if (formValueVariable.Creator is IReadHttpFrame frame)
                {
                    frame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _props.Add(frame);
                }
            }
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Binding Form values to the argument marked with [FromForm]");
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