using Microsoft.CodeAnalysis.Differencing;
using Xunit.Abstractions;

namespace CircuitBreakingTests;

public static class Recorder
{
    public static int Received = 0;
    private static TaskCompletionSource<int> _completion;
    private static int _expected;
    private static ITestOutputHelper _output;
    public static bool NeverFail { get; set; }

    public static Task WaitForMessagesToBeProcessed(ITestOutputHelper output, int number, TimeSpan timeout)
    {
        NeverFail = false;
        _output = output;
        Received = 0;
        _completion = new TaskCompletionSource<int>();
        _expected = number;

        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                $"Listener did not process the expected message count {number} in the time allowed. The actual was {Received}"));
        });

        return _completion.Task;
    }

    public static void Increment()
    {
        Interlocked.Increment(ref Received);

        if (Received % 50 == 0)
        {
            _output.WriteLine("Received " + Received);
        }

        if (Received >= _expected)
        {
            _completion.SetResult(Received);
        }

    }
}
