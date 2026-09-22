using System.Reflection;

namespace CreatorPantry.Tests.Architecture;

public class DomainReferenceTests
{
    private static readonly string[] HostAssemblies =
    [
        "CreatorPantry.AppHost",
        "CreatorPantry.ServiceDefaults",
        "CreatorPantry.Gateway",
        "CreatorPantry.Web",
        "CreatorPantry.ApiService",
        "CreatorPantry.Worker",
        "CreatorPantry.MigrationService",
    ];

    [Fact]
    public void Domain_does_not_reference_any_host_project()
    {
        var domain = Assembly.Load("CreatorPantry.Domain");

        var hostReferences = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => HostAssemblies.Contains(name))
            .ToList();

        Assert.Empty(hostReferences);
    }
}
