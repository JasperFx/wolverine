using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
/// Reproducer for the duplicate <c>var messageContext = new MessageContext(_wolverineRuntime);</c>
/// codegen bug surfaced by an HTTP endpoint that:
///
///   (1) takes an <c>IDocumentSession</c> parameter (triggers Wolverine.Marten's
///       <c>OutboxedSessionFactory.OpenSession(messageContext)</c> contribution), AND
///   (2) requires the post-save outbox flush per
///       https://github.com/JasperFx/wolverine/issues/536 (a cascading message return,
///       <c>IMessageBus</c>/<c>IMessageContext</c> dependency, or a post-processor that
///       MaySendMessages), AND
///   (3) does NOT participate in tenant-id detection (so the MessageContext is not
///       pre-emitted by tenant-aware codegen at the top of the method).
///
/// In that combination, <c>CreateDocumentSessionFrame.FindVariables</c> resolves its
/// context via <c>chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices)</c>
/// — request type IMessageContext. Wolverine.Http's
/// <see cref="Wolverine.Http.CodeGen.MessageBusSource"/> matches IMessageBus /
/// IMessageContext / MessageContext. Later in the same chain,
/// <c>FlushOutgoingMessages : MethodCall(typeof(MessageContext), ...)</c> asks the
/// chain for typeof(MessageContext) — a DIFFERENT request type — and the source
/// previously returned <c>new CreateMessageContextWithMaybeTenantFrame().Variable</c>
/// on every call, producing two distinct frame instances that each emitted
/// <c>var messageContext = new MessageContext(_wolverineRuntime);</c>. CS0128 at
/// compile time on the second declaration prevented the chain from ever booting.
///
/// JasperFx.CodeGeneration's MethodVariables caches resolved variables by REQUEST
/// type, so requests for IMessageContext and MessageContext live in separate cache
/// entries — even though MessageBusSource produces a variable typed as
/// MessageContext in both cases. The fix caches the produced frame inside
/// MessageBusSource so that any of the three matched types resolves to the same
/// frame instance. The cache is per-source, and a fresh MessageBusSource is added
/// to every HTTP chain in <c>HttpChain.Codegen.cs</c>, so the cache scope is
/// exactly one generated handler method.
///
/// We assert on the compiled generated source code (forced by
/// <see cref="JasperFx.CodeGeneration.CodeFileExtensions.InitializeSynchronously"/>)
/// because the failing frame composition is only visible after the
/// MethodFrameArranger pulls variable-producing frames in. The pre-arrangement
/// <c>chain.DetermineFrames(...)</c> list never includes the duplicates, but the
/// final SourceCode does.
///
/// Endpoint under test: <c>POST /todoitems</c> in WolverineWebApi.Samples.
/// </summary>
public class Bug_marten_outbox_duplicate_message_context_codegen : IntegrationContext
{
    public Bug_marten_outbox_duplicate_message_context_codegen(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void http_chain_emits_exactly_one_message_context_declaration()
    {
        var chain = HttpChains.ChainFor("POST", "/todoitems");
        chain.ShouldNotBeNull();
        chain.As<ICodeFile>().InitializeSynchronously(
            HttpChains.Rules,
            HttpChains,
            Host.Services);

        var source = chain.SourceCode;
        source.ShouldNotBeNull("Failed to generate the source code");
        
        const string declaration = "var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);";
        var count = TotalFound(source, declaration);

        count.ShouldBe(1,
            $"Expected exactly one `{declaration}` line in the generated source, found {count}. " +
            "Two would produce CS0128 at compile time and prevent the chain from booting. " +
            $"Source code follows:\n{source}");
    }

    private static int TotalFound(string lookin, string lookfor)
    {
        var count = 0;
        var index = 0;
        while ((index = lookin.IndexOf(lookfor, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += lookfor.Length;
        }
        return count;
    }
}
