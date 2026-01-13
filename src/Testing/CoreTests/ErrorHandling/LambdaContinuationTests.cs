using System.Diagnostics;
using CoreTests.Runtime;
using JasperFx.Core.Reflection;
using NSubstitute;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.ErrorHandling;

public class LambdaContinuationTests
{
    [Fact]
    public async Task execute_as_inline_with_no_invoke_usage()
    {
        var wasCalled = false;

        var continuation = new LambdaContinuation((_, _, _) =>
        {
            wasCalled = true;
            return new ValueTask();
        }, new Exception());

        await Should.ThrowAsync<Exception>(async () =>
        {
            var result = await continuation.ExecuteInlineAsync(Substitute.For<IEnvelopeLifecycle>(),
                new MockWolverineRuntime(), DateTimeOffset.UtcNow, null, CancellationToken.None);

        });

        wasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task execute_as_inline_with_stop()
    {
        var wasCalled = false;

        var continuation = new LambdaContinuation((_, _, _) =>
        {
            wasCalled = true;
            return new ValueTask();
        }, new Exception())
        {
            InvokeUsage = InvokeResult.Stop
        };

        var result = await continuation.ExecuteInlineAsync(Substitute.For<IEnvelopeLifecycle>(),
            new MockWolverineRuntime(), DateTimeOffset.UtcNow, null, CancellationToken.None);
        
        result.ShouldBe(InvokeResult.Stop);
        wasCalled.ShouldBeTrue();
    }
    
    [Fact]
    public async Task execute_as_inline_with_try_again()
    {
        var wasCalled = false;

        var continuation = new LambdaContinuation((_, _, _) =>
        {
            wasCalled = true;
            return new ValueTask();
        }, new Exception())
        {
            InvokeUsage = InvokeResult.TryAgain
        };

        var result = await continuation.ExecuteInlineAsync(Substitute.For<IEnvelopeLifecycle>(),
            new MockWolverineRuntime(), DateTimeOffset.UtcNow, null, CancellationToken.None);
        
        result.ShouldBe(InvokeResult.TryAgain);
        wasCalled.ShouldBeTrue();
    }

    [Fact]
    public void source_mechanics_passes_along_invoke_usage()
    {
        var wasCalled = false;
        var source = new UserDefinedContinuationSource((_, _, _) =>
        {
            wasCalled = true;
            return new ValueTask();
        })
        {
            InvokeUsage = InvokeResult.TryAgain
        };

        var continuation = source.Build(new Exception(), new Envelope());
        
        continuation.As<LambdaContinuation>()
            .InvokeUsage.ShouldBe(InvokeResult.TryAgain);
    }
}