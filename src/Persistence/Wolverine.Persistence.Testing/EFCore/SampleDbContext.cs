using Microsoft.EntityFrameworkCore;

namespace Wolverine.Persistence.Testing.EFCore;

public class SampleDbContext : DbContext
{
    private readonly DbContextOptions<SampleDbContext> _options;

    public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options)
    {
        _options = options;
    }
}
