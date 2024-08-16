using Microsoft.EntityFrameworkCore;

public class WolverineReproDbContext : DbContext
{
    public WolverineReproDbContext(DbContextOptions<WolverineReproDbContext> options) : base(options) { }
}