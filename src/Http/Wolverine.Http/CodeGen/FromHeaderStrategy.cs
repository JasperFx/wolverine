using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.CodeGen;

public class FromHeaderStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable? variable)
    {
        var att = parameter.GetCustomAttributes().OfType<IFromHeaderMetadata>().FirstOrDefault();
        if (att != null)
        {
            if (parameter.ParameterType == typeof(string))
            {
                var frame = new FromHeaderValue(att, parameter);
                chain.Middleware.Add(frame);
                variable = frame.Variable;
            }
            else
            {
                var frame = new ParsedHeaderValue(att, parameter);
                chain.Middleware.Add(frame);
                variable = frame.Variable;
            }


            
            return true;
        }

        variable = default;
        return false;
    }
}

internal class FromHeaderValue : SyncFrame
{
    private Variable _httpContext;
    private readonly string _header;

    public FromHeaderValue(IFromHeaderMetadata header, ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name, this);
        _header = header.Name ?? parameter.Name;
    }
    
    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = {nameof(HttpHandler.ReadSingleHeaderValue)}({_httpContext.Usage}, \"{_header}\");");
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
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }
    
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var alias = Variable.VariableType.ShortNameInCode();
        writer.Write($"{alias} {Variable.Usage} = default;");
        writer.Write($"{alias}.TryParse({nameof(HttpHandler.ReadSingleHeaderValue)}({_httpContext!.Usage}, \"{_header}\"), out {Variable.Usage});");

        Next?.GenerateCode(method, writer);
    }
}