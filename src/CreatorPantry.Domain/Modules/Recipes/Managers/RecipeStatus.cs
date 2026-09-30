namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Where a recipe sits in the creator's own editorial workflow.
/// </summary>
/// <remarks>
/// <para>
/// Editorial state only. It says nothing about whether the recipe has been delivered to any external
/// provider: content.md is explicit that status does not imply publication success, and provider delivery
/// state lives on <c>Publication</c> records that do not exist yet. <see cref="Approved"/> means the creator
/// has declared the recipe finished, not that anything has been posted anywhere.
/// </para>
/// <para>
/// <strong>This is a state machine, not a set of labels</strong> (TESTRUN-005).
/// <see cref="RecipeStatusTransitions"/> owns which moves exist and who may make them, and the transition
/// seam is the only thing that moves a recipe between these states — a write that set one directly would be
/// a second way to reach <see cref="Approved"/> without the readiness gate, which is why the settable enum
/// accepts only <see cref="Draft"/>. See <see cref="SettableRecipeStatusViewModel"/>.
/// </para>
/// <para>
/// <strong>The numeric values are what existing rows store</strong>, so renumbering one silently changes the
/// state every recipe already written is in. That is why the values are not in workflow order: the three
/// states that shipped keep the numbers they shipped with, and the three TESTRUN-005 added take the next
/// ones. Declaration order below is the workflow, because that is what a reader needs; the numbers are
/// history. Append new states at the end rather than renumbering.
/// </para>
/// <para>
/// Zero is <see cref="Draft"/> deliberately: an unset status meaning "being written" is the right default,
/// unlike <see cref="RecipeAssetRole"/> where zero would be a claim.
/// </para>
/// </remarks>
public enum RecipeStatus
{
    /// <summary>Being written. The default for a newly created recipe.</summary>
    Draft = 0,

    /// <summary>
    /// Actively being worked on: the recipe exists and the creator is developing it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Draft"/>, which is where a recipe lands on creation, because they answer
    /// different questions — "nobody has started" and "somebody is on it". It is also where a recipe returns
    /// to when it is reopened from any later state, so a reader of the history can tell a recipe that was
    /// never advanced from one that came back.
    /// </remarks>
    InDevelopment = 3,

    /// <summary>In the test kitchen: the creator is cooking it and recording what happened.</summary>
    Testing = 4,

    /// <summary>Put up for review. The creator considers it done and is asking for an approval.</summary>
    ReadyForReview = 5,

    /// <summary>
    /// Declared finished by somebody with the standing to say so. Not a publication claim.
    /// </summary>
    /// <remarks>
    /// Reached only through the transition seam, and only over a readiness evaluation with no blockers
    /// (TESTRUN-004). The approval is pinned to the <c>RecipeVersion</c> that transition writes, so it
    /// describes content that cannot subsequently change underneath it — and an ordinary edit afterwards
    /// returns the recipe to <see cref="InDevelopment"/> rather than leaving this claim standing over words
    /// nobody approved.
    /// </remarks>
    Approved = 1,

    /// <summary>Retired from the working set. Still readable, and its versions remain intact.</summary>
    Archived = 2,
}
