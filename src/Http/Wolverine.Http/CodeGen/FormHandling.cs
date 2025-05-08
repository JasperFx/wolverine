using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

public enum FormAssignMode
{
    WriteToVariable,
    WriteToProperty
}


public interface IReadFormFrame
{
    void AssignToProperty(string usage);
    FormAssignMode Mode { get; }

    void GenerateCode(GeneratedMethod method, ISourceWriter writer);
}


public class FormVariable : Variable
{
    public FormVariable(Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
        Name = usage;
    }

    public string Name { get; set; }

}


internal class ReadStringFormValue : SyncFrame, IReadFormFrame
{
    public ReadStringFormValue(string name)
    {
        Variable = new FormVariable(typeof(string), name, this);
    }

    public FormVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == FormAssignMode.WriteToVariable)
        {
            writer.Write($"string {Variable.Usage} = httpContext.Request.Form[\"{Variable.Name}\"].FirstOrDefault();");
        }
        else
        {
            writer.Write($"{Variable.Usage} = httpContext.Request.Form[\"{Variable.Name}\"].FirstOrDefault();");
        }

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        Variable.OverrideName(usage);
        Mode = FormAssignMode.WriteToProperty;
    }

    public FormAssignMode Mode { get; private set; } = FormAssignMode.WriteToVariable;
}


internal class ParsedFormValue : SyncFrame, IReadFormFrame
{
    private string _property;

    public ParsedFormValue(Type parameterType, string parameterName)
    {
        Variable = new FormVariable(parameterType, parameterName!, this);
    }

    public FormVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.FullNameInCode();
        var prefix = Mode == FormAssignMode.WriteToVariable ? "" : "if (";
        var suffix = Mode == FormAssignMode.WriteToVariable ? "" : $") {_property} = {Variable.Usage}";
        var outUsage = Mode == FormAssignMode.WriteToVariable ? Variable.Usage : $"var {Variable.Usage}";
        
        if (Mode == FormAssignMode.WriteToVariable)
        {
            writer.Write($"{alias} {Variable.Usage} = default;");
        }

        if (Variable.VariableType.IsEnum)
        {
            writer.Write($"{prefix}{alias}.TryParse<{alias}>(httpContext.Request.Form[\"{Variable.Name}\"],true, out {outUsage}){suffix};");
        }
        else if (Variable.VariableType.IsBoolean())
        {
            writer.Write($"{prefix}{alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], out {outUsage}){suffix};");
        }
        else
        {
            writer.Write($"{prefix}{alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], System.Globalization.CultureInfo.InvariantCulture, out {outUsage}){suffix};");
        }

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        Mode = FormAssignMode.WriteToProperty;
        _property = usage;
    }

    public FormAssignMode Mode { get; private set; } = FormAssignMode.WriteToVariable;
}


internal class ParsedNullableFormValue : SyncFrame, IReadFormFrame
{
    private readonly string _alias;
    private Type _innerTypeFromNullable;
    private string _property;

    public ParsedNullableFormValue(Type parameterType, string parameterName)
    {
        Variable = new FormVariable(parameterType, parameterName, this);
        _innerTypeFromNullable = parameterType.GetInnerTypeFromNullable();
        _alias = _innerTypeFromNullable.FullNameInCode();
    }

    public FormVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == FormAssignMode.WriteToVariable)
        {
            writer.Write($"{_alias}? {Variable.Usage} = null;");
            
            if (_innerTypeFromNullable.IsEnum)
            {
                writer.Write(
                    $"if ({_alias}.TryParse<{_innerTypeFromNullable.FullNameInCode()}>(httpContext.Request.Form[\"{Variable.Name}\"], true, out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
            else if (_innerTypeFromNullable.IsBoolean())
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
            else
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}Parsed)) {Variable.Usage} = {Variable.Usage}Parsed;");
            }
        }
        else
        {
            if (_innerTypeFromNullable.IsEnum)
            {
                writer.Write(
                    $"if ({_alias}.TryParse<{_innerTypeFromNullable.FullNameInCode()}>(httpContext.Request.Form[\"{Variable.Name}\"], true, out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
            else if (_innerTypeFromNullable.IsBoolean())
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
            else
            {
                writer.Write(
                    $"if ({_alias}.TryParse(httpContext.Request.Form[\"{Variable.Name}\"], System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage})) {_property} = {Variable.Usage};");
            }
        }
        

        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = FormAssignMode.WriteToProperty;
    }

    public FormAssignMode Mode { get; private set; } = FormAssignMode.WriteToVariable;
}

internal class ParsedCollectionFormValue : SyncFrame, IReadFormFrame
{
    private readonly Type _collectionElementType;

    public ParsedCollectionFormValue(Type parameterType, string parameterName)
    {
        Variable = new FormVariable(parameterType, parameterName!, this);
        _collectionElementType = GetCollectionElementType(parameterType);
    }

