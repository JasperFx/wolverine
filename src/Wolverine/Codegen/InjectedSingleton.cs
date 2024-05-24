using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Codegen;

public class InjectedSingleton : InjectedField
{
    public ServiceDescriptor Descriptor { get; }
    private bool _isOnlyOne;

    public InjectedSingleton(ServiceDescriptor descriptor) : base(descriptor.ServiceType)
    {
        Descriptor = descriptor;
    }
    
    public bool IsOnlyOne
    {
        private get => _isOnlyOne;
        set
        {
            _isOnlyOne = value;
            if (value)
            {
                var defaultArgName = DefaultArgName(VariableType);
                OverrideName("_" + defaultArgName);
                CtorArg = defaultArgName;
            }
        }
    }
}