using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Npgsql;
using Wolverine.RDBMS;

namespace Wolverine.Postgresql;

internal class NpgsqlConnectionFrame : SyncFrame
{
    private readonly Type _serviceType;
    private Variable _settings;

    public NpgsqlConnectionFrame(Type serviceType)
    {
        _serviceType = serviceType;
        Connection = new Variable(typeof(NpgsqlConnection), this);
    }

    public Variable Connection { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _settings = chain.FindVariable(typeof(DatabaseSettings));
        yield return _settings;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"BLOCK:using ({_serviceType.FullNameInCode()} {Connection.Usage} = new {typeof(NpgsqlConnection).FullNameInCode()}({_settings.Usage}.{nameof(DatabaseSettings.ConnectionString)}))");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();
    }
}