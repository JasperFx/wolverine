using Microsoft.Extensions.Hosting;

namespace PersistenceTests.Agents;

internal static class HostExtensions
{
    internal static Task<bool> WaitUntilAssignmentsChangeTo(this IHost expectedLeader,
        Action<AssignmentWaiter> configure, TimeSpan timeout)
    {
        var waiter = new AssignmentWaiter(expectedLeader);
        configure(waiter);

        return waiter.Start(timeout);
    }
}