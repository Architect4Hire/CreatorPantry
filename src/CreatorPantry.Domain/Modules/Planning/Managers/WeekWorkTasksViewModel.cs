using System.ComponentModel;
using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Planning.Managers;

/// <summary>Which week of one person's work to read (13A.26).</summary>
/// <remarks>
/// A string rather than a <see cref="DateOnly"/>, so the format is the API's and not the binder's: a bound
/// date would also accept <c>10/12/2026</c>, which reads as two different days depending on who wrote it.
/// </remarks>
public sealed record WeekWorkTasksViewModel(
    [property: FromQuery(Name = "weekStart")]
    [property: Description("The Monday the week starts on, as yyyy-MM-dd. Leave it out for the week holding today in the workspace's own time zone.")]
    string? WeekStart = null);

public static class WeekWorkTasksInputs
{
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The years a week may be asked for. Wide enough for any plan, and far enough from the ends of the
    /// calendar that converting a week's edges to instants in any zone cannot run off them.
    /// </summary>
    public const int EarliestYear = 1900;

    public const int LatestYear = 9998;

    public static bool TryParse(string? value, out DateOnly? weekStart)
    {
        weekStart = null;
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !IsAllowed(date))
        {
            return false;
        }

        weekStart = date;
        return true;
    }

    /// <summary>A week's first day, inside the years a week may be asked for.</summary>
    public static bool IsAllowed(DateOnly date) =>
        WorkTaskWeek.IsStart(date) && date.Year is >= EarliestYear and <= LatestYear;
}

public sealed class WeekWorkTasksViewModelValidator : AbstractValidator<WeekWorkTasksViewModel>
{
    public WeekWorkTasksViewModelValidator() =>
        RuleFor(model => model.WeekStart)
            .Must(value => WeekWorkTasksInputs.TryParse(value, out _))
            .WithMessage(
                $"Name the week by the {WorkTaskWeek.StartsOn} it starts on, as yyyy-MM-dd, between "
                    + $"{WeekWorkTasksInputs.EarliestYear} and {WeekWorkTasksInputs.LatestYear}.")
            .OverridePropertyName("weekStart");
}
