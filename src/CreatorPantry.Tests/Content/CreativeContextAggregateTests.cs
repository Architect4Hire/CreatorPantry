using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The creative context's schema and isolation, over the same two-workspace SQLite fixture the recipe, brand and
/// prompt aggregates use. Proves the EF configuration — the per-kind column rule, the workspace-paired keys, the
/// filtered unique indexes, the cascade and the query filter — not that SQL Server accepts the DDL or bumps a
/// row version, which <see cref="CreativeContextSqlServerTests"/> covers.
/// </summary>
public sealed class CreativeContextAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;

    private static readonly Guid A = RecipeAggregateFixture.WorkspaceA;

    private static readonly Guid B = RecipeAggregateFixture.WorkspaceB;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);

        return await work(RecipeAggregateFixture.Db(scope));
    }

    private Task<CreativeContext> SeedContextAsync(Guid workspaceId, string? title = "Soda bread, autumn") =>
        InAsync(workspaceId, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now, title);
            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return context;
        });

    /// <summary>Adds one reference to a stored context and reports whether the database took it.</summary>
    private Task SaveReferenceAsync(
        Guid workspaceId,
        CreativeContext context,
        CreativeContextReferenceKind kind,
        Action<CreativeContextReference> shape,
        int sortOrder = 0) =>
        InAsync(workspaceId, async db =>
        {
            var reference = CreativeContextSeeds.Reference(context, kind, sortOrder, Now);
            shape(reference);
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);

            return reference;
        });

    [Fact]
    public async Task Each_workspace_holds_its_own_contexts_and_cannot_see_the_others()
    {
        // The same title, brief, day, theme key and channel in both: a context is creator IP, never deduplicated.
        var a = await SeedContextAsync(A);
        var b = await SeedContextAsync(B);

        foreach (var (workspaceId, context) in new[] { (A, a), (B, b) })
        {
            await InAsync(workspaceId, async db =>
            {
                db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "instagram", 0));
                var reference = CreativeContextSeeds.Reference(
                    context, CreativeContextReferenceKind.SocialPackage, 0, Now);
                reference.SocialPackageId = Guid.NewGuid();
                db.CreativeContextReferences.Add(reference);
                await db.SaveChangesAsync(Ct);

                return 0;
            });
        }

        await InAsync(A, async db =>
        {
            Assert.Equal([a.Id], await db.CreativeContexts.Select(context => context.Id).ToListAsync(Ct));
            Assert.Null(await db.CreativeContexts.FirstOrDefaultAsync(context => context.Id == b.Id, Ct));

            // The interior rows are filtered in their own right, not merely reachable only through the root.
            Assert.Equal(a.Id, (await db.CreativeContextChannels.SingleAsync(Ct)).CreativeContextId);
            Assert.Equal(a.Id, (await db.CreativeContextReferences.SingleAsync(Ct)).CreativeContextId);

            return 0;
        });

        await InAsync(B, async db =>
        {
            Assert.Equal(b.Id, (await db.CreativeContexts.SingleAsync(Ct)).Id);
            Assert.Equal(b.Id, (await db.CreativeContextReferences.SingleAsync(Ct)).CreativeContextId);

            return 0;
        });
    }

    [Fact]
    public async Task Ownership_is_stamped_from_the_resolved_workspace_on_all_three_tables()
    {
        var context = await SeedContextAsync(A);

        await InAsync(A, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "blog", 0));
            var reference = CreativeContextSeeds.Reference(
                context, CreativeContextReferenceKind.SocialPackage, 0, Now);
            reference.SocialPackageId = Guid.NewGuid();
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await InAsync(A, async db =>
        {
            var loaded = await db.CreativeContexts
                .Include(item => item.Channels)
                .Include(item => item.References)
                .SingleAsync(Ct);

            Assert.Equal(A, loaded.WorkspaceId);
            Assert.Equal(A, loaded.Channels.Single().WorkspaceId);
            Assert.Equal(A, loaded.References.Single().WorkspaceId);

            return 0;
        });
    }

    [Fact]
    public async Task One_workspace_cannot_change_remove_or_add_to_anothers_context()
    {
        var theirs = await SeedContextAsync(B, title: "B's piece");
        await InAsync(B, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(theirs, "instagram", 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        // From A, B's rows are not there to be loaded, so there is nothing to edit or remove.
        await InAsync(A, async db =>
        {
            Assert.Null(await db.CreativeContexts.FirstOrDefaultAsync(context => context.Id == theirs.Id, Ct));
            Assert.Empty(await db.CreativeContextChannels
                .Where(channel => channel.CreativeContextId == theirs.Id).ToListAsync(Ct));

            return 0;
        });

        // A child stamped for A cannot hang off B's context: (A, theirs.Id) names no context.
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(A, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(theirs, "pinterest", 1));
            await db.SaveChangesAsync(Ct);

            return 0;
        }));

        // And a child that claims B's ownership outright is refused before it reaches the database.
        await Assert.ThrowsAsync<InvalidOperationException>(() => InAsync(A, async db =>
        {
            var reference = CreativeContextSeeds.Reference(
                theirs, CreativeContextReferenceKind.SocialPackage, 0, Now);
            reference.WorkspaceId = B;
            reference.SocialPackageId = Guid.NewGuid();
            db.CreativeContextReferences.Add(reference);
            await db.SaveChangesAsync(Ct);

            return 0;
        }));

        var untouched = await InAsync(B, db => db.CreativeContexts
            .Include(context => context.Channels)
            .Include(context => context.References)
            .SingleAsync(Ct));

        Assert.Equal("B's piece", untouched.WorkingTitle);
        Assert.Equal("instagram", untouched.Channels.Single().ChannelKey);
        Assert.Empty(untouched.References);
    }

    [Fact]
    public async Task None_of_the_three_sets_can_be_read_before_a_workspace_is_resolved()
    {
        await SeedContextAsync(A);

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        // Throws rather than returning nothing: a background job that forgot to resolve its workspace must
        // not be able to mistake "no filter" for "no rows".
        await Assert.ThrowsAnyAsync<Exception>(() => db.CreativeContexts.ToListAsync(Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => db.CreativeContextChannels.ToListAsync(Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => db.CreativeContextReferences.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_context_can_name_one_of_every_kind_in_order()
    {
        var context = await SeedContextAsync(A);

        var (recipeId, versionId) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var conceptRequestId = await InAsync(A, db => CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A, Now, Ct));
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var promptId = await InAsync(A, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.Recipe, reference =>
        {
            reference.RecipeId = recipeId;
            reference.RecipeVersionId = versionId;
        });
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.RecipeConcept, reference =>
        {
            reference.ConceptRequestId = conceptRequestId;
            reference.ConceptId = Guid.NewGuid();
        }, sortOrder: 1);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.DamAsset, reference =>
        {
            reference.MediaAssetId = assetId;
            reference.MediaAssetVersionNumber = 1;
        }, sortOrder: 2);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId, sortOrder: 3);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.PromptRecord,
            reference => reference.PromptRecordId = promptId, sortOrder: 4);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = Guid.NewGuid(), sortOrder: 5);

        var kinds = await InAsync(A, db => db.CreativeContextReferences
            .OrderBy(reference => reference.SortOrder)
            .Select(reference => reference.Kind)
            .ToListAsync(Ct));

        Assert.Equal(Enum.GetValues<CreativeContextReferenceKind>(), kinds);
    }

    [Fact]
    public async Task A_recipe_and_an_asset_may_be_named_without_a_version_pin()
    {
        var context = await SeedContextAsync(A);
        var (recipeId, _) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A, Now, Ct));

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.Recipe,
            reference => reference.RecipeId = recipeId);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.DamAsset,
            reference => reference.MediaAssetId = assetId, sortOrder: 1);

        Assert.Equal(2, await InAsync(A, db => db.CreativeContextReferences.CountAsync(Ct)));
    }

    public static TheoryData<int, string> MisshapenReferences => new()
    {
        // A kind without its own column.
        { (int)CreativeContextReferenceKind.Recipe, "none" },
        { (int)CreativeContextReferenceKind.GeneratedImage, "none" },

        // Half a concept.
        { (int)CreativeContextReferenceKind.RecipeConcept, "concept-only" },

        // A kind carrying another kind's column beside its own.
        { (int)CreativeContextReferenceKind.SocialPackage, "package-and-concept" },

        // A version pin on a kind that has no version.
        { (int)CreativeContextReferenceKind.SocialPackage, "package-and-asset-version" },

        // Not a kind at all.
        { 0, "package" },
        { 7, "package" },
    };

    [Theory]
    [MemberData(nameof(MisshapenReferences))]
    public async Task A_reference_carries_exactly_its_own_kinds_columns(int kind, string shape)
    {
        // Only columns with no foreign key are used, so what refuses each row is the check and nothing else.
        var context = await SeedContextAsync(A);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, (CreativeContextReferenceKind)kind, reference =>
            {
                switch (shape)
                {
                    case "concept-only":
                        reference.ConceptId = Guid.NewGuid();
                        break;
                    case "package":
                        reference.SocialPackageId = Guid.NewGuid();
                        break;
                    case "package-and-concept":
                        reference.SocialPackageId = Guid.NewGuid();
                        reference.ConceptId = Guid.NewGuid();
                        break;
                    case "package-and-asset-version":
                        reference.SocialPackageId = Guid.NewGuid();
                        reference.MediaAssetVersionNumber = 1;
                        break;
                }
            }));
    }

    [Fact]
    public async Task A_context_cannot_name_another_workspaces_records()
    {
        // Workspace B's records, named from a context in A. Refused by the composite keys with no validation
        // involved: (WorkspaceId, id) has nothing to resolve to.
        var context = await SeedContextAsync(A);

        var (recipeId, _) = await InAsync(B, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var conceptRequestId = await InAsync(B, db => CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct));
        var assetId = await InAsync(B, db => CreativeContextSeeds.MediaAssetAsync(db, B, Now, Ct));
        var imageId = await InAsync(B, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));
        var promptId = await InAsync(B, db => CreativeContextSeeds.PromptRecordAsync(db, Now, Ct));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.Recipe, reference => reference.RecipeId = recipeId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.RecipeConcept, reference =>
            {
                reference.ConceptRequestId = conceptRequestId;
                reference.ConceptId = Guid.NewGuid();
            }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.DamAsset, reference => reference.MediaAssetId = assetId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.GeneratedImage, reference => reference.GeneratedImageId = imageId));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.PromptRecord, reference => reference.PromptRecordId = promptId));

        Assert.Equal(0, await InAsync(A, db => db.CreativeContextReferences.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_version_pin_must_be_a_version_of_the_named_record()
    {
        var context = await SeedContextAsync(A);
        var (recipeId, _) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var (_, otherVersionId) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A, Now, Ct));

        // Another recipe's version.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.Recipe, reference =>
            {
                reference.RecipeId = recipeId;
                reference.RecipeVersionId = otherVersionId;
            }));

        // A version the asset does not have.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.DamAsset, reference =>
            {
                reference.MediaAssetId = assetId;
                reference.MediaAssetVersionNumber = 2;
            }));

        // And not a version at all: refused by name rather than as a key violation.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.DamAsset, reference =>
            {
                reference.MediaAssetId = assetId;
                reference.MediaAssetVersionNumber = 0;
            }));
    }

    [Fact]
    public async Task A_context_names_a_given_source_once_but_two_contexts_may_share_it()
    {
        var first = await SeedContextAsync(A);
        var second = await SeedContextAsync(A, title: "Soda bread, the reel");
        var (recipeId, versionId) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));

        await SaveReferenceAsync(A, first, CreativeContextReferenceKind.Recipe,
            reference => reference.RecipeId = recipeId);

        // The same recipe again in the same context, even pinned differently.
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, first, CreativeContextReferenceKind.Recipe, reference =>
            {
                reference.RecipeId = recipeId;
                reference.RecipeVersionId = versionId;
            }, sortOrder: 1));

        // The same recipe in another context is two pieces of work about one recipe.
        await SaveReferenceAsync(A, second, CreativeContextReferenceKind.Recipe,
            reference => reference.RecipeId = recipeId);

        Assert.Equal(2, await InAsync(A, db => db.CreativeContextReferences.CountAsync(Ct)));
    }

    [Fact]
    public async Task Two_concepts_from_one_request_are_two_sources()
    {
        var context = await SeedContextAsync(A);
        var requestId = await InAsync(A, db => CreativeContextSeeds.ConceptRequestAsync(db, Now, Ct));
        var conceptId = Guid.NewGuid();

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.RecipeConcept, reference =>
        {
            reference.ConceptRequestId = requestId;
            reference.ConceptId = conceptId;
        });
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.RecipeConcept, reference =>
        {
            reference.ConceptRequestId = requestId;
            reference.ConceptId = Guid.NewGuid();
        }, sortOrder: 1);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.RecipeConcept, reference =>
            {
                reference.ConceptRequestId = requestId;
                reference.ConceptId = conceptId;
            }, sortOrder: 2));
    }

    [Fact]
    public async Task References_that_share_no_column_do_not_collide_on_each_others_indexes()
    {
        // Each uniqueness index is filtered to its own kind. Unfiltered, every reference of another kind would
        // share one NULL slot per context and the second would be refused.
        var context = await SeedContextAsync(A);

        foreach (var order in Enumerable.Range(0, 3))
        {
            await SaveReferenceAsync(A, context, CreativeContextReferenceKind.SocialPackage,
                reference => reference.SocialPackageId = Guid.NewGuid(), sortOrder: order);
        }

        Assert.Equal(3, await InAsync(A, db => db.CreativeContextReferences.CountAsync(Ct)));
    }

    [Fact]
    public async Task Order_is_unique_within_a_context_for_references_and_channels()
    {
        var context = await SeedContextAsync(A);
        var other = await SeedContextAsync(A, title: "Another piece");

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = Guid.NewGuid());
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveReferenceAsync(
            A, context, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = Guid.NewGuid()));

        // Position zero is free again in a different context.
        await SaveReferenceAsync(A, other, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = Guid.NewGuid());

        await InAsync(A, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "instagram", 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(A, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "pinterest", 0));
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
    }

    [Fact]
    public async Task A_channel_is_chosen_once_per_context_and_freely_across_contexts_and_workspaces()
    {
        var context = await SeedContextAsync(A);
        var sibling = await SeedContextAsync(A, title: "Another piece");
        var theirs = await SeedContextAsync(B);

        foreach (var (workspaceId, owner) in new[] { (A, context), (A, sibling), (B, theirs) })
        {
            await InAsync(workspaceId, async db =>
            {
                db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(owner, "instagram", 0));
                await db.SaveChangesAsync(Ct);

                return 0;
            });
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(A, async db =>
        {
            db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(context, "instagram", 1));
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("brief")]
    [InlineData("theme")]
    [InlineData("day")]
    public async Task Optional_means_absent_not_blank(string field) =>
        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(A, async db =>
        {
            var context = CreativeContextSeeds.NewContext(Now);

            switch (field)
            {
                case "title":
                    context.WorkingTitle = "   ";
                    break;
                case "brief":
                    context.PictureBrief = string.Empty;
                    break;
                case "theme":
                    context.WeeklyThemeKey = " ";
                    break;
                case "day":
                    context.Day = (DayOfWeek)7;
                    break;
            }

            db.CreativeContexts.Add(context);
            await db.SaveChangesAsync(Ct);

            return 0;
        }));

    [Fact]
    public async Task A_context_needs_none_of_its_optional_facts()
    {
        // Started from a bare hand-off, before the creator has said anything about it.
        await InAsync(A, async db =>
        {
            db.CreativeContexts.Add(new CreativeContext
            {
                Id = Guid.NewGuid(),
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var loaded = await InAsync(A, db => db.CreativeContexts.SingleAsync(Ct));
        Assert.Null(loaded.WorkingTitle);
        Assert.Null(loaded.Day);
        Assert.Null(loaded.ArchivedAt);
    }

    [Fact]
    public async Task A_theme_key_that_names_no_theme_is_stored_as_given()
    {
        // A weak reference by key: nothing here points at a theme row, so a theme deleted later cannot break
        // the context that named it. Whether the key resolves is the weekly-theme facade's answer at read time.
        var context = await SeedContextAsync(A);

        Assert.Empty(await InAsync(A, db => db.WorkspaceWeeklyThemes.ToListAsync(Ct)));
        Assert.Equal(
            "meat-free-monday",
            (await InAsync(A, db => db.CreativeContexts.SingleAsync(item => item.Id == context.Id, Ct))).WeeklyThemeKey);
    }

    [Fact]
    public async Task A_target_that_is_archived_tombstoned_or_expired_leaves_the_context_whole()
    {
        var context = await SeedContextAsync(A);
        var (recipeId, _) = await InAsync(A, db => CreativeContextSeeds.RecipeAsync(db, Now, Ct));
        var assetId = await InAsync(A, db => CreativeContextSeeds.MediaAssetAsync(db, A, Now, Ct));
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.Recipe,
            reference => reference.RecipeId = recipeId);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.DamAsset,
            reference => reference.MediaAssetId = assetId, sortOrder: 1);
        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId, sortOrder: 2);

        // Each target leaves use the way its own module takes it out: none of them is a row delete.
        await InAsync(A, async db =>
        {
            (await db.Recipes.SingleAsync(recipe => recipe.Id == recipeId, Ct)).Status = RecipeStatus.Archived;

            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now.AddDays(1);
            asset.DeletedByMembershipId = Guid.NewGuid();

            var image = await db.GeneratedImages.SingleAsync(item => item.Id == imageId, Ct);
            image.Status = GeneratedImageStatus.Expired;
            image.ObjectDeletedAt = Now.AddDays(8);

            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var loaded = await InAsync(A, db => db.CreativeContexts
            .Include(item => item.References)
            .SingleAsync(item => item.Id == context.Id, Ct));

        // Still three, still naming what they named. Nothing on the row says "unavailable": that is the owning
        // facade's answer when the reference is read, and a flag here would be a second copy of it.
        Assert.Equal(
            [recipeId, assetId, imageId],
            loaded.References.OrderBy(reference => reference.SortOrder)
                .Select(reference => reference.RecipeId ?? reference.MediaAssetId ?? reference.GeneratedImageId));
    }

    [Fact]
    public async Task A_social_package_id_that_resolves_to_nothing_does_not_stop_a_context_loading()
    {
        // The one kind that can truly point at nothing today: its table arrives with AF.6.1.
        var context = await SeedContextAsync(A);
        var packageId = Guid.NewGuid();

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.SocialPackage,
            reference => reference.SocialPackageId = packageId);

        var loaded = await InAsync(A, db => db.CreativeContexts
            .Include(item => item.References)
            .SingleAsync(item => item.Id == context.Id, Ct));

        Assert.Equal(packageId, loaded.References.Single().SocialPackageId);
    }

    [Fact]
    public async Task Removing_a_context_takes_its_channels_and_references_and_leaves_what_they_named()
    {
        var context = await SeedContextAsync(A);
        var keep = await SeedContextAsync(A, title: "Kept");
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        foreach (var owner in new[] { context, keep })
        {
            await SaveReferenceAsync(A, owner, CreativeContextReferenceKind.GeneratedImage,
                reference => reference.GeneratedImageId = imageId);
            await InAsync(A, async db =>
            {
                db.CreativeContextChannels.Add(CreativeContextSeeds.Channel(owner, "instagram", 0));
                await db.SaveChangesAsync(Ct);

                return 0;
            });
        }

        await InAsync(A, async db =>
        {
            db.CreativeContexts.Remove(await db.CreativeContexts.SingleAsync(item => item.Id == context.Id, Ct));
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        await InAsync(A, async db =>
        {
            Assert.Equal(keep.Id, (await db.CreativeContextChannels.SingleAsync(Ct)).CreativeContextId);
            Assert.Equal(keep.Id, (await db.CreativeContextReferences.SingleAsync(Ct)).CreativeContextId);

            // A context records that the work used a picture; it never owned it.
            Assert.True(await db.GeneratedImages.AnyAsync(image => image.Id == imageId, Ct));

            return 0;
        });
    }

    [Fact]
    public async Task A_named_source_cannot_be_deleted_out_from_under_a_context()
    {
        // The other face of the restricted keys, stated so it is not discovered by a purge job: a row a context
        // names is not removed until the reference is.
        var context = await SeedContextAsync(A);
        var imageId = await InAsync(A, db => CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct));

        await SaveReferenceAsync(A, context, CreativeContextReferenceKind.GeneratedImage,
            reference => reference.GeneratedImageId = imageId);

        await Assert.ThrowsAsync<DbUpdateException>(() => InAsync(A, async db =>
        {
            db.GeneratedImages.Remove(await db.GeneratedImages.SingleAsync(image => image.Id == imageId, Ct));
            await db.SaveChangesAsync(Ct);

            return 0;
        }));
    }

    [Fact]
    public async Task Archiving_keeps_the_context_readable()
    {
        var context = await SeedContextAsync(A);

        await InAsync(A, async db =>
        {
            (await db.CreativeContexts.SingleAsync(Ct)).ArchivedAt = Now.AddDays(3);
            await db.SaveChangesAsync(Ct);

            return 0;
        });

        var loaded = await InAsync(A, db => db.CreativeContexts.SingleAsync(item => item.Id == context.Id, Ct));
        Assert.Equal(Now.AddDays(3), loaded.ArchivedAt);
    }
}
