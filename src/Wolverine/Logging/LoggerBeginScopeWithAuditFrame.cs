using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Logging;

public abstract class LoggerBeginScopeWithAuditBaseFrame : SyncFrame
{
    private readonly Type? _inputType;
    private readonly List<Variable> _loggers = [];
    private readonly IEnumerable<Type> _loggerTypes;

    protected LoggerBeginScopeWithAuditBaseFrame(IChain chain, IContainer container, IReadOnlyList<AuditedMember> auditedMembers, Variable? auditedVariable)
    {
        _loggerTypes = chain.ServiceDependencies(container, Type.EmptyTypes).Where(type => type.CanBeCastTo<ILogger>());
        AuditedMembers = auditedMembers;
        _inputType = chain.InputType()!;
        AuditedVariable = auditedVariable;
    }

    protected Variable? AuditedVariable;
    protected Variable? LoggingContext;
    protected readonly IReadOnlyList<AuditedMember> AuditedMembers;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var loggerType in _loggerTypes)
        {
            var logger = chain.FindVariable(loggerType);
            _loggers.Add(logger);
            yield return logger;
        }
        LoggingContext = chain.FindVariable(typeof(LoggingContext));
        if (AuditedVariable is not null || _inputType is null)
        {
            yield break;
        }
        AuditedVariable = chain.FindVariable(_inputType);
        yield return AuditedVariable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (AuditedMembers.Count > 0 && _loggers.Count > 0 && AuditedVariable is not null)
        {
            writer.WriteComment("Adding audited members to log context");
            writer.Write($"{LoggingContext!.Usage}.{nameof(Logging.LoggingContext.AddRange)}({string.Join(", ", AuditedMembers.Select(member => $"(\"{member.MemberName}\", {AuditedVariable!.Usage}.{member.Member.Name})"))});");
            writer.WriteComment("Beginning logging scopes for new context");
            foreach (var logger in _loggers)
            {
                writer.Write(
                    $"using var {createRandomVariable("disposable")} = {logger.Usage}.{nameof(ILogger.BeginScope)}({LoggingContext!.Usage});");
            }
        }
        GenerateAdditionalCode(method, writer, _loggers);
        Next?.GenerateCode(method, writer);
    }

    protected virtual void GenerateAdditionalCode(GeneratedMethod method, ISourceWriter writer, IEnumerable<Variable> loggerVariables)
    {
    }
    static string createRandomVariable(string prefix) => $"{prefix}_{Guid.NewGuid().ToString().Replace('-', '_')}";
}

public class LoggerBeginScopeWithAuditFrame : LoggerBeginScopeWithAuditBaseFrame
{
    public LoggerBeginScopeWithAuditFrame(IChain chain, IContainer container)
        : base(chain, container, chain.AuditedMembers.AsReadOnly(), null)
    {
    }
}

public class LoggerBeginScopeWithAuditForAggregateFrame : LoggerBeginScopeWithAuditBaseFrame
{
    public LoggerBeginScopeWithAuditForAggregateFrame(IChain chain, IContainer container, IReadOnlyList<AuditedMember> members, Variable variable)
        : base(chain, container, members, variable)
    {
    }

    protected override void GenerateAdditionalCode(GeneratedMethod method, ISourceWriter writer, IEnumerable<Variable> loggerVariables)
    {
        if (AuditedMembers.Count == 0)
        {
            return;
        }
        writer.WriteComment("Application-specific Open Telemetry auditing");
        foreach (var member in AuditedMembers)
        {
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {AuditedVariable!.Usage}.{member.Member.Name});");
        }
    }
}
