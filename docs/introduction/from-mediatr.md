# Wolverine for MediatR Users

[MediatR](https://github.com/jbogard/MediatR) is an extraordinarily successful OSS project in the .NET ecosystem, but it's
a very limited tool and the Wolverine team frequently fields questions from folks converting to Wolverine from MediatR.
Offhand, the common reasons to do so are:

1. Wolverine has built in support for the [transactional outbox](/guide/durability), even for its [in memory, local queues](/guide/messaging/transports/local)
2. Many people are using MediatR *and* a separate asynchronous messaging framework like MassTransit or NServiceBus while Wolverine handles the same use cases as MediatR *and* [asynchronous messaging](/guide/messaging/introduction) as well with one single set of rules for message handlers
3. Wolverine's programming model can easily result in significantly less application code than the same functionality would with MediatR

It's important to note that Wolverine allows for a completely different coding model than MediatR or other "IHandler of T" 
application frameworks in .NET. While you can use Wolverine as a near exact drop in replacement for MediatR, that's not
taking advantages of Wolverine's capabilities.

::: info
The word "unambitious" is literally part of MediatR's tagline. For better or worse, Wolverine on the other hand,
is most definitely an ambitious project and covers some very important use cases that MediatR does not.
:::

## Handlers

MediatR is an example of what I call an "IHandler of T" framework, just meaning
that the primary way to plug into the framework is by implementing an interface signature from the framework like this simple
example in MediatR:

```csharp
public class Ping : IRequest<Pong>
{
    public string Message { get; set; }
}

public class PingHandler : IRequestHandler<Ping, Pong> 
{
    private readonly TextWriter _writer;

    public PingHandler(TextWriter writer)
    {
        _writer = writer;
    }

    public async Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync($"--- Handled Ping: {request.Message}");
        return new Pong { Message = request.Message + " Pong" };
    }
}
```

::: info
No, Wolverine is not using reflection at runtime to call your methods because that would be slow. Instead, Wolverine is 
generating C# code (even if the handler is F#) to effectively create its own adapter type which is more or less the same
thing as MediatR's `IRequestHandler<T>` interface. Learn much more about that in the [Runtime Architecture](/guide/runtime) section.
:::

Now, if you assume that `TextWriter` is a registered service in your application's IoC container, Wolverine could easily run 
the exact class above as a Wolverine handler. While most [Hollywood Principle](https://deviq.com/principles/hollywood-principle) application frameworks usually require you to
implement some kind of adapter interface, Wolverine instead wraps around *your* code, with this being a perfectly acceptable
handler implementation to Wolverine:

```csharp
// No marker interface necessary, and records work well for this kind of little data structure
public record Ping(string Message);
public record Pong(string Message);

// It is legal to implement more than message handler in the same class
public static class PingHandler
{
    public static Pong Handle(Ping command, TextWriter writer)
    {
        _writer.WriteLine($"--- Handled Ping: {request.Message}");
        return new Pong(command.Message);
    }
}
```

So you might notice a couple of things that are different right away:

* While Wolverine is perfectly capable of using constructor injection for your handlers and class instances, you can eschew all that
  ceremony and use static methods for just a wee bit fewer object allocations
* Like MVC Core and Minimal API, Wolverine supports "method injection" such that you can pass in IoC registered services directly as arguments to the handler methods for a wee bit less ceremony
* There are no required interfaces on either the message type or the handler type
* Wolverine [discovers message handlers](/guide/handlers/discovery) through naming conventions (or you can also use marker interfaces or attributes if you have to)
* You can use synchronous methods for your handlers when that's valuable so you don't have to scatter `return Task.CompletedTask();` all over your code
* Moreover, Wolverine's [best practice](/tutorials/best-practices) as much as possible is to use pure functions for the message handlers for the absolute best testability

There are more differences though. At a minimum, you probably want to look at Wolverine's [compound handler](/guide/handlers/#compound-handlers) capability as a way
to build more complex handlers. 

::: tip
Wolverine was built with the express goal of allowing you to write very low ceremony code. To that end we try to minimize the usage of adapter interfaces,
mandatory base classes, or attributes in your code.
:::

## Built in Error Handling

Wolverine's `IMessageBus.InvokeAsync()` is the direct equivalent to MediatR's `IMediator.Send()`, *but*, the Wolverine usage
also builds in support for *some* of Wolverine's [error handling policies](/guide/handlers/error-handling) such that you can build in selective retries.

## MediatR's INotificationHandler

::: warning
You should not be using MediatR's `INotificationHandler` for any kind of background work that needs a true delivery guarantee (i.e., the notification will get processed even if the 
process fails unexpectedly).
:::

MediatR's `INotificationHandler` concept is strictly [fire and forget](https://www.enterpriseintegrationpatterns.com/patterns/conversation/FireAndForget.html), 
which is just not suitable if you need delivery guarantees of that work. Wolverine on the other hand supports both a "fire and forget" (`Buffered` in Wolverine parlance)  or a [durable, transactional inbox/outbox](/guide/durability) approach
with its in memory, local queues such that work will *not* be lost in the case of errors. Moreover, using the Wolverine local queues
allows you to take advantage of Wolverine's error handling capabilities for a much more resilient system that you'll achieve with MediatR.

`INotificationHandler` in Wolverine is just a message handler. You can publish messages anytime through the `IMessageBus.PublishAsync()` API, but if you're just
needing to publish additional messages (either commands or events, to Wolverine it's all just a message), you can utilize Wolverine's
[cascading message](/guide/handlers/cascading) usage as a way of building more testable handler methods. 

## MediatR IPipelineBehavior to Wolverine Middleware

MediatR uses its `IPipelineBehavior` model as a "Russian Doll" model for handling cross cutting concerns across handlers. 
Wolverine has its own mechanism for cross cutting concerns with its [middleware](/guide/handlers/middleware) capabilities that
are far more capable and potentially much more efficient at runtime than the nested doll approach that MediatR (and MassTransit for that matter) take in 
its pipeline behavior model. 

::: tip
The Fluent Validation example is just about the most complicated middleware solution in Wolverine, but you can expect that
most custom middleware that you'd write in your own application would be much simpler.
:::

Let's just jump into an example. With MediatR, you might try to use a pipeline behavior to apply [Fluent Validation](https://docs.fluentvalidation.net/en/latest/)
to any handlers where there are Fluent Validation validators for the message type like [this sample](https://garywoodfine.com/how-to-use-mediatr-pipeline-behaviours/):

```csharp
    public class ValidationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        private readonly IEnumerable<IValidator<TRequest>> _validators;
        public ValidationBehaviour(IEnumerable<IValidator<TRequest>> validators)
        {
            _validators = validators;
        }
        public async Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken, RequestHandlerDelegate<TResponse> next)
        {
            if (_validators.Any())
            {
                var context = new ValidationContext<TRequest>(request);
                var validationResults = await Task.WhenAll(_validators.Select(v => v.ValidateAsync(context, cancellationToken)));
                var failures = validationResults.SelectMany(r => r.Errors).Where(f => f != null).ToList();
                if (failures.Count != 0)
                    throw new ValidationException(failures);
            }
            return await next();
        }
    }
```

It's cheating a little bit, because Wolverine has both an add on for incorporating [Fluent Validation middleware for message handlers](/guide/handlers/fluent-validation)
and a [separate one for HTTP usage](/guide/http/fluentvalidation) that relies on the [ProblemDetails](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.problemdetails?view=aspnetcore-9.0) specification for relaying validation errors. 
Let's still dive into how that works just to see how Wolverine really differs -- and why we think those differences matter 
for performance and also to keep exception stack traces cleaner (don't laugh, we really did design Wolverine quite purposely to 
avoid the really nasty kind of Exception stack traces you get from many other middleware or "behavior" using frameworks).

Let's say that you have a Wolverine.HTTP endpoint like so:

<!-- snippet: sample_CreateCustomer_endpoint_with_validation -->
<a id='snippet-sample_createcustomer_endpoint_with_validation'></a>
```cs
public record CreateCustomer
(
    string FirstName,
    string LastName,
    string PostalCode
)
{
    public class CreateCustomerValidator : AbstractValidator<CreateCustomer>
    {
        public CreateCustomerValidator()
        {
            RuleFor(x => x.FirstName).NotNull();
            RuleFor(x => x.LastName).NotNull();
            RuleFor(x => x.PostalCode).NotNull();
        }
    }
}

public static class CreateCustomerEndpoint
{
    [WolverinePost("/validate/customer")]
    public static string Post(CreateCustomer customer)
    {
        return "Got a new customer";
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/CreateCustomerEndpoint.cs#L7-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_createcustomer_endpoint_with_validation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the application bootstrapping, I've added this option:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // more configuration for HTTP...

    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();
}
```

Just like with MediatR, you would need to register the Fluent Validation validator types in your IoC container as part of
application bootstrapping. Now, here's how Wolverine's model is very different from MediatR's pipeline behaviors. While MediatR
is applying that `ValidationBehaviour` to each and every message handler in your application whether or not that message type
actually has any registered validators, Wolverine is able to peek into the IoC configuration and "know" whether there are registered
validators for any given message type. If there are any registered validators, Wolverine will utilize them in the code it 
generates to execute the HTTP endpoint method shown above for creating a customer. If there is only one validator, and that
validator is registered as a `Singleton` scope in the IoC container, Wolverine generates this code:

```csharp
    public class POST_validate_customer : Wolverine.Http.HttpHandler
    {
        private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;
        private readonly Wolverine.Http.FluentValidation.IProblemDetailSource<WolverineWebApi.Validation.CreateCustomer> _problemDetailSource;
        private readonly FluentValidation.IValidator<WolverineWebApi.Validation.CreateCustomer> _validator;

        public POST_validate_customer(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions, Wolverine.Http.FluentValidation.IProblemDetailSource<WolverineWebApi.Validation.CreateCustomer> problemDetailSource, FluentValidation.IValidator<WolverineWebApi.Validation.CreateCustomer> validator) : base(wolverineHttpOptions)
        {
            _wolverineHttpOptions = wolverineHttpOptions;
            _problemDetailSource = problemDetailSource;
            _validator = validator;
        }



        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            // Reading the request body via JSON deserialization
            var (customer, jsonContinue) = await ReadJsonAsync<WolverineWebApi.Validation.CreateCustomer>(httpContext);
            if (jsonContinue == Wolverine.HandlerContinuation.Stop) return;
            
            // Execute FluentValidation validators
            var result1 = await Wolverine.Http.FluentValidation.Internals.FluentValidationHttpExecutor.ExecuteOne<WolverineWebApi.Validation.CreateCustomer>(_validator, _problemDetailSource, customer).ConfigureAwait(false);

            // Evaluate whether or not the execution should be stopped based on the IResult value
            if (result1 != null && !(result1 is Wolverine.Http.WolverineContinue))
            {
                await result1.ExecuteAsync(httpContext).ConfigureAwait(false);
                return;
            }


            
            // The actual HTTP request handler execution
            var result_of_Post = WolverineWebApi.Validation.ValidatedEndpoint.Post(customer);

            await WriteString(httpContext, result_of_Post);
        }

    }
```

The point here is that Wolverine is trying to generate the most efficient code possible based on what it can glean from the IoC
container registrations and the signature of the HTTP endpoint or message handler methods. The MediatR model has to effectively
use runtime wrappers and conditional logic at runtime.

Do note that Wolverine has built in middleware for logging, validation, and transactional middleware out of the box. Most
of the custom middleware that folks are building for Wolverine are much simpler than the validation middleware I talked about
in this guide.

## Vertical Slice Architecture

MediatR is almost synonymous with the "Vertical Slice Architecture" (VSA) approach in .NET circles, but Wolverine arguably enables
a much lower ceremony version of VSA. The typical approach you'll see is folks delegating to MediatR commands or queries
from either an MVC Core `Controller` like this ([stolen from this blog post](https://dev.to/ifleonardo_/agile-and-modular-development-with-vertical-slice-architecture-and-mediatr-in-c-projects-3p4o)):

```csharp
public class AddToCartRequest : IRequest<Result>
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public class AddToCartHandler : IRequestHandler<AddToCartRequest, Result>
{
    private readonly ICartService _cartService;

    public AddToCartHandler(ICartService cartService)
    {
        _cartService = cartService;
    }

    public async Task<Result> Handle(AddToCartRequest request, CancellationToken cancellationToken)
    {
        // Logic to add the product to the cart using the cart service
        bool addToCartResult = await _cartService.AddToCart(request.ProductId, request.Quantity);

        bool isAddToCartSuccessful = addToCartResult; // Check if adding the product to the cart was successful.
        return Result.SuccessIf(isAddToCartSuccessful, "Failed to add the product to the cart."); // Return failure if adding to cart fails.
    }
    
public class CartController : ControllerBase
{
    private readonly IMediator _mediator;

    public CartController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    public async Task<IActionResult> AddToCart([FromBody] AddToCartRequest request)
    {
        var result = await _mediator.Send(request);

        if (result.IsSuccess)
        {
            return Ok("Product added to the cart successfully.");
        }
        else
        {
            return BadRequest(result.ErrorMessage);
        }
    }
}

```

While the introduction of MediatR probably is a valid way to sidestep the common code bloat from MVC Core Controllers, with
Wolverine we'd recommend just using the [Wolverine.HTTP](/guide/http) mechanism for writing HTTP endpoints in a much lower ceremony way
and ditch the "mediator" step altogether. Moreover, we'd even go so far as to drop repository and domain service layers and
just put the functionality right into an HTTP endpoint method if that code isn't going to be reused any where else in your application.

::: tip
See [Automatically Loading Entities to Method Parameters](https://wolverinefx.net/guide/handlers/persistence.html#automatically-loading-entities-to-method-parameters) for some context around that `[Entity]`
attribute usage
:::

So something like this:

```csharp
public static class AddToCartRequestEndpoint
{
    // Remember, we can do validation in middleware, or
    // even do a custom Validate() : ProblemDetails method
    // to act as a filter so the main method is the happy path
    
    [WolverinePost("/api/cart/add")]
    public static Update<Cart> Post(
        AddToCartRequest request, 
        
        // See 
        [Entity] Cart cart)
    {
        return cart.TryAddRequest(request) ? Storage.Update(cart) : Storage.Nothing(cart);
    }
}
```

We of course believe that Wolverine is more optimized for Vertical Slice Architecture than MediatR or any other "mediator"
tool by how Wolverine can reduce the number of moving parts, layers, and code ceremony.


## IoC Usage

Just know that [Wolverine has a very different relationship with your application's IoC container](/guide/runtime.html#ioc-container-integration) than MediatR. 
Wolverine's philosophy all along has been to keep the usage of IoC service location at runtime to a bare minimum. Instead, Wolverine
wants to mostly use the IoC tool as a service registration model at bootstrapping time.
