using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using TeleHealth.Common;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

namespace TeleHealth.WebApi;

public record CompleteCharting(
    Guid ProviderShiftId,
    
    // This version is meant to mean "I was issued
    // assuming that the ProviderShift is currently
    // at this version in the server, and if the version
    // has shifted since, then this command is now invalid"
    int Version
);

public record ChartingResponse(ProviderStatus Status);

public record StartProviderShift(Guid BoardId, Guid ProviderId);
public record ShiftStartingResponse(Guid ShiftId) : CreationResponse("/shift/" + ShiftId);

public static class StartProviderShiftEndpoint
{
    // This would be called before the method below
    public static async Task<(Board, Provider, IResult)> LoadAsync(StartProviderShift command, IQuerySession session)
    {
        // You could get clever here and batch the queries to Marten
        // here, but let that be a later optimization step
        var board = await session.LoadAsync<Board>(command.BoardId);
        var provider = await session.LoadAsync<Provider>(command.ProviderId);

        if (board == null || provider == null) return (board, provider, Results.BadRequest());

        // This just means "full speed ahead"
        return (board, provider, WolverineContinue.Result());
    }

    // Validate or ValidateAsync() is considered by Wolverine to be a "before" method
    public static IResult Validate(Provider provider, Board board)
    {
        // Check if you can proceed to add the provider to the board
        // This logic is out of the scope of this sample:)
        if (provider.CanJoin(board))
        {
            // Again, this value tells Wolverine to keep processing
            // the HTTP request
            return WolverineContinue.Result();
        }

        // No soup for you!
        var problems = new ProblemDetails
        {
            Detail = "Provider is ineligible to join this Board",
            Status = 400,
            Extensions =
            {
                [nameof(StartProviderShift.ProviderId)] = provider.Id,
                [nameof(StartProviderShift.BoardId)] = board.Id
            }
        };

        // Wolverine will execute this IResult
        // and stop all other HTTP processing
        return Results.Problem(problems);
    }

    [WolverinePost("/shift/start")]
    // In the tuple that's returned below,
    // The first value of ShiftStartingResponse is assumed by Wolverine to be the
    // HTTP response body
    // The subsequent IStartStream value is executed as a side effect by Wolverine
    public static (ShiftStartingResponse, IStartStream) Create(StartProviderShift command, Board board, Provider provider)
    {
        var started = new ProviderJoined(board.Id, provider.Id);
        var op = MartenOps.StartStream<ProviderShift>(started);

        return (new ShiftStartingResponse(op.StreamId), op);
    }
}

public class ProviderShiftEndpoint
{
    [WolverineGet("/shift/{shiftId}")]
    public Task GetProviderShift(Guid shiftId, IQuerySession session, HttpContext context)
    {
        return session.Json.WriteById<ProviderShift>(shiftId, context);
    }

    [WolverinePost("/shift/charting/complete")]
    [AggregateHandler]
    public (ChartingResponse ,ChartingFinished) CompleteCharting(
        CompleteCharting charting,
        ProviderShift shift)
    {
        if (shift.Status != ProviderStatus.Charting)
        {
            throw new Exception("The shift is not currently charting");
        }

        return (
            // The HTTP response body
            new ChartingResponse(ProviderStatus.Paused),

            // An event to be appended to the ProviderShift aggregate event stream
            new ChartingFinished()
        );
    }
}

// This is auto-discovered by Wolverine
public class CompleteChartingHandler
{
    // Decider pattern
    [AggregateHandler(ConcurrencyStyle.Exclusive)] // this opts into some Wolverine middleware
    public ChartingFinished Handle(CompleteCharting charting, ProviderShift shift)
    {
        if (shift.Status != ProviderStatus.Charting)
        {
            throw new Exception("The shift is not currently charting");
        }

        return new ChartingFinished();
    }
}


// Since we're focusing on Marten, I'm using an MVC Core
// controller just because it's commonly used and understood
public class CompleteChartingController : ControllerBase
{
    [HttpPost("/provider/charting/complete")]
    public async Task Post(
        [FromBody] CompleteCharting charting,
        [FromServices] IDocumentSession session)
    {
// We're looking up the current state of the ProviderShift aggregate
// for the designated provider
var stream = await session
    .Events
    .FetchForExclusiveWriting<ProviderShift>(
        charting.ProviderShiftId, 
        HttpContext.RequestAborted);

        // The current state
        var shift = stream.Aggregate;
        
        if (shift.Status != ProviderStatus.Charting)
        {
            // Obviously do something smarter in your app, but you 
            // get the point
            throw new Exception("The shift is not currently charting");
        }
        
        // Append a single new event just to say 
        // "charting is finished". I'm relying on 
        // Marten's automatic metadata to capture
        // the timestamp of this event for me
        stream.AppendOne(new ChartingFinished());

        // Commit the transaction
        await session.SaveChangesAsync();
    }
}