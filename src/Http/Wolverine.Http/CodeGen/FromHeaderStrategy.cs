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

internal class FromHeaderValue : SyncFrame
{
    private Variable? _httpContext;
    private readonly string _header;

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

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve header value from the request");
        writer.Write(
            $"var {Variable.Usage} = {nameof(HttpHandler.ReadSingleHeaderValue)}({_httpContext!.Usage}, \"{_header}\");");
        Next?.GenerateCode(method, writer);
    }
}

internal class ParsedHeaderValue : SyncFrame
{
    private readonly string _header;
    private Variable? _httpContext;

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

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public HeaderValueVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.ShortNameInCode();
        writer.Write($"{alias} {Variable.Usage} = default;");
        writer.Write(
            $"{alias}.TryParse({nameof(HttpHandler.ReadSingleHeaderValue)}({_httpContext!.Usage}, \"{_header}\"), out {Variable.Usage});");

        Next?.GenerateCode(method, writer);
    }
}