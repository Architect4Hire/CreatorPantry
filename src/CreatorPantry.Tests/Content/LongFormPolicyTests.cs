using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// LF.1.3: the long-form shape rules — which roles a kind allows, the B-34 ceilings and targets, the word-count
/// rule, and what is a failure, an over-ceiling flag or a warning.
/// </summary>
public sealed class LongFormPolicyTests
{
    private const string Family = "\U0001F468‍\U0001F469‍\U0001F467";

    // ---- The figures ----

    [Fact]
    public void The_figures_are_the_ones_B34_approved()
    {
        Assert.Equal(3000, LongFormPolicy.SectionMaxCharacters);
        Assert.Equal(20000, LongFormPolicy.DocumentMaxCharacters);
        Assert.Equal(new LongFormWordRange(900, 1600), LongFormPolicy.TargetWords(LongFormKind.BlogArticle));
        Assert.Equal(new LongFormWordRange(300, 700), LongFormPolicy.TargetWords(LongFormKind.NewsletterIssue));
        Assert.Equal(4, LongFormPolicy.MaxBodySections);
        Assert.Equal("1.1.0", LongFormPolicy.Version);
    }

    [Fact]
    public void The_channel_profiles_keep_their_short_form_figures()
    {
        var profiles = new ContentChannelProfileCatalog();

        Assert.Equal(600, profiles.Find("blog")!.MaxLength);
        Assert.Equal(400, profiles.Find("newsletter")!.MaxLength);
        Assert.Equal("1.0.0", profiles.Version);
    }

    [Fact]
    public void Each_kind_allows_exactly_its_own_roles()
    {
        Assert.Equal(
            [
                LongFormSectionRole.Hook, LongFormSectionRole.Context, LongFormSectionRole.WhyItWorks,
                LongFormSectionRole.IngredientNotes, LongFormSectionRole.MethodNarrative, LongFormSectionRole.Tips,
                LongFormSectionRole.Variations, LongFormSectionRole.Storage, LongFormSectionRole.Faq,
                LongFormSectionRole.Cta, LongFormSectionRole.Closing, LongFormSectionRole.Body,
            ],
            LongFormPolicy.RolesFor(LongFormKind.BlogArticle).Select(rule => rule.Role));
        Assert.Equal(
            [
                LongFormSectionRole.Greeting, LongFormSectionRole.Lead, LongFormSectionRole.Item,
                LongFormSectionRole.Aside, LongFormSectionRole.Closing, LongFormSectionRole.Signoff,
            ],
            LongFormPolicy.RolesFor(LongFormKind.NewsletterIssue).Select(rule => rule.Role));

        // Every role in the vocabulary belongs to at least one kind, so none is stored and never usable.
        var used = Enum.GetValues<LongFormKind>().SelectMany(LongFormPolicy.RolesFor).Select(rule => rule.Role).ToHashSet();
        Assert.Equal(Enum.GetValues<LongFormSectionRole>().ToHashSet(), used);
    }

