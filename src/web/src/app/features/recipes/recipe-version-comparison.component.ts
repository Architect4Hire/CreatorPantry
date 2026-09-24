import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CpDiffKind, CpDiffLegendComponent, cpDiffGlyph, cpDiffLabel } from '@creator-pantry/ui';

import {
  RecipeComparisonSectionResult,
  RecipeFieldChange,
  RecipeItemChange,
  RecipeVersionComparison,
} from '../../models/recipe-version.models';

/** One line a reader sees: what kind of change it is, what it is about, and the two values. */
interface ComparisonRow {
  readonly key: string;
  readonly kind: CpDiffKind;
  /** The legend's own wording for {@link kind}, rendered as text beside the glyph rather than implied by colour. */
  readonly kindLabel: string;
  readonly label: string;
  readonly from: string | null;
  readonly to: string | null;
  /**
   * Present when a retained item also changed position. Separate from {@link kind} so that a move is never
   * reported as a replacement, and an item that was both reworded and dragged says both.
   */
  readonly movement: string | null;
}

interface ComparisonSectionView {
  readonly key: string;
  readonly heading: string;
  readonly rows: readonly ComparisonRow[];
}

/** Makes each panel's heading id unique, so `aria-labelledby` cannot point at another instance's heading. */
let nextInstance = 0;

/**
 * Field names whose derived wording reads badly. Everything else is humanised from the server's name, so a
 * field added to the snapshot renders sensibly the day it ships rather than waiting for a map to be updated.
 */
const FIELD_LABELS: Readonly<Record<string, string>> = {
  IngredientReferenceId: 'Matched ingredient',
  IngredientNameText: 'Ingredient name',
  IngredientDisplayText: 'Ingredient',
  IngredientGroupTitle: 'Ingredient group',
  InstructionGroupTitle: 'Instruction group',
  TagWorkspaceTagId: 'Tag',
  AssetMediaAssetId: 'Media asset',
  AssetRole: 'Media role',
  AssetCaption: 'Media caption',
  SourceUrl: 'Source URL',
  StepText: 'Step',
  EquipmentDisplayText: 'Equipment',
};

/**
 * Section headings that differ from the server's name. Only one does: `Publication` holds the recipe's
 * editorial status and nothing about any provider, and content.md is explicit that status does not mean
 * anything was delivered anywhere — so a creator reads "Status" rather than a word that implies it was.
 */
const SECTION_HEADINGS: Readonly<Record<string, string>> = {
  Publication: 'Status',
};

/**
 * "IngredientQuantityUpper" -> "Ingredient quantity upper", "YieldUnitId" -> "Yield unit".
 *
 * The trailing "Id" is dropped because it names how the value is stored rather than what changed: a creator
 * reads "Cuisine", not "Cuisine id". The value beside it is still the raw identifier — resolving vocabulary
 * to display names needs reads the comparison route deliberately does not perform.
 */
