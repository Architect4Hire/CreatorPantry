extern alias ApiService;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CreatorPantry.Tests.Auth;

public class AccountMessageDeliveryTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Api_refuses_to_start_outside_development_without_a_message_provider(string environment)
    {
        using var factory = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);

            // Message delivery is the missing provider under test, so every other provider this host demands
            // outside Development has to be present — otherwise the model deployments' own startup failure
            // gets there first and this test passes on the wrong exception.
            TestDatabase.ConfigureModelDeployments(web);

            web.UseEnvironment(environment);
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("No account message delivery is configured", error.Message);
    }
}
