using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaTests;

public class SodaRequested
{
    [SagaIdentity] public int OrderId { get; set; }
}

public class SodaFetched
{
    [SagaIdentity] public int OrderId { get; set; }
}

public class SodaHandler
{
    public SodaFetched Handle(
        SodaRequested requested
    )
    {
        // get the soda, then return the update message
        return new SodaFetched() { OrderId = requested.OrderId };
    }
}

public interface IOrderService
{
    void Close(
        int order
    );
}

public class HappyMealOrder
{
    public string Drink { get; set; }
    public string Toy { get; set; }
    public string SideDish { get; set; }
    public string MainDish { get; set; }
}

public class ToyOnTray
{
    // There's always *some* reason to deviate,
    // so you can use this attribute to tell Wolverine
    // that this property refers to the Id of the
    // Saga state document
    [SagaIdentity] public int OrderId { get; set; }
}

public class BurgerReady
{
    // By default, Wolverine is going to look for a property
    // called SagaId as the identifier for the stateful
    // document
    public int SagaId { get; set; }
}

public class HappyMealSaga3 : Saga
{
    private int _orderIdSequence;

    // Wolverine wants you to make the saga state
    // document have an "Id" property, but
    // that can be overridden
    public int Id { get; set; }
    public HappyMealOrder Order { get; set; }

    public bool DrinkReady { get; set; }
    public bool ToyReady { get; set; }
    public bool SideReady { get; set; }
    public bool MainReady { get; set; }

    // The order is complete if *everything*
    // is complete
    public bool IsOrderComplete()
    {
        return DrinkReady && ToyReady && SideReady && MainReady;
    }


    // This is a little bit cute, but the HappyMealOrderState type
    // is known to be the saga state document, so it'll be treated as
    // the state document, while the object[] will be treated as
    // cascading messages
    public object[] Starts(
        HappyMealOrder order
    )
    {
        Order = order;
        Id = ++_orderIdSequence;

        return chooseActions(order, Id)
            .ToArray();
    }

    public void Handle(
        SodaFetched soda // The first argument is the message type
        // IOrderService service // Additional arguments are injected services
    )
    {
        DrinkReady = true;

        // Determine if the happy meal is completely ready
        if (IsOrderComplete())
        {
            // Maybe you need to remove this
            // order from some kind of screen display
            // service.Close(Id);

            // And we're done here, so let's mark the Saga as complete
            MarkCompleted();
        }
    }

    public void Handle(
        ToyOnTray toyReady
    )
    {
        ToyReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }


    public void Handle(
        BurgerReady burgerReady
    )
    {
        MainReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    private IEnumerable<object> chooseActions(
        HappyMealOrder order,
        int stateId
    )
    {
        // choose the outgoing messages to other systems -- or the local
        // system tracking all this -- to start having this happy meal
        // order put together

        if (order.Drink == "Soda")
        {
            yield return new SodaRequested { OrderId = stateId };
        }

        // and others
    }
}
