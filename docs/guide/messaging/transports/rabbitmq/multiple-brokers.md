# Connecting to Multiple Brokers <Badge type="tip" text="3.0" />

If you have a need to exchange messages with multiple Rabbit MQ brokers from one application, you have the option
to add additional, named brokers identified by Wolverine's `BrokerName` identity. Here's the syntax to work with
extra, named brokers:

snippet: sample_configure_additional_rabbit_mq_broker

The `Uri` values for endpoints to the additional broker follows the same rules as the normal usage of the Rabbit MQ
transport, but the `Uri.Scheme` is the name of the additional broker. For example, connecting to a queue named
"incoming" at a broker named by `new BrokerName("external")` would be `external://queue/incoming`.