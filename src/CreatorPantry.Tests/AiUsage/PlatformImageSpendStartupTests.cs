extern alias ApiService;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The image spend ceiling is checked when the API starts, not when the first picture is asked for (AP.2.7).
/// </summary>
public sealed class PlatformImageSpendStartupTests
{
    private const string Setting = "Ai:Quota:PlatformImageSpendPerDay";

    /// <summary>
    /// Outside Development a host with no ceiling is the leak the breaker closes, so it does not start.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Api_refuses_to_start_outside_development_without_a_ceiling(string environment)
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);

            // The other providers a deployed host demands, so their own startup failures do not get there first.
            TestDatabase.ConfigureModelDeployments(web);

            web.UseSetting(Setting, string.Empty);
            web.UseEnvironment(environment);
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains($"{Setting} is not set", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ceiling that is set must be a usable one. Nought, a negative figure or a typo past the bound would
    /// otherwise start cleanly and then refuse every picture — or every AI request — at the first admission.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1000000.01")]
    public void Api_refuses_to_start_with_an_unusable_ceiling(string ceiling)
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.UseSetting(Setting, ceiling);
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Setting, error.ToString(), StringComparison.Ordinal);
    }
}
