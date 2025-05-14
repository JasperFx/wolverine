using Wolverine;

namespace SharedPersistenceModels;

public static class TestingOverrides
{
    public static IWolverineExtension? Extension { get; set; }
}