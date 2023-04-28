using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Wolverine.EntityFrameworkCore.Internals;

public class WolverineModelCustomizer : RelationalModelCustomizer
{
    public WolverineModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.MapWolverineEnvelopeStorage();
    }
}