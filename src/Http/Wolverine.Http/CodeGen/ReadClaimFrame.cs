using System.Security.Claims;
using System.Text.RegularExpressions;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// Code generation frame that reads a claim value from the ClaimsPrincipal.
/// Supports string and typed values via TryParse.
/// </summary>
internal class ReadClaimFrame : SyncFrame
{
    private readonly string _claimType;
    private readonly Type _valueType;
    private readonly bool _isNullable;
    private readonly Type _rawType;

    public ReadClaimFrame(Type valueType, string claimType)
    {
        _claimType = claimType;
        _valueType = valueType;
        _isNullable = valueType.IsNullable();
        _rawType = _isNullable ? valueType.GetInnerTypeFromNullable() : valueType;
        Variable = new Variable(valueType, $"claim_{toIdentifier(claimType)}", this);
    }

    // Claim types can be URIs (e.g. ClaimTypes.NameIdentifier), so replace every non-identifier character.
    // TODO: replace with JasperFx's Variable.SanitizeVariableName once it handles this
    private static string toIdentifier(string claimType) => Regex.Replace(claimType, @"\W", "_");

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_rawType == typeof(string))
        {
            writeStringValue(writer);
        }
        else
        {
            writeTypedValue(writer);
        }

        Next?.GenerateCode(method, writer);
    }

    private void writeStringValue(ISourceWriter writer)
    {
        writer.Write(
            $"var {Variable.Usage} = httpContext.User?.FindFirst(\"{_claimType}\")?.Value;");
    }

    private void writeTypedValue(ISourceWriter writer)
    {
        writer.Write(
            $"var {Variable.Usage}_raw = httpContext.User?.FindFirst(\"{_claimType}\")?.Value;");
        writer.Write($"{_valueType.FullNameInCode()} {Variable.Usage} = default;");

        writer.Write(
            $"BLOCK:if ({Variable.Usage}_raw != null && {ParsingCode.TryParse(_rawType, $"{Variable.Usage}_raw", $"var {Variable.Usage}_parsed")})");

        writer.Write($"{Variable.Usage} = {Variable.Usage}_parsed;");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield break;
    }
}
