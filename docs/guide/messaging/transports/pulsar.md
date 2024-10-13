# Using Pulsar <Badge type="tip" text="3.0" />

::: info
Fun fact, the Pulsar transport was actually the very first messaging broker to be supported
by Jasper/Wolverine, but for whatever reason, wasn't officially released until Wolverine 3.0. 
:::

## Installing

To use [Apache Pulsar](https://pulsar.apache.org/) as a messaging transport with Wolverine, first install the `WolverineFx.Pulsar` library via nuget to your project. Behind the scenes, this package uses the [DotPulsar client library](https://pulsar.apache.org/docs/next/client-libraries-dotnet/) managed library for accessing Pulsar brokers.

```bash
dotnet add WolverineFx.Pulsar
```

To connect to Pulsar and configure senders and listeners, use this syntax:

snippet: sample_configuring_pulsar

The topic name format is set by Pulsar itself, and you can learn more about its format in [Pulsar Topics](https://pulsar.apache.org/docs/next/concepts-messaging/#topics). 

::: info
Depending on demand, the Pulsar transport will be enhanced to support conventional routing topologies and more advanced
topic routing later.
::: 