using Wolverine;

namespace TestingSupport.Sagas;

public abstract class Start<T>
{
    public T Id { get; set; }
    public string Name { get; set; }
}

public class GuidStart : Start<Guid>
{
}

public class IntStart : Start<int>
{
}

public class LongStart : Start<long>
{
}

public class StringStart : Start<string>
{
}

public abstract class CompleteThree<T>
{
    public T SagaId { get; set; }
}

public class GuidCompleteThree : CompleteThree<Guid>
{
}

public class IntCompleteThree : CompleteThree<int>
{
}

public class LongCompleteThree : CompleteThree<long>
{
}

public class StringCompleteThree : CompleteThree<string>
{
}

public class CompleteOne
{
}

public class CompleteTwo
{
}

public class CompleteFour
{
}

public class FinishItAll
{
}

public class WildcardStart
{
    public string Id { get; set; }
    public string Name { get; set; }
}

public class BasicWorkflow<TStart, TCompleteThree, TId> : Saga
    where TCompleteThree : CompleteThree<TId>
    where TStart : Start<TId>
{
    public TId Id { get; set; }

    public bool OneCompleted { get; set; }
    public bool TwoCompleted { get; set; }
    public bool ThreeCompleted { get; set; }
    public bool FourCompleted { get; set; }

    public string Name { get; set; }


    public void Start(TStart starting)
    {
        Id = starting.Id;
        Name = starting.Name;
    }

    public CompleteTwo Handle(CompleteOne one)
    {
        OneCompleted = true;
        return new CompleteTwo();
    }

    public void Handle(CompleteTwo message)
    {
        TwoCompleted = true;
    }

    public void Handle(CompleteFour message)
    {
        FourCompleted = true;
    }


    public void Handle(TCompleteThree three)
    {
        ThreeCompleted = true;
    }

    public void Handle(FinishItAll finish)
    {
        MarkCompleted();
    }
}