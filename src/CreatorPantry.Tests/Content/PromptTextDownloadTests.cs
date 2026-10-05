using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// PRM-004's download: that the body is the prompt and nothing else, that the name it is offered under is safe
/// and deterministic however the creator labelled it, and that a neighbour's prompt is refused exactly as an
/// unknown id is.
/// </summary>
/// <remarks>
/// Driven through the facade over a real database, so the workspace query filter — which is what makes the two
/// refusals one answer rather than two written to match — is in the path rather than stubbed. The HTTP
/// contract, the headers and the role bar are <c>PromptsEndpointTests</c>; the real engine's version of the
/// isolation case is <c>PromptRecordSqlServerTests</c>.
/// </remarks>
public sealed class PromptTextDownloadTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Saved = new(2026, 10, 5, 9, 7, 3, TimeSpan.Zero);
    private const string UserId = "user-1";

    /// <summary>The name <see cref="Saved"/> produces, in UTC and in the order a folder sorts by.</summary>
    private const string SavedStamp = "20261005-090703";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new() { UtcNow = Saved };
    private readonly ServiceProvider _provider;

    public PromptTextDownloadTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddApplicationTime()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddContentModule()
            .AddMediaModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(_clock)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Saved },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Saved });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            UserId);

        return scope;
    }

    private static SavePromptRecordViewModel Manual(string? text = null, string? label = "Soda bread hero") => new()
    {
        ChannelKey = "instagram",
        ImageKind = PromptImageKind.Hero,
        Text = text ?? "Overhead shot of soda bread on a linen cloth, soft window light.",
        Label = label,
        Source = PromptRecordSource.Manual,
    };

    private async Task<Guid> SaveAsync(Guid workspaceId, SavePromptRecordViewModel model)
    {
        await using var scope = ScopeFor(workspaceId);
        var outcome = await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .SaveAsync(UserId, model, idempotencyKey: null, Ct);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Code);

        return outcome.Result.Value!.PromptRecordId;
    }

    private async Task<OperationResult<PromptTextDownloadServiceModel>> DownloadAsync(
        Guid workspaceId, Guid promptRecordId)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .GetTextDownloadAsync(promptRecordId, Ct);
    }

    private async Task<PromptTextDownloadServiceModel> DownloadFoundAsync(Guid workspaceId, Guid promptRecordId)
    {
        var result = await DownloadAsync(workspaceId, promptRecordId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    // ---- what the file contains --------------------------------------------------------------------------

    [Fact]
    public async Task The_download_is_the_prompt_and_nothing_else()
    {
        const string text = "Overhead shot of soda bread on a linen cloth, soft window light.";
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(text));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        // Not "starts with", not "contains": the whole body, so a header line, a label or a trailing newline
        // added later fails here rather than passing a looser assertion.
        Assert.Equal(text, download.Text);
    }

    [Fact]
    public async Task The_file_is_byte_for_byte_the_text_the_detail_route_publishes()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual());

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        var detail = await facade.GetDetailAsync(promptRecordId, Ct);
        var download = await facade.GetTextDownloadAsync(promptRecordId, Ct);

        Assert.True(detail.Succeeded, detail.Error?.Code);
        Assert.True(download.Succeeded, download.Error?.Code);

        // Two routes reading one immutable column. If they ever disagree, one of them is editing a prompt.
        Assert.Equal(detail.Value!.Text, download.Value!.Text);
    }

    [Fact]
    public async Task A_prompt_at_the_column_limit_downloads_whole()
    {
        var text = new string('a', ContentPolicy.PromptTextMaxLength);
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(text));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal(ContentPolicy.PromptTextMaxLength, download.Text.Length);
        Assert.Equal(text, download.Text);
    }

    [Fact]
    public async Task The_creators_own_line_breaks_survive_and_none_is_added()
    {
        // Mixed on purpose: whatever the creator pasted is what the library holds, and a download that
        // normalised line endings would be handing back something other than the prompt that was used.
        const string text = "Hero shot, overhead.\r\nSoft window light.\nLinen cloth, crumbs.";
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(text));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal(text, download.Text);
        Assert.False(download.Text.EndsWith('\n'));
    }

    [Fact]
    public void The_download_shape_publishes_nothing_but_a_name_and_the_text()
    {
        var properties = typeof(PromptTextDownloadServiceModel).GetProperties()
            .Select(property => property.Name)
            .ToList();

        // Exactly two, so lineage cannot arrive in a .txt file by a field being added here. A label, channel,
        // kind, template triple or proposal id on this record would be PRM-005's JSON record in disguise.
        Assert.Equal(["FileName", "Text"], properties);

        // And nothing named like the two Guids that never leave the server, or like a storage location.
        Assert.DoesNotContain(
            properties,
            name => new[] { "Workspace", "Membership", "Url", "Uri", "Path", "ObjectKey", "Location", "Href" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- what the file is called -------------------------------------------------------------------------

    [Fact]
    public async Task The_name_is_the_label_slugged_with_the_moment_it_was_saved()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: "Soda Bread Hero"));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal($"soda-bread-hero-{SavedStamp}.txt", download.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public async Task A_prompt_with_no_usable_label_is_simply_called_prompt(string? label)
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: label));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal($"prompt-{SavedStamp}.txt", download.FileName);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\windows\system32")]
    [InlineData("hero\"; filename=\"owned.html")]
    [InlineData("hero\r\nSet-Cookie: a=b")]
    [InlineData("hero\u0000.txt")]
    [InlineData("Crème brûlée, été — ÆØ")]
    [InlineData("con")]
    [InlineData(".")]
    public async Task A_label_cannot_reach_the_header_as_anything_but_a_slug(string label)
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: label));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        // No separator, no quote, no line break, no dot segment and no control character survives, whatever
        // the creator typed: a-z, 0-9, single hyphens, the stamp, and one dot before the extension.
        Assert.Matches(new Regex(@"^[a-z0-9]+(-[a-z0-9]+)*-\d{8}-\d{6}\.txt$"), download.FileName);
    }

    [Fact]
    public async Task A_reserved_device_name_is_prefixed_rather_than_refused()
    {
        // "con.txt" is a name Windows will not accept whatever the extension, so a creator who labels a prompt
        // "con" still gets a download — they just get a differently named one.
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: "con"));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal($"prompt-con-{SavedStamp}.txt", download.FileName);
    }

    [Fact]
    public async Task A_very_long_label_is_capped_before_the_stamp()
    {
        // As long a label as the column accepts, so the cap under test is the file name's and not the column's.
        var label = string.Join(' ', Enumerable.Repeat("buttermilk", 18));
        Assert.True(label.Length <= ContentPolicy.PromptLabelMaxLength, label.Length.ToString());

        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: label));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        var slug = download.FileName[..^$"-{SavedStamp}.txt".Length];
        Assert.True(slug.Length <= PromptDownloadFileName.MaxSlugLength, slug);
        Assert.DoesNotContain("--", download.FileName, StringComparison.Ordinal);
        Assert.False(slug.EndsWith('-'), slug);
    }

    [Fact]
    public async Task The_same_prompt_downloads_under_the_same_name_every_time()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual());

        var first = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        // The clock has moved on, which must change nothing: the name comes from when the row was saved, and
        // the row is immutable. A name built from "now" would make every download of one prompt a new file.
        _clock.UtcNow = Saved.AddDays(40);
        var second = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal(first.FileName, second.FileName);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public async Task Two_prompts_with_one_label_are_told_apart_by_when_they_were_saved()
    {
        // The reason the stamp is there at all. A label is optional and repeatable, and a library's worth of
        // downloads that all land as prompt.txt silently overwrite each other in a downloads folder.
        var first = await SaveAsync(WorkspaceA, Manual(text: "First take.", label: null));

        _clock.UtcNow = Saved.AddMinutes(1);
        var second = await SaveAsync(WorkspaceA, Manual(text: "Second take.", label: null));

        var one = await DownloadFoundAsync(WorkspaceA, first);
        var two = await DownloadFoundAsync(WorkspaceA, second);

        Assert.Equal($"prompt-{SavedStamp}.txt", one.FileName);
        Assert.Equal("prompt-20261005-090803.txt", two.FileName);
    }

    [Fact]
    public async Task The_stamp_is_the_saved_moment_in_utc_whatever_offset_it_was_written_with()
    {
        // The same instant as Saved, carried with a +02:00 offset. The name must not move by two hours
        // because of how the row happened to be constructed or read.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 5, 11, 7, 3, TimeSpan.FromHours(2));
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: null));

        var download = await DownloadFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal($"prompt-{SavedStamp}.txt", download.FileName);
    }

    // ---- who may read it ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_neighbours_prompt_is_refused_exactly_as_an_unknown_id_is()
    {
        var theirs = await SaveAsync(WorkspaceB, Manual(text: "Theirs, about risotto."));

        // Readable in its own workspace first, so the refusal below is the boundary rather than a prompt that
        // was never saved.
        Assert.Equal("Theirs, about risotto.", (await DownloadFoundAsync(WorkspaceB, theirs)).Text);

        var borrowed = await DownloadAsync(WorkspaceA, theirs);
        var invented = await DownloadAsync(WorkspaceA, Guid.NewGuid());

        Assert.False(borrowed.Succeeded);
        Assert.False(invented.Succeeded);

        // Code, sentence and field set, so a prompt id cannot be used to ask what a neighbour owns — and
        // nothing of the prompt itself, which is the point of refusing in the first place.
        Assert.Equal(ContentErrorCodes.PromptNotFound, borrowed.Error!.Code);
        Assert.Equal(invented.Error!.Code, borrowed.Error!.Code);
        Assert.Equal(invented.Error!.Message, borrowed.Error!.Message);
        Assert.Equal(invented.Error!.FieldErrors.Count, borrowed.Error!.FieldErrors.Count);
        Assert.Empty(borrowed.Error!.FieldErrors);
        Assert.Null(borrowed.Value);
    }

    [Fact]
    public async Task The_refusal_says_nothing_about_a_workspace_or_a_file()
    {
        // Two identical disclosures would pass the equality check above, so the shared wording is read too.
        var theirs = await SaveAsync(WorkspaceB, Manual());

        var refusal = await DownloadAsync(WorkspaceA, theirs);

        Assert.False(refusal.Succeeded);
        Assert.DoesNotContain("workspace", refusal.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission", refusal.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".txt", refusal.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_refusal_is_the_same_object_the_detail_route_answers_with()
    {
        var theirs = await SaveAsync(WorkspaceB, Manual());

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        var detail = await facade.GetDetailAsync(theirs, Ct);
        var download = await facade.GetTextDownloadAsync(theirs, Ct);

        // One refusal written once, so a client branching on `code` sees one condition on both routes however
        // either is edited later.
        Assert.Equal(detail.Error!.Code, download.Error!.Code);
        Assert.Equal(detail.Error!.Message, download.Error!.Message);
    }

    [Fact]
    public async Task No_workspace_context_is_no_download()
    {
        // Nothing resolved the workspace, so there is no set to read inside. It throws rather than quietly
        // reading every workspace's library — the same guarantee the detail read holds.
        await SaveAsync(WorkspaceA, Manual());

        await using var scope = _provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => facade.GetTextDownloadAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>A clock a test can move, so "when it was saved" and "now" can be told apart.</summary>
    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
