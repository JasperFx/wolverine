using System;
using System.Linq;
using Baseline;
using Lamar;
using Lamar.IoC;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace TestingSupport
{
        public static class ContainerExtensions
    {
        public static IContainer DefaultRegistrationIs<T, TConcrete>(this IContainer container) where TConcrete : T
        {
            container.Model.DefaultTypeFor<T>().ShouldBe(typeof(TConcrete));
            return container;
        }

        public static IContainer DefaultRegistrationIs(this IContainer container, Type pluginType, Type concreteType)
        {
            container.Model.DefaultTypeFor(pluginType).ShouldBe(concreteType);

            return container;
        }

        public static IContainer DefaultRegistrationIs<T>(this IContainer container, T value) where T : class
        {
            container.Model.For<T>().Default.Instance.Resolve(container.As<Scope>()).ShouldBeSameAs(value);

            return container;
        }

        public static IContainer DefaultSingletonIs(this IContainer container, Type pluginType, Type concreteType)
        {
            container.DefaultRegistrationIs(pluginType, concreteType);
            container.Model.For(pluginType).Default.Lifetime.ShouldBe(ServiceLifetime.Singleton);

            return container;
        }

        public static IContainer DefaultSingletonIs<T, TConcrete>(this IContainer container) where TConcrete : T
        {
            container.DefaultRegistrationIs<T, TConcrete>();
            container.Model.For<T>().Default.Lifetime.ShouldBe(ServiceLifetime.Singleton);

            return container;
        }

        public static IContainer ShouldHaveRegistration<T, TConcrete>(this IContainer container)
        {
            var plugin = container.Model.For<T>();
            plugin.Instances.Any(x => x.ImplementationType == typeof(TConcrete)).ShouldBeTrue();

            return container;
        }

        public static IContainer ShouldNotHaveRegistration<T, TConcrete>(this IContainer container)
        {
            var plugin = container.Model.For<T>();
            plugin.Instances.Any(x => x.ImplementationType == typeof(TConcrete)).ShouldBeFalse();

            return container;
        }
    }
}
