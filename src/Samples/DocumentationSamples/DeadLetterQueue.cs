using Wolverine.Persistence.Durability;

namespace DocumentationSamples
{
    public class DeadLetterQueue
    {
        #region sample_FetchErrorReport
        public async Task load_error_report(IEnvelopePersistence persistence, Guid envelopeId)
        {
            var report = await persistence.LoadDeadLetterEnvelopeAsync(envelopeId);

            // The Id
            Console.WriteLine(report.Id);

            // Why it was moved out
            Console.WriteLine(report.Explanation);

            // The underlying message typ
            Console.WriteLine(report.Envelope.MessageType);

            // The name ofthe system that sent the message
            Console.WriteLine(report.Envelope.Source);

            // The .Net Exception type name
            Console.WriteLine(report.ExceptionType);

            // Just the message of the exception
            Console.WriteLine(report.ExceptionMessage);

            // JUST SHOW ME THE FULL STACKTRACE ALREADY!!!!
            Console.WriteLine(report.ExceptionText);
        }

        #endregion
    }
}
