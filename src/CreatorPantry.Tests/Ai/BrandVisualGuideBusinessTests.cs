using System.Reflection;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The reference-reading rules of <c>GET .../brand-visual-guide</c>, with the three brand facades scripted: what is
/// asked of them, how much, and what happens when the library cannot be read. Workspace isolation is the endpoint
/// tests' job; this holds the bounds.
/// </summary>
public sealed class BrandVisualGuideBusinessTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly BrandVisualGuideViewModel Request = new("image-prompt");

    // ---- Scripted facades ----

    /// <summary>Answers every facade method from one delegate, so a test states only what it cares about.</summary>
    public class Scripted : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = (name, _) => throw new InvalidOperationException(name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = Handler(targetMethod!.Name, args ?? []);
            var inner = targetMethod.ReturnType.GetGenericArguments()[0];

            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [result]);
        }
    }

    private static T Script<T>(Func<string, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Scripted>();
        ((Scripted)(object)proxy).Handler = handler;

        return proxy;
    }

    private sealed class Library
    {
        public int ListCalls;

        public int PassageCalls;

        public int RequestedLimit;

        public List<BrandSourcePassageSelector> Probed { get; } = [];
    }

    private static BrandActiveStyleGuideServiceModel Active() => new(
        Guid.NewGuid(),
        "Bright table",
        null,
        BrandStyleGuideStatus.Active,
        new BrandStyleGuideVersionDetailServiceModel(
            Guid.NewGuid(), 1, null, null, Moment, null, null,
            [new BrandStyleGuideSectionServiceModel(BrandStyleGuideSectionKey.VisualIdentity, null, "Warm.")],
            [],
            []));

    private static BrandSourceVisualReferenceServiceModel Reference(
        BrandSourceExtractionState state = BrandSourceExtractionState.Succeeded) =>
        new(Guid.NewGuid(), "A board", 2, state);

    private static IBrandVisualGuideBusiness Build(
        Library library,
        BrandActiveStyleGuideServiceModel? active,
        Func<BrandSourceVisualReferenceListServiceModel> list,
        Func<BrandSourcePassageSelector, bool>? hasText = null)
    {
        var guides = Script<IBrandStyleGuideFacade>((name, _) => name == nameof(IBrandStyleGuideFacade.GetActiveAsync)
            ? OperationResult<BrandActiveStyleGuideServiceModel?>.Success(active)
            : throw new InvalidOperationException(name));

        var documents = Script<IBrandSourceDocumentFacade>((name, args) =>
        {
            Assert.Equal(nameof(IBrandSourceDocumentFacade.ListVisualReferencesAsync), name);
            library.ListCalls++;
            library.RequestedLimit = (int)args[0]!;

            return list();
        });

        var passages = Script<IBrandSourcePassageFacade>((name, args) =>
        {
            Assert.Equal(nameof(IBrandSourcePassageFacade.ListDocumentsWithTextAsync), name);
            var selectors = (IReadOnlyList<BrandSourcePassageSelector>)args[0]!;
            library.PassageCalls++;
            library.Probed.AddRange(selectors);

            return (IReadOnlyList<Guid>)[.. selectors.Where(selector => hasText?.Invoke(selector) ?? true).Select(selector => selector.DocumentId)];
        });

        return new BrandVisualGuideBusiness(guides, documents, passages, NullLogger<BrandVisualGuideBusiness>.Instance);
    }

    private static BrandSourceVisualReferenceListServiceModel Listed(
        IReadOnlyList<BrandSourceVisualReferenceServiceModel> items, bool truncated = false) => new(items, truncated);

    // ---- Tests ----

    [Fact]
    public async Task A_library_that_cannot_be_read_is_reported_as_such_and_the_look_is_still_returned()
    {
        var business = Build(new Library(), Active(), () => throw new InvalidOperationException("library offline"));

        var result = (await business.GetAsync(Request, TestContext.Current.CancellationToken)).Value!;

        Assert.False(result.ReferencesAvailable);
        Assert.Empty(result.References);
        Assert.NotNull(result.ActiveGuide);
        Assert.True(result.HasVisualGuidance);
    }

    [Fact]
    public async Task With_no_active_guide_the_library_is_never_read()
    {
        var library = new Library();
        var business = Build(library, active: null, () => Listed([Reference()]));

        var result = (await business.GetAsync(Request, TestContext.Current.CancellationToken)).Value!;

        Assert.Null(result.ActiveGuide);
        Assert.Empty(result.References);
        Assert.True(result.ReferencesAvailable);
        Assert.Equal(0, library.ListCalls);
        Assert.Equal(0, library.PassageCalls);
    }

    [Fact]
    public async Task The_library_is_asked_for_a_bounded_number_of_references()
    {
        var library = new Library();
        var business = Build(library, Active(), () => Listed([Reference()]));

        await business.GetAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(25, library.RequestedLimit);
        Assert.Equal(1, library.ListCalls);
    }

    [Fact]
    public async Task Only_references_with_extracted_text_are_probed_for_indexed_text_at_their_current_version()
    {
        var library = new Library();
        var readable = Reference();
        var business = Build(library, Active(), () => Listed(
            [readable, Reference(BrandSourceExtractionState.NotExtracted), Reference(BrandSourceExtractionState.Failed)]));

        await business.GetAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal([new BrandSourcePassageSelector(readable.DocumentId, 2)], library.Probed);
    }

    [Fact]
    public async Task A_reference_is_usable_only_when_indexed_text_exists_and_otherwise_says_why()
    {
        var withText = Reference();
        var withoutText = Reference();
        var unread = Reference(BrandSourceExtractionState.NotExtracted);
        var business = Build(
            new Library(), Active(), () => Listed([withText, withoutText, unread]), selector => selector.DocumentId == withText.DocumentId);

        var result = (await business.GetAsync(Request, TestContext.Current.CancellationToken)).Value!;

        Assert.True(result.References.Single(r => r.DocumentId == withText.DocumentId).Usable);
        Assert.StartsWith("Its text is not ready to use yet.", result.References.Single(r => r.DocumentId == withoutText.DocumentId).UnusableReason);
        Assert.StartsWith("We have not read it yet.", result.References.Single(r => r.DocumentId == unread.DocumentId).UnusableReason);
    }

    [Fact]
    public async Task Truncation_is_passed_through_so_a_long_library_says_it_stopped()
    {
        var truncated = Build(new Library(), Active(), () => Listed([Reference()], truncated: true));
        var whole = Build(new Library(), Active(), () => Listed([Reference()]));

        Assert.True((await truncated.GetAsync(Request, TestContext.Current.CancellationToken)).Value!.ReferencesTruncated);
        Assert.False((await whole.GetAsync(Request, TestContext.Current.CancellationToken)).Value!.ReferencesTruncated);
    }

    [Fact]
    public async Task A_reference_carries_a_title_an_id_and_a_boolean_and_no_file_detail()
    {
        var business = Build(new Library(), Active(), () => Listed([Reference()]));

        var result = (await business.GetAsync(Request, TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(
            ["DocumentId", "Title", "UnusableReason", "Usable"],
            typeof(BrandVisualReferenceServiceModel).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Single(result.References);
    }
}
