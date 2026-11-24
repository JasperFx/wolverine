using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Wolverine.Core.FSharpTests;

/// <summary>
///     F# cannot reassign the loaded saga the way C# does, so CreateMissingSagaFrame shadows it inside the
///     saga-missing branch. The chain's postprocessors are written after that branch, where the shadowed binding
///     is null again, so the F# generation refuses such a chain instead of handing them null.
/// </summary>
public class saga_read_by_a_postprocessor
{
    [Fact]
    public void fsharp_codegen_refuses_a_postprocessor_that_reads_a_created_saga()
    {
        var exception = Should.Throw<Exception>(() => generate(typeof(AuditedTallySaga), withAuditMiddleware: true));

        var notSupported = innermost<NotSupportedException>(exception);
        notSupported.ShouldNotBeNull(exception.ToString());
        notSupported.Message.ShouldContain(typeof(AuditedTallySaga).FullNameInCode());
        notSupported.Message.ShouldContain($"{typeof(AuditedTallyMiddleware).FullNameInCode()}.After()");
    }

    [Fact]
    public void fsharp_codegen_still_creates_a_saga_without_such_a_postprocessor()
    {
        var code = generate(typeof(AuditedTallySaga), withAuditMiddleware: false);

        code.ShouldContain("// Create a new instance of the saga state");
    }

    private static string generate(Type sagaType, bool withAuditMiddleware)
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType(sagaType);

                    if (withAuditMiddleware)
                    {
                        opts.Policies.AddMiddleware<AuditedTallyMiddleware>(c =>
                            c.MessageType == typeof(AddToAuditedTally));
                    }
                })
                .Build();

            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);
            var chain = handlerGraph.ChainFor<AddToAuditedTally>()!;
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);

            return generatedAssembly.GenerateFSharpCode(host.Services.GetService<IServiceVariableSource>());
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static T? innermost<T>(Exception? exception) where T : Exception
    {
        T? found = null;
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is T match) found = match;
        }

        return found;
    }
}

public record AddToAuditedTally(string Id, int Amount);

public class AuditedTallySaga : Saga
{
    public string? Id { get; set; }
    public int Total { get; set; }

    public void StartOrHandle(AddToAuditedTally command)
    {
        Id ??= command.Id;
        Total += command.Amount;
    }
}

public class AuditedTallyMiddleware
{
    public static void After(AuditedTallySaga saga)
    {
    }
}
