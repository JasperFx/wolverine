using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime.Handlers;
using WolverineFSharpSample;
using Xunit;

namespace Wolverine.EfCore.FSharpTests;

public record ReadTenantItems;

[WolverineIgnore]
public static class ReadTenantItemsHandler
{
    [NonTransactional]
    public static void Handle(ReadTenantItems command, ItemsDbContext db)
    {
    }
}

public class tenant_db_context_fsharp_codegen
{
    [Fact]
    public void a_non_transactional_chain_taking_a_managed_tenant_db_context_renders_as_fsharp()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>(
                        (builder, _, _) => builder.UseInMemoryDatabase("tenant-items"), AutoCreate.None);

                    opts.UseEntityFrameworkCoreTransactions();
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(ReadTenantItemsHandler));
                })
                .Build();

            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(ReadTenantItems)).ShouldNotBeNull();

            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);

            var code = generatedAssembly.GenerateFSharpCode(host.Services.GetService<IServiceVariableSource>());

            code.ShouldContain("BuildForTenantAsync");
            code.ShouldNotContain("BuildAndEnrollAsync");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
