# Interoperability

Hey, it's a complicated world and Wolverine is a relative newcomer, so it's somewhat likely you'll find yourself needing to make a Wolverine application talk via Azure Service Bus to
a non-Wolverine application. Not to worry (too much), Wolverine has you covered with the ability to customize Wolverine to Azure Service Bus mapping.

You can create interoperability with non-Wolverine applications by writing a custom `IAzureServiceBusEnvelopeMapper`
as shown in the following sample:

snippet: sample_custom_azure_service_bus_mapper

To apply that mapper to specific endpoints, use this syntax on any type of Azure Service Bus endpoint:

snippet: sample_configuring_custom_envelope_mapper_for_azure_service_bus~~~~