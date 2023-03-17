using System.Diagnostics;
using CoreTests.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module1;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class HandlerGraphTests
{
    [Fact]
    public async Task can_find_the_message_type_by_the_message_type_name_of_one_of_its_interfaces()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.RegisterInteropMessageAssembly(typeof(IMessageAbstraction).Assembly);
            }).StartAsync();

        var graph = host.Services.GetRequiredService<HandlerGraph>();

        // Making sure the graph has the handler below, or this is all invalid
        graph.TryFindMessageType(typeof(ConcreteMessage).ToMessageTypeName(), out var concreteType).ShouldBeTrue();
        concreteType.ShouldBe(typeof(ConcreteMessage));
        
        var interfaceTypeName = typeof(IMessageAbstraction).ToMessageTypeName();
        graph.TryFindMessageType(interfaceTypeName, out var toBeSerializedType).ShouldBeTrue();
        toBeSerializedType.ShouldBe(typeof(ConcreteMessage));
        
        // And same using the attribute
        graph.TryFindMessageType(typeof(IMessageMarker).ToMessageTypeName(), out var markedType).ShouldBeTrue();
        markedType.ShouldBe(typeof(MarkedMessage));
    }
    
    
}

public interface IMessageMarker{}

[InteropMessage(typeof(IMessageMarker))]
public class MarkedMessage : IMessageMarker{}

public class MarkedMessageHandler
{
    public static void Handle(MarkedMessage message){}
}

public class ConcreteMessage : IMessageAbstraction
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class ConcreteMessageHandler
{
    public void Handle(ConcreteMessage message) => Debug.WriteLine("Hey");
}