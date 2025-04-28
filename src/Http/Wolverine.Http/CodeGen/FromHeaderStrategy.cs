using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

public class HeaderValueVariable : Variable
{
    public HeaderValueVariable(IFromHeaderMetadata metadata, Type variableType, string usage, Frame? creator) : base(variableType, usage, creator)
    {
        Name = metadata.Name!;
    }

    public string Name { get; }
}

public class FromHeaderStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        var att = parameter.GetCustomAttributes().OfType<IFromHeaderMetadata>().FirstOrDefault();

        if (att != null)
        {
            variable = chain.GetOrCreateHeaderVariable(att, parameter);
            return true;
        }

        variable = default;
        return false;
    }
}

internal class FromHeaderValue : SyncFrame, IReadHttpFrame
{
    private readonly string _header;
    private string _property;

    public FromHeaderValue(IFromHeaderMetadata header, ParameterInfo parameter)
    {
        Variable = new HeaderValueVariable(header, parameter.ParameterType, parameter.Name!, this);
        _header = header.Name ?? parameter.Name!;
    }
    
    public FromHeaderValue(IFromHeaderMetadata header, PropertyInfo property)
    {
        Variable = new HeaderValueVariable(header, property.PropertyType, property.Name!, this);
        _header = header.Name ?? property.Name!;
    }


    public HeaderValueVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve header value from the request");
        if (Mode == AssignMode.WriteToVariable)
        {
            writer.Write(
                $"var {Variable.Usage} = {nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{_header}\");");
        }
        else
        {
            writer.Write(
                $"{_property} = {nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{_property}\");");
        }
        
        Next?.GenerateCode(method, writer);
    }

    public void AssignToProperty(string usage)
    {
        Mode = AssignMode.WriteToProperty;
        _property = usage;
    }

    public AssignMode Mode { get; private set; }
}

internal class ParsedHeaderValue : SyncFrame, IReadHttpFrame
{
    private readonly string _header;
    private string _property;

    public ParsedHeaderValue(IFromHeaderMetadata header, ParameterInfo parameter)
    {
        _header = header.Name ?? parameter.Name!;
        Variable = new HeaderValueVariable(header, parameter.ParameterType, parameter.Name!, this);
    }
    
    public ParsedHeaderValue(IFromHeaderMetadata header, PropertyInfo property)
    {
        _header = header.Name ?? property.Name!;
        Variable = new HeaderValueVariable(header, property.PropertyType, property.Name!, this);
    }

    public HeaderValueVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var valueType = Variable.VariableType;
        var isNullable = false;
        if (Variable.VariableType.IsNullable())
        {
            isNullable = true;
            valueType = Variable.VariableType.GetInnerTypeFromNullable();
        }
        
        var alias = valueType.FullNameInCode();

        if (isNullable)
        {
            
        }
        else
        {
            // TODO -- YUCK. Gotta accomodate bools and enums
            if (Mode == AssignMode.WriteToVariable)
            {
                writer.Write($"{alias} {Variable.Usage} = default;");
            
                writer.Write(
                    $"{alias}.TryParse({nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{_header}\"), out {Variable.Usage});");
            }
            else
            {
                writer.Write(
                    $"{alias}.TryParse({nameof(HttpHandler.ReadSingleHeaderValue)}(httpContext, \"{_header}\"), out var {Variable.Usage}) {_property} = {Variable.Usage};");
            }

        }



        Next?.GenerateCode(method, writer);
    }
    
    public void AssignToProperty(string usage)
    {
        Mode = AssignMode.WriteToProperty;
        _property = usage;
    }

    public AssignMode Mode { get; private set; }
}