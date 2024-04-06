using FluentValidation;
using JasperFx.Core.Reflection;
using Lamar;
using Lamar.IoC.Instances;
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
                    .Any(x => x.GetParameters().Length != 0);

                instance.Lifetime = hasNonDefaultConstructor ? ServiceLifetime.Scoped : ServiceLifetime.Singleton;
            }
            else
            {
                instance.Lifetime = ServiceLifetime.Scoped;
            }
        }
    }
}