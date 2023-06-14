using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Logging;

internal class LogStartingActivity : SyncFrame
{
    private readonly Type _inputType;
    private readonly LogLevel _level;
    private readonly List<AuditedMember> _members;
    private Variable? _envelope;
    private Variable? _input;
    private Variable? _logger;

    public LogStartingActivity(LogLevel level, IChain chain)
    {
        _level = level;
        _inputType = chain.InputType()!;
        _members = chain.AuditedMembers;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;

        _input = chain.FindVariable(_inputType);
        yield return _input;

        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var template = $"Starting to process {_inputType.FullNameInCode()} ({{Id}})";
        if (_members.Any())
        {
            template += " with " + _members.Select(m => $"{m.MemberName}: {{{m.Member.Name}}}").Join(", ");
        }

        var args = new[] { $"{_envelope!.Usage}.{nameof(Envelope.Id)}" };
        args = args.Concat(_members.Select(x => $"{_input!.Usage}.{x.Member.Name}")).ToArray();

        writer.WriteLine(
            $"{_logger!.Usage}.{nameof(ILogger.Log)}({typeof(LogLevel).FullNameInCode()}.{_level.ToString()}, \"{template}\", {args.Join(", ")});");
        Next?.GenerateCode(method, writer);
    }
}