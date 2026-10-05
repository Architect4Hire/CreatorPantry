using System.Text.Json;
using System.Text.Json.Nodes;
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
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
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
/// PRM-005's export: the exact document one prompt produces, that it never grows a field by accident, that the
/// bytes are the same every time, and that a neighbour's prompt cannot be exported.
/// </summary>
/// <remarks>
/// The document itself is pinned against <see cref="PromptRecordExportWriter"/> directly, because a byte-exact
/// expectation is the only assertion that covers field order, indentation, line endings and the whole key set
/// at once. The route, the name and the boundary are driven through the facade over a real database; the HTTP
/// contract is <c>PromptsEndpointTests</c>.
/// </remarks>
public sealed class PromptRecordExportTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Saved = new(2026, 10, 5, 9, 7, 3, TimeSpan.Zero);
    private const string UserId = "user-1";
    private const string SavedStamp = "20261005-090703";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new() { UtcNow = Saved };
    private readonly ServiceProvider _provider;

    public PromptRecordExportTests()
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

    private async Task<OperationResult<PromptRecordExportServiceModel>> ExportAsync(
        Guid workspaceId, Guid promptRecordId)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>()
            .GetRecordDownloadAsync(promptRecordId, Ct);
    }

    private async Task<PromptRecordExportServiceModel> ExportFoundAsync(Guid workspaceId, Guid promptRecordId)
    {
        var result = await ExportAsync(workspaceId, promptRecordId);

        Assert.True(result.Succeeded, result.Error?.Code);

        return result.Value!;
    }

    /// <summary>A line-ending-independent expectation, so the test file's own CRLF cannot leak into it.</summary>
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // ---- the document, byte for byte ---------------------------------------------------------------------

    [Fact]
    public void A_manual_prompt_writes_this_exact_document()
    {
        var prompt = new PromptDetailServiceModel(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "instagram",
            PromptImageKind.Hero,
            "Overhead shot of soda bread on a linen cloth, soft window light.",
            GeneratedText: null,
            Label: "Soda bread hero",
            PromptRecordSource.Manual,
            AiProposalId: null,
            RecipeId: null,
            RecipeVersionId: null,
            PromptTemplateId: null,
            PromptTemplateVersion: null,
            PromptTemplateBodyChecksum: null,
            Saved);

        // Every promise the contract makes, in one assertion: the key set, the order, two-space indentation,
        // newline endings, nulls written rather than dropped, the enum names, and the timestamp's shape.
        Assert.Equal(
            Lines(
                "{",
                "  \"schemaVersion\": \"prompt.record.v1\",",
                "  \"prompt\": {",
                "    \"promptRecordId\": \"11111111-1111-1111-1111-111111111111\",",
                "    \"channelKey\": \"instagram\",",
                "    \"imageKind\": \"Hero\",",
                "    \"text\": \"Overhead shot of soda bread on a linen cloth, soft window light.\",",
                "    \"generatedText\": null,",
                "    \"label\": \"Soda bread hero\",",
                "    \"source\": \"Manual\",",
                "    \"aiProposalId\": null,",
                "    \"recipeId\": null,",
                "    \"recipeVersionId\": null,",
                "    \"promptTemplateId\": null,",
                "    \"promptTemplateVersion\": null,",
                "    \"promptTemplateBodyChecksum\": null,",
                "    \"createdAt\": \"2026-10-05T09:07:03.0000000Z\"",
                "  }",
                "}"),
            PromptRecordExportWriter.Write(prompt));
    }

    [Fact]
    public void A_fully_pinned_generated_prompt_writes_every_field()
    {
        Assert.Equal(
            Lines(
                "{",
                "  \"schemaVersion\": \"prompt.record.v1\",",
                "  \"prompt\": {",
                "    \"promptRecordId\": \"11111111-1111-1111-1111-111111111111\",",
                "    \"channelKey\": \"pinterest\",",
                "    \"imageKind\": \"ProcessStep\",",
                "    \"text\": \"Hands shaping the dough, close crop.\",",
                "    \"generatedText\": \"Hands shaping dough, close crop.\",",
                "    \"label\": \"Step 3\",",
                "    \"source\": \"ImagePromptComposition\",",
                "    \"aiProposalId\": \"22222222-2222-2222-2222-222222222222\",",
                "    \"recipeId\": \"33333333-3333-3333-3333-333333333333\",",
                "    \"recipeVersionId\": \"44444444-4444-4444-4444-444444444444\",",
                "    \"promptTemplateId\": \"image.prompt\",",
                "    \"promptTemplateVersion\": \"1.0.0\",",
                "    \"promptTemplateBodyChecksum\": \"sha256:abc\",",
                "    \"createdAt\": \"2026-10-05T09:07:03.0000000Z\"",
                "  }",
                "}"),
            PromptRecordExportWriter.Write(Pinned()));
    }

    /// <summary>
    /// Accents and markup are escaped, and that is a documented difference from the text download.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Utf8JsonWriter</c>'s default encoder escapes non-ASCII and the HTML-significant characters, so a
    /// prompt mentioning <c>&lt;b&gt;</c> cannot put a tag in the file even though the response is JSON. The
    /// text download passes the same characters through untouched; both are correct for their own media type,
    /// and the pair being different is worth a test rather than a surprise.
    /// </para>
    /// <para>
    /// It is also the tripwire for a later switch to <c>UnsafeRelaxedJsonEscaping</c>, which someone might
    /// reach for to make an exported file prettier. The escaping is what keeps this document safe to open in
    /// anything, and this test is what makes removing it a visible decision.
    /// </para>
    /// </remarks>
    [Fact]
    public void Accents_markup_and_quotes_are_escaped_rather_than_passed_through()
    {
        var prompt = Pinned() with
        {
            Text = "Crème brûlée & <b>hero</b>\nSecond line, \"quoted\".",
            GeneratedText = null,
            Label = null,
        };

        var json = PromptRecordExportWriter.Write(prompt);

        Assert.Contains(
            "\"text\": \"Cr\\u00E8me br\\u00FBl\\u00E9e \\u0026 \\u003Cb\\u003Ehero\\u003C/b\\u003E\\n"
            + "Second line, \\u0022quoted\\u0022.\"",
            json,
            StringComparison.Ordinal);

        // No raw tag, and no raw quote that could end the JSON string early.
        Assert.DoesNotContain("<b>", json, StringComparison.Ordinal);

        // The escapes are what a JSON reader decodes back to the creator's own words, unchanged.
        Assert.Equal(
            prompt.Text,
            JsonNode.Parse(json)!.AsObject()["prompt"]!.AsObject()["text"]!.GetValue<string>());
    }

    /// <summary>
    /// The schema version is a published string, so changing it is a contract change rather than a rename.
    /// </summary>
    [Fact]
    public void The_schema_version_is_the_one_the_route_documents()
    {
        Assert.Equal("prompt.record.v1", PromptRecordExportWriter.SchemaVersion);
    }

    [Fact]
    public void The_document_uses_newlines_that_do_not_depend_on_the_host()
    {
        var json = PromptRecordExportWriter.Write(Pinned());

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.Contains("\n", json, StringComparison.Ordinal);

        // And no byte-order mark, which would make the file unparseable to a strict reader.
        Assert.StartsWith("{", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The document's fields are exactly the detail route's, so neither contract can quietly outgrow the other.
    /// </summary>
    /// <remarks>
    /// Both directions matter, for different reasons. A field on the detail response that is missing here makes
    /// the export an incomplete record of a prompt; a field here that detail does not publish is this download
    /// disclosing something no reader could otherwise get. The writer states each one, so either drift is a
    /// line someone had to add.
    /// </remarks>
    [Fact]
    public void The_documents_fields_are_exactly_what_the_detail_route_publishes()
    {
        var detail = typeof(PromptDetailServiceModel).GetProperties()
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToList();

        var written = JsonNode.Parse(PromptRecordExportWriter.Write(Pinned()))!
            .AsObject()["prompt"]!.AsObject()
            .Select(pair => pair.Key)
            .ToList();

        // Order as well as membership: the document is read by people as well as by parsers.
        Assert.Equal(detail, written);
    }

    /// <summary>
    /// Nothing that never leaves the server, and nothing that points at storage, anywhere in the document.
    /// </summary>
    /// <remarks>
    /// Every key at every depth, so a nested block added later is covered too. This is PRM-005's RESTRICTION
    /// read literally — though a prompt row holds no credential or object path to begin with, which is why
    /// this guards the shape rather than filtering a value.
    /// </remarks>
    [Fact]
    public void No_key_at_any_depth_names_a_secret_an_owner_or_a_storage_location()
    {
        string[] banned =
        [
            "workspace", "membership", "secret", "token", "credential", "password",
            "url", "uri", "path", "blob", "objectkey", "container", "location", "href",
        ];

        var offenders = Keys(JsonNode.Parse(PromptRecordExportWriter.Write(Pinned()))!)
            .Where(key => banned.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    private static PromptDetailServiceModel Pinned() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "pinterest",
        PromptImageKind.ProcessStep,
        "Hands shaping the dough, close crop.",
        "Hands shaping dough, close crop.",
        "Step 3",
        PromptRecordSource.ImagePromptComposition,
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        "image.prompt",
        "1.0.0",
        "sha256:abc",
        Saved);

    /// <summary>Every property name in the document, at every depth.</summary>
    private static IEnumerable<string> Keys(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                yield return key;

                if (value is not null)
                {
                    foreach (var nested in Keys(value))
                    {
                        yield return nested;
                    }
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.OfType<JsonNode>())
            {
                foreach (var nested in Keys(item))
                {
                    yield return nested;
                }
            }
        }
    }

    // ---- through the route -------------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_prompt_exports_as_the_document_the_writer_produces()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual());

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        var detail = await facade.GetDetailAsync(promptRecordId, Ct);
        var export = await facade.GetRecordDownloadAsync(promptRecordId, Ct);

        Assert.True(detail.Succeeded, detail.Error?.Code);
        Assert.True(export.Succeeded, export.Error?.Code);

        // The route composes nothing of its own: the document is the detail record written out, so these two
        // cannot disagree about a prompt even if one of them is changed.
        Assert.Equal(PromptRecordExportWriter.Write(detail.Value!), export.Value!.Json);
    }

    [Fact]
    public async Task The_name_is_the_label_slugged_with_the_moment_it_was_saved()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: "Soda Bread Hero"));

        var export = await ExportFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal($"soda-bread-hero-{SavedStamp}.json", export.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("!!!")]
    [InlineData("../../etc/passwd")]
    [InlineData("hero\r\nSet-Cookie: a=b")]
    [InlineData("Crème brûlée, été")]
    public async Task A_label_cannot_reach_the_header_as_anything_but_a_slug(string? label)
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual(label: label));

        var export = await ExportFoundAsync(WorkspaceA, promptRecordId);

        Assert.Matches(new Regex(@"^[a-z0-9]+(-[a-z0-9]+)*-\d{8}-\d{6}\.json$"), export.FileName);
    }

    [Fact]
    public async Task The_same_prompt_exports_identically_however_much_later_it_is_asked_for()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual());

        var first = await ExportFoundAsync(WorkspaceA, promptRecordId);

        // The clock has moved on, which must change neither the bytes nor the name — there is no exportedAt,
        // and the stamp comes from when the row was saved.
        _clock.UtcNow = Saved.AddDays(40);
        var second = await ExportFoundAsync(WorkspaceA, promptRecordId);

        Assert.Equal(first.Json, second.Json);
        Assert.Equal(first.FileName, second.FileName);
    }

    [Fact]
    public async Task The_document_names_no_moment_but_the_one_the_prompt_was_saved()
    {
        var promptRecordId = await SaveAsync(WorkspaceA, Manual());

        _clock.UtcNow = Saved.AddDays(40);
        var export = await ExportFoundAsync(WorkspaceA, promptRecordId);

        // One timestamp in the document, and it is the row's. An exportedAt would be the clock above.
        var prompt = JsonNode.Parse(export.Json)!.AsObject()["prompt"]!.AsObject();
        Assert.Equal("2026-10-05T09:07:03.0000000Z", prompt["createdAt"]!.GetValue<string>());
        Assert.DoesNotContain("exportedAt", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026-11-14", export.Json, StringComparison.Ordinal);
    }

    // ---- who may read it ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_neighbours_prompt_is_refused_exactly_as_an_unknown_id_is()
    {
        var theirs = await SaveAsync(WorkspaceB, Manual(text: "Theirs, about risotto."));

        // Exportable in its own workspace first, so the refusal below is the boundary rather than a prompt
        // that was never saved.
        Assert.Contains("risotto", (await ExportFoundAsync(WorkspaceB, theirs)).Json, StringComparison.Ordinal);

        var borrowed = await ExportAsync(WorkspaceA, theirs);
        var invented = await ExportAsync(WorkspaceA, Guid.NewGuid());

        Assert.False(borrowed.Succeeded);
        Assert.False(invented.Succeeded);
        Assert.Equal(ContentErrorCodes.PromptNotFound, borrowed.Error!.Code);
        Assert.Equal(invented.Error!.Code, borrowed.Error!.Code);
        Assert.Equal(invented.Error!.Message, borrowed.Error!.Message);
        Assert.Empty(borrowed.Error!.FieldErrors);

        // And nothing of the neighbour's prompt came back on the refusal.
        Assert.Null(borrowed.Value);
        Assert.DoesNotContain("workspace", borrowed.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_refusal_is_the_same_one_the_other_two_reads_answer_with()
    {
        var theirs = await SaveAsync(WorkspaceB, Manual());

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        var detail = await facade.GetDetailAsync(theirs, Ct);
        var text = await facade.GetTextDownloadAsync(theirs, Ct);
        var export = await facade.GetRecordDownloadAsync(theirs, Ct);

        // Three routes, one refusal written once, so a client branching on code sees one condition.
        Assert.Equal(detail.Error!.Code, export.Error!.Code);
        Assert.Equal(text.Error!.Code, export.Error!.Code);
        Assert.Equal(detail.Error!.Message, export.Error!.Message);
    }

    [Fact]
    public async Task No_workspace_context_is_no_export()
    {
        await SaveAsync(WorkspaceA, Manual());

        await using var scope = _provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IPromptRecordFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => facade.GetRecordDownloadAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>A clock a test can move, so "when it was saved" and "now" can be told apart.</summary>
    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
