using System.ComponentModel;
using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The query string of <c>GET .../content-cards</c> (13.5, KAN-001). Carries no workspace: that comes from the
/// route and the caller's membership.
/// </summary>
/// <remarks>
/// Every filter is a string here and typed by <see cref="ContentCardListQueryFactory"/>, so a value the board
/// does not know is answered with a field message naming it rather than with model binding's own refusal.
/// There is no <c>sort</c>: a board has one order — column, then place in the column.
/// </remarks>
public sealed record ContentCardListViewModel(
    [property: FromQuery(Name = "channel")]
    [property: Description("One content channel key, such as instagram. A key no channel has, or one retired since a card stored it, is an empty page rather than an error.")]
    string? Channel = null,
    [property: FromQuery(Name = "day")]
    [property: Description("A weekday by name, such as Friday: cards planned for any date falling on it. A card with no planned date matches no day.")]
    string? Day = null,
    [property: FromQuery(Name = "date")]
    [property: Description("One planned date as yyyy-MM-dd, matched exactly. A calendar date in the workspace's own zone, as it was entered.")]
    string? Date = null,
    [property: FromQuery(Name = "column")]
    [property: Description("One column: ToDo, InProcess, ReadyForReview or Approved. Leave it out to read the whole board in column order.")]
    string? Column = null,
    [property: FromQuery(Name = "cursor")]
    [property: Description("Pass the previous page's nextCursor exactly as it was returned. A cursor is bound to the workspace and filters it was issued for.")]
    string? Cursor = null,
    [property: FromQuery(Name = "limit")]
    [property: Description("Rows per page. Out-of-range values are clamped rather than rejected.")]
    int? Limit = null);

/// <summary>
/// How each filter of <see cref="ContentCardListViewModel"/> is read. One place, used by the validator to say
/// what is wrong and by the factory to build the query, so the two cannot disagree about what a value means.
/// </summary>
public static class ContentCardListInputs
{
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>Trimmed, and null when nothing is left.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// A weekday by its English name, in any case. Not by number: <c>5</c> would be Friday to .NET and nothing
    /// to a creator, and <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> accepts it unless told not to.
    /// </summary>
    public static bool TryParseDay(string value, out DayOfWeek day) =>
        Enum.TryParse(value, ignoreCase: true, out day) && Enum.IsDefined(day) && !IsNumber(value);

    public static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>A column by name, in any case, and never by number — for <see cref="TryParseDay"/>'s reason.</summary>
    public static bool TryParseColumn(string value, out ContentCardColumn column) =>
        Enum.TryParse(value, ignoreCase: true, out column) && Enum.IsDefined(column) && !IsNumber(value);

    private static bool IsNumber(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}

/// <summary>Shape and format validation for <see cref="ContentCardListViewModel"/>.</summary>
public sealed class ContentCardListViewModelValidator : AbstractValidator<ContentCardListViewModel>
{
    public ContentCardListViewModelValidator()
    {
        RuleFor(model => model.Channel)
            .MaximumLength(ContentPolicy.ChannelKeyMaxLength)
            .WithMessage($"A channel key is at most {ContentPolicy.ChannelKeyMaxLength} characters.")
            .OverridePropertyName("channel");

        RuleFor(model => model.Day)
            .Must(value => ContentCardListInputs.TryParseDay(ContentCardListInputs.Normalize(value)!, out _))
            .WithMessage("Name a weekday, such as Friday.")
            .When(model => ContentCardListInputs.Normalize(model.Day) is not null)
            .OverridePropertyName("day");

        RuleFor(model => model.Date)
            .Must(value => ContentCardListInputs.TryParseDate(ContentCardListInputs.Normalize(value)!, out _))
            .WithMessage($"Write the date as {ContentCardListInputs.DateFormat}, such as 2026-10-16.")
            .When(model => ContentCardListInputs.Normalize(model.Date) is not null)
            .OverridePropertyName("date");

        RuleFor(model => model.Column)
            .Must(value => ContentCardListInputs.TryParseColumn(ContentCardListInputs.Normalize(value)!, out _))
            .WithMessage("Name a column: ToDo, InProcess, ReadyForReview or Approved.")
            .When(model => ContentCardListInputs.Normalize(model.Column) is not null)
            .OverridePropertyName("column");

        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");
    }
}