function humanise(name: string): string {
  const spaced = name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\bId\b/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/**
 * The names of the two sections whose items are nested, so that an item with nothing else to call itself by
 * can be named from its section and whether it is a group.
 */
const ITEM_NAMES: Readonly<Record<string, { readonly group: string; readonly child: string }>> = {
  Ingredients: { group: 'Ingredient group', child: 'Ingredient' },
  Instructions: { group: 'Instruction group', child: 'Step' },
  Equipment: { group: 'Equipment', child: 'Equipment' },
  Media: { group: 'Media asset', child: 'Media asset' },
  Metadata: { group: 'Tag', child: 'Tag' },
};

/**
 * What to call an item that has no field changes to name itself by — one that only moved, or one that
 * arrived or left carrying nothing but defaults.
 *
 * Within the two nested sections a null parent means the item is a group, which is the discriminator the
 * comparison contract states. Elsewhere there are no groups and the two names are the same.
 */
function labelForItem(section: string, item: RecipeItemChange): string {
  const names = ITEM_NAMES[section];
  if (!names) return humanise(section);

  return item.fromParentId === null && item.toParentId === null ? names.group : names.child;
}

function labelForField(field: string): string {
  return FIELD_LABELS[field] ?? humanise(field);
}

/**
 * Where a retained item went.
 *
 * The word "Moved" is prefixed only when the row's own kind is not already `moved` — a row that moved and
 * nothing else already prints "Moved" in its kind label, and repeating it reads as "Moved · Ingredient ·
 * Moved from position 3 to 1". On a `changed` row the word has to be here, because nothing else says it.
 */
function describeMovement(item: RecipeItemChange, kind: CpDiffKind): string {
  // A changed parent is the fact worth stating; the ranks beneath it are positions within two different
  // groups and would read as a contradiction without it.
  const where =
    item.fromParentId !== item.toParentId
      ? 'to another group'
      : // Ranks are 0-based on the wire and 1-based to a reader, who counts from the first line, not zero.
        `from position ${(item.fromRank ?? 0) + 1} to ${(item.toRank ?? 0) + 1}`;

  return kind === 'moved' ? where : `${cpDiffLabel('moved')} ${where}`;
}

/**
 * Renders a version comparison the server calculated. Presentation only.
 *
 * It derives no difference of its own: it re-orders nothing, matches nothing, and decides nothing about what
 * counts as a move. The sections arrive in a fixed order with every one present whether or not it changed,
 * and this walks that array as given — a second, competing diff computed here could disagree with the server
 * about what a creator is approving.
 *
 * Every row states its kind in words beside the glyph, and the glyph comes from the same library lookup the
 * legend above it explains. Nothing is conveyed by colour alone (WCAG 2.2 AA, 1.4.1).
 */
@Component({
  selector: 'cp-recipe-version-comparison',
  standalone: true,
  imports: [CpDiffLegendComponent],
  templateUrl: './recipe-version-comparison.component.html',
  styleUrl: './recipe-version-comparison.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeVersionComparisonComponent {
  readonly comparison = input.required<RecipeVersionComparison>();

  /** Only the kinds this panel can actually render, so the legend explains nothing a reader will not meet. */
  readonly legendKinds: CpDiffKind[] = ['added', 'removed', 'changed', 'moved'];

  /** Per-instance, so two panels on one page cannot name each other's heading. */
  readonly headingId = `cp-recipe-comparison-${nextInstance++}`;

  readonly hasChanges = computed(() => this.comparison().comparison.hasChanges);

  readonly sections = computed<readonly ComparisonSectionView[]>(() =>
    this.comparison().comparison.sections.map((section) => ({
      key: section.section,
      heading: SECTION_HEADINGS[section.section] ?? humanise(section.section),
      rows: this.rowsFor(section),
    })),
  );

  private rowsFor(section: RecipeComparisonSectionResult): readonly ComparisonRow[] {
    return [
      ...section.fieldChanges.map((change) => this.fieldRow(section.section, change)),
      ...section.itemChanges.flatMap((item) => this.itemRows(section.section, item)),
    ];
  }

  /** A change to one of the recipe's own fields. Always a replacement — a field has no position to move to. */
  private fieldRow(section: string, change: RecipeFieldChange): ComparisonRow {
    return {
      key: `${section}:field:${change.field}`,
      kind: 'changed',
      kindLabel: cpDiffLabel('changed'),
      label: labelForField(change.field),
      from: change.from,
      to: change.to,
      movement: null,
    };
  }

  /**
   * One identified item, as one row per field it changed — or a single row when it only moved, or when it
   * arrived or left carrying nothing worth printing.
   */
  private itemRows(section: string, item: RecipeItemChange): readonly ComparisonRow[] {
    const kind: CpDiffKind =
      item.presence === 'Added' ? 'added' : item.presence === 'Removed' ? 'removed' : item.moved && item.fieldChanges.length === 0 ? 'moved' : 'changed';

    const movement = item.presence === 'Retained' && item.moved ? describeMovement(item, kind) : null;

    if (item.fieldChanges.length === 0) {
      return [
        {
          key: `${section}:item:${item.id}`,
          kind,
          kindLabel: cpDiffLabel(kind),
          label: labelForItem(section, item),
          from: null,
          to: null,
          movement,
        },
      ];
    }

    // The movement note rides on the first row only: repeating "moved from position 3 to 1" beside every
    // field a line changed would say the same thing four times about one drag.
    return item.fieldChanges.map((change, index) => ({
      key: `${section}:item:${item.id}:${change.field}`,
      kind,
      kindLabel: cpDiffLabel(kind),
      label: labelForField(change.field),
      from: change.from,
      to: change.to,
      movement: index === 0 ? movement : null,
    }));
  }

  glyphFor(kind: CpDiffKind): string {
    return cpDiffGlyph(kind);
  }
}
