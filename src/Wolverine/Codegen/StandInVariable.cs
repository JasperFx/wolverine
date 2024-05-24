using JasperFx.CodeGeneration.Model;

namespace Wolverine.Codegen;

internal class StandInVariable : Variable
{
    private Variable _inner;

    // TODO -- watch the ServiceType
    public StandInVariable(ServicePlan plan) : base(plan.ServiceType)
    {
        Plan = plan;
    }

    public ServicePlan Plan { get; }

    public override string Usage
    {
        get => _inner?.Usage;
        protected set
        {
            {
                base.Usage = value;
            }
        }
    }

    public void UseInner(Variable variable)
    {
        _inner = variable ?? throw new ArgumentNullException(nameof(variable));
        Dependencies.Add(variable);
    }

    public override void OverrideName(string variableName)
    {
        _inner.OverrideName(variableName);
    }

    public override int GetHashCode()
    {
        return _inner == null ? Plan.GetHashCode() : _inner.GetHashCode();
    }
}