using SagaWorkflows.OrderApproval;
using SagaWorkflows.Subscriptions;

namespace SagaWorkflows.Providers;

// The storefront's payment gateway, its managers' inbox and the billing provider, in the providers service. Nothing to
// keep: the specifications judge the messages the sagas send them
public static class StorefrontHandler
{
    public static void Handle(RequestApproval request) { }

    public static PaymentProcessed Handle(ProcessPayment payment) => new(payment.OrderId);

    public static void Handle(NotifyCustomer notification) { }

    public static void Handle(ChargeCustomer charge) { }

    public static void Handle(SubscriptionEnded ended) { }
}
