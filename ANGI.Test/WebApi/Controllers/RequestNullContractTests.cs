using System.Reflection;
using ANGI.Application;
using ANGI.WebApi;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Test.WebApi.Controllers
{
    /// <summary>
    /// MVC's implicit [Required] for non-nullable references is off (MvcOptions
    /// SuppressImplicitRequiredAttributeForNonNullableReferenceTypes), so an explicit JSON null reaches the
    /// validator. These tests make sure every validator then rejects it, so a null never reaches a use case.
    /// </summary>
    public sealed class RequestNullContractTests
    {
        private static readonly NullabilityInfoContext Nullability = new();

        /// <summary>Lists every non-nullable reference property of every Application request DTO a controller binds.</summary>
        public static TheoryData<Type, string> NonNullableRequestProperties()
        {
            var data = new TheoryData<Type, string>();
            foreach (var type in RequestDtoTypes().OrderBy(type => type.FullName))
            {
                foreach (var property in NonNullableReferenceProperties(type))
                {
                    data.Add(type, property.Name);
                }
            }

            return data;
        }

        // TEST-01: Keep implicit [Required] off, so required-field messages come from the validators, in Vietnamese.
        /// <summary>Verifies the MVC option is set by AddWebApi.</summary>
        [Fact]
        public void AddWebApi_ShouldSuppressImplicitRequiredForNonNullableReferences()
        {
            var services = new ServiceCollection();
            services.AddWebApi(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var options = services.BuildServiceProvider()
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<MvcOptions>>().Value;

            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes.Should().BeTrue();
        }

        // TEST-02: Every non-nullable reference property of a request DTO is reported by its validator, once, when null.
        /// <summary>Sets one property to null and expects exactly one validation error on that property.</summary>
        [Theory]
        [MemberData(nameof(NonNullableRequestProperties))]
        public async Task Validator_WithNullNonNullableProperty_ShouldReportIt(Type dtoType, string propertyName)
        {
            using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();
            var validator = (IValidator?)provider.GetService(typeof(IValidator<>).MakeGenericType(dtoType));
            validator.Should().NotBeNull($"{dtoType.Name} has non-nullable reference properties, so it needs a validator");

            var instance = Activator.CreateInstance(dtoType)!;
            dtoType.GetProperty(propertyName)!.SetValue(instance, null);
            var context = (IValidationContext)Activator.CreateInstance(
                typeof(ValidationContext<>).MakeGenericType(dtoType), instance)!;

            var result = await validator!.ValidateAsync(context);

            result.Errors.Where(error => error.PropertyName == propertyName).Should().ContainSingle(
                $"{dtoType.Name}.{propertyName} = null must be rejected once, before the use case runs");
        }

        /// <summary>Request DTOs bound by a controller action, with the DTOs nested inside them.</summary>
        private static IEnumerable<Type> RequestDtoTypes()
        {
            var found = new HashSet<Type>();
            var actionParameters = typeof(ANGI.WebApi.DependencyInjection).Assembly.GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .SelectMany(method => method.GetParameters());
            foreach (var parameter in actionParameters)
            {
                Collect(parameter.ParameterType, found);
            }

            return found;
        }

        private static void Collect(Type type, HashSet<Type> found)
        {
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Collect(argument, found);
                }

                return;
            }

            if (!type.IsClass || type == typeof(string) ||
                type.Namespace?.StartsWith("ANGI.Application.DTOs", StringComparison.Ordinal) != true ||
                !found.Add(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Collect(property.PropertyType, found);
            }
        }

        private static IEnumerable<PropertyInfo> NonNullableReferenceProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite && property.SetMethod!.IsPublic && !property.PropertyType.IsValueType)
                .Where(property => Nullability.Create(property).WriteState == NullabilityState.NotNull);
    }
}
