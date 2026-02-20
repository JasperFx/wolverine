using MediatR;
using Wolverine.Attributes;

namespace Wolverine.Migrations.MediatR.Tests;

[WolverineHandler]
public class RequestWithResponseHandlerWithMediatR : IRequestHandler<RequestWithResponse, Response>
{
    public Task<Response> Handle(RequestWithResponse request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Response($"passed: {request.Data}", "MediatR"));
    }
}

[WolverineHandler]
public class RequestWithoutResponseHandler : IRequestHandler<RequestWithoutResponse>
{
    public Task Handle(RequestWithoutResponse request, CancellationToken cancellationToken)
    {
        // Just process and return
        return Task.CompletedTask;
    }
}


[WolverineHandler]
public class RequestCascadeHandler : IRequestHandler<RequestCascade, CascadingMessage>
{
    public async Task<CascadingMessage> Handle(RequestCascade request, CancellationToken cancellationToken)
    {
        return new CascadingMessage(request.Data);
    }
}

[WolverineHandler]
public class RequestAdditionHandler(IAdditionService service) : IRequestHandler<RequestAdditionFromService, int>
{
    public async Task<int> Handle(RequestAdditionFromService request, CancellationToken cancellationToken)
    {
        return service.Process(request.number);
    }

}
