using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
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

    

    // parameter.ParameterType flows into FormBindingFrame's
    // [DAM(PublicConstructors|PublicProperties)]-annotated ctor parameter;
    // ParameterInfo.ParameterType doesn't carry the DAM annotation. Suppress
    // at the call site — the user's [FromForm] type is statically rooted via
    // endpoint discovery (chunk Q HandlerDiscovery RUC propagation upstream).
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "User [FromForm] type statically rooted via endpoint discovery; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
         variable = default;
        if (!parameter.HasAttribute<FromFormAttribute>())
        {
            return false;
        }
        variable = chain.TryFindOrCreateFormValue(parameter);
        if(variable != null)
        {
            chain.RequestType = typeof(void);
            chain.IsFormData = true; // THIS IS IMPORTANT!
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

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "type originates from [FromForm] attribute usage on a Wolverine.Http endpoint parameter (already RUC-suppressed in TryMatch above). The IsEnumerable call inspects the type's generic-interface graph; the user's parameter type is statically rooted via endpoint discovery.")]
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
    public FormBindingFrame([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type queryType, HttpChain chain){
        Variable = new Variable(queryType, this);

        var constructors = queryType.GetConstructors();
        if (constructors.Length > 1)
            throw new ArgumentOutOfRangeException(nameof(queryType),
                $"Wolverine can only bind form values to a type with only one public constructor. {queryType.FullNameInCode()} has multiple constructors");

        _constructor = constructors.Single();
        foreach (var parameter in _constructor.GetParameters())
        {
            if (parameter.ParameterType == typeof(IFormFile))
            {
                var fileFrame = new FormFilePropertyFrame(parameter.Name!);
                _parameters.Add(fileFrame.Variable);
                continue;
            }

            if (parameter.ParameterType == typeof(IFormFileCollection))
            {
                var filesFrame = new FormFileCollectionPropertyFrame();
                _parameters.Add(filesFrame.Variable);
                continue;
            }

            var formValueVariable = chain.TryFindOrCreateFormValue(parameter);
            _parameters.Add(formValueVariable!);
        }

        if (!_constructor.GetParameters().Any())
        {
            foreach (var propertyInfo in queryType.GetProperties().Where(x => x.CanWrite))
            {
                var formName = propertyInfo.Name;
                if (propertyInfo.TryGetAttribute<FromFormAttribute>(out var att) && att.Name.IsNotEmpty())
                {
                    formName = att.Name;
                }

                if (propertyInfo.PropertyType == typeof(IFormFile))
                {
                    var fileFrame = new FormFilePropertyFrame(formName);
                    fileFrame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _props.Add(fileFrame);
                    continue;
                }

                if (propertyInfo.PropertyType == typeof(IFormFileCollection))
                {
                    var filesFrame = new FormFileCollectionPropertyFrame();
                    filesFrame.AssignToProperty($"{Variable.Usage}.{propertyInfo.Name}");
                    _props.Add(filesFrame);
                    continue;
                }

                var formValueVariable =
                    chain.TryFindOrCreateFormValue(propertyInfo.PropertyType, propertyInfo.Name, formName);

                if (formValueVariable?.Creator is IReadHttpFrame frame)
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