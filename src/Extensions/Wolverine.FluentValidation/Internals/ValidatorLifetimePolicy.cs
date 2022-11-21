using FluentValidation;
using Lamar;
using Lamar.IoC.Instances;
using LamarCodeGeneration.Util;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.FluentValidation.Internals;

internal class ValidatorLifetimePolicy : IInstancePolicy
{
    public void Apply(Instance instance)
    {
        if (instance.ServiceType.Closes(typeof(IValidator<>)))
        {
            if (instance is ConstructorInstance i)
            {
                var hasNonDefaultConstructor = i.ImplementationType
                    .GetConstructors()
                    .Any(x => x.GetParameters().Any());

                instance.Lifetime = hasNonDefaultConstructor ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;
            }
            else
            {
                instance.Lifetime = ServiceLifetime.Scoped;
            }
        }
    }
}