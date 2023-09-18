# Interoperability

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via AWS SQS to
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to Amazon SQS mapping.

## Receive Raw JSON

If you need to receive raw JSON from an upstream system *and* you can expect only one message type for the current
queue, you can do that with this option:

snippet: sample_receive_raw_json_in_sqs

Likewise, to send raw JSON to external systems, you have this option:

snippet: sample_publish_raw_json_in_sqs

## Advanced Interoperability

For any kind of advanced interoperability between Wolverine and any other kind of application communicating with your
Wolverine application using SQS, you can build custom implementations of the `ISqsEnvelopeMapper` like this one:

snippet: sample_custom_sqs_mapper

And apply this to any or all of your SQS endpoints with the configuration fluent interface as shown in this sample:

snippet: sample_apply_custom_sqs_mapping