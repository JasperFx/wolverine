using JasperFx;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

#region sample_concurrency_exception_with_onexception

// This endpoint handles the Marten concurrency failure itself with the OnException
// convention, so the UseProblemDetailsForConcurrencyExceptions() policy leaves
// its catch block completely alone
public static class HandleConcurrencyExceptionYourselfEndpoint
{
    [AggregateHandler]
    [WolverinePost("/orders/itemready/custom-handled")]
    public static (OrderStatus, Events) Post(MarkItemReady command, Order order)
    {
        return (new OrderStatus(order.Id, order.IsReadyToShip()), [new ItemReady(command.ItemName)]);
    }

    public static ProblemDetails OnException(ConcurrencyException ex)
    {
        return new ProblemDetails
        {
            Status = 400,
            Title = "Somebody else got there first",
            Detail = ex.Message
        };
    }
}

#endregion
