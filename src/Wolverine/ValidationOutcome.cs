using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine;

public record WolverineValidationResult(string Key, string ValidationMessage);

/// <summary>
/// The validation messages grouped by key. Validation will fail if not empty.
/// </summary>
public class ValidationOutcome : List<WolverineValidationResult>, IWolverineReturnType, INotToBeRouted
{
    public ValidationOutcome(){ }

    public ValidationOutcome(IEnumerable<WolverineValidationResult> collection) : base(collection)
    {
        
    }

    public bool IsValid() => Count == 0;
}