    [Fact]
    public void A_kind_that_does_not_exist_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LongFormPolicy.RolesFor((LongFormKind)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongFormPolicy.TargetWords((LongFormKind)2));
    }

    // ---- Roles ----

    public static TheoryData<LongFormKind, LongFormSectionRole> EveryAllowedRole()
    {
        var data = new TheoryData<LongFormKind, LongFormSectionRole>();

        foreach (var kind in Enum.GetValues<LongFormKind>())
        {
            foreach (var rule in LongFormPolicy.RolesFor(kind))
            {
                data.Add(kind, rule.Role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryAllowedRole))]
    public void A_section_of_any_allowed_role_is_measured_under_at_and_over_its_ceiling(LongFormKind kind, LongFormSectionRole role)
    {
        foreach (var (length, over) in new[] { (2999, false), (3000, false), (3001, true) })
        {
            var section = Section(role, new string('a', length));
            var result = LongFormDocumentValidator.Validate(kind, new LongFormDocument([section]));

            Assert.True(result.IsPlanValid);
            Assert.Equal(new LongFormSectionMeasure(section.Id, length, over), Assert.Single(result.Sections));
            Assert.Equal(over, result.HasOverCeiling);

            if (over)
            {
                var finding = Assert.Single(result.Findings, item => item.Severity == LongFormFindingSeverity.OverCeiling);
                Assert.Equal(LongFormFindingCodes.SectionOverCeiling, finding.Code);
                Assert.Equal(section.Id, finding.SectionId);
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryAllowedRole))]
    public void A_single_use_role_used_twice_fails_and_names_the_second_section(LongFormKind kind, LongFormSectionRole role)
    {
        var first = Section(role);
        var second = Section(role);
        var result = LongFormDocumentValidator.Validate(kind, new LongFormDocument([first, second]));

        if (LongFormPolicy.FindRole(kind, role)!.MaxOccurrences == 1)
        {
            var finding = Assert.Single(result.Findings, item => item.Severity == LongFormFindingSeverity.Failure);
            Assert.Equal(LongFormFindingCodes.RoleRepeated, finding.Code);
            Assert.Equal(second.Id, finding.SectionId);
            Assert.False(result.IsPlanValid);
        }
        else
        {
            Assert.True(result.IsPlanValid);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(19)]
    [InlineData(25)]
    [InlineData(-1)]
    public void A_role_outside_the_vocabulary_fails(int number)
    {
        var section = Section((LongFormSectionRole)number);

        foreach (var kind in Enum.GetValues<LongFormKind>())
        {
            var result = LongFormDocumentValidator.Validate(kind, new LongFormDocument([section]));

            var finding = Assert.Single(result.Findings, item => item.Severity == LongFormFindingSeverity.Failure);
            Assert.Equal(LongFormFindingCodes.UnknownRole, finding.Code);
            Assert.Equal(section.Id, finding.SectionId);
        }
    }

    [Theory]
    [InlineData(LongFormKind.BlogArticle, LongFormSectionRole.Item)]
    [InlineData(LongFormKind.BlogArticle, LongFormSectionRole.Greeting)]
    [InlineData(LongFormKind.BlogArticle, LongFormSectionRole.Signoff)]
    [InlineData(LongFormKind.NewsletterIssue, LongFormSectionRole.Hook)]
    [InlineData(LongFormKind.NewsletterIssue, LongFormSectionRole.Faq)]
    [InlineData(LongFormKind.NewsletterIssue, LongFormSectionRole.Body)]
    public void A_role_from_the_other_kind_fails(LongFormKind kind, LongFormSectionRole role)
    {
        var result = LongFormDocumentValidator.Validate(kind, new LongFormDocument([Section(role)]));

        Assert.Equal(
            LongFormFindingCodes.RoleNotAllowed,
            Assert.Single(result.Findings, item => item.Severity == LongFormFindingSeverity.Failure).Code);
    }

    [Fact]
    public void A_blog_article_holds_four_creator_headed_sections_and_not_a_fifth()
    {
        var four = Enumerable.Range(0, 4).Select(_ => Section(LongFormSectionRole.Body)).ToList();
        Assert.True(LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument(four)).IsPlanValid);

        var fifth = Section(LongFormSectionRole.Body);
        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([.. four, fifth]));

        var finding = Assert.Single(result.Findings, item => item.Severity == LongFormFindingSeverity.Failure);
        Assert.Equal(LongFormFindingCodes.RoleRepeated, finding.Code);
        Assert.Equal(fifth.Id, finding.SectionId);
    }

    [Fact]
    public void A_newsletter_issue_holds_as_many_items_as_it_covers()
    {
        var items = Enumerable.Range(0, 7).Select(_ => Section(LongFormSectionRole.Item)).ToList();

        Assert.True(LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, new LongFormDocument(items)).IsPlanValid);
    }

    // ---- The plan's shape ----

    [Fact]
    public void A_document_with_no_sections_fails()
    {
        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([]));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(LongFormFindingCodes.NoSections, finding.Code);
        Assert.Null(finding.SectionId);
        Assert.Equal(LongFormWordTargetStatus.NotAssessed, result.WordTarget);
    }

    [Fact]
    public void Two_sections_with_one_id_fail()
    {
        var hook = Section(LongFormSectionRole.Hook);
        var closing = Section(LongFormSectionRole.Closing) with { Id = hook.Id };

        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([hook, closing]));

        Assert.Equal(LongFormFindingCodes.DuplicateSectionId, Assert.Single(result.Findings).Code);
    }

    [Theory]
    [InlineData("", "Say why it works.", LongFormFindingCodes.HeadingBlank)]
    [InlineData("   ", "Say why it works.", LongFormFindingCodes.HeadingBlank)]
    [InlineData("Why it works", "", LongFormFindingCodes.IntentBlank)]
    [InlineData("Why it works", " \t ", LongFormFindingCodes.IntentBlank)]
    [InlineData("Why it works", "Say why.\nThen say more.", LongFormFindingCodes.IntentNotOneLine)]
    [InlineData("Why it works", "Say why.\rThen say more.", LongFormFindingCodes.IntentNotOneLine)]
    public void A_section_needs_a_heading_and_a_one_line_intent(string heading, string intent, string expected)
    {
        var section = Section(LongFormSectionRole.WhyItWorks) with { Heading = heading, Intent = intent };

        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([section]));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(expected, finding.Code);
        Assert.Equal(LongFormFindingSeverity.Failure, finding.Severity);
        Assert.Equal(section.Id, finding.SectionId);
    }

    [Fact]
    public void A_heading_and_an_intent_are_capped_in_characters_a_reader_would_count()
    {
        // 120 and 200 family emoji are several thousand UTF-16 units each, and exactly at the caps.
        var atCaps = Section(LongFormSectionRole.Hook) with
        {
            Heading = string.Concat(Enumerable.Repeat(Family, LongFormPolicy.HeadingMaxCharacters)),
            Intent = string.Concat(Enumerable.Repeat(Family, LongFormPolicy.IntentMaxCharacters)),
        };
        var over = atCaps with { Heading = atCaps.Heading + "a", Intent = atCaps.Intent + "a" };

        Assert.Empty(LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([atCaps])).Findings);
        Assert.Equal(
            [LongFormFindingCodes.HeadingTooLong, LongFormFindingCodes.IntentTooLong],
            LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([over])).Findings.Select(f => f.Code));
    }

    // ---- Ceilings ----

    [Fact]
    public void A_ceiling_counts_what_a_reader_would_call_a_character()
    {
        // Each of these is one grapheme cluster and several code units: a ZWJ family, a letter with a combining
        // accent, and a flag.
        foreach (var cluster in new[] { Family, "é", "\U0001F1EE\U0001F1EA" })
        {
            var atCeiling = Section(LongFormSectionRole.Hook, string.Concat(Enumerable.Repeat(cluster, 3000)));
            var over = Section(LongFormSectionRole.Hook, atCeiling.Body + cluster);

            var within = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([atCeiling]));
            Assert.Equal(3000, within.Sections[0].CharacterCount);
            Assert.False(within.HasOverCeiling);

            var beyond = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([over]));
            Assert.Equal(3001, beyond.Sections[0].CharacterCount);
            Assert.True(beyond.Sections[0].OverCeiling);
        }
    }

    [Fact]
    public void An_over_long_body_is_flagged_and_left_exactly_as_written()
    {
        var body = new string('a', 3500);
        var section = Section(LongFormSectionRole.Hook, body);
        var document = new LongFormDocument([section]);

        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, document);

        // Flagged, not failed: the plan is still valid and the save is not the validator's to refuse.
        Assert.True(result.IsPlanValid);
        Assert.True(result.HasOverCeiling);
        Assert.Equal(3500, result.Sections[0].CharacterCount);
        Assert.Equal(3500, result.CharacterCount);
        Assert.Same(body, document.Sections[0].Body);
    }

    [Fact]
    public void A_document_is_measured_under_at_and_over_its_own_ceiling()
    {
        // Seven sections of 2,800 and one that makes up the difference: none is over its own ceiling.
        LongFormDocument Of(int total) => new(
        [
            .. Enumerable.Range(0, 7).Select(_ => Section(LongFormSectionRole.Item, new string('a', 2800))),
            Section(LongFormSectionRole.Item, new string('a', total - (7 * 2800))),
        ]);

        foreach (var (total, over) in new[] { (19999, false), (20000, false), (20001, true) })
        {
            var result = LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, Of(total));

            Assert.Equal(total, result.CharacterCount);
            Assert.All(result.Sections, section => Assert.False(section.OverCeiling));
            Assert.Equal(over, result.Findings.Any(finding => finding.Code == LongFormFindingCodes.DocumentOverCeiling));
            Assert.True(result.IsPlanValid);
        }
    }

    [Fact]
    public void An_unwritten_section_has_no_count_and_headings_are_not_counted()
    {
        var planned = Section(LongFormSectionRole.Hook);
        var blank = Section(LongFormSectionRole.Context, "   ");
        var written = Section(LongFormSectionRole.Closing, "Bake it.");

        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, new LongFormDocument([planned, blank, written]));

        Assert.Equal([null, null, 8], result.Sections.Select(section => section.CharacterCount));
        Assert.Equal(8, result.CharacterCount);
        Assert.Equal(2, result.WordCount);
    }

    // ---- Words ----

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  \n\t ", 0)]
    [InlineData("Bake the bread.", 3)]
    // Hyphens: a compound is one word; a dash of any kind separates.
    [InlineData("a well-known slow-cooked stew", 4)]
    [InlineData("bread—and butter", 3)]
    [InlineData("bread — and butter", 3)]
    [InlineData("pages 3–5", 3)]
    [InlineData("bread--and butter", 3)]
    // Numerals: each run is one word.
    [InlineData("Bake at 350°F for 45 minutes", 6)]
    [InlineData("1 1/2 cups", 3)]
    [InlineData("2.5 kg in a 9x13 pan", 6)]
    // Apostrophes.
    [InlineData("don't skip the baker's trick", 5)]
    [InlineData("don’t", 1)]
    // Markdown that is not prose.
    [InlineData("## Why it works", 3)]
    [InlineData("- flour\n- butter\n* salt", 3)]
    [InlineData("1. Mix\n2. Rest\n10) Bake", 3)]
    [InlineData("> 1. Mix well", 2)]
    [InlineData("> a quoted line", 3)]
    [InlineData("**bold** and _soft_ and `code`", 5)]
    [InlineData("---\n\n***\n\n___", 0)]
    [InlineData("| flour | 500 g |\n|---|---|", 3)]
    [InlineData("```python\nprint hello\n```", 2)]
    // Links, images and bare addresses.
    [InlineData("See [my soda bread](https://example.com/soda-bread-recipe) today", 5)]
    [InlineData("![a loaf on linen](https://example.com/loaf.jpg)", 0)]
    [InlineData("Read https://example.com/soda-bread now", 3)]
    // Not words: marks standing alone, and emoji.
    [InlineData("salt & pepper", 2)]
    [InlineData("so good \U0001F35E !", 2)]
    // Whitespace of any kind separates.
    [InlineData("one two\tthree\r\nfour", 4)]
    // A decimal at the start of a line is a numeral, not a list number.
    [InlineData("2.5 cups", 2)]
    public void Words_are_counted_by_the_documented_rule(string? markdown, int expected) =>
        Assert.Equal(expected, LongFormWordCounter.CountWords(markdown));

    [Theory]
    [InlineData(LongFormKind.BlogArticle, 899, LongFormWordTargetStatus.Under)]
    [InlineData(LongFormKind.BlogArticle, 900, LongFormWordTargetStatus.Within)]
    [InlineData(LongFormKind.BlogArticle, 1600, LongFormWordTargetStatus.Within)]
    [InlineData(LongFormKind.BlogArticle, 1601, LongFormWordTargetStatus.Over)]
    [InlineData(LongFormKind.NewsletterIssue, 299, LongFormWordTargetStatus.Under)]
    [InlineData(LongFormKind.NewsletterIssue, 300, LongFormWordTargetStatus.Within)]
    [InlineData(LongFormKind.NewsletterIssue, 700, LongFormWordTargetStatus.Within)]
    [InlineData(LongFormKind.NewsletterIssue, 701, LongFormWordTargetStatus.Over)]
    public void A_missed_word_target_is_a_warning_and_never_a_failure(LongFormKind kind, int words, LongFormWordTargetStatus expected)
    {
        // Split across two sections so the target is the document's, not any one section's.
        var role = kind == LongFormKind.BlogArticle ? LongFormSectionRole.Body : LongFormSectionRole.Item;
        var document = new LongFormDocument(
        [
            Section(role, Words(words / 2)),
            Section(role, Words(words - (words / 2))),
        ]);

        var result = LongFormDocumentValidator.Validate(kind, document);

        Assert.Equal(words, result.WordCount);
        Assert.Equal(expected, result.WordTarget);
        Assert.True(result.IsPlanValid);
        Assert.DoesNotContain(result.Findings, finding => finding.Severity == LongFormFindingSeverity.Failure);

        var warnings = result.Findings.Where(finding => finding.Severity == LongFormFindingSeverity.Warning).Select(f => f.Code);
        Assert.Equal(
            expected switch
            {
                LongFormWordTargetStatus.Under => [LongFormFindingCodes.WordsUnderTarget],
                LongFormWordTargetStatus.Over => [LongFormFindingCodes.WordsOverTarget],
                _ => Array.Empty<string>(),
            },
            warnings);
    }

    [Fact]
    public void A_half_written_document_is_unfinished_not_short()
    {
        var document = new LongFormDocument(
        [
            Section(LongFormSectionRole.Hook, "Just the opening so far."),
            Section(LongFormSectionRole.Closing),
        ]);

        var result = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, document);

        Assert.Equal(5, result.WordCount);
        Assert.Equal(LongFormWordTargetStatus.NotAssessed, result.WordTarget);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void A_result_says_which_rules_produced_it_and_the_same_document_scores_the_same_twice()
    {
        var document = new LongFormDocument(
        [
            Section(LongFormSectionRole.Hook, new string('a', 3200)),
            Section(LongFormSectionRole.Hook),
            Section((LongFormSectionRole)99),
        ]);

        var first = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, document);
        var second = LongFormDocumentValidator.Validate(LongFormKind.BlogArticle, document);

        Assert.Equal(LongFormPolicy.Version, first.PolicyVersion);
        Assert.Equal(first.Findings, second.Findings);
        Assert.Equal(first.Sections, second.Sections);

        // A failure, a flag and nothing else: each kept in its own lane.
        Assert.Equal(
            [LongFormFindingCodes.SectionOverCeiling, LongFormFindingCodes.RoleRepeated, LongFormFindingCodes.UnknownRole],
            first.Findings.Select(finding => finding.Code));
    }

    // ---- A newsletter's items and the references its revision pinned (LF.4.1) ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void An_issue_with_one_item_for_each_pinned_reference_is_valid_wherever_the_items_sit(int count)
    {
        var references = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var document = new LongFormDocument(
        [
            .. references.Take(1).Select(Item),
            Section(LongFormSectionRole.Aside),
            .. references.Skip(1).Reverse<Guid>().Select(Item),
            Section(LongFormSectionRole.Closing),
        ]);

        Assert.Empty(LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, document, references).Findings);
    }

    [Fact]
    public void An_issue_with_no_pinned_references_has_no_items()
    {
        var greeting = new LongFormDocument([Section(LongFormSectionRole.Greeting)]);
        Assert.True(LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, greeting, []).IsPlanValid);

        var item = Item(Guid.NewGuid());
        var finding = Assert.Single(
            LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, new LongFormDocument([item]), []).Findings);

        Assert.Equal((LongFormFindingCodes.ItemSourceUnknown, item.Id), (finding.Code, finding.SectionId!.Value));
    }

    [Fact]
    public void Each_way_the_items_and_the_references_can_disagree_has_its_own_finding()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var repeated = Item(first);
        var missing = Section(LongFormSectionRole.Item);
        var unknown = Item(Guid.NewGuid());
        var notAnItem = Section(LongFormSectionRole.Aside) with { SourceReferenceId = second };

        var result = LongFormDocumentValidator.Validate(
            LongFormKind.NewsletterIssue,
            new LongFormDocument([Item(first), repeated, missing, unknown, notAnItem]),
            [first, second]);

        Assert.False(result.IsPlanValid);
        Assert.All(result.Findings, finding => Assert.Equal(LongFormFindingSeverity.Failure, finding.Severity));
        Assert.Equal(
            new (string, Guid?)[]
            {
                (LongFormFindingCodes.ItemSourceRepeated, repeated.Id),
                (LongFormFindingCodes.ItemSourceMissing, missing.Id),
                (LongFormFindingCodes.ItemSourceUnknown, unknown.Id),
                (LongFormFindingCodes.SectionSourceNotAllowed, notAnItem.Id),

                // The second reference has no item — a source named by a section that is not one covers nothing.
                (LongFormFindingCodes.ItemsSourceUncovered, null),
            },
            result.Findings.Select(finding => (finding.Code, finding.SectionId)));
    }

    [Fact]
    public void Without_pins_the_binding_is_not_assessed()
    {
        var document = new LongFormDocument([Section(LongFormSectionRole.Item), Item(Guid.NewGuid())]);

        Assert.Empty(LongFormDocumentValidator.Validate(LongFormKind.NewsletterIssue, document).Findings);
    }

    [Fact]
    public void An_item_is_unresolved_when_the_piece_no_longer_names_its_reference_for_the_recipe_that_was_pinned()
    {
        Guid kept = Guid.NewGuid(), removed = Guid.NewGuid(), repointed = Guid.NewGuid(), recipe = Guid.NewGuid();
        var sections = new[] { Item(kept), Item(removed), Item(repointed), Section(LongFormSectionRole.Item), Section(LongFormSectionRole.Aside) };

        var states = LongFormItemSources.Resolve(
            new LongFormDocument(sections),
            [new(kept, recipe), new(removed, recipe), new(repointed, recipe)],
            [new(kept, recipe), new(repointed, Guid.NewGuid())]);

        Assert.Equal(
            [
                LongFormItemSourceState.Resolved,
                LongFormItemSourceState.Unresolved,
                LongFormItemSourceState.Unresolved,
                LongFormItemSourceState.Unresolved,
            ],
            sections.Take(4).Select(section => states[section.Id]));

        // A section that is not an item has no source to have a state.
        Assert.False(states.ContainsKey(sections[4].Id));
    }

    [Fact]
    public void A_document_with_no_framing_is_stored_exactly_as_it_was_before_framing_existed()
    {
        var document = new LongFormDocument([Section(LongFormSectionRole.Greeting)]);

        Assert.DoesNotContain("framing", document.Serialize());

        var framed = document with { Framing = new LongFormIssueFraming("Three loaves", null) };

        Assert.Equal(framed.Framing, LongFormDocument.Deserialize(framed.Serialize()).Framing);
        Assert.Null(LongFormDocument.Deserialize(document.Serialize()).Framing);
    }

    // ---- Helpers ----

    private static LongFormSection Item(Guid reference) => Section(LongFormSectionRole.Item) with { SourceReferenceId = reference };

    private static LongFormSection Section(LongFormSectionRole role, string? body = null) => new(
        Guid.NewGuid(), role, "A heading", "One line on what this is for.", body, false, null, null);

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("bread", count));
}
