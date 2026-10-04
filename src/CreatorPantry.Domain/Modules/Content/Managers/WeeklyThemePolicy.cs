namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Limits for the creator's weekly themes, shared by request validation and EF configuration.
/// </summary>
public static class WeeklyThemePolicy
{
    /// <summary>
    /// A stable identifier other records store. The same width as a channel key, and for the same reason: both
    /// are opaque slugs that travel in stored rows rather than being read by a person.
    /// </summary>
    public const int KeyMaxLength = 64;

    /// <summary>The creator's own name for the day. Shorter than a brand name, which is a title, not a label.</summary>
    public const int DisplayNameMaxLength = 120;

    public const int DescriptionMaxLength = 500;

    /// <summary>
    /// Seven days, so seven live themes at most. Also the cap on a replace body: an eighth entry could only be
    /// a second theme on some day, which the unique index would refuse anyway.
    /// </summary>
    public const int MaxLiveThemes = 7;

    /// <summary>
    /// Every row a workspace may hold, live and retired together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retirement keeps rows so stored keys keep resolving, which means the retired tail grows every time a
    /// creator reworks their week. Ten changes per day is a generous ceiling for a seven-row list and still
    /// bounds the table; past it a new theme is refused and the creator deletes a retired one, which is what
    /// the delete route is for.
    /// </para>
    /// <para>
    /// <strong>A bound on growth, not an invariant.</strong> It is checked against a read taken before the write's
    /// transaction, so two replaces racing each other can land a few rows past it — deliberately, because the
    /// alternative is to decide the cap inside the data layer, and nothing breaks at 71 rows. One-theme-per-day
    /// and one-row-per-key are the real invariants, and those are unique indexes rather than checks.
    /// </para>
    /// </remarks>
    public const int MaxThemes = 70;

    /// <summary>
    /// Where <paramref name="day"/> sits in a creator's week, Monday first.
    /// </summary>
    /// <remarks>
    /// <see cref="DayOfWeek"/> starts at Sunday because C's <c>tm_wday</c> did, which is not how an editorial
    /// calendar reads — a week of food content starts on Monday and ends with Sunday supper. Used for display
    /// ordering only; nothing is stored in this order.
    /// </remarks>
    public static int WeekOrder(DayOfWeek day) => ((int)day + 6) % 7;
}
