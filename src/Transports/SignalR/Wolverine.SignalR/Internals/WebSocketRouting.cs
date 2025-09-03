using JasperFx.Core;
using Microsoft.AspNetCore.SignalR;

namespace Wolverine.SignalR.Internals;

internal class WebSocketRouting
{
    public static IClientProxyLocator? ParseLocator(string expression)
    {
        if (expression.IsEmpty()) return new All();
        if (expression.EqualsIgnoreCase("all")) return new All();
        
        var parts = expression.Split('=');
        if (parts.Length != 2) return new All();

        switch (parts[0])
        {
            case "Connection":
                return new Connection(parts[1].Trim());
            
            case "Group":
                return new Group(parts[1].Trim());
        }

        return new All();
    }
    
    internal interface IClientProxyLocator
    {
        IClientProxy Find<T>(IHubContext<T> context) where T : WolverineHub;
    }

    internal record Connection(string ConnectionId) : IClientProxyLocator
    {
        public IClientProxy Find<T>(IHubContext<T> context) where T : WolverineHub
        {
            return context.Clients.Client(ConnectionId);
        }

        public override string ToString()
        {
            return $"Connection={ConnectionId}";
        }
    }

    internal record All : IClientProxyLocator
    {
        public IClientProxy Find<T>(IHubContext<T> context) where T : WolverineHub
        {
            return context.Clients.All;
        }
    }

    internal record Group(string GroupName) : IClientProxyLocator
    {
        public IClientProxy Find<T>(IHubContext<T> context) where T : WolverineHub
        {
            return context.Clients.Group(GroupName);
        }

        public override string ToString()
        {
            return $"Group={GroupName}";
        }
    }
    
    // TODO -- flesh out with this: https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs?view=aspnetcore-9.0#the-clients-object
}

