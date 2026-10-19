using Marten;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=cqrs_minimal_api;Username=postgres;Password=postgres";

    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "cqrs_demo";

    // Student is Guid-keyed (the RESTful API expects Guid ids per the spec);
    // Marten's default Guid identity is fine here. UseNumericRevisions / HiLo
    // were dropped along with the old int-based contract.
    opts.Schema.For<CqrsMinimalApi.Student>().Index(x => x.Name);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints();

// Seed initial data
await SeedData(app);

await app.RunAsync();

static async Task SeedData(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

    var existing = await session.Query<CqrsMinimalApi.Student>().AnyAsync();
    if (!existing)
    {
        // Explicit ids so the seeded set is reproducible and stable across restarts.
        session.Store(
            new CqrsMinimalApi.Student { Id = Guid.NewGuid(), Name = "Tonny Blatt", Address = "123 Main St", Email = "tonny@example.com", DateOfBirth = new DateTime(1991, 10, 7) },
            new CqrsMinimalApi.Student { Id = Guid.NewGuid(), Name = "Anitta Goldman", Address = "456 Oak Ave", Email = "anitta@example.com", DateOfBirth = new DateTime(1975, 5, 31) },
            new CqrsMinimalApi.Student { Id = Guid.NewGuid(), Name = "Alan Ford", Address = "789 Pine Rd", Email = "alan@example.com", DateOfBirth = new DateTime(2000, 8, 26) },
            new CqrsMinimalApi.Student { Id = Guid.NewGuid(), Name = "Jim Beam", Address = "321 Elm St", Email = "jim@example.com", DateOfBirth = new DateTime(1984, 1, 12) },
            new CqrsMinimalApi.Student { Id = Guid.NewGuid(), Name = "Suzanne White", Address = "654 Birch Ln", Email = "suzanne@example.com", DateOfBirth = new DateTime(1992, 3, 10) }
        );
        await session.SaveChangesAsync();
    }
}
