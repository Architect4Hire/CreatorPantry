using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The inverse of <c>RecipeFirstDraftAiTaskHandler.Translate</c>, on its own: a pure function over stored
/// change rows and a creator's rewrites, with no database and no transaction.
/// </summary>
/// <remarks>
/// Tested here rather than only through the acceptance seam because the convention it rests on — that a
/// line's group is the most recent group <c>Add</c> before it in <c>SortOrder</c> — cannot be exercised by a
/// draft with one group in it, and a realistic draft has several.
/// </remarks>
public sealed class AiRecipeDraftComposerTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ---- recipe-level fields ---------------------------------------------------------------------------

    [Fact]
    public void The_recipe_level_fields_are_read_from_their_own_rows()
    {
        var builder = new DraftBuilder()
            .Recipe("title", "Weeknight Mapo Tofu")
            .Recipe("description", "A quick version.")
            .Recipe("notes", "Best right away.")
            .Recipe("prepTimeMinutes", "10")
            .Recipe("cookTimeMinutes", "15")
            .Recipe("restTimeMinutes", "5")
            .Recipe("totalTimeMinutes", "40")
            .Recipe("yieldText", "Serves 4")
            .Recipe("yieldQuantity", "2")
            .Recipe("servingCount", "4");

        var draft = builder.ComposeAll().Draft;

        Assert.Equal("Weeknight Mapo Tofu", draft.Title);
        Assert.Equal("A quick version.", draft.Description);
        Assert.Equal("Best right away.", draft.Notes);
        Assert.Equal(10, draft.PrepTimeMinutes);
        Assert.Equal(15, draft.CookTimeMinutes);
        Assert.Equal(5, draft.RestTimeMinutes);
        Assert.Equal(2m, draft.YieldQuantity);
        Assert.Equal(4m, draft.ServingCount);
    }

    /// <summary>recipes.md stores total time independently, and no write path sums it.</summary>
    [Fact]
    public void Total_time_is_carried_as_proposed_even_when_it_disagrees_with_the_parts()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .Recipe("prepTimeMinutes", "10")
            .Recipe("cookTimeMinutes", "15")
            .Recipe("totalTimeMinutes", "90")
            .ComposeAll().Draft;

        Assert.Equal(90, draft.TotalTimeMinutes);
    }

    /// <summary>
    /// The two fields a created recipe has nowhere for. The handler emits both whenever the model returns a
    /// yield, so a realistic draft drops them on every acceptance.
    /// </summary>
    [Theory]
    [InlineData("yieldUnitText", "loaves")]
    [InlineData("servingSize", "1")]
    public void An_unmappable_recipe_field_is_reported_as_dropped(string field, string value)
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var unmappable = builder.Recipe(field, value).Last;

        var composition = builder.ComposeAll();

        Assert.Contains(unmappable, composition.DroppedChangeIds);
    }

    [Fact]
    public void A_recipe_field_nothing_recognises_is_reported_as_dropped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var unknown = builder.Recipe("somethingNew", "a value").Last;

        Assert.Contains(unknown, builder.ComposeAll().DroppedChangeIds);
    }

    // ---- the positional group convention ---------------------------------------------------------------

    [Fact]
    public void Each_line_belongs_to_the_group_that_precedes_it()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup("For the sauce")
            .Ingredient("2 tbsp doubanjiang")
            .Ingredient("1 tsp sugar")
            .IngredientGroup("For the tofu")
            .Ingredient("1 block silken tofu")
            .ComposeAll().Draft;

        Assert.Equal(2, draft.IngredientGroups.Count);
        Assert.Equal(
            ["2 tbsp doubanjiang", "1 tsp sugar"],
            draft.IngredientGroups[0].Ingredients.Select(line => line.DisplayText));
        Assert.Equal(
            ["1 block silken tofu"],
            draft.IngredientGroups[1].Ingredients.Select(line => line.DisplayText));
    }

    [Fact]
    public void Each_step_belongs_to_the_group_that_precedes_it()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .InstructionGroup("Make the sauce")
            .Step("Fry the paste.")
            .InstructionGroup("Finish")
            .Step("Add the tofu.")
            .Step("Serve.")
            .ComposeAll().Draft;

        Assert.Equal(2, draft.Instructions.Count);
        Assert.Single(draft.Instructions[0].Steps);
        Assert.Equal(2, draft.Instructions[1].Steps.Count);
    }

    /// <summary>
    /// The ordering is the only thing carrying group membership, so a composer that stopped sorting would
    /// still pass every test built from an already-ordered list. This one is shuffled.
    /// </summary>
    [Fact]
    public void The_stored_sort_order_decides_membership_not_the_order_rows_arrive_in()
    {
        var builder = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup("First")
            .Ingredient("One")
            .IngredientGroup("Second")
            .Ingredient("Two");

        var draft = builder.ComposeAll(shuffle: true).Draft;

        Assert.Equal("One", draft.IngredientGroups[0].Ingredients[0].DisplayText);
        Assert.Equal("Two", draft.IngredientGroups[1].Ingredients[0].DisplayText);
    }

    /// <summary>recipes.md makes grouping opt-in: an untitled group is the ordinary case.</summary>
    [Fact]
    public void An_untitled_group_stays_untitled()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup(null)
            .Ingredient("Water")
            .ComposeAll().Draft;

        Assert.Null(draft.IngredientGroups[0].Title);
    }

    [Fact]
    public void A_blank_group_title_is_treated_as_untitled()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup(string.Empty)
            .Ingredient("Water")
            .ComposeAll().Draft;

        Assert.Null(draft.IngredientGroups[0].Title);
    }

    /// <summary>
    /// The rule the type's remarks call out: a line whose group was declined has nowhere to live, and is
    /// reported rather than reparented into a group the creator did not put it in.
    /// </summary>
    [Fact]
    public void A_line_whose_group_was_declined_is_dropped_rather_than_reparented()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var declinedGroup = builder.IngredientGroup("Declined").Last;
        var orphan = builder.Ingredient("Orphan").Last;
        builder.IngredientGroup("Kept");
        builder.Ingredient("Kept line");

        var composition = builder.Compose(builder.Ids.Where(id => id != declinedGroup));

        Assert.Contains(orphan, composition.DroppedChangeIds);
        Assert.Single(composition.Draft.IngredientGroups);
        Assert.Equal("Kept", composition.Draft.IngredientGroups[0].Title);
        Assert.Equal(["Kept line"], composition.Draft.IngredientGroups[0].Ingredients.Select(line => line.DisplayText));
    }

    [Fact]
    public void A_line_the_creator_did_not_accept_is_simply_absent()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup(null);
        var kept = builder.Ingredient("Kept").Last;
        var declined = builder.Ingredient("Declined").Last;

        var composition = builder.Compose(builder.Ids.Where(id => id != declined));

        Assert.Equal(["Kept"], composition.Draft.IngredientGroups[0].Ingredients.Select(line => line.DisplayText));
        Assert.DoesNotContain(declined, composition.DroppedChangeIds);
        Assert.Contains(kept, builder.Ids);
    }

    // ---- line and step fields --------------------------------------------------------------------------

    [Fact]
    public void A_lines_own_fields_land_on_that_line()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup(null)
            .Ingredient("2 tbsp doubanjiang")
            .On("ingredientNameText", "doubanjiang")
            .On("unitText", "tbsp")
            .On("quantity", "2")
            .On("quantityUpper", "3")
            .On("preparationNote", "finely chopped")
            .ComposeAll().Draft;

        var line = draft.IngredientGroups[0].Ingredients[0];
        Assert.Equal("doubanjiang", line.IngredientNameText);
        Assert.Equal("tbsp", line.UnitText);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(3m, line.QuantityUpper);
        Assert.Equal("finely chopped", line.PreparationNote);
    }

    [Fact]
    public void A_steps_own_fields_land_on_that_step()
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .InstructionGroup(null)
            .Step("Fry the paste.")
            .On("note", "Do not burn it.")
            .On("durationMinutes", "2")
            .ComposeAll().Draft;

        var step = draft.Instructions[0].Steps[0];
        Assert.Equal("Do not burn it.", step.Note);
        Assert.Equal(2, step.DurationMinutes);
    }

    /// <summary>
    /// The handler only ever emits "true", so anything else can only come from a creator's rewrite — and
    /// reading "False" as optional would be the opposite of what they typed.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void A_readable_optional_flag_is_honoured(string value, bool expected)
    {
        var draft = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup(null)
            .Ingredient("Salt")
            .On("isOptional", value)
            .ComposeAll().Draft;

        Assert.Equal(expected, draft.IngredientGroups[0].Ingredients[0].IsOptional);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void An_unreadable_optional_flag_is_dropped_rather_than_guessed_at(string value)
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup(null).Ingredient("Salt");
        var flag = builder.On("isOptional", value).Last;

        var composition = builder.ComposeAll();

        Assert.Contains(flag, composition.DroppedChangeIds);
        Assert.False(composition.Draft.IngredientGroups[0].Ingredients[0].IsOptional);
    }

    /// <summary>
    /// A part reported as accepted whose value is not in the recipe is exactly what the dropped count exists
    /// to prevent being invisible.
    /// </summary>
    [Theory]
    [InlineData("quantity", "about two")]
    [InlineData("quantityUpper", "a few")]
    public void An_unparsable_line_number_is_reported_as_dropped(string field, string value)
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup(null).Ingredient("Flour");
        var unparsable = builder.On(field, value).Last;

        Assert.Contains(unparsable, builder.ComposeAll().DroppedChangeIds);
    }

    [Fact]
    public void An_unparsable_step_duration_is_reported_as_dropped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").InstructionGroup(null).Step("Simmer.");
        var unparsable = builder.On("durationMinutes", "a while").Last;

        Assert.Contains(unparsable, builder.ComposeAll().DroppedChangeIds);
    }

    [Fact]
    public void A_field_belonging_to_a_different_kind_of_row_is_dropped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup(null).Ingredient("Flour");
        var stray = builder.On("durationMinutes", "5").Last;

        Assert.Contains(stray, builder.ComposeAll().DroppedChangeIds);
    }

    // ---- what a recipe cannot hold ---------------------------------------------------------------------

    [Fact]
    public void Equipment_and_its_own_fields_are_all_reported_as_dropped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var item = builder.Equipment("Wok").Last;
        var note = builder.On("note", "A wide pan works.").Last;
        var optional = builder.On("isOptional", "true").Last;

        var dropped = builder.ComposeAll().DroppedChangeIds;

        Assert.Contains(item, dropped);
        Assert.Contains(note, dropped);
        Assert.Contains(optional, dropped);
    }

    /// <summary>
    /// A whole-group optional flag has no column to land in — <c>RecipeIngredientGroup</c> has no
    /// <c>IsOptional</c> — which the first-draft handler's own remarks already record.
    /// </summary>
    [Fact]
    public void A_whole_group_optional_flag_is_reported_as_dropped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup("Streusel");
        var flag = builder.On("isOptional", "true").Last;

        Assert.Contains(flag, builder.ComposeAll().DroppedChangeIds);
    }

    [Fact]
    public void An_accepted_remove_or_move_is_reported_rather_than_skipped()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var removal = builder.Raw(AiChangeKind.Remove, AiChangeTargetKind.Recipe, null, null, "gone").Last;

        Assert.Contains(removal, builder.ComposeAll().DroppedChangeIds);
    }

    // ---- the creator's rewrites ------------------------------------------------------------------------

    [Fact]
    public void A_rewritten_line_carries_the_creators_words_and_is_counted()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup").IngredientGroup(null);
        var line = builder.Ingredient("2 tbsp doubanjiang").Last;

        var composition = builder.ComposeAll(
            edits: new Dictionary<(Guid, string), string> { [(line, "displayText")] = "3 tbsp doubanjiang" });

        Assert.Equal("3 tbsp doubanjiang", composition.Draft.IngredientGroups[0].Ingredients[0].DisplayText);
        Assert.Contains(line, composition.RewrittenChangeIds);
    }

    [Fact]
    public void A_rewritten_recipe_field_carries_the_creators_words()
    {
        var builder = new DraftBuilder();
        var title = builder.Recipe("title", "Weeknight Mapo Tofu").Last;

        var composition = builder.ComposeAll(
            edits: new Dictionary<(Guid, string), string> { [(title, "title")] = "My Mapo Tofu" });

        Assert.Equal("My Mapo Tofu", composition.Draft.Title);
        Assert.Contains(title, composition.RewrittenChangeIds);
    }

    [Fact]
    public void A_rewritten_group_heading_carries_the_creators_words()
    {
        var builder = new DraftBuilder().Recipe("title", "Soup");
        var group = builder.IngredientGroup("For teh sauce").Last;
        builder.Ingredient("Water");

        var composition = builder.ComposeAll(
            edits: new Dictionary<(Guid, string), string> { [(group, "title")] = "For the sauce" });

        Assert.Equal("For the sauce", composition.Draft.IngredientGroups[0].Title);
    }

    [Fact]
    public void Nothing_is_counted_as_rewritten_when_the_creator_wrote_nothing()
    {
        var composition = new DraftBuilder()
            .Recipe("title", "Soup")
            .IngredientGroup(null)
            .Ingredient("Water")
            .ComposeAll();

        Assert.Empty(composition.RewrittenChangeIds);
    }

    // ---- what a caller may rewrite ---------------------------------------------------------------------

    [Theory]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.Ingredient, null, "displayText", true)]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.Ingredient, null, "text", false)]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.InstructionStep, null, "text", true)]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.InstructionStep, null, "displayText", false)]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.IngredientGroup, null, "title", true)]
    [InlineData(AiChangeKind.Add, AiChangeTargetKind.Equipment, null, "displayText", true)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Recipe, "title", "title", true)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Recipe, "title", "description", false)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Recipe, "yieldUnitText", "yieldUnitText", false)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Ingredient, "unitText", "unitText", true)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.Ingredient, "note", "note", false)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.InstructionStep, "note", "note", true)]
    [InlineData(AiChangeKind.Set, AiChangeTargetKind.InstructionStep, "unitText", "unitText", false)]
    [InlineData(AiChangeKind.Remove, AiChangeTargetKind.Recipe, null, "title", false)]
    public void Only_a_field_that_row_can_carry_may_be_rewritten(
        AiChangeKind kind, AiChangeTargetKind target, string? storedField, string edited, bool expected)
    {
        var change = new AiStructuredChange
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Workspace,
            ChangeKind = kind,
            TargetKind = target,
            FieldName = storedField,
        };

        Assert.Equal(expected, AiRecipeDraftComposer.CanEdit(change, edited));
    }

    // ---- builder ---------------------------------------------------------------------------------------

    /// <summary>
    /// Assembles change rows the way <c>RecipeFirstDraftAiTaskHandler.Translate</c> does: recipe-level
    /// <c>Set</c> rows with no target, then one <c>Add</c> per row with its own <c>Set</c> rows after it.
    /// </summary>
    private sealed class DraftBuilder
    {
        private readonly List<AiStructuredChange> _changes = [];
        private Guid? _current;
        private AiChangeTargetKind _currentKind;
        private int _order;
        private int _position;

        /// <summary>The id of the row added last — what a test names to accept, decline or rewrite it.</summary>
        public Guid Last => _changes[^1].Id;

        public IEnumerable<Guid> Ids => _changes.Select(change => change.Id);

        public DraftBuilder Recipe(string field, string value) =>
            Raw(AiChangeKind.Set, AiChangeTargetKind.Recipe, null, field, value);

        public DraftBuilder IngredientGroup(string? title) => Open(AiChangeTargetKind.IngredientGroup, title);

        public DraftBuilder InstructionGroup(string? title) => Open(AiChangeTargetKind.InstructionGroup, title);

        public DraftBuilder Ingredient(string displayText) => Open(AiChangeTargetKind.Ingredient, displayText);

        public DraftBuilder Step(string text) => Open(AiChangeTargetKind.InstructionStep, text);

        public DraftBuilder Equipment(string displayText) => Open(AiChangeTargetKind.Equipment, displayText);

        /// <summary>A <c>Set</c> addressed to whichever row was opened last.</summary>
        public DraftBuilder On(string field, string value) =>
            Raw(AiChangeKind.Set, _currentKind, _current, field, value);

        public DraftBuilder Raw(
            AiChangeKind kind, AiChangeTargetKind target, Guid? targetId, string? field, string? value)
        {
            _changes.Add(new AiStructuredChange
            {
                Id = Guid.NewGuid(),
                WorkspaceId = Workspace,
                ChangeKind = kind,
                TargetKind = target,
                TargetId = targetId,
                FieldName = field,
                AfterValue = value,
                ProposedPosition = kind is AiChangeKind.Add ? _position++ : null,
                SortOrder = _order++,
            });

            return this;
        }

        private DraftBuilder Open(AiChangeTargetKind kind, string? value)
        {
            var targetId = Guid.NewGuid();
            Raw(AiChangeKind.Add, kind, targetId, null, value);
            _current = targetId;
            _currentKind = kind;
            return this;
        }

        public AiDraftComposition ComposeAll(
            bool shuffle = false, IReadOnlyDictionary<(Guid, string), string>? edits = null) =>
            Compose(Ids, shuffle, edits);

        public AiDraftComposition Compose(
            IEnumerable<Guid> accepted,
            bool shuffle = false,
            IReadOnlyDictionary<(Guid, string), string>? edits = null)
        {
            // Reversed rather than randomised: a deterministic wrong order still proves the sort is load-bearing,
            // and a flaky ordering test would be worse than none.
            var rows = shuffle ? Enumerable.Reverse(_changes).ToList() : _changes;

            return AiRecipeDraftComposer.Compose(
                rows,
                accepted.ToHashSet(),
                edits ?? new Dictionary<(Guid, string), string>());
        }
    }
}
