namespace CoreTests.Codegen;

public interface IWidget{}

public class AWidget : IWidget{}
public class BWidget : IWidget{}

public class ServiceUsingWidget : IWidget
{
    public ServiceUsingWidget(IColor color)
    {
        Color = color;
    }

    public IColor Color { get; set; }
}

public interface ISingletonLambda{}
public class SingletonLambda : ISingletonLambda{}

public interface IScopedLambda{}
public class ScopedLambda : IScopedLambda{}

public interface ITransientLambda{}
public class TransientLambda : ITransientLambda{}

public interface IInternalSingleton{}
internal class InternalSingleton : IInternalSingleton{}

public interface IColor;
public class Red : IColor;



public interface IGenericSingleton<T>;
public class GenericSingleton<T> : IGenericSingleton<T>;

public interface IGenericScoped<T>;
public class GenericScoped<T> : IGenericScoped<T>;

public interface IUsesScopedLambda;
public class UsesScopedLambda : IUsesScopedLambda
{
    public UsesScopedLambda(IScopedLambda o)
    {
    }
}

public interface ITopThing;

public class TopWidget
{
    private readonly WidgetHolder _holder;

    public TopWidget(WidgetHolder holder)
    {
        _holder = holder;
    }
}

public class SimpleThing;

public class WidgetHolder
{
    public WidgetHolder(IWidget widget)
    {
        
    }
}

public class TopThing : ITopThing
{
    public TopThing(TopWidget top, IColor color)
    {
    }
}

public interface IRule;
public class Rule1 : IRule;
public class Rule2 : IRule;
public class Rule3 : IRule;
public class Rule4 : IRule;

public class RuleHolder1(IRule[] rules);
public class RuleHolder2(IEnumerable<IRule> rules);