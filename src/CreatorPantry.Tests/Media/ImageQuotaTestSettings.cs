using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The allowance configuration a fixture needs before it can generate a picture (AP.2.5).
/// </summary>
/// <remarks>
/// <para>
/// A picture with no credit rate is refused at admission and never generated, which is the right answer to a
/// deployment pointed at a model nobody priced and the wrong one for a fixture whose subject is something
/// else. <c>AddAiUsageModule</c> reads its rates from configuration, and a fixture that passes none gets an
/// empty table — so every suite that requests a generation hands it this.
/// </para>
/// <para>
/// The allowance is deliberately large. These fixtures are about generation, not about running out; the
/// suites that are about running out set their own.
/// </para>
/// </remarks>
internal static class ImageQuotaTestSettings
{
    /// <summary>What one fake picture costs, in credits.</summary>
    public const decimal RatePerImage = 10m;

    /// <summary>An allowance no fixture will exhaust by accident.</summary>
    public const decimal Allowance = 100_000m;

    /// <summary>
    /// Rates <paramref name="model"/> at <paramref name="ratePerImage"/> credits a picture, with
    /// <paramref name="allowance"/> credits a period to spend.
    /// </summary>
    public static IConfiguration For(
        string model = TestDatabase.FakeImageModel,
        decimal ratePerImage = RatePerImage,
        decimal allowance = Allowance) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Ai:Quota:ImageRates:{model}"] = Text(ratePerImage),
                [$"Ai:Cost:Images:{model}"] = "0.008",
                ["Ai:Quota:DefaultAllowance"] = Text(allowance),
            })
            .Build();

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
