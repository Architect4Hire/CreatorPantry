namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// How good a picture a creator asks for (PRICE-006, AP.3.1). Each is mapped by configuration to a model, a
/// quality and a resolution; the request names the class and never the model.
/// </summary>
/// <remarks>
/// Fixed, because plans name them (PRICE-007: a plan sets "allowed quality classes") and the landing page
/// publishes them; what each one <em>is</em> — the model and settings behind it — is
/// <see cref="GeneratedImageQualityClassOptions"/>, so it can change without a release. Declared in ascending
/// order, which is the order every list of them is given in.
/// </remarks>
public enum GeneratedImageQualityClass
{
    Draft = 1,
    Standard = 2,
    High = 3,
    High2K = 4,
    High4K = 5,
}

/// <summary>
/// What one quality class asks the image deployment for (AP.3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The price is not here, on purpose (B-55).</strong> A class costs the rate
/// <c>Ai:Quota:ImageRates</c> holds for exactly this model, resolution and quality, read through the converter
/// admission charges with; a figure here would be a second copy of it to drift.
/// </para>
/// <para>
/// A model id is a configuration string, not a provider type: the Media module never names a provider SDK.
/// Startup checks every class against the image deployment's sizing profiles and both price tables
/// (<c>AiProviderRegistration</c>), so a class this host cannot send or cannot price never starts.
/// </para>
/// </remarks>
public sealed class GeneratedImageQualityClassMapping
{
    /// <summary>The image model the class sends to.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The quality, as the model names it; blank or absent for a model that takes none.</summary>
    public string? Quality { get; set; }

    /// <summary>The resolution tier, as the model names it; blank or absent for a model that takes none.</summary>
    public string? Resolution { get; set; }
}

/// <summary>
/// The quality classes this host offers and the one a request that names none gets (<c>Ai:Images</c>, AP.3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identical in the API and the Worker</strong>, because the API prices a class when it is asked for and
/// the Worker sends it; <c>QualityClassTableTests</c> holds the two copies together.
/// </para>
/// <para>
/// <strong>None configured is today's behaviour, not an error.</strong> A host with no classes offers no choice:
/// every picture is the image deployment's own default, as before AP.3.1, and a request that names a class is
/// refused. A deployed host always configures them — the image deployment's startup check requires it.
/// </para>
/// </remarks>
public sealed class GeneratedImageQualityClassOptions
{
    public const string SectionName = "Ai:Images";

    /// <summary>What a request that names no class is made at. Must be one of <see cref="Classes"/>.</summary>
    public GeneratedImageQualityClass? DefaultClass { get; set; }

    /// <summary>Each offered class and what it sends.</summary>
    public Dictionary<GeneratedImageQualityClass, GeneratedImageQualityClassMapping> Classes { get; } = [];
}

/// <summary>A quality class resolved to what is sent for it (AP.3.1).</summary>
/// <param name="QualityClass">The class; null on a host that configures none.</param>
/// <param name="ModelName">The model the picture is asked of.</param>
/// <param name="Resolution">The resolution tier, or null when the model takes none.</param>
/// <param name="Quality">The quality, or null when the model offers none.</param>
public sealed record GeneratedImageSettings(
    GeneratedImageQualityClass? QualityClass, string ModelName, string? Resolution, string? Quality);

/// <summary>
/// Resolves a requested class against the configured ones: the one place "which class, and what does it send"
/// is decided, so the quote, the hold, the worker and the classes list cannot disagree.
/// </summary>
public static class GeneratedImageQualityClassCatalogue
{
    /// <summary>Whether this host offers any classes at all.</summary>
    public static bool Offers(GeneratedImageQualityClassOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Classes.Count > 0;
    }

    /// <summary>
    /// The class a request is made at: the one it named, or the default when it named none. Null when the
    /// named class is not offered here, or when nothing is configured and the request named one.
    /// </summary>
    /// <remarks>
    /// On a host with no classes, a request that names none resolves to null too; the caller then uses the
    /// image deployment's own default, which is what <see cref="Offers"/> is for telling apart.
    /// </remarks>
    public static GeneratedImageQualityClass? Choose(
        GeneratedImageQualityClassOptions options, GeneratedImageQualityClass? requested)
    {
        ArgumentNullException.ThrowIfNull(options);

        var chosen = requested ?? options.DefaultClass;

        return chosen is { } quality && options.Classes.ContainsKey(quality) ? quality : null;
    }

    /// <summary>What <paramref name="qualityClass"/> sends, or null when it is not offered here.</summary>
    public static GeneratedImageSettings? Settings(
        GeneratedImageQualityClassOptions options, GeneratedImageQualityClass qualityClass)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Classes.TryGetValue(qualityClass, out var mapping) && !string.IsNullOrWhiteSpace(mapping.Model)
            ? new GeneratedImageSettings(qualityClass, mapping.Model.Trim(), Blank(mapping.Resolution), Blank(mapping.Quality))
            : null;
    }

    /// <summary>The offered classes in ascending order, each with what it sends.</summary>
    public static IReadOnlyList<GeneratedImageSettings> All(GeneratedImageQualityClassOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return [.. options.Classes.Keys
            .Order()
            .Select(qualityClass => Settings(options, qualityClass))
            .OfType<GeneratedImageSettings>()];
    }

    /// <summary>Blank reads as absent, so a setting cleared in configuration is a part the model takes none of.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Why a quality class this host offers may not be asked for by this account: its plan does not allow it
/// (PRICE-006/007, AP.4.3).
/// </summary>
/// <param name="QualityClass">The class that was asked for.</param>
/// <param name="RequiresPlanKey">
/// The cheapest plan that would allow it, or null when no plan does — locked with no plan to name.
/// </param>
/// <param name="RequiresPlanName">That plan's name, under the same condition.</param>
public sealed record GeneratedImageQualityPlanRefusal(
    GeneratedImageQualityClass QualityClass, string? RequiresPlanKey, string? RequiresPlanName);

/// <summary>
/// One quality this host offers and what a picture of it costs, as anything outside Media reads it (AP.4.5).
/// </summary>
/// <param name="QualityClass">
/// The class's canonical name — <c>PictureQualityVocabulary</c>'s spelling, so a reader needs no knowledge of
/// how a catalogue or a configuration section writes it.
/// </param>
/// <param name="PerPicture">
/// Credits per picture: the rate admission holds for exactly what this class sends. Exact, not typical.
/// </param>
/// <remarks>
/// It names no model, tier or quality setting. A request names a class; what that class sends is this
/// deployment's configuration, and a caller publishing prices has no business knowing it.
/// </remarks>
public sealed record GeneratedImageQualityOfferServiceModel(string QualityClass, decimal PerPicture);

/// <summary>Every quality this host offers, and the one a request naming none is made at (AP.4.5).</summary>
/// <param name="DefaultClass">
/// The canonical name of the default, or null on a host that offers none — or whose default it cannot price.
/// </param>
/// <param name="Classes">The offered classes in ascending order, which is cheapest first.</param>
public sealed record GeneratedImageQualityOffersServiceModel(
    string? DefaultClass, IReadOnlyList<GeneratedImageQualityOfferServiceModel> Classes);
