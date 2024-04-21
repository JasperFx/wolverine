# Event Subscriptions <Badge type="tip" text="2.2" />

::: tip
The older [Event Forwarding](./event-forwarding) feature is a subset of subscriptions, but happens at the time of event
capture whereas the event subscriptions are processed in strict order in a background process through Marten's [async daemon](https://martendb.io/events/projections/async-daemon.html)
subsystem. The **strong suggestion from the Wolverine team is to use one or the other approach, but not both in the same system**.
:::

Wolverine has the ability to extend Marten's [event subscription functionality]() to carry out message processing by Wolverine on
the events being captured by Marten in strict order. This new functionality works through Marten's [async daemon](https://martendb.io/events/projections/async-daemon.html)

There are easy recipes for processing events through Wolverine message handlers, and also for just publishing events
through Wolverine's normal message publishing to be processed locally or by being propagated through asynchronous messaging
to other systems. 

## Publish Events as Messages

TODO -- samples coming in the 2.3 wave

## Process Events as Messages in Strict Order

TODO -- samples coming in the 2.3 wave

## Custom Subscriptions

TODO -- samples coming in the 2.3 wave

## Using IoC Services in Subscriptions

TODO -- samples coming in the 2.3 wave

