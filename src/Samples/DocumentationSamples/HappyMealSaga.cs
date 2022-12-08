using Wolverine;
using Wolverine.Persistence.Sagas;

namespace DocumentationSamples;

#region sample_HappyMealOrder

public class HappyMealOrder
{
    public string Drink { get; set; }
    public string Toy { get; set; }
    public string SideDish { get; set; }
    public string MainDish { get; set; }
}

#endregion

public class FetchDrink
{
    public string DrinkName { get; set; }
}

public class FetchFries
{
}

public class FetchToy
{
    public string ToyName { get; set; }
}

public class MakeHamburger
{
}

public class FetchChickenNuggets
{
}

public class SodaRequested
{
    public int OrderId { get; set; }
}

#region sample_HappyMealSaga1

/// <summary>
///     This is being done completely in memory, which you most likely wouldn't
///     do in "real" systems
/// </summary>
public class HappyMealSaga : Saga
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
    public object[] Starts(HappyMealOrder order)
    {
        Order = order;
        Id = ++_orderIdSequence;

        return chooseActions(order, Id).ToArray();
    }

    private IEnumerable<object> chooseActions(HappyMealOrder order, int stateId)
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

#endregion

public class HappyMealSagaNoTuple : Saga
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

    #region sample_HappyMealSaga1NoTuple

    public async Task Starts(
        HappyMealOrder order, // The first argument is assumed to be the message type
        IMessageBus context) // Additional arguments are assumed to be services
    {
        Order = order;
        Id = ++_orderIdSequence;

        if (order.Drink == "Soda")
        {
            await context.SendAsync(new SodaRequested { OrderId = Id });
        }

        // And other outgoing messages to coordinate gathering up the happy meal
    }

    #endregion
}

public class HappyMealSagaAllLocal : Saga
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

    #region sample_HappyMealSaga1Local

    public async Task Starts(
        HappyMealOrder order, // The first argument is assumed to be the message type
        IMessageBus context) // Additional arguments are assumed to be services
    {
        Order = order;
        Id = ++_orderIdSequence;

        if (order.Drink == "Soda")
        {
            await context.PublishAsync(new SodaRequested { OrderId = Id });
        }

        // And other outgoing messages to coordinate gathering up the happy meal
    }

    #endregion
}

#region sample_SodaHandler

// This message handler is in another system responsible for
// filling sodas
public class SodaHandler
{
    public SodaFetched Handle(SodaRequested requested)
    {
        // get the soda, then return the update message
        return new SodaFetched();
    }
}

#endregion

/// <summary>
///     This is being done completely in memory, which you most likely wouldn't
///     do in "real" systems
/// </summary>
public class HappyMealSaga2 : Saga
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
    public async Task Starts(HappyMealOrder order, IMessageBus bus)
    {
        Order = order;
        Id = ++_orderIdSequence;

        // You can explicitly call the IMessagePublisher or IMessageContext if you prefer
        if (order.Drink == "Soda")
        {
            await bus.SendAsync(new SodaRequested { OrderId = Id });
        }
    }
}

public interface IOrderService
{
    void Close(int order);
}

public class SodaFetched
{
}

#region sample_BurgerReady

public class BurgerReady
{
    // By default, Wolverine is going to look for a property
    // called SagaId as the identifier for the stateful
    // document
    public int SagaId { get; set; }
}

#endregion

#region sample_ToyOnTray

public class ToyOnTray
{
    // There's always *some* reason to deviate,
    // so you can use this attribute to tell Wolverine
    // that this property refers to the Id of the
    // Saga state document
    [SagaIdentity] public int OrderId { get; set; }
}

#endregion

public class HappyMealSaga3 : Saga
{
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

    #region sample_completing_saga

    public void Handle(
        SodaFetched soda, // The first argument is the message type
        IOrderService service // Additional arguments are injected services
    )
    {
        DrinkReady = true;

        // Determine if the happy meal is completely ready
        if (IsOrderComplete())
        {
            // Maybe you need to remove this
            // order from some kind of screen display
            service.Close(Id);

            // And we're done here, so let's mark the Saga as complete
            MarkCompleted();
        }
    }

    #endregion


    #region sample_passing_saga_state_id_through_message

    public void Handle(ToyOnTray toyReady)
    {
        ToyReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    public void Handle(BurgerReady burgerReady)
    {
        MainReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    #endregion
}