using System.Reflection;
using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Managers.Storage;

namespace CreatorPantry.Tests.Architecture;

/// <summary>
/// Only a module's typed object gateway may hold the object store (15.2, SEC-005).
/// </summary>
/// <remarks>
/// The gateway is where a key is generated from server identifiers and re-checked against the resolved workspace
/// before storage is touched. A business class, a data layer, a controller or a worker holding the store directly
/// could hand it a key nobody checked — so the boundary is asserted rather than left to a comment on the interface.
/// </remarks>
public sealed class ObjectStoreBoundaryTests
{
    private static readonly string[] Hosts =
    [
        "CreatorPantry.Domain",
        "CreatorPantry.ApiService",
        "CreatorPantry.Worker",
        "CreatorPantry.AiProvider",
        "CreatorPantry.Gateway",
        "CreatorPantry.MigrationService",
    ];

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void Only_module_object_gateways_and_the_store_itself_take_the_object_store()
    {
        var holders = Hosts
            .Select(name => Assembly.Load(new AssemblyName(name)))
            .SelectMany(LoadableTypes)
            .Where(Holds)

            // A lambda or async state machine that captures the store is charged to the type that wrote it, so a
            // closure in a business class is still that business class holding the store.
            .Select(type => Author(type).FullName!)
            .Distinct()
            .Order()
            .ToList();

        // Non-vacuous: the four gateways that exist today are found.
        Assert.Contains("CreatorPantry.Domain.Modules.Media.Gateways.MediaAssetObjectGateway", holders);
        Assert.Contains("CreatorPantry.Domain.Modules.Brand.Gateways.BrandSourceObjectGateway", holders);

        Assert.All(holders, holder => Assert.True(
            holder.EndsWith("ObjectGateway", StringComparison.Ordinal)
                || holder.StartsWith("CreatorPantry.Domain.Managers.Storage.", StringComparison.Ordinal),
            $"{holder} takes IPrivateObjectStore. Only a module's *ObjectGateway may: it generates and checks keys."));
    }

    private static bool Holds(Type type) =>
        type.GetConstructors(Everything).Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IPrivateObjectStore)))
        || type.GetMethods(Everything).Any(method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(IPrivateObjectStore)))
        || type.GetFields(Everything).Any(field => field.FieldType == typeof(IPrivateObjectStore))
        || type.GetProperties(Everything).Any(property => property.PropertyType == typeof(IPrivateObjectStore));

    private static Type Author(Type type)
    {
        while (type.DeclaringType is { } outer && type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
        {
            type = outer;
        }

        return type;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }
}
