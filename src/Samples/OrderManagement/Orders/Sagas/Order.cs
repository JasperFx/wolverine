using Messages;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Orders.Sagas;

public enum OrderStatus
{
    Pending = 0,
    CreditReserved = 1,
    CreditLimitExceeded = 2,
    Approved = 3,
    Rejected = 4
}

public class Order : Saga
{
    public string? Id { get; set; }
    public OrderStatus OrderStatus { get; set; } = OrderStatus.Pending;

    public object[] Start(
        OrderPlaced orderPlaced,
        ILogger logger
    )
    {
        Id = orderPlaced.OrderId;
        logger.LogInformation("Order {OrderId} placed", Id);
        OrderStatus = OrderStatus.Pending;
        return
        [
            new ReserveCredit(
                orderPlaced.OrderId,
                orderPlaced.CustomerId,
                orderPlaced.Amount
            )
        ];
    }

    public object[] Handle(
        CreditReserved creditReserved,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.CreditReserved;
        logger.LogInformation("Credit reserver for Order {OrderId}", Id);
        return [new ApproveOrder(creditReserved.OrderId, creditReserved.CustomerId)];
    }

    public void Handle(
        OrderApproved orderApproved,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.Approved;
        logger.LogInformation("Order {OrderId} approved", Id);
    }

    public object[] Handle(
        CreditLimitExceeded creditLimitExceeded,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.CreditLimitExceeded;
        return [new RejectOrder(creditLimitExceeded.OrderId)];
    }

    public void Handle(
        OrderRejected orderRejected,
        ILogger logger
    )
    {
        OrderStatus = OrderStatus.Rejected;
        logger.LogInformation("Order {OrderId} rejected", Id);
        MarkCompleted();
    }
}