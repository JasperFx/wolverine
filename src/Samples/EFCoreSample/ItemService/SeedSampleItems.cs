using Weasel.EntityFrameworkCore;

namespace ItemService;

#region sample_initial_data_seeder
/// <summary>
///     Seed data applied every time <c>host.ResetAllDataAsync&lt;ItemsDbContext&gt;()</c>
///     or <c>DatabaseCleaner&lt;ItemsDbContext&gt;.ResetAllDataAsync()</c> runs.
///     Multiple <see cref="IInitialData{TContext}" /> registrations execute in order.
/// </summary>
public class SeedSampleItems : IInitialData<ItemsDbContext>
{
    public static readonly Item[] Items =
    [
        new Item { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), Name = "Alpha" },
        new Item { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), Name = "Beta" }
    ];

    public async Task Populate(ItemsDbContext context, CancellationToken cancellation)
    {
        context.Items.AddRange(Items);
        await context.SaveChangesAsync(cancellation);
    }
}
#endregion
