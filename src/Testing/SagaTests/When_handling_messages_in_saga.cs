using Alba;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace SagaTests;

public class When_handling_messages_in_saga
{
  [Fact]
  public async Task should_not_throw()
  {
    await using var host =
      await Host.CreateDefaultBuilder()
        .UseWolverine()
        .StartAlbaAsync();

    var subscriptionId = Guid.NewGuid();

    await Should.NotThrowAsync(
      () => host.InvokeMessageAndWaitAsync(
        new Registered(
          "ACME, Inc",
          "Jane",
          "Doe",
          "jd@acme.inc",
          subscriptionId.ToString()
        )
      )
    );
  }
}