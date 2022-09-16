using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Alba;
using CommandBusSamples;
using Wolverine;
using Wolverine.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Xunit;

namespace SampleTests
{
    public class CommandBusTests
    {
        [Fact]
        public async Task can_post_without_errors()
        {
            OaktonEnvironment.AutoStartHost = true;
            using var host = await AlbaHost.For<Program>(x => { });

            var store = host.Services.GetRequiredService<IDocumentStore>();
            var reservation = new Reservation();

            await store.BulkInsertAsync(new[] { reservation });

            var bus = host.Services.GetRequiredService<ICommandBus>();
            await bus.InvokeAsync(new ConfirmReservation(reservation.Id));

        }

    }
}
