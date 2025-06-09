using Shouldly;
using Wolverine.Http;

namespace WolverineWebApi;

public interface IThing;
public class RedThing : IThing;
public class BlueThing : IThing;
public class GreenThing : IThing;

public static class ThingEndpoint
{
    [WolverineGet("/thing/red")]
    public static string GetRed([FromKeyedServices("Red")] IThing thing)
    {
        thing.ShouldBeOfType<RedThing>();
        return "red";
    }
    
    [WolverineGet("/thing/blue")]
    public static string GetBlue([FromKeyedServices("Blue")] IThing thing)
    {
        thing.ShouldBeOfType<BlueThing>();
        return "blue";
    }
}