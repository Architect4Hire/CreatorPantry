namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which part of the recipe a proposed change addresses.
/// </summary>
/// <remarks>
/// An enum rather than a path string, so a change can be checked against its operation's
/// <see cref="AiOperationScope"/> before anything is applied. A free-text path would make that check string
/// matching, and an unparseable path arriving from a model is exactly the unvalidated instruction a proposal
/// must never carry.
/// </remarks>
public enum AiChangeTargetKind
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>The recipe's own fields — title, headnote, times, yield. Has no child id.</summary>
    Recipe = 1,

    Ingredient = 2,

    IngredientGroup = 3,

    InstructionStep = 4,

    InstructionGroup = 5,

    Equipment = 6,

    AssetLink = 7,

    Tag = 8,

    /// <summary>
    /// A generated recipe concept — a pitch, not a canonical recipe. Only ever <see cref="AiChangeKind.Add"/>
    /// (the concept's title) followed by <see cref="AiChangeKind.Set"/> rows (its other fields), and never
    /// resolved against a pinned recipe snapshot: a concept-generation operation names no recipe at all, so
    /// there is nothing for <c>AiDiffCalculator</c> to diff against. <c>AiChangeApplicability</c> and
    /// <c>AiDiffFields</c> do not cover it for the same reason — neither is consulted for a target with no
    /// recipe to apply to.
    /// </summary>
    RecipeConcept = 9,

    /// <summary>
    /// One proposed ingredient substitution — advice about an ingredient, not a change to it. Only ever
    /// <see cref="AiChangeKind.Add"/> (the alternative's name) followed by <see cref="AiChangeKind.Set"/> rows
    /// carrying its guidance, the same flattening <see cref="RecipeConcept"/> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absent from <see cref="AiChangeApplicability"/> deliberately, and that absence is the
    /// guarantee.</strong> AIREC-004 must never replace an ingredient automatically, so there must be no code
    /// path from a stored substitution row to a recipe edit — and there is none, because the translation at
    /// the recipe-module boundary answers <c>null</c> for any target that enum does not cover. A creator acts
    /// on substitution advice by editing the recipe themselves, which is the point.
    /// </para>
    /// <para>
    /// Unlike <see cref="RecipeConcept"/>, an operation producing these rows <em>does</em> name a recipe and a
    /// pinned version: the advice depends on what the ingredient is doing in that method. What it does not do
    /// is address a change to either. See <see cref="AiOperationScope.Advisory"/>.
    /// </para>
    /// </remarks>
    IngredientSubstitution = 10,

    /// <summary>
    /// One field-linked review finding — a completeness, consistency, timing, temperature, ambiguous-step,
    /// unused-ingredient, likely-failure, allergen, dietary, or unsupported-claim observation about the recipe
    /// as a whole or about one of its lines, steps, or equipment items. Advice about the recipe, not a change
    /// to it. Only ever <see cref="AiChangeKind.Add"/> (the finding's summary) followed by
    /// <see cref="AiChangeKind.Set"/> rows carrying its category, severity, field reference, evidence and
    /// confidence — the same flattening <see cref="IngredientSubstitution"/> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absent from <see cref="AiChangeApplicability"/> deliberately, and that absence is the
    /// guarantee.</strong> AIREC-006 must never rewrite the recipe it reviews — a finding is a review signal,
    /// not a certification, and never an edit — so there must be no code path from a stored finding row to a
    /// recipe edit. The translation at the recipe-module boundary answers <c>null</c> for any target that enum
    /// does not cover, exactly as it does for <see cref="IngredientSubstitution"/>.
    /// </para>
    /// <para>
    /// Like <see cref="IngredientSubstitution"/>, an operation producing these rows names a recipe and a pinned
    /// version — the review depends on what the recipe actually says — but addresses no change to either. See
    /// <see cref="AiOperationScope.Advisory"/>.
    /// </para>
    /// </remarks>
    RecipeReviewFinding = 11,

    /// <summary>
    /// One item in AIREC-008's explanation of an existing proposal — a summary of what changed on one target,
    /// or a general note — flattened the same way as <see cref="RecipeReviewFinding"/>: one
    /// <see cref="AiChangeKind.Add"/> row carrying its summary, followed by <see cref="AiChangeKind.Set"/> rows
    /// naming the source proposal's own change and warning ids it describes.
    /// </summary>
    /// <remarks>
    /// <strong>Absent from <see cref="AiChangeApplicability"/> deliberately, and that absence is the
    /// guarantee.</strong> An explanation must never become an edit to anything, including the proposal it
    /// explains — it is a read projection of rows that already exist, and there must be no code path from a
    /// stored explanation item to a recipe edit or a change to the source proposal.
    /// </remarks>
    ProposalExplanationItem = 12,

    /// <summary>
    /// One section, or one item of a list section, of a generated editorial package (RCPUB-001): flattened the
    /// same way as <see cref="RecipeReviewFinding"/>. Absent from <see cref="AiChangeApplicability"/> and
    /// answering <c>null</c> in <see cref="AiChangeTargetPolicy"/> deliberately — there is no code path from a
    /// stored row to a recipe edit. It becomes content only when a creator accepts a <c>ContentRevision</c>.
    /// </summary>
    ContentSection = 13,

    /// <summary>
    /// One proposed brand-guide section, rule, conflict or uncertainty (11A.17), flattened the same way as
    /// <see cref="ContentSection"/>: the dimension, the citations and the evidence basis travel as
    /// <see cref="AiChangeKind.Set"/> rows beside the item's own text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent from <see cref="AiChangeApplicability"/> and answering <c>null</c> in
    /// <see cref="AiChangeTargetPolicy"/> deliberately and permanently — both by omission, since each falls
    /// through to nothing. A brand guide is not a recipe, so <strong>there is no code path from one of these
    /// rows to a recipe</strong>, and those two omissions are the guarantee.
    /// </para>
    /// <para>
    /// <strong>There is exactly one path to a guide, and 11A.18 is it.</strong> A creator accepts items of the
    /// proposal and <c>AiBrandGuideAcceptanceBusiness</c> writes them, through the brand module's facade, as one
    /// new <em>draft</em> version laid over the guide's working version. It is not an exception to the two
    /// omissions above: nothing is applied to anything by a target policy, the mapping from a dimension to a
    /// section key is its own translation, the creator names every item, a guide edited in the meantime is
    /// refused rather than written over, and the version that results approves nothing and activates nothing.
    /// Becoming what generations are grounded on still needs an approval and an Owner.
    /// </para>
    /// </remarks>
    BrandGuideSection = 14,

    /// <summary>
    /// One sample of <see cref="AiTaskType.BrandStyleTestDrive"/>'s left-hand column (11A.24): a blog
    /// introduction, a social caption or an image prompt written with no brand context whatsoever.
    /// <see cref="Data.Entities.AiStructuredChange.FieldName"/> says which of the three it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The variant is an enum member rather than a suffix on the field name</strong>, because which
    /// half of a comparison a row belongs to is the whole point of the row and a reader of the table should
    /// not have to parse a string to find out.
    /// </para>
    /// <para>
    /// <strong><see cref="Data.Entities.AiStructuredChange.BeforeValue"/> is null on these rows, deliberately.</strong> It is
    /// tempting to read "without the guide" as the before of "with the guide" and store one row per sample —
    /// but that column is documented, and asserted by <c>AiProposalAssembler</c>, as a value the server read
    /// from the pinned source and never one the model supplied. A test drive pins no source and both halves
    /// come from the model, so it carries two rows and no before.
    /// </para>
    /// <para>
    /// Absent from <see cref="AiChangeApplicability"/> and answering <c>null</c> in
    /// <see cref="AiChangeTargetPolicy"/>, both by omission, as <see cref="RecipeReviewFinding"/> is. There is
    /// no acceptance route for a test drive at all, so unlike <see cref="BrandGuideSection"/> there is not even
    /// a separate seam that writes one of these anywhere.
    /// </para>
    /// </remarks>
    BrandStyleSampleWithoutGuide = 15,

    /// <summary>
    /// One sample of <see cref="AiTaskType.BrandStyleTestDrive"/>'s right-hand column (11A.24): the same three
    /// pieces, written from the guide version the creator selected.
    /// </summary>
    /// <remarks>
    /// Identical in every structural respect to <see cref="BrandStyleSampleWithoutGuide"/> — same field names,
    /// same null before value, same two omissions — and separate from it only so that which prompt produced a
    /// row is recorded rather than inferred. The guide version itself is recorded once, on the proposal's
    /// <c>AiProposalBrandContext</c>.
    /// </remarks>
    BrandStyleSampleWithGuide = 16,
}
