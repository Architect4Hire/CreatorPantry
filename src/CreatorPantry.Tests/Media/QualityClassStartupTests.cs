using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// A host whose plans cannot reach the quality a picture is made at by default does not start (AP.4.3, AP.4.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Media's check, not Billing's.</strong> <c>Ai:Images:DefaultClass</c> is this module's, and a request
/// that names no class is made at it — so "every plan has to allow my default" is Media asserting its own
/// precondition. It is also the direction that costs nothing: Media already depends on Billing, because a
/// picture request resolves the asking account's plan, where the reverse would be a new edge back and make the
/// two modules mutually dependent.
/// </para>
/// <para>
/// What it buys: admission can treat a request that names no class as never plan-refusable, which is what the
/// facade's remarks promise and what keeps every client shipped before the picker working.
/// </para>
/// </remarks>
public sealed class QualityClassStartupTests
{
    /// <summary>Two classes and a plan allowing the default; each case below breaks one thing about it.</summary>
    private static (string, string?)[] Coherent() =>
    [
        ("Ai:Images:DefaultClass", "Standard"),
        ("Ai:Images:Classes:Standard:Model", "a-model"),
        ("Ai:Images:Classes:High:Model", "a-model"),
        ("Billing:DefaultPlan", "pro"),
        ("Billing:Plans:0:Key", "pro"),
        ("Billing:Plans:0:Name", "Pro"),
        ("Billing:Plans:0:Credits", "18000"),
        ("Billing:Plans:0:DailyImageCap", "75"),
        ("Billing:Plans:0:Period", "Monthly"),
        ("Billing:Plans:0:RunningImages", "2"),
        ("Billing:Plans:0:QualityClasses:0", "standard"),
    ];

    [Fact]
    public void A_coherent_host_starts() =>
        Assert.Equal(GeneratedImageQualityClass.Standard, Classes(Coherent()).Value.DefaultClass);

    /// <summary>
    /// A plan stopping below the default would refuse every picture asked for without choosing a quality — the
    /// request every client made before the picker existed.
    /// </summary>
    [Fact]
    public void A_plan_that_does_not_allow_the_default_class_refuses_to_start()
    {
        var options = Classes([.. Coherent(), ("Billing:Plans:0:QualityClasses:0", "high")]);

        var error = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains("DefaultClass", string.Join(" ", error.Failures), StringComparison.Ordinal);
    }

    /// <summary>One plan short is enough: the refusal is about every plan, not the default one.</summary>
    [Fact]
    public void A_second_plan_that_does_not_allow_the_default_class_refuses_to_start()
    {
        var options = Classes(
        [
            .. Coherent(),
            ("Billing:Plans:1:Key", "trial"),
            ("Billing:Plans:1:Name", "Trial"),
            ("Billing:Plans:1:Credits", "1000"),
            ("Billing:Plans:1:DailyImageCap", "5"),
            ("Billing:Plans:1:Period", "Once"),
            ("Billing:Plans:1:RunningImages", "1"),
            ("Billing:Plans:1:TrialDays", "14"),
            ("Billing:Plans:1:QualityClasses:0", "draft"),
        ]);

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    /// <summary>The catalogue's spelling is matched, so a plan writing the landing page's form is accepted.</summary>
    [Theory]
    [InlineData("standard")]
    [InlineData("STANDARD")]
    [InlineData("  Standard  ")]
    public void A_plan_may_spell_the_default_class_however_the_catalogue_does(string spelling) =>
        Assert.Equal(
            GeneratedImageQualityClass.Standard,
            Classes([.. Coherent(), ("Billing:Plans:0:QualityClasses:0", spelling)]).Value.DefaultClass);

    /// <summary>With no plans there is nothing to hold to the default: every deployment today.</summary>
    [Fact]
    public void Quality_classes_with_no_plans_start()
    {
        var options = Classes(
        [
            ("Ai:Images:DefaultClass", "Standard"),
            ("Ai:Images:Classes:Standard:Model", "a-model"),
        ]);

        Assert.Equal(GeneratedImageQualityClass.Standard, options.Value.DefaultClass);
    }

    /// <summary>And a host offering no classes has no default to allow.</summary>
    [Fact]
    public void A_host_that_offers_no_quality_classes_starts()
    {
        var plans = Coherent().Where(setting => !setting.Item1.StartsWith("Ai:Images", StringComparison.Ordinal));

        var options = Classes([.. plans, ("Billing:Plans:0:QualityClasses:0", "high")]);

        Assert.Empty(options.Value.Classes);
    }

    private static IOptions<GeneratedImageQualityClassOptions> Classes((string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        // Media's options validation reads Billing's catalogue, so both are registered — which is what a host
        // does. Media's own registration needs no database to bind and validate its configuration.
        return new ServiceCollection()
            .AddBillingModule(configuration)
            .AddMediaModule(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<GeneratedImageQualityClassOptions>>();
    }
}
