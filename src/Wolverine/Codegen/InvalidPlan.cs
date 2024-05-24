using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

internal class InvalidPlan : ServicePlan
{
    public InvalidPlan(ServiceDescriptor descriptor) : base(descriptor)
    {
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return true;
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        return "It's just bad";
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        throw new NotSupportedException();
    }
}