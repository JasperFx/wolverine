using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-4416. An OnException declared on a HANDLER class that returns OutgoingMessages failed code
// generation outright:
//
//   System.InvalidOperationException: Frame chain is being re-arranged, tried to set
//   MessageContext.EnqueueCascadingAsync as the 'Next' property on DoThingHandler.OnException
//
// The two paths built the catch block's cascade frame differently. MiddlewarePolicy used the catch-safe
// CaptureCascadingMessagesInCatch; Chain.ApplyImpliedMiddlewareFromHandlers still used
// CaptureCascadingMessages, a MethodCall whose FindVariables exposes the dependency on the OnException
// call's return variable -- so the codegen arranger pre-linked the two catch frames' Next pointers and
// collided with TryCatchFinallyFrame's own manual chaining.
//
// The behavioural tests below assert the cascaded message is HANDLED, not merely sent. That distinction
// is load-bearing: a chain that fails to compile still sends, so a send-only assertion stays green over
// a handler that never ran -- the trap on_exception_cascade_name_collision was written for.
public class on_exception_outgoing_messages_4416
{
    [Fact]
    public async Task handler_on_exception_returning_outgoing_messages_compiles_and_cascades()
    {
        var recorder = new OnExceptionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DoThingHandler))
                    .IncludeType(typeof(ThingFailedHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new DoThing(id));

        // Before the fix the chain never compiled, so the OnException hook and the message it returns
        // never ran at all.
        recorder.Actions.ShouldContain($"Handled:{id}:vendor refused");
    }

    // The failure the issue actually reports: `codegen test` blows up. Asserted directly, because the
    // behavioural tests go red for a SYMPTOM -- an empty recorder, because nothing ran -- and would read
    // the same way for an unrelated cause. Generating the code is what names this bug.
    [Fact]
    public async Task the_handler_chain_generates_code_at_all()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(new OnExceptionRecorder());
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DoThingHandler))
                    .IncludeType(typeof(ThingFailedHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        // Before the fix this threw CodeGenerationException wrapping "Frame chain is being re-arranged,
        // tried to set MessageContext.EnqueueCascadingAsync as the 'Next' property on
        // DoThingHandler.OnException".
        Should.NotThrow(() => builder.GenerateAllCode());
    }

    [Fact]
    public async Task every_message_in_the_returned_collection_is_cascaded()
    {
        // EnqueueCascadingAsync unwraps OutgoingMessages, so more than one message in the collection
        // must each cascade -- the property that makes a single catch frame sufficient.
        var recorder = new OnExceptionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DoManyThingsHandler))
                    .IncludeType(typeof(ThingFailedHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new DoManyThings(id));

        recorder.Actions.ShouldContain($"Handled:{id}:first");
        recorder.Actions.ShouldContain($"Handled:{id}:second");
    }
}

#region sample_onexception_returning_outgoing_messages

public record DoThing(Guid Id);

public record ThingFailed(Guid Id, string Reason);

public class VendorRefusedException : Exception
{
    public VendorRefusedException(string message) : base(message)
    {
    }
}

public static class DoThingHandler
{
    public static void Handle(DoThing command)
    {
        throw new VendorRefusedException("vendor refused");
    }

    // Any OutgoingMessages returned from an OnException method is published exactly as it would be from
    // the handler method itself -- so the failure of DoThing becomes a ThingFailed message, and the
    // original exception is swallowed.
    public static OutgoingMessages OnException(VendorRefusedException exception, DoThing command)
    {
        var messages = new OutgoingMessages();
        messages.Add(new ThingFailed(command.Id, exception.Message));

        return messages;
    }
}

#endregion

public record DoManyThings(Guid Id);

public static class DoManyThingsHandler
{
    public static void Handle(DoManyThings command)
    {
        throw new VendorRefusedException("several");
    }

    public static OutgoingMessages OnException(VendorRefusedException exception, DoManyThings command)
    {
        return new OutgoingMessages
        {
            new ThingFailed(command.Id, "first"),
            new ThingFailed(command.Id, "second")
        };
    }
}

// Local handler so each cascaded message has a route and is tracked to EXECUTION rather than just sent.
public static class ThingFailedHandler
{
    public static void Handle(ThingFailed message, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"Handled:{message.Id}:{message.Reason}");
    }
}
