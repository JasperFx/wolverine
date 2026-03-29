using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// Code generation frame that reads a header value from the message Envelope.
/// Supports string and typed values via TryParse.
/// </summary>
internal class ReadEnvelopeHeaderFrame : SyncFrame
{
    private readonly string _headerKey;
    private readonly Type _valueType;
    private readonly bool _isNullable;
    private readonly Type _rawType;

    public ReadEnvelopeHeaderFrame(Type valueType, string headerKey)
    {
        _headerKey = headerKey;
        _valueType = valueType;
        _isNullable = valueType.IsNullable();
        _rawType = _isNullable ? valueType.GetInnerTypeFromNullable() : valueType;
        Variable = new Variable(valueType, $"envelopeHeader_{headerKey.Replace("-", "_")}", this);
    }

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
            $"context.Envelope!.TryGetHeader(\"{_headerKey}\", out var {Variable.Usage}_raw);");
        writer.Write($"var {Variable.Usage} = {Variable.Usage}_raw;");
    }

    private void writeTypedValue(ISourceWriter writer)
    {
        var typeName = _rawType.FullNameInCode();

        writer.Write(
            $"context.Envelope!.TryGetHeader(\"{_headerKey}\", out var {Variable.Usage}_raw);");
        writer.Write($"{_valueType.FullNameInCode()} {Variable.Usage} = default;");

        if (_rawType.IsEnum)
        {
            writer.Write(
                $"BLOCK:if ({Variable.Usage}_raw != null && {typeName}.TryParse<{typeName}>({Variable.Usage}_raw, true, out var {Variable.Usage}_parsed))");
        }
        else if (_rawType.IsBoolean())
        {
            writer.Write(
                $"BLOCK:if ({Variable.Usage}_raw != null && {typeName}.TryParse({Variable.Usage}_raw, out var {Variable.Usage}_parsed))");
        }
        else
        {
            writer.Write(
                $"BLOCK:if ({Variable.Usage}_raw != null && {typeName}.TryParse({Variable.Usage}_raw, System.Globalization.CultureInfo.InvariantCulture, out var {Variable.Usage}_parsed))");
        }

        writer.Write($"{Variable.Usage} = {Variable.Usage}_parsed;");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield break;
    }
}
