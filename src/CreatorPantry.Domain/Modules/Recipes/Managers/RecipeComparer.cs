using System.Globalization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Compares two recipe snapshots and says what changed. Pure and total: no persistence, no clock, no
/// workspace, no identity, and no model — give it the same two documents twice and it produces the same
/// answer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here is generated, and nothing here is written.</strong> REC-008 wants a comparison a
/// creator can trust enough to approve an edit from, and AIREC-GR-002 wants the server to calculate that
/// comparison rather than believe one. Both are the same requirement seen from two sides: the diff is
/// arithmetic over two documents the server read for itself. A model may propose a change; it may not
/// describe one.
/// </para>
/// <para>
/// <strong>Stable ids are matched before anything else.</strong> Every group, line and step carries the
/// identifier it had in the live aggregate (<see cref="RecipeSnapshotDocument"/>), so an ingredient dragged
/// into another group is recognised as itself rather than reported as a deletion and an unrelated insertion.
/// Matching is done across the whole document for this reason — never within a group, which would make a
/// cross-group move impossible to see.
/// </para>
/// <para>
/// <strong>Position is compared as rank, never as <c>SortOrder</c>.</strong> Stored sort orders may be
/// gapped, renumbered, or rewritten wholesale by an editor that saves positions as 0,1,2 where the previous
/// save used 0,10,20. None of that moved anything. Comparing the ordinal position among siblings instead
/// means a renumbering produces an empty diff, and it also means that deleting the first line of a group
/// does not report the lines beneath it as having moved — see <see cref="MarkMoves"/>.
/// </para>
/// <para>
/// <strong>The two documents may carry different schema versions.</strong> Comparing a year-old version to
/// today's is the ordinary case, and a self-describing archive exists precisely so it stays possible. Both
/// are checked against the readable range, and neither is read under the other's shape.
/// </para>
/// </remarks>
public static class RecipeComparer
{
    /// <summary>Compares an earlier snapshot against a later one.</summary>
    /// <param name="from">The earlier document — the left-hand side, whose values are reported as <c>From</c>.</param>
    /// <param name="to">The later document — the right-hand side, whose values are reported as <c>To</c>.</param>
    /// <exception cref="NotSupportedException">
    /// Either document carries no schema version, or one this build cannot read.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Either document repeats an identifier. That is a corrupt archive rather than a difference, and
    /// picking one of the duplicates would make the comparison quietly describe a recipe that never existed.
    /// </exception>
    public static RecipeComparison Compare(RecipeSnapshotDocument from, RecipeSnapshotDocument to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        from.EnsureReadable();
        to.EnsureReadable();

        return new RecipeComparison
        {
            Sections =
            [
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Metadata,
                    FieldChanges = DiffFields(MetadataFields(from.Recipe), MetadataFields(to.Recipe)),
                    ItemChanges = CompareTags(from.Tags, to.Tags),
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Timing,
                    FieldChanges = DiffFields(TimingFields(from.Recipe), TimingFields(to.Recipe)),
                    ItemChanges = [],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Yield,
                    FieldChanges = DiffFields(YieldFields(from.Recipe), YieldFields(to.Recipe)),
                    ItemChanges = [],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Notes,
                    FieldChanges = DiffFields(NotesFields(from.Recipe), NotesFields(to.Recipe)),
                    ItemChanges = [],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Publication,
                    FieldChanges = DiffFields(PublicationFields(from.Recipe), PublicationFields(to.Recipe)),
                    ItemChanges = [],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Ingredients,
                    FieldChanges = [],

                    // Groups first, then the lines beneath them, so a reader meets the containers before
                    // their contents. Within these two sections a null parent id means the item is a group.
                    ItemChanges =
                    [
                        .. CompareItems(IngredientGroupEntries(from), IngredientGroupEntries(to), IngredientGroupFields),
                        .. CompareItems(IngredientEntries(from), IngredientEntries(to), IngredientFields),
                    ],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Instructions,
                    FieldChanges = [],
                    ItemChanges =
                    [
                        .. CompareItems(InstructionGroupEntries(from), InstructionGroupEntries(to), InstructionGroupFields),
                        .. CompareItems(StepEntries(from), StepEntries(to), StepFields),
                    ],
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Equipment,
                    FieldChanges = [],
                    ItemChanges = CompareItems(EquipmentEntries(from), EquipmentEntries(to), EquipmentFields),
                },
                new RecipeComparisonSectionResult
                {
                    Section = RecipeComparisonSection.Media,
                    FieldChanges = [],
                    ItemChanges = CompareItems(AssetEntries(from), AssetEntries(to), AssetFields),
                },
            ],
        };
    }

    /// <summary>One item as it sits in a document: what it is, whose child it is, and where among its siblings.</summary>
    /// <remarks>
    /// <see cref="Rank"/> is the ordinal position among siblings, assigned while flattening, rather than the
    /// stored <c>SortOrder</c>. The documents arrive already ordered — <c>RecipeSnapshotMapper.Capture</c>
    /// sorts every collection by <c>SortOrder</c> — so flattening preserves the creator's order while
    /// discarding the particular integers it was recorded with.
    /// </remarks>
    private sealed record Entry<T>(Guid Id, Guid? ParentId, int Rank, T Item);

    private static Entry<RecipeSnapshotIngredientGroup>[] IngredientGroupEntries(RecipeSnapshotDocument document) =>
        [.. document.IngredientGroups.Select((group, rank) => new Entry<RecipeSnapshotIngredientGroup>(group.Id, null, rank, group))];

    private static Entry<RecipeSnapshotIngredient>[] IngredientEntries(RecipeSnapshotDocument document) =>
        [.. document.IngredientGroups.SelectMany(group => group.Ingredients.Select((line, rank) =>
            new Entry<RecipeSnapshotIngredient>(line.Id, group.Id, rank, line)))];

    private static Entry<RecipeSnapshotInstructionGroup>[] InstructionGroupEntries(RecipeSnapshotDocument document) =>
        [.. document.InstructionGroups.Select((group, rank) => new Entry<RecipeSnapshotInstructionGroup>(group.Id, null, rank, group))];

    private static Entry<RecipeSnapshotInstructionStep>[] StepEntries(RecipeSnapshotDocument document) =>
        [.. document.InstructionGroups.SelectMany(group => group.Steps.Select((step, rank) =>
            new Entry<RecipeSnapshotInstructionStep>(step.Id, group.Id, rank, step)))];

    private static Entry<RecipeSnapshotEquipment>[] EquipmentEntries(RecipeSnapshotDocument document) =>
        [.. document.Equipment.Select((item, rank) => new Entry<RecipeSnapshotEquipment>(item.Id, null, rank, item))];

    private static Entry<RecipeSnapshotAssetLink>[] AssetEntries(RecipeSnapshotDocument document) =>
        [.. document.AssetLinks.Select((link, rank) => new Entry<RecipeSnapshotAssetLink>(link.Id, null, rank, link))];

    /// <summary>
    /// The whole item algebra: match by id, classify what is only on one side, and for what is on both,
    /// report field changes and movement as independent facts.
    /// </summary>
    /// <remarks>
    /// The result is ordered deterministically: everything that exists in <paramref name="to"/> first, in
    /// that document's order, then everything that only existed in <paramref name="from"/>, in its order.
    /// Removed items sit at the end because there is no position in the later document to interleave them
    /// at without inventing one; a reader that wants them in place has
    /// <see cref="RecipeItemChange.FromRank"/> and <see cref="RecipeItemChange.FromParentId"/> to do it with.
    /// </remarks>
    private static List<RecipeItemChange> CompareItems<T>(
        IReadOnlyList<Entry<T>> from,
        IReadOnlyList<Entry<T>> to,
        Func<T, (RecipeComparisonField Field, object? Value)[]> fields)
    {
        var before = Index(from);
        var after = Index(to);

        var moved = MarkMoves(from, after);
        var changes = new List<RecipeItemChange>();

        foreach (var entry in to)
        {
            if (!before.TryGetValue(entry.Id, out var previous))
            {
                changes.Add(new RecipeItemChange
                {
                    Id = entry.Id,
                    Presence = RecipeItemPresence.Added,
                    Moved = false,
                    FromParentId = null,
                    ToParentId = entry.ParentId,
                    FromRank = null,
                    ToRank = entry.Rank,
                    FieldChanges = DescribeFields(fields(entry.Item), RecipeItemPresence.Added),
                });

                continue;
            }

            var fieldChanges = DiffFields(fields(previous.Item), fields(entry.Item));
            var hasMoved = moved.Contains(entry.Id);

            // An item that neither moved nor changed is not a change. Reporting it would make every
            // comparison the size of the recipe and bury the handful of things a reviewer is looking for.
            if (fieldChanges.Count == 0 && !hasMoved)
            {
                continue;
            }

            changes.Add(new RecipeItemChange
            {
                Id = entry.Id,
                Presence = RecipeItemPresence.Retained,
                Moved = hasMoved,
                FromParentId = previous.ParentId,
                ToParentId = entry.ParentId,
                FromRank = previous.Rank,
                ToRank = entry.Rank,
                FieldChanges = fieldChanges,
            });
        }

        foreach (var entry in from.Where(entry => !after.ContainsKey(entry.Id)))
        {
            changes.Add(new RecipeItemChange
            {
                Id = entry.Id,
                Presence = RecipeItemPresence.Removed,
                Moved = false,
                FromParentId = entry.ParentId,
                ToParentId = null,
                FromRank = entry.Rank,
                ToRank = null,
                FieldChanges = DescribeFields(fields(entry.Item), RecipeItemPresence.Removed),
            });
        }

        return changes;
    }

    private static Dictionary<Guid, Entry<T>> Index<T>(IReadOnlyList<Entry<T>> entries)
    {
        var index = new Dictionary<Guid, Entry<T>>(entries.Count);

        foreach (var entry in entries)
        {
            if (!index.TryAdd(entry.Id, entry))
            {
                throw new InvalidOperationException(
                    $"Recipe snapshot repeats the identifier {entry.Id:D}. Identifiers are what a comparison "
                        + "matches on, so a document that repeats one cannot be compared — it is a corrupt "
                        + "archive rather than a recipe that differs.");
            }
        }

        return index;
    }

    /// <summary>
    /// Decides which surviving items actually moved, as opposed to merely being pushed along by something
    /// else that was added or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comparing ranks directly is the obvious implementation and it is wrong. Delete the first line of a
    /// group and every line beneath it shifts by one, so a rank comparison reports the whole group as moved
    /// when in truth nothing changed places relative to anything else. Movement only means something
    /// relative to the items that survived alongside it.
    /// </para>
    /// <para>
    /// So: an item whose parent changed has moved, full stop, and is set aside. The rest are bucketed by
    /// the parent they share on both sides, and within each bucket the longest increasing subsequence of
    /// their later positions is taken as the part that stayed put. Everything outside it moved. Dragging
    /// one line from the top of a group of ten to the bottom therefore reports one move rather than ten,
    /// which is the case a comparison panel has to get right to be readable at all.
    /// </para>
    /// <para>
    /// The subsequence is computed per parent rather than across the document, so reordering two ingredient
    /// groups reports the two groups as moved and says nothing about the lines they carry with them.
    /// </para>
    /// <para>
    /// Where several answers are equally minimal — a straight swap of two adjacent lines can be described
    /// as either one having moved — this picks one deterministically rather than reporting both. The
    /// selection is whatever patience sorting yields over the earlier document's order, which is fixed, so
    /// the same pair of documents always produces the same answer. It is minimal in the number of items
    /// marked, not unique.
    /// </para>
    /// </remarks>
    private static HashSet<Guid> MarkMoves<T>(IReadOnlyList<Entry<T>> from, Dictionary<Guid, Entry<T>> after)
    {
        var moved = new HashSet<Guid>();
        var buckets = new Dictionary<Guid, List<Entry<T>>>();

        // Walk the earlier document so that each bucket is already in "before" order.
        foreach (var entry in from)
        {
            if (!after.TryGetValue(entry.Id, out var later))
            {
                continue;
            }

            if (entry.ParentId != later.ParentId)
            {
                moved.Add(entry.Id);
                continue;
            }

            // A null parent is the recipe itself, which owns equipment, asset links and the groups. Guid.Empty
            // stands in for it as a bucket key and cannot collide with a real group id.
            var key = entry.ParentId ?? Guid.Empty;

            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = bucket = [];
            }

            bucket.Add(entry);
        }

        foreach (var bucket in buckets.Values)
        {
            var laterRanks = bucket.Select(entry => after[entry.Id].Rank).ToArray();
            var stable = LongestIncreasingSubsequence(laterRanks);

            for (var i = 0; i < bucket.Count; i++)
            {
                if (!stable.Contains(i))
                {
                    moved.Add(bucket[i].Id);
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// The indices of a longest strictly increasing subsequence of <paramref name="values"/>, by patience
    /// sorting. Deterministic for a given input.
    /// </summary>
    private static HashSet<int> LongestIncreasingSubsequence(IReadOnlyList<int> values)
    {
        // tails[k] is the index of the smallest value that ends an increasing subsequence of length k + 1.
        var tails = new List<int>();
        var previous = new int[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            var low = 0;
            var high = tails.Count;

            while (low < high)
            {
                var middle = (low + high) / 2;

                if (values[tails[middle]] < values[i])
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            previous[i] = low > 0 ? tails[low - 1] : -1;

            if (low == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[low] = i;
            }
        }

        var subsequence = new HashSet<int>();

        for (var index = tails.Count > 0 ? tails[^1] : -1; index >= 0; index = previous[index])
        {
            subsequence.Add(index);
        }

        return subsequence;
    }

    /// <summary>
    /// Tags, which are a set rather than a sequence.
    /// </summary>
    /// <remarks>
    /// <see cref="RecipeSnapshotTag"/> carries a workspace tag id and nothing else — no order, no other
    /// field — so a tag can only be added or removed. It cannot be edited, because there is nothing on it to
    /// edit, and it cannot move, because tags have no order to move within. Running the ordered algebra over
    /// them would produce ranks that mean nothing and invite a reader to believe them, so this does not.
    /// </remarks>
    private static List<RecipeItemChange> CompareTags(
        IReadOnlyList<RecipeSnapshotTag> from,
        IReadOnlyList<RecipeSnapshotTag> to)
    {
        var before = from.Select(tag => tag.WorkspaceTagId).ToHashSet();
        var after = to.Select(tag => tag.WorkspaceTagId).ToHashSet();

        List<RecipeItemChange> changes = [];

        foreach (var id in to.Select(tag => tag.WorkspaceTagId).Where(id => !before.Contains(id)))
        {
            changes.Add(TagChange(id, RecipeItemPresence.Added));
        }

        foreach (var id in from.Select(tag => tag.WorkspaceTagId).Where(id => !after.Contains(id)))
        {
            changes.Add(TagChange(id, RecipeItemPresence.Removed));
        }

        return changes;
    }

    private static RecipeItemChange TagChange(Guid workspaceTagId, RecipeItemPresence presence) => new()
    {
        Id = workspaceTagId,
        Presence = presence,
        Moved = false,
        FromParentId = null,
        ToParentId = null,
        FromRank = null,
        ToRank = null,
        FieldChanges =
        [
            new RecipeFieldChange
            {
                Field = RecipeComparisonField.TagWorkspaceTagId,
                From = presence == RecipeItemPresence.Removed ? Render(workspaceTagId) : null,
                To = presence == RecipeItemPresence.Added ? Render(workspaceTagId) : null,
            },
        ],
    };

    private static (RecipeComparisonField Field, object? Value)[] MetadataFields(RecipeSnapshotHeader header) =>
    [
        (RecipeComparisonField.Title, header.Title),
        (RecipeComparisonField.Description, header.Description),
        (RecipeComparisonField.AttributionText, header.AttributionText),
        (RecipeComparisonField.SourceUrl, header.SourceUrl),
        (RecipeComparisonField.CuisineId, header.CuisineId),
        (RecipeComparisonField.CourseId, header.CourseId),
        (RecipeComparisonField.PrimaryTechniqueId, header.PrimaryTechniqueId),
    ];

    private static (RecipeComparisonField Field, object? Value)[] TimingFields(RecipeSnapshotHeader header) =>
    [
        (RecipeComparisonField.PrepTimeMinutes, header.PrepTimeMinutes),
        (RecipeComparisonField.CookTimeMinutes, header.CookTimeMinutes),
        (RecipeComparisonField.RestTimeMinutes, header.RestTimeMinutes),
        (RecipeComparisonField.TotalTimeMinutes, header.TotalTimeMinutes),
    ];

    private static (RecipeComparisonField Field, object? Value)[] YieldFields(RecipeSnapshotHeader header) =>
    [
        (RecipeComparisonField.YieldText, header.YieldText),
        (RecipeComparisonField.YieldQuantity, header.YieldQuantity),
        (RecipeComparisonField.YieldUnitId, header.YieldUnitId),
        (RecipeComparisonField.YieldUnitDimension, header.YieldUnitDimension),
    ];

    private static (RecipeComparisonField Field, object? Value)[] NotesFields(RecipeSnapshotHeader header) =>
    [
        (RecipeComparisonField.Headnote, header.Headnote),
        (RecipeComparisonField.Notes, header.Notes),
        (RecipeComparisonField.StorageNotes, header.StorageNotes),
    ];

    private static (RecipeComparisonField Field, object? Value)[] PublicationFields(RecipeSnapshotHeader header) =>
    [
        (RecipeComparisonField.Status, header.Status),
    ];

    private static (RecipeComparisonField Field, object? Value)[] IngredientGroupFields(RecipeSnapshotIngredientGroup group) =>
    [
        (RecipeComparisonField.IngredientGroupTitle, group.Title),
    ];

    private static (RecipeComparisonField Field, object? Value)[] IngredientFields(RecipeSnapshotIngredient line) =>
    [
        (RecipeComparisonField.IngredientDisplayText, line.DisplayText),
        (RecipeComparisonField.IngredientNameText, line.IngredientNameText),
        (RecipeComparisonField.IngredientQuantity, line.Quantity),
        (RecipeComparisonField.IngredientQuantityUpper, line.QuantityUpper),
        (RecipeComparisonField.IngredientMeasurementUnitId, line.MeasurementUnitId),
        (RecipeComparisonField.IngredientMeasurementUnitDimension, line.MeasurementUnitDimension),
        (RecipeComparisonField.IngredientReferenceId, line.IngredientId),
        (RecipeComparisonField.IngredientMatchStatus, line.MatchStatus),
        (RecipeComparisonField.IngredientPreparationNote, line.PreparationNote),
        (RecipeComparisonField.IngredientIsOptional, line.IsOptional),
        (RecipeComparisonField.IngredientScalingBehavior, line.ScalingBehavior),
    ];

    private static (RecipeComparisonField Field, object? Value)[] InstructionGroupFields(RecipeSnapshotInstructionGroup group) =>
    [
        (RecipeComparisonField.InstructionGroupTitle, group.Title),
    ];

    private static (RecipeComparisonField Field, object? Value)[] StepFields(RecipeSnapshotInstructionStep step) =>
    [
        (RecipeComparisonField.StepText, step.Text),
        (RecipeComparisonField.StepTechniqueId, step.TechniqueId),
        (RecipeComparisonField.StepDurationMinutes, step.DurationMinutes),
        (RecipeComparisonField.StepTemperatureValue, step.TemperatureValue),
        (RecipeComparisonField.StepTemperatureUnitId, step.TemperatureUnitId),
        (RecipeComparisonField.StepTemperatureUnitDimension, step.TemperatureUnitDimension),
        (RecipeComparisonField.StepNote, step.Note),
    ];

    private static (RecipeComparisonField Field, object? Value)[] EquipmentFields(RecipeSnapshotEquipment equipment) =>
    [
        (RecipeComparisonField.EquipmentDisplayText, equipment.DisplayText),
        (RecipeComparisonField.EquipmentTypeId, equipment.EquipmentTypeId),
        (RecipeComparisonField.EquipmentIsOptional, equipment.IsOptional),
        (RecipeComparisonField.EquipmentNote, equipment.Note),
    ];

    private static (RecipeComparisonField Field, object? Value)[] AssetFields(RecipeSnapshotAssetLink link) =>
    [
        (RecipeComparisonField.AssetMediaAssetId, link.MediaAssetId),
        (RecipeComparisonField.AssetRole, link.Role),
        (RecipeComparisonField.AssetCaption, link.Caption),
    ];

    /// <summary>
    /// The fields that differ between two versions of the same thing.
    /// </summary>
    /// <remarks>
    /// Both sides come from the same extraction method, so the arrays are the same fields in the same order
    /// and can be walked in step. Comparison is on the boxed values rather than on their rendered strings,
    /// which is what makes <c>240</c> and <c>240.0</c> the same quantity and keeps a decimal scale that
    /// survived a serialization round trip from reading as an edit.
    /// </remarks>
    private static List<RecipeFieldChange> DiffFields(
        (RecipeComparisonField Field, object? Value)[] from,
        (RecipeComparisonField Field, object? Value)[] to)
    {
        var changes = new List<RecipeFieldChange>();

        for (var i = 0; i < from.Length; i++)
        {
            if (Equals(from[i].Value, to[i].Value))
            {
                continue;
            }

            changes.Add(new RecipeFieldChange
            {
                Field = from[i].Field,
                From = Render(from[i].Value),
                To = Render(to[i].Value),
            });
        }

        return changes;
    }

    /// <summary>The content of an item that exists on only one side, so a reader can render what appeared or vanished.</summary>
    private static List<RecipeFieldChange> DescribeFields(
        (RecipeComparisonField Field, object? Value)[] fields,
        RecipeItemPresence presence)
    {
        var added = presence == RecipeItemPresence.Added;

        return
        [
            .. fields
                .Where(field => !IsAbsent(field.Value))
                .Select(field => new RecipeFieldChange
                {
                    Field = field.Field,
                    From = added ? null : Render(field.Value),
                    To = added ? Render(field.Value) : null,
                }),
        ];
    }

    /// <summary>
    /// Whether a value says nothing — used only when describing an item that exists on one side.
    /// </summary>
    /// <remarks>
    /// An added line announcing <c>IsOptional: false</c> and <c>MatchStatus: NotAttempted</c> is noise: it
    /// reports the absence of a claim as though it were one. So a false flag, an empty string and an enum's
    /// zero member are treated as unsaid here. This applies to additions and removals only — a retained item
    /// is compared field by field with no suppression at all, so turning a flag off is reported exactly like
    /// turning it on. Numbers and identifiers are never suppressed: a zero quantity is a thing a creator
    /// wrote, not a default they never touched.
    /// </remarks>
    private static bool IsAbsent(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        bool flag => !flag,
        Enum member => Convert.ToInt64(member, CultureInfo.InvariantCulture) == 0,
        _ => false,
    };

    /// <summary>Renders an established difference for reading. Never used to decide whether one exists.</summary>
    private static string? Render(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        Guid id => id.ToString("D", CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        Enum member => member.ToString(),
        _ => throw new NotSupportedException(
            $"Recipe comparison has no rendering for {value.GetType()}. A field was added to the snapshot "
                + "whose type this has not been taught to read; teach it here rather than letting the value "
                + "reach a creator as a type name."),
    };
}
