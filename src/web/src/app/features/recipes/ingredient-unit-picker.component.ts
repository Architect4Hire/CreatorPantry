import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CpComboboxComponent, CpComboboxOption } from '@creator-pantry/ui';

import { UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { UnitCatalogueState, recipeUnitOptions } from './recipe-unit-options';

/**
 * The unit control on one ingredient line.
 *
 * Its own component rather than a second use of the yield field's, because the two are the same widget doing
 * different jobs. A yield unit is an id or nothing, so its box is `restricted` and unrecognised text reverts.
 * An ingredient line is the creator's own wording first (recipes.md): "a glug", "1 bunch", "to taste" all have
 * to stay typeable and saveable, so this box is `free-text` and what it holds is kept whether or not the
 * catalogue knows the word. Sharing one control would have meant a mode flag hiding two different promises
 * about the creator's text.
 *
 * A line linked to a catalogue unit shows that unit's own name in the box — "pound", not the "lb" a generated
 * draft wrote — the same name a creator sees when they pick it from the list. Only the display: the line's word
 * is still what {@link unitLabel} carries and what a save keeps (recipes.md), and the editor says it under the
 * field when the two differ. Text the creator types replaces it exactly as before.
 *
 * It reads no data and holds no state. The catalogue arrives as {@link catalogue}, the line's current unit as
 * {@link unitId}/{@link unitLabel}, and what the creator does goes back out — the match machinery that decides
 * what a pick or an edit means to a recipe line stays with the editor that owns the line.
 */
@Component({
  selector: 'cp-ingredient-unit-picker',
  standalone: true,
  imports: [CpComboboxComponent],
  template: `<cp-combobox
    [inputId]="inputId()"
    [options]="options()"
    mode="free-text"
    placeholder="e.g. cups"
    [value]="shownText()"
    (valueChange)="textChanged.emit($event)"
    [selected]="selection()"
    (selectedChange)="onSelected($event)"
  >
    <span cpComboboxEmpty>{{ emptyText() }}</span>
  </cp-combobox>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngredientUnitPickerComponent {
  /** The wrapping `cp-field`'s `forId`, so that field's label labels the real input. */
  readonly inputId = input.required<string>();

  readonly catalogue = input<UnitCatalogueState>({ status: 'loading' });

  /** The unit this line is matched to, or null. Presentation only; the line's own record stays with the editor. */
  readonly unitId = input<string | null>(null);

  /** The creator's own wording for the unit, which is what the box shows and what a save keeps. */
  readonly unitLabel = input('');

  readonly textChanged = output<string>();

  /**
   * A unit the creator picked, or that the combobox resolved their typing to exactly.
   *
   * `displayName` is the label they were shown, not the catalogue's own — someone who picks "cup" off a list
   * that said "cup" has written "cup", and storing "US cup" as their wording would be putting words in their
   * mouth.
   */
  readonly unitPicked = output<UnitMatchCandidate>();

  protected readonly options = computed<readonly CpComboboxOption[]>(() => {
    const catalogue = this.catalogue();

    return catalogue.status === 'ready' ? recipeUnitOptions(catalogue.units) : [];
  });

  /** Which row the box shows as chosen. Null when nothing is set, or when the catalogue cannot name it. */
  protected readonly selection = computed<CpComboboxOption | null>(() => {
    const unitId = this.unitId();
    if (unitId === null) return null;

    return this.options().find((option) => option.id === unitId) ?? null;
  });

  /**
   * What the box shows: the linked catalogue unit's name when there is one and the list can name it, otherwise the
   * line's own word. The combobox only reports text the creator types or picks, so showing the name is not an edit.
   */
  protected readonly shownText = computed(() => this.selection()?.label ?? this.unitLabel());

  /** What an empty list means, which is not always "there is no such unit". */
  protected readonly emptyText = computed(() => {
    switch (this.catalogue().status) {
      case 'loading':
        return 'Loading the unit list…';
      case 'unavailable':
        return 'Unit list unavailable — the unit you type is still saved as you wrote it.';
      default:
        return 'No unit by that name — it will be saved as you wrote it.';
    }
  });

  /**
   * A null selection is swallowed here rather than passed on.
   *
   * The combobox recomputes its selection from whatever list it holds whenever the field is left, so a null
   * arriving on blur says nothing about what the creator wants. Giving a match up follows changed text, which
   * the editor reads from {@link textChanged}.
   */
  protected onSelected(option: CpComboboxOption | null): void {
    if (option === null) return;

    this.unitPicked.emit({ measurementUnitId: option.id, displayName: option.label, kind: 'DisplayName' });
  }
}
