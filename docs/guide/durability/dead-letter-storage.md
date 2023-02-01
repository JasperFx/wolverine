# Dead Letter Storage

If [message storage](../durability/index.md) is configured for your application, and you're using either the local queues or messaging
transports where Wolverine doesn't (yet) support native [dead letter queueing](https://en.wikipedia.org/wiki/Dead_letter_queue), Wolverine is actually moving messages
to the `wolverine_dead_letters` table in your database in lieu of native dead letter queueing. 

You can browse the messages in this table and see some of the exception details that led them to being moved
to the dead letter queue. To recover messages from the dead letter queue after possibly fixing a production support
issue, you can update this table's `replayable` column for any messages you want to recover with some kind of
SQL command like:

```sql
update wolverine_dead_letters set replayable = true where exception_type = 'InvalidAccountException';
```

When you do this, Wolverine's durability agent that manages the inbox and outbox processing in the background
will move these messages back into active incoming message handling. Just note that this process happens
through some polling, so it won't be instantaneous.

To replay dead lettered messages back to the incoming table, you also have a command line option:

```bash
dotnet run -- storage replay
```
