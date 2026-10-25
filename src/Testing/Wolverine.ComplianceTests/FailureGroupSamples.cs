// Deliberately outside the Wolverine.* namespaces, which the default dead letter fingerprinter treats as
// framework code, so these frames stand in for application code
namespace FailureGroupSamples
{
    public static class PaymentProcessor
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void ChargeCard(string detail)
        {
            throw new InvalidOperationException($"Card was declined: {detail}");
        }
    }

    public static class Shipping
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void ShipOrder(string detail)
        {
            throw new InvalidOperationException($"Nothing left in stock: {detail}");
        }
    }

    public static class Failures
    {
        /// <summary>
        /// Run the action and return the exception it threw, with its stack trace
        /// </summary>
        public static Exception Capture(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                return e;
            }

            throw new InvalidOperationException("The action did not throw");
        }

        public static Exception CardDeclined(string detail) => Capture(() => PaymentProcessor.ChargeCard(detail));

        public static Exception OutOfStock(string detail) => Capture(() => Shipping.ShipOrder(detail));
    }
}
