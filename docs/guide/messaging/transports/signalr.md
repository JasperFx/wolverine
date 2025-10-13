# Using SignalR <Badge type="tip" text="5.0" />

::: info
The SignalR transport has been requested several times, but finally got built specifically for the forthcoming
"CritterWatch" product that will be used to monitor and manage Wolverine applications. In other words, the Wolverine
team has heavily dog-fooded this feature.
:::


SignalR from Microsoft isn't hard to use from Wolverine for simplistic things, but what if you want a server side
application to exchange any number of different messages between a browser (or other WebSocket client because that's
actually possible) and your server side code? To that end, Wolverine now supports a first class messaging transport
for SignalR. To get started, just add a Nuget reference to the `WolverineFx.SignalR` library:

```bash
dotnet add package WolverineFx.SignalR
```



* How to register
* Access to HubOptions
* Routing messages to SignalR
* Sending to the current connection
* Sending to a group
* Tracking for replies
* Side effect to add or remove a connection to a group
* Using the SignalRClient for testing
* Using from browser