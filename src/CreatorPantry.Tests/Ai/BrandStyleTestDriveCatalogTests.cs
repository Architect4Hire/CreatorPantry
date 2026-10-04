using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.24's place in the task allow-list: that the server recognises its discriminator, that no deployment runs
/// it until one says so, and that the generic recipe-nested proposal route cannot start it.
/// </summary>
/// <remarks>
/// Unit tests over two pure tables. They are here rather than folded into the handler's own tests because they
/// are the guarantees that hold before a handler exists at all — a route that was deployed and a task that was
/// switched on are deliberately different facts.
/// </remarks>
public sealed class BrandStyleTestDriveCatalogTests
{
    /// <summary>The discriminator a client sends, and the one task it may name.</summary>
    [Fact]
    public void The_discriminator_resolves_to_the_test_drive_and_nothing_else()
    {
        Assert.Equal(
            AiTaskType.BrandStyleTestDrive,
            AiTaskCatalog.Resolve(AiTaskCatalog.BrandStyleTestDrive));
        Assert.Contains(AiTaskCatalog.BrandStyleTestDrive, AiTaskCatalog.Known);
    }

    /// <summary>
    /// Two short generations are not free, so the capability ships dark like every other: being deployed is not
    /// being switched on.
    /// </summary>
    [Fact]
    public void It_is_not_enabled_until_a_deployment_opts_in()
    {
        var unconfigured = AiTaskCatalog.Bind(new ConfigurationBuilder().Build());

        Assert.False(AiTaskCatalog.IsEnabled(unconfigured, AiTaskCatalog.BrandStyleTestDrive));

        var configured = AiTaskCatalog.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AiTaskOptions.SectionName}:Enabled:0"] = AiTaskCatalog.BrandStyleTestDrive,
            })
            .Build());

        Assert.True(AiTaskCatalog.IsEnabled(configured, AiTaskCatalog.BrandStyleTestDrive));
    }

    /// <summary>
    /// The guide version it is testing is required, not optional, so there is no reading of a request without
    /// it that would still be a test drive of anything — and the generic contract has nowhere to carry it.
    /// </summary>
    [Fact]
    public void The_generic_proposal_route_cannot_start_this_task()
    {
        Assert.True(AiTaskCatalog.RequiresTaskInputs(AiTaskType.BrandStyleTestDrive));
    }
}
