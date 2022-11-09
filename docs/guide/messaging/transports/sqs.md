# Amazon SQS

::: tip
Wolverine is only supporting SQS queues for right now, but support for publishing or subscribing through [Amazon SNS](https://aws.amazon.com/sns/) will
come shortly.
:::

Wolverine supports [Amazon SQS](https://aws.amazon.com/sqs/) as a messaging transport through the WolverineFx.AmazonSqs package.

## Connecting to the Broker

First, if you are using the [shared AWS config and credentials files](https://docs.aws.amazon.com/sdkref/latest/guide/file-format.html), the SQS connection is just this:

snippet: sample_simplistic_aws_sqs_setup


snippet: sample_config_aws_sqs_connection


If you'd just like to connect to Amazon SQS running from within [LocalStack](https://localstack.cloud/) on your development box,
there's this helper:

snippet: sample_connect_to_sqs_and_localstack

## Configuring Queues

## Listening to Queues

## Publishing to Queues

## Conventional Message Routing