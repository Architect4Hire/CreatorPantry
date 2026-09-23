using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One step of a recipe's method. Interior to the <see cref="Recipe"/> aggregate, with a stable
/// <see cref="Id"/> so an edit, a comment, a diff, or a linked image can name the same step across revisions
/// even after the steps around it move.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Text"/> is the step. The technique, duration, and temperature beside it are a structured
/// reading of that prose, all nullable, all additive. They exist so a temperature can be converted and a
/// time can be summed by deterministic code — never so the sentence can be regenerated from them.
/// </para>
/// <para>
/// A temperature in a step is frequently a food-safety fact ("until the centre reads 74 °C"). recipes.md
/// forbids AI silently changing temperatures while rewriting prose, and storing the value structurally is
/// what makes that checkable rather than merely stated.
/// </para>
/// </remarks>
public class RecipeInstructionStep : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <inheritdoc cref="RecipeIngredient.RecipeId"/>
    public Guid RecipeId { get; set; }

    public Guid RecipeInstructionGroupId { get; set; }

    /// <summary>Position within the group, unique there.</summary>
    public int SortOrder { get; set; }

    /// <summary>The step exactly as the creator wrote it. Required, and canonical.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The shared <c>CookingTechnique</c> this step performs, when one was recognised. Additive.</summary>
    public Guid? TechniqueId { get; set; }

    /// <summary>How long the step takes, when the creator said. Additive to the prose, not extracted from it.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>The temperature the step calls for, paired with <see cref="TemperatureUnitId"/>.</summary>
    public decimal? TemperatureValue { get; set; }

    /// <summary>The unit the temperature is expressed in. Always a temperature unit — see below.</summary>
    public Guid? TemperatureUnitId { get; set; }

    /// <summary>
    /// Always <see cref="MeasurementDimension.Temperature"/> when <see cref="TemperatureUnitId"/> is set, and
    /// null when it is not. Redundant by design: carrying the dimension lets the composite foreign key
    /// <c>(TemperatureUnitId, TemperatureUnitDimension)</c> point at <c>MeasurementUnits (Id, Dimension)</c>,
    /// which together with <c>CK_RecipeInstructionSteps_Temperature_Dimension</c> makes "an oven temperature
    /// is measured in degrees" a fact the database enforces rather than a convention that holds until
    /// something writes grams here.
    /// </summary>
    public MeasurementDimension? TemperatureUnitDimension { get; set; }

    /// <summary>A creator's aside on this step — a tip, a warning, a doneness cue.</summary>
    public string? Note { get; set; }
}
