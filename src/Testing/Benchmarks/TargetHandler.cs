using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TestMessages;

namespace Benchmarks
{
    public static class TargetHandler
    {
        private static int _count = 0;
        private static TaskCompletionSource<int> _waiter;
        private static int _number;

        public static Task WaitForNumber(int number, TimeSpan timeout)
        {
            _waiter = new TaskCompletionSource<int>();
            _count = 0;
            _number = number;

            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(timeout);
            cancellation.Token.Register(() => _waiter.TrySetException(new TimeoutException()));


            return _waiter.Task;
        }

        public static void Increment()
        {
            Interlocked.Increment(ref _count);
            if (_count >= _number)
            {
                _waiter?.TrySetResult(_count);
            }
        }

        public static void Handle(Target target)
        {
            long sum = 0;
            foreach (var child in target.Children ?? Array.Empty<Target>())
            {
                sum += child.Number;
            }

            Increment();
        }
    }
}
