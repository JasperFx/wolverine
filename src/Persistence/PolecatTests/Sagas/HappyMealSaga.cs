using Wolverine.Persistence.Sagas;

namespace PolecatTests.Sagas;

public class PcSodaRequested
{
    [SagaIdentity] public int OrderId { get; set; }
}

public class PcSodaFetched
{
    [SagaIdentity] public int OrderId { get; set; }
}

public class PcSodaHandler
{
    public PcSodaFetched Handle(PcSodaRequested requested)
    {
        return new PcSodaFetched { OrderId = requested.OrderId };
    }
}

public class PcHappyMealOrder
{
    public string? Drink { get; set; }
    public string? Toy { get; set; }
    public string? SideDish { get; set; }
    public string? MainDish { get; set; }
}

public class PcToyOnTray
{
    [SagaIdentity] public int OrderId { get; set; }
}

public class PcBurgerReady
{
    public int SagaId { get; set; }
}

public class PcHappyMealSaga3 : Wolverine.Saga
{
    private int _orderIdSequence;

    public int Id { get; set; }
    public PcHappyMealOrder? Order { get; set; }

    public bool DrinkReady { get; set; }
    public bool ToyReady { get; set; }
    public bool SideReady { get; set; }
    public bool MainReady { get; set; }

    public bool IsOrderComplete()
    {
        return DrinkReady && ToyReady && SideReady && MainReady;
    }

    public object[] Starts(PcHappyMealOrder order)
    {
        Order = order;
        Id = ++_orderIdSequence;

        return chooseActions(order, Id).ToArray();
    }

    public void Handle(PcSodaFetched soda)
    {
        DrinkReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    public void Handle(PcToyOnTray toyReady)
    {
        ToyReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    public void Handle(PcBurgerReady burgerReady)
    {
        MainReady = true;
        if (IsOrderComplete())
        {
            MarkCompleted();
        }
    }

    private IEnumerable<object> chooseActions(PcHappyMealOrder order, int stateId)
    {
        if (order.Drink == "Soda")
        {
            yield return new PcSodaRequested { OrderId = stateId };
        }
    }
}
