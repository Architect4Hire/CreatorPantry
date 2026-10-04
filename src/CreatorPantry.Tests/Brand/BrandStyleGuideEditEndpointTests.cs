using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>POST .../brand-style-guides/{guideId}/versions</c> through the real Gateway (11A.15): what a submitted
/// edit writes, what it leaves alone, what it clears, when it writes nothing at all, how a stale base and an
/// unusable citation are refused, replay, the Editor policy and workspace isolation.
/// </summary>
public sealed class BrandStyleGuideEditEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "guide-edit-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private readonly InMemoryPrivateObjectStore _store = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
        });

        var userId = await _fixture.Api.CreateUserAsync(ContributorEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- What an edit writes ----

    [Fact]
    public async Task An_edit_writes_the_next_version_and_leaves_the_one_before_it_alone()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            changeReason = "Sounds more like me.",
            sections = new[] { new { sectionKey = "Voice", body = "A friend who cooks." } },
        });

        var saved = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, saved.GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, saved.GetProperty("parentVersionNumber").GetInt32());
        Assert.Equal(1, saved.GetProperty("sectionsReplaced").GetInt32());
        Assert.Equal(0, saved.GetProperty("sectionsAdded").GetInt32());

        var read = await ReadGuideAsync(client, _fixture.WorkspaceA, guide);
        var working = read.GetProperty("workingVersion");

        Assert.Equal(2, working.GetProperty("versionNumber").GetInt32());
        Assert.Equal("A friend who cooks.", SectionBody(working, "Voice"));
        Assert.Equal("Sounds more like me.", working.GetProperty("changeReason").GetString());

        // The parent is immutable, and this is the assertion that says so: the words it was written with are
        // still the words it holds.
        var parentId = await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuideVersions
            .Where(version => version.VersionNumber == 1)
            .Select(version => version.Id)
            .SingleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ["Plain."],
            await InScopeAsync(_fixture.WorkspaceA, db => db.BrandStyleGuideSections
                .Where(section => section.BrandStyleGuideVersionId == parentId)
                .Select(section => section.Body)
                .ToListAsync(TestContext.Current.CancellationToken)));
    }

    /// <summary>The property a submitted edit has to have: saving one part is not rewriting the guide.</summary>
    [Fact]
    public async Task A_section_the_request_does_not_name_survives()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(
            client, _fixture.WorkspaceA, Sections(("Voice", "Plain."), ("Audience", "Home cooks.")));

        await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Tone", body = "Warm." } },
        });

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Equal("Plain.", SectionBody(working, "Voice"));
        Assert.Equal("Home cooks.", SectionBody(working, "Audience"));
        Assert.Equal("Warm.", SectionBody(working, "Tone"));
    }

    [Fact]
    public async Task A_section_named_with_no_body_is_cleared()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(
            client, _fixture.WorkspaceA, Sections(("Voice", "Plain."), ("Tone", "Warm.")));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Tone", body = (string?)null } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, (await BodyOf(response)).GetProperty("sectionsCleared").GetInt32());

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Equal(
            ["Voice"],
            working.GetProperty("sections").EnumerateArray().Select(section => section.GetProperty("sectionKey").GetString()));
    }

    [Fact]
    public async Task A_submitted_rule_list_replaces_the_stored_rules()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { alwaysDo = new[] { "Lead with the dish" }, neverDo = new[] { "Pad the intro" } },
        });

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            rules = new { items = new[] { new { kind = "Do", text = "Name the pan size" } } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await BodyOf(response);
        Assert.Equal(1, saved.GetProperty("rulesAdded").GetInt32());
        Assert.Equal(2, saved.GetProperty("rulesRemoved").GetInt32());
        Assert.Equal(1, saved.GetProperty("ruleCount").GetInt32());

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Equal(
            ["Name the pan size"],
            working.GetProperty("rules").EnumerateArray().Select(rule => rule.GetProperty("text").GetString()));
    }

    /// <summary>
    /// The distinction the null-versus-empty rule exists for: a request that says nothing about rules is not a
    /// request to delete them.
    /// </summary>
    [Fact]
    public async Task Rules_left_out_of_the_request_are_kept()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { alwaysDo = new[] { "Lead with the dish" } },
        });

        await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Plain." } },
        });

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Single(working.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task An_empty_rule_list_clears_every_rule()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { alwaysDo = new[] { "Lead with the dish" } },
        });

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            rules = new { items = Array.Empty<object>() },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Empty(working.GetProperty("rules").EnumerateArray());
    }

    /// <summary>
    /// Deleting every rule is a decision, and a rules object that happens to carry no array is not a way to
    /// make it.
    /// </summary>
    [Fact]
    public async Task A_rules_object_with_no_items_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { alwaysDo = new[] { "Lead with the dish" } },
        });

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            rules = new { },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideInvalidRequest, Code(await BodyOf(response)));
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_citation_can_be_added_and_dropped_by_exact_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var document = await UploadDocumentAsync(client, _fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            sections = new[] { new { sectionKey = "Voice", body = "Plain." } },
            sourceDocuments = new[] { new { documentId = document, versionNumber = 1 } },
        });

        var dropped = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sourceDocuments = new { uncite = new[] { new { documentId = document, versionNumber = 1 } } },
        });

        Assert.Equal(HttpStatusCode.Created, dropped.StatusCode);
        Assert.Equal(1, (await BodyOf(dropped)).GetProperty("sourcesUncited").GetInt32());

        var added = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 2,
            sourceDocuments = new { cite = new[] { new { documentId = document, versionNumber = 1 } } },
        });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(1, (await BodyOf(added)).GetProperty("sourcesCited").GetInt32());

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Equal(document, Assert.Single(working.GetProperty("sourceDocuments").EnumerateArray()).GetProperty("documentId").GetGuid());
    }

    // ---- When nothing is written ----

    /// <summary>
    /// The case a creator reaches by pressing Save twice. No version, and nothing in the audit trail saying the
    /// guide changed when it did not.
    /// </summary>
    [Fact]
    public async Task An_edit_that_changes_nothing_writes_no_version_and_no_audit_entry()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Plain." } },
        });

        var saved = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("versionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("versionNumber").ValueKind);
        Assert.Equal(1, saved.GetProperty("parentVersionNumber").GetInt32());
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));

        Assert.Empty(await InScopeAsync(_fixture.WorkspaceA, db => db.AuditLogs
            .Where(entry => entry.Action == BrandAuditActions.StyleGuideVersionEdited)
            .ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_request_that_asks_for_nothing_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        var response = await SaveAsync(
            client, _fixture.WorkspaceA, guide, new { expectedWorkingVersionNumber = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideInvalidRequest, Code(await BodyOf(response)));
    }

    // ---- Concurrency ----

    /// <summary>
    /// Two creators with the guide open, or one with it open in two tabs: the second save is refused rather
    /// than rebased onto words its author never saw, and the refusal says how far behind they are.
    /// </summary>
    [Fact]
    public async Task An_edit_made_against_an_older_version_is_refused_and_writes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Tone", body = "Warm." } },
        });

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Audience", body = "Home cooks." } },
        });

        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideWorkingVersionConflict, Code(problem));
        Assert.Equal(1, problem.GetProperty("expectedWorkingVersionNumber").GetInt32());
        Assert.Equal(2, problem.GetProperty("workingVersionNumber").GetInt32());
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// Editing an approved version writes a draft beside it. The approval is never withdrawn and never moves:
    /// every generation that cited that version stays traceable to a version that was approved when it was used.
    /// </summary>
    [Fact]
    public async Task Editing_an_approved_version_leaves_its_approval_intact()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        var approval = await client.PostAsJsonAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide}/versions/1/approval",
            new { confirmed = true },
            Key(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "A friend who cooks." } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var working = (await ReadGuideAsync(client, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");

        Assert.Equal(2, working.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, working.GetProperty("approval").ValueKind);

        var history = await client.GetAsync(
            $"{GuidesIn(_fixture.WorkspaceA)}/{guide}/versions", TestContext.Current.CancellationToken);
        var rows = (await BodyOf(history)).GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("versionNumber").GetInt32(), row => row.GetProperty("status").GetString());

        Assert.Equal("Approved", rows[1]);
        Assert.Equal("Draft", rows[2]);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replay_returns_the_version_the_first_request_wrote()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));
        var key = Key();
        var body = new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "A friend who cooks." } },
        };

        var first = await SaveAsync(client, _fixture.WorkspaceA, guide, body, key);
        var replay = await SaveAsync(client, _fixture.WorkspaceA, guide, body, key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal(
            (await BodyOf(first)).GetProperty("versionId").GetGuid(),
            (await BodyOf(replay)).GetProperty("versionId").GetGuid());
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// The same words saved against two different versions are two different edits, so the key cannot carry
    /// from one to the other.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_edit_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));
        var key = Key();

        await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "A friend who cooks." } },
        }, key);

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 2,
            sections = new[] { new { sectionKey = "Voice", body = "Something else." } },
        }, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(response)));
    }

    // ---- Limits and unusable citations ----

    [Fact]
    public async Task A_change_past_a_limit_is_refused_and_writes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = Enumerable.Range(0, BrandPolicy.MaxStyleGuideChannelVariants + 1)
                .Select(index => new { sectionKey = "ChannelVariant", channelKey = $"channel-{index}", body = "x" })
                .ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideVersionLimitExceeded, Code(await BodyOf(response)));
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_citation_that_cannot_be_used_is_refused_and_names_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));
        var missing = Guid.NewGuid();

        var response = await SaveAsync(client, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sourceDocuments = new { cite = new[] { new { documentId = missing, versionNumber = 1 } } },
        });

        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideSourceUnprocessable, Code(problem));
        Assert.Equal(
            missing,
            Assert.Single(problem.GetProperty("unusableSources").EnumerateArray()).GetProperty("documentId").GetGuid());
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    // ---- Policy and isolation ----

    [Fact]
    public async Task A_contributor_cannot_edit_a_guide()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        using var contributor = await _fixture.SignInAsync(
            ContributorEmail, Password, TestContext.Current.CancellationToken);

        var response = await SaveAsync(contributor, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Mine now." } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// Workspace B's Owner, naming A's guide id under B's own slug: indistinguishable from a guide that was
    /// never created, and A's guide is untouched.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_guide_cannot_be_edited()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var response = await SaveAsync(ownerB, _fixture.WorkspaceB, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Ours now." } },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideNotFound, Code(await BodyOf(response)));
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));

        var working = (await ReadGuideAsync(ownerA, _fixture.WorkspaceA, guide)).GetProperty("workingVersion");
        Assert.Equal("Plain.", SectionBody(working, "Voice"));
    }

    /// <summary>
    /// The 404 above has to be the same answer, not merely the same status: a body that differed would let a
    /// caller tell "your guide, elsewhere" from "no such guide" and so confirm the guide exists.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_guide_answers_exactly_as_a_guide_that_does_not_exist()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var edit = new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Ours now." } },
        };

        var foreign = await SaveAsync(ownerB, _fixture.WorkspaceB, guide, edit);
        var unknown = await SaveAsync(ownerB, _fixture.WorkspaceB, Guid.NewGuid(), edit);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(await ProblemShapeOf(unknown), await ProblemShapeOf(foreign));
    }

    /// <summary>
    /// The workspace out of reach rather than the guide: A's own slug, by someone with no membership in A. Only
    /// the status is asserted, because this refusal is the edge policy's and not this route's.
    /// </summary>
    [Fact]
    public async Task A_workspace_the_caller_does_not_belong_to_answers_404()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));

        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var response = await SaveAsync(ownerB, _fixture.WorkspaceA, guide, new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "Ours now." } },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// A citation is resolved in the resolved workspace and nowhere else, so another workspace's document is
    /// unusable for the same reason a never-issued one is — and the two refusals are the same document with
    /// only the id swapped, which is what keeps the cause from being readable.
    /// </summary>
    [Fact]
    public async Task A_document_from_another_workspace_is_refused_exactly_as_one_that_does_not_exist()
    {
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var theirDocument = await UploadDocumentAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));
        var missing = Guid.NewGuid();

        var foreign = await CiteAsync(ownerA, guide, theirDocument);
        var foreignBody = await BodyOf(foreign);
        var unknown = await CiteAsync(ownerA, guide, missing);
        var unknownBody = await BodyOf(unknown);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideSourceUnprocessable, Code(foreignBody));
        Assert.Equal(
            Anonymised(ProblemShape(unknownBody), missing),
            Anonymised(ProblemShape(foreignBody), theirDocument));
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// One creator in two workspaces, one key: the second request is a different request, not a replay of the
    /// first. The key is scoped to the workspace as well as the user, and nothing but a test says so.
    /// </summary>
    [Fact]
    public async Task An_idempotency_key_does_not_carry_from_one_workspace_to_another()
    {
        await JoinAsync(_fixture.WorkspaceA.OwnerEmail, _fixture.WorkspaceB, WorkspaceRole.Editor);

        using var creator = await OwnerOf(_fixture.WorkspaceA);
        var here = await CreateGuideAsync(creator, _fixture.WorkspaceA, Sections(("Voice", "Plain.")));
        var there = await CreateGuideAsync(creator, _fixture.WorkspaceB, Sections(("Voice", "Plain.")));
        var key = Key();
        var edit = new
        {
            expectedWorkingVersionNumber = 1,
            sections = new[] { new { sectionKey = "Voice", body = "A friend who cooks." } },
        };

        var first = await SaveAsync(creator, _fixture.WorkspaceA, here, edit, key);
        var second = await SaveAsync(creator, _fixture.WorkspaceB, there, edit, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceB));

        // And the version each workspace wrote stays in it: B's edit is not visible from A.
        Assert.Equal(
            there,
            (await ReadGuideAsync(creator, _fixture.WorkspaceB, there)).GetProperty("id").GetGuid());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await creator.GetAsync(
                $"{GuidesIn(_fixture.WorkspaceA)}/{there}", TestContext.Current.CancellationToken)).StatusCode);
    }

    // ---- helpers ----

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// A problem document with the per-request trace id dropped: everything two refusals have to agree on if
    /// one is not to be distinguishable from the other.
    /// </summary>
    private static async Task<string> ProblemShapeOf(HttpResponseMessage response) =>
        ProblemShape(await BodyOf(response));

    /// <inheritdoc cref="ProblemShapeOf"/>
    private static string ProblemShape(JsonElement body) =>
        string.Join(
            "\n",
            body.EnumerateObject()
                .Where(field => field.Name is not "traceId")
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .Select(field => $"{field.Name}={field.Value.GetRawText()}"));

    /// <summary>The caller's own id out of a problem document, so two refusals differing only in it compare equal.</summary>
    private static string Anonymised(string problem, Guid documentId) =>
        problem.Replace(documentId.ToString(), "<document>", StringComparison.OrdinalIgnoreCase);

    private static object Sections(params (string Key, string Body)[] sections) => new
    {
        displayName = "House voice",
        sections = sections.Select(section => new { sectionKey = section.Key, body = section.Body }).ToArray(),
    };

    private static string? SectionBody(JsonElement version, string sectionKey) =>
        version.GetProperty("sections").EnumerateArray()
            .Single(section => section.GetProperty("sectionKey").GetString() == sectionKey)
            .GetProperty("body").GetString();

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<Guid> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Key(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SaveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, object body, string? key = null) =>
        client.PostAsJsonAsync(
            $"{GuidesIn(workspace)}/{guideId}/versions", body, key ?? Key(), TestContext.Current.CancellationToken);

    /// <summary>One citation added to workspace A's guide: the smallest request that resolves a pointer.</summary>
    private Task<HttpResponseMessage> CiteAsync(GatewayClient client, Guid guideId, Guid documentId) =>
        SaveAsync(client, _fixture.WorkspaceA, guideId, new
        {
            expectedWorkingVersionNumber = 1,
            sourceDocuments = new { cite = new[] { new { documentId, versionNumber = 1 } } },
        });

    /// <summary>
    /// Gives an existing user a membership in a second workspace, so one creator can be tested across two —
    /// which the fixture's own seeding does not do, each of its users belonging to one workspace only.
    /// </summary>
    private async Task JoinAsync(string email, SeededWorkspace workspace, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);

        Assert.NotNull(user);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> ReadGuideAsync(GatewayClient client, SeededWorkspace workspace, Guid guideId)
    {
        var response = await client.GetAsync(
            $"{GuidesIn(workspace)}/{guideId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    /// <summary>Uploads a document so a citation has something real to point at. Version 1, unextracted.</summary>
    private async Task<Guid> UploadDocumentAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        var response = await client.PostAsync(
            SourcesIn(workspace), form, Key(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private Task<int> VersionCountAsync(SeededWorkspace workspace) =>
        InScopeAsync(workspace, db => db.BrandStyleGuideVersions.CountAsync(TestContext.Current.CancellationToken));
}
