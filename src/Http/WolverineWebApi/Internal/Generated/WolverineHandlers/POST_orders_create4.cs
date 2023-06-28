using Marten;
using Marten.Exceptions;
using Wolverine;
using Wolverine.Http;
using WolverineWebApi.Marten;

namespace Internal.Generated.WolverineHandlers;

// START: POST_orders_create4
public class POST_orders_create4 : HttpHandler
{
    private readonly WolverineHttpOptions _options;
    private readonly ISessionFactory _sessionFactory;

    public POST_orders_create4(WolverineHttpOptions options, ISessionFactory sessionFactory) : base(options)
    {
        _options = options;
        _sessionFactory = sessionFactory;
    }

    public override async Task Handle(HttpContext httpContext)
    {
        await using var documentSession = _sessionFactory.OpenSession();
        try
        {
            var (command, jsonContinue) = await ReadJsonAsync<StartOrderWithId>(httpContext);
            if (jsonContinue == HandlerContinuation.Stop)
            {
                return;
            }

            var (orderStatus, startStream) = MarkItemEndpoint.StartOrder4(command);

            // Placed by Wolverine's ISideEffect policy
            startStream.Execute(documentSession);


            // Commit any outstanding Marten changes
            await documentSession.SaveChangesAsync(httpContext.RequestAborted).ConfigureAwait(false);

            await WriteJsonAsync(httpContext, orderStatus);
        }
        catch (ExistingStreamIdCollisionException e)
        {
            await StreamCollisionExceptionPolicy.RespondWithProblemDetails(e, httpContext);
        }
    }
}

// END: POST_orders_create4