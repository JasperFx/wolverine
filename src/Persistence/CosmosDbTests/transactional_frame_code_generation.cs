using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.Runtime.Handlers;

namespace CosmosDbTests;

/// <summary>
///     Verifies that <c>TransactionalFrame.FindVariables</c> does not declare a
///     <see cref="CancellationToken" /> dependency, so the generated
///     <c>EnlistInOutbox</c> call only passes the two arguments
///     <c>CosmosDbEnvelopeTransaction</c> actually requires (<c>Container</c>
///     and <c>MessageContext</c>).
/// </summary>
public class transactional_frame_code_generation
{
    // Dummy emulator connection string — CosmosClient is lazy; no real connection
    // is made during codegen.
    private const string DummyConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [Fact]
    public void enlist_in_outbox_call_does_not_include_cancellation_token()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton(_ => new CosmosClient(DummyConnectionString));
                    opts.UseCosmosDbPersistence("wolverine_codegen_test");
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<TransactionalFrameCodegenTestHandler>();
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host.
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            var chain = handlerGraph.ChainFor(typeof(TransactionalFrameCodegenTestMessage));
            chain.ShouldNotBeNull();

            // TransactionalFrame should be present in middleware: the handler depends on
            // Container, so CosmosDbPersistenceFrameProvider.CanApply returns true.
            chain.Middleware.ShouldContain(f => f.GetType().Name == "TransactionalFrame");

            ((ICodeFile)chain).AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            // TransactionalFrame.GenerateCode emits EnlistInOutbox with exactly two args:
            // Container and MessageContext.  It must NOT forward the method-level
            // cancellation token — CosmosDbEnvelopeTransaction doesn't accept one, and
            // declaring the variable in FindVariables without using it was a latent bug.
            code.ShouldContain(
                "EnlistInOutbox(new Wolverine.CosmosDb.Internals.CosmosDbEnvelopeTransaction(_container, context));");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}

public record TransactionalFrameCodegenTestMessage(string Text);

public class TransactionalFrameCodegenTestHandler(Container container)
{
    public void Handle(TransactionalFrameCodegenTestMessage message)
    {
        // Handler that injects Container so CosmosDbPersistenceFrameProvider.CanApply
        // returns true, causing TransactionalFrame to be added to the chain.
        _ = container;
    }
}