    public FormVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var collectionAlias = typeof(List<>).MakeGenericType(_collectionElementType).FullNameInCode();
        var elementAlias = _collectionElementType.FullNameInCode();

        if (Mode == FormAssignMode.WriteToVariable)
        {
            writer.Write($"var {Variable.Usage} = new {collectionAlias}();");
        }
        else
        {
            writer.Write($"{Variable.Usage} = new {collectionAlias}();");
        }

        if (_collectionElementType == typeof(string))
        {
            writer.Write($"{Variable.Usage}.AddRange(httpContext.Request.Form[\"{Variable.Name}\"]);");
        }
        else
        {
            writer.Write($"BLOCK:foreach (var {Variable.Name}Value in httpContext.Request.Form[\"{Variable.Name}\"])");

            if (_collectionElementType.IsEnum)
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse<{elementAlias}>({Variable.Name}Value, true, out var {Variable.Name}ValueParsed))");
            }
            else if (_collectionElementType.IsBoolean())
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Name}Value, out var {Variable.Name}ValueParsed))");
            }
            else
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Name}Value, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Name}ValueParsed))");
            }

            writer.Write($"{Variable.Usage}.Add({Variable.Name}ValueParsed);");
            writer.FinishBlock(); // parsing block

            writer.FinishBlock(); // foreach blobck
        }

        Next?.GenerateCode(method, writer);
    }

    public static bool CanParse(Type argType)
    {
        if (!argType.IsGenericType)
        {
            return false;
        }

        var elementType = GetCollectionElementType(argType);

        var genericListConcreteType = typeof(List<>).MakeGenericType(elementType);
        var genericListInterfaceType = typeof(IList<>).MakeGenericType(elementType);
        var genericReadOnlyListType = typeof(IReadOnlyList<>).MakeGenericType(elementType);
        var genericEnumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);

        var collectionTypeSupported = argType == genericListConcreteType
            || argType == genericListInterfaceType
            || argType == genericReadOnlyListType
            || argType == genericEnumerableType;

        var elementTypeSupported = elementType == typeof(string) || RouteParameterStrategy.CanParse(elementType);

        return collectionTypeSupported && elementTypeSupported;
    }

    private static Type GetCollectionElementType(Type collectionType)
        => collectionType.GetGenericArguments()[0];
    
    public void AssignToProperty(string usage)
    {
        Variable.OverrideName(usage);
        Mode = FormAssignMode.WriteToProperty;
    }

    public FormAssignMode Mode { get; private set; } = FormAssignMode.WriteToVariable;
}


internal class ParsedArrayFormValue : SyncFrame, IReadFormFrame
{
    private string _property;

    public ParsedArrayFormValue(Type parameterType, string parameterName) 
    {
        Variable = new FormVariable(parameterType, parameterName!, this);
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = FormAssignMode.WriteToProperty;
    }

    public FormAssignMode Mode { get; private set; } = FormAssignMode.WriteToVariable;

    public FormVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var elementType = Variable.VariableType.GetElementType();
        if (elementType == typeof(string))
        {
            if (Mode == FormAssignMode.WriteToVariable)
            {
                writer.Write($"var {Variable.Usage} = httpContext.Request.Form[\"{Variable.Usage}\"].ToArray();");
            }
            else
            {
                writer.Write($"{_property} = httpContext.Request.Form[\"{Variable.Usage}\"].ToArray();");
            }
        }
        else
        {
            var collectionAlias = typeof(List<>).MakeGenericType(elementType).FullNameInCode();
            var elementAlias = elementType.FullNameInCode();

            writer.Write($"var {Variable.Usage}_List = new {collectionAlias}();");
            
            writer.Write($"BLOCK:foreach (var {Variable.Usage}Value in httpContext.Request.Form[\"{Variable.Usage}\"])");

            if (elementType.IsEnum)
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse<{elementAlias}>({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else if (elementType.IsBoolean())
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, out var {Variable.Usage}ValueParsed))");
            }
            else
            {
                writer.Write($"BLOCK:if ({elementAlias}.TryParse({Variable.Usage}Value, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}ValueParsed))");
            }

            writer.Write($"{Variable.Usage}_List.Add({Variable.Usage}ValueParsed);");
            writer.FinishBlock(); // parsing block

            writer.FinishBlock(); // foreach blobck
            
            if (Mode == FormAssignMode.WriteToVariable)
            {
                writer.Write($"var {Variable.Usage} = {Variable.Usage}_List.ToArray();");
            }
            else
            {
                writer.Write($"if ({Variable.Usage}_List.Any()) {_property} = {Variable.Usage}_List.ToArray();");
            }
        }
        
        Next?.GenerateCode(method, writer);
    }
}

internal class FormParameterStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if(!parameter.HasAttribute<FromFormAttribute>())
        {
            variable = default;
            return false;
        }
        variable = chain.TryFindOrCreateFormValue(parameter);
        return variable != null;
    }
}
