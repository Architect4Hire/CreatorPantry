using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>Reads the configured catalogue and works out the figures that are derived rather than typed.</summary>
public interface IPlanCatalogueBusiness
{
    PlanCatalogueServiceModel Published();
}

/// <remarks>
/// <see cref="IOptions{TOptions}"/> rather than a monitor on purpose: the value is the one
/// <c>ValidateOnStart</c> checked, and it cannot be replaced by a later configuration reload that no validator
/// has seen. A catalogue is not something to hot-swap under a running host; publishing a different price is a
/// deployment.
/// </remarks>
internal sealed class PlanCatalogueBusiness(
    IOptions<BillingOptions> options,
    IGeneratedImageQualityOfferFacade pictures) : IPlanCatalogueBusiness
{
    public PlanCatalogueServiceModel Published()
    {
        var billing = options.Value;

        // Unpublished is not an error and not an empty page: it is a catalogue with nothing published in it,
        // which the landing page renders as no pricing section at all. The alternative — a 404 — would make an
        // expected state look like a fault in every log on every page load.
        if (!billing.PublishPlans)
        {
            return PlanCatalogueServiceModel.Unpublished(billing.Currency);
        }

        var offered = pictures.Offered();
        var published = PictureClasses(billing, offered);
        var fallback = offered.DefaultClass;

        // What a picture costs at the quality nobody chose, which is what the plans' capacity figures are in.
        var perDefaultPicture = published
            .FirstOrDefault(row => row.QualityClass == fallback)?.PerPicture;

        return new PlanCatalogueServiceModel(
            billing.Currency,
            [.. billing.Plans.OrderBy(plan => plan.MonthlyPrice).Select(plan => Describe(plan, perDefaultPicture))],
            [.. billing.Packs.OrderBy(pack => pack.Credits).Select(pack =>
                new CreditPackServiceModel(pack.Key, pack.Credits, pack.Price))],
            new CreditRatesServiceModel(billing.Rates.TextDraft, billing.Rates.LongFormSection),
            published,
            // Only when it is one of the classes published here, which drops it on a host whose default class
            // no plan includes: the page never starts from a quality it shows no price for.
            published.Any(row => row.QualityClass == fallback) ? fallback : null);
    }

    /// <summary>
    /// What one picture of each offered quality costs, ascending — read from the charging table rather than
    /// written down (AP.4.5, B-55).
    /// </summary>
    /// <summary>
    /// What one picture of each offered quality costs, for the classes a plan on this catalogue includes
    /// (AP.4.5, B-55).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One rate source, reached through Media's own facade.</strong> The figures are
    /// <c>Ai:Quota:ImageRates</c>'s, read by the module that owns the classes and priced by the converter
    /// admission charges with — so changing one row there changes what the page says and what a creator is
    /// charged together. This module names no quality class type, no model and no rate table: it asks what is
    /// offered and keeps the classes its own plans include.
    /// </para>
    /// <para>
    /// <strong>A class no plan includes is not advertised.</strong> A host may offer a class its catalogue puts
    /// behind no plan — nothing on sale reaches it — and a price on the public page for one would be an offer
    /// the product cannot honour.
    /// </para>
    /// <para>
    /// Configuration only on both sides of the call. No account, no workspace, no period — which is what keeps
    /// this route anonymous and publicly cacheable, as the plans it sits beside are.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<PictureClassRateServiceModel> PictureClasses(
        BillingOptions billing, GeneratedImageQualityOffersServiceModel offered) =>
    [
        .. offered.Classes
            .Where(offer => billing.Plans.Any(plan => Allows(plan, offer.QualityClass)))
            .Select(offer => new PictureClassRateServiceModel(offer.QualityClass, offer.PerPicture)),
    ];

    /// <summary>Whether a plan's configured class list names <paramref name="qualityClass"/>.</summary>
    /// <remarks>
    /// Through the shared vocabulary, which is where the one comparison lives: a plan writes the landing page's
    /// spelling and an offer carries the canonical one.
    /// </remarks>
    private static bool Allows(PlanOption plan, string qualityClass) =>
        plan.QualityClasses.Any(listed =>
            PictureQualityVocabulary.Normalise(listed) == PictureQualityVocabulary.Normalise(qualityClass));

    /// <summary>
    /// How many pictures <paramref name="plan"/>'s credits buy at the default quality, or null when that
    /// quality has no published price (AP.4.5).
    /// </summary>
    /// <remarks>
    /// Floored, because two-thirds of a picture is not a picture a creator can make, and bounded by a trial's
    /// lifetime cap where there is one: <c>PlanServiceModel.PicturesAtDefaultQuality</c> says why.
    /// </remarks>
    private static int? PicturesAtDefaultQuality(PlanOption plan, decimal? perPicture)
    {
        if (perPicture is not { } price || price <= 0m)
        {
            return null;
        }

        var affordable = (int)decimal.Floor(plan.Credits / price);

        return plan.TrialImageCap is { } cap ? Math.Min(affordable, cap) : affordable;
    }

    private static PlanServiceModel Describe(PlanOption plan, decimal? perDefaultPicture)
    {
        // A year paid up front, and what that saves against twelve monthly payments. Both are derived from the
        // two prices the owner sets, so a catalogue cannot claim a saving its own prices do not add up to.
        var annualPrice = plan.AnnualMonthlyPrice * 12;
        var annualSaving = (plan.MonthlyPrice * 12) - annualPrice;

        return new PlanServiceModel(
            plan.Key,
            plan.Name,
            plan.MonthlyPrice,
            plan.AnnualMonthlyPrice,
            annualPrice,
            annualSaving,
            plan.Credits,
            ExampleMixOf(plan.ExampleCounts),
            plan.DailyImageCap,
            QualityClassesOf(plan),
            plan.Recommended,
            plan.TrialDays,
            plan.TrialImageCap,
            PicturesAtDefaultQuality(plan, perDefaultPicture),
            plan.ExampleCounts is { } example
                ? new PlanExampleServiceModel(example.RecipeDrafts, example.SocialPosts, example.Pictures)
                : null);
    }

    /// <summary>
    /// The example as the sentence <c>exampleMix</c> has always carried — "60 recipe drafts, 120 social posts and
    /// 70 pictures" — so a reader of the field from before <see cref="PlanServiceModel.ExampleCounts"/> reads the same
    /// figures the card leads with. Empty when the plan states none, as an unset prose field was.
    /// </summary>
    private static string ExampleMixOf(PlanExampleOption? example)
    {
        if (example is null)
        {
            return string.Empty;
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var drafts = example.RecipeDrafts.ToString("N0", culture) + " recipe drafts";
        var pictures = example.Pictures.ToString("N0", culture) + " pictures";

        return example.SocialPosts > 0
            ? $"{drafts}, {example.SocialPosts.ToString("N0", culture)} social posts and {pictures}"
            : $"{drafts} and {pictures}";
    }

    /// <summary>
    /// The quality classes a plan includes, published in the one spelling every reader gets (AP.4.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The catalogue's spelling is the landing page's; the wire's is the canonical one.</strong>
    /// <c>Billing:Plans</c> writes <c>high-2k</c> because an owner edits it by hand, while
    /// <see cref="PlanCatalogueServiceModel.PictureClasses"/> and the studio's own price list both say
    /// <c>High2K</c>. Canonicalising here means the browser reads one vocabulary and needs no copy of the
    /// comparison, which is what <see cref="PictureQualityVocabulary"/> is for.
    /// </para>
    /// <para>
    /// Ascending whatever order the section listed them in, and a name matching no class is left out rather
    /// than published raw: startup refuses such a catalogue, so its absence here guards a section edited while
    /// the host ran.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> QualityClassesOf(PlanOption plan) =>
        PictureQualityVocabulary.CanonicalAll(plan.QualityClasses);
}
