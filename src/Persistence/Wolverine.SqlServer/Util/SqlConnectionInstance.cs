using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Lamar.IoC;
using Lamar.IoC.Frames;
using Lamar.IoC.Instances;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.SqlServer.Util;

internal class SqlConnectionInstance : Instance
{
    private Instance? _settings;

    public SqlConnectionInstance(Type serviceType) : base(serviceType, typeof(SqlConnection),
        ServiceLifetime.Scoped)
    {
        Name = Variable.DefaultArgName(serviceType);
    }

    public override bool RequiresServiceProvider(IMethodVariables method)
    {
        return false;
    }

    public override Func<Scope, object> ToResolver(Scope topScope)
    {
        return _ => new SqlConnection(topScope.GetInstance<SqlServerSettings>().ConnectionString);
    }

    public override object Resolve(Scope scope)
    {
        return new SqlConnection(scope.GetInstance<SqlServerSettings>().ConnectionString);
    }

    public override Variable CreateVariable(BuildMode mode, ResolverVariables variables, bool isRoot)
    {
        var settings = variables.Resolve(_settings, mode);
        return new SqlConnectionFrame(settings, this).Connection;
    }

    protected override IEnumerable<Instance> createPlan(ServiceGraph services)
    {
        _settings = services.FindDefault(typeof(SqlServerSettings));
        yield return _settings;
    }
}

public class SqlConnectionFrame : SyncFrame
{
    private readonly Instance _instance;
    private readonly Variable _settings;

    public SqlConnectionFrame(Variable settings, Instance instance)
    {
        _settings = settings;
        Connection = new ServiceVariable(instance, this);
        _instance = instance;
    }

    public ServiceVariable Connection { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _settings;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"BLOCK:using ({_instance.ServiceType.FullNameInCode()} {Connection.Usage} = new {typeof(SqlConnection).FullName}({_settings.Usage}.{nameof(SqlServerSettings.ConnectionString)}))");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();
    }
}