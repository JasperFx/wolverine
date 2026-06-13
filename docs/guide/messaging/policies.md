# Endpoint Policies

An *endpoint policy* is a reusable rule that Wolverine applies to every messaging endpoint (listeners, senders,
and local queues) as your application bootstraps. Rather than configuring each Rabbit MQ queue or Azure Service
Bus subscription one at a time, you can write a single policy that's evaluated against *all* endpoints —
including ones created by [conventional routing](/guide/messaging/subscriptions) that you never explicitly
declared.

Endpoint policies are how a lot of Wolverine's own behavior is implemented. For example, the Rabbit MQ
`UseQuorumQueues()` helper is just an endpoint policy that flips every application-owned queue to a quorum queue.

## Writing an Endpoint Policy

Implement the `Wolverine.Configuration.IEndpointPolicy` interface. It has a single method that receives each
`Endpoint` together with the live `IWolverineRuntime`:

<!-- snippet: sample_custom_endpoint_policy -->
<a id='snippet-sample_custom_endpoint_policy'></a>
```cs
// Force every application listening endpoint to process messages inline.
// Wolverine's own internal/system endpoints are left alone.
public class InlineListenersPolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        // Don't touch endpoints that Wolverine itself owns
        if (endpoint.Role == EndpointRole.System) return;

        if (endpoint.IsListener)
        {
            endpoint.Mode = EndpointMode.Inline;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EndpointPolicySamples.cs#L8-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_endpoint_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A few things worth knowing about the `Endpoint` you're handed:

* `endpoint.Role` is either `EndpointRole.System` (something Wolverine created and owns internally) or
  `EndpointRole.Application` (something you declared, or that conventional routing created on your behalf). It's
  almost always correct to skip `System` endpoints in your policy.
* `endpoint.Mode` controls the durability/buffering behavior and is one of `EndpointMode.Durable`,
  `EndpointMode.BufferedInMemory`, or `EndpointMode.Inline`.
* `endpoint.IsListener` tells you whether the endpoint is receiving messages.
* `endpoint.Uri` is the unique address of the endpoint, which is handy for matching by transport scheme or queue
  name.

Your policy runs once per endpoint during bootstrapping, so it's the right place for cross-cutting configuration
but *not* for per-message logic.

## Registering a Policy

Policies are registered through `opts.Policies` inside `UseWolverine()`. You can add a policy type that will be
created by Wolverine, or add a pre-built instance:

<!-- snippet: sample_register_endpoint_policy -->
<a id='snippet-sample_register_endpoint_policy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Register a custom IEndpointPolicy type
        opts.Policies.Add<InlineListenersPolicy>();

        // Or register a policy inline with LambdaEndpointPolicy<T>.
        // The lambda only runs for endpoints assignable to T (here, every Endpoint)
        opts.Policies.Add(new LambdaEndpointPolicy<Endpoint>((endpoint, runtime) =>
        {
            if (endpoint.Role == EndpointRole.System) return;
            endpoint.Mode = EndpointMode.BufferedInMemory;
        }));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EndpointPolicySamples.cs#L32-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_endpoint_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When you only need to react to one kind of endpoint (say, just Rabbit MQ queues), the generic
`LambdaEndpointPolicy<T>` is the most convenient option. The supplied lambda is only invoked for endpoints that
are assignable to `T`. This is exactly how Wolverine's Rabbit MQ integration implements `UseQuorumQueues()`:

```cs
// From Wolverine's own Rabbit MQ integration
Options.Policies.Add(new LambdaEndpointPolicy<RabbitMqQueue>((queue, _) =>
{
    if (queue.Role == EndpointRole.Application)
    {
        queue.QueueType = QueueType.quorum;
    }
}));
```

## Built-in Endpoint Policy Helpers

You don't always need to write your own `IEndpointPolicy`. For the most common cases, `opts.Policies` already
exposes convenience methods that build endpoint policies for you. Each one automatically skips Wolverine's
internal `System` endpoints:

* `opts.Policies.AllListeners(x => ...)` — apply a `ListenerConfiguration` action to every (non-local) listening
  endpoint.
* `opts.Policies.AllSenders(x => ...)` — apply an `ISubscriberConfiguration` action to every sending endpoint.
* `opts.Policies.AllLocalQueues(x => ...)` — apply configuration to every local queue.

For example:

```cs
opts.Policies.AllListeners(x => x.MaximumParallelMessages(5));
```

See [Configuring Listeners](/guide/messaging/listeners) for more on what's available through these helpers.
