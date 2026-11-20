import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';

import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RuntimeConfigService } from '../../core/runtime-config.service';
import {
  EditableIngredientGroup,
  INGREDIENT_SEARCH_DEBOUNCE_MS,
  composeDisplayText,
  RecipeIngredientEditorComponent,
} from './recipe-ingredient-editor.component';

async function createFixture(): Promise<ComponentFixture<RecipeIngredientEditorComponent>> {
  await TestBed.configureTestingModule({
    imports: [RecipeIngredientEditorComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const fixture = TestBed.createComponent(RecipeIngredientEditorComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.detectChanges();
  return fixture;
}

/**
 * Opens every closed line by its Edit button, as a creator would: a list opens with its lines closed to their words,
 * and a test about a line's fields has to open it first.
 */
function openEveryLine(fixture: ComponentFixture<RecipeIngredientEditorComponent>): void {
  const element = fixture.nativeElement as HTMLElement;
  element
    .querySelectorAll<HTMLButtonElement>('button[aria-label^="Edit ingredient"][aria-expanded="false"]')
    .forEach((button) => button.click());
  fixture.detectChanges();
}

const SEEDED_GROUP: RecipeIngredientGroup = {
  id: 'group1',
  title: 'For the crust',
  sortOrder: 0,
  ingredients: [
    {
      id: 'ing1',
      sortOrder: 0,
      displayText: '2 cups flour',
      displayTextSource: 'Creator',
      ingredientNameText: 'flour',
      unitText: 'cups',
      quantity: 2,
      quantityUpper: null,
      measurementUnitId: 'unit1',
      ingredientId: 'ref1',
      matchStatus: 'Matched',
      preparationNote: null,
      isOptional: false,
      scalingBehavior: 'Proportional',
    },
    {
      id: 'ing2',
      sortOrder: 1,
      displayText: '1 tsp salt',
      displayTextSource: 'Creator',
      ingredientNameText: 'salt',
      unitText: 'tsp',
      quantity: 1,
      quantityUpper: null,
      measurementUnitId: 'unit2',
      ingredientId: null,
      matchStatus: 'NoMatch',
      preparationNote: null,
      isOptional: false,
      scalingBehavior: 'Proportional',
    },
  ],
};

describe('RecipeIngredientEditorComponent', () => {
  it('shows an empty state when there are no ingredients', async () => {
    const fixture = await createFixture();
    expect(fixture.nativeElement.textContent).toContain('No ingredients yet');
  });

  it('seeds editable groups and rows from initialGroups, mapping server matchStatus to a UI matchState', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    expect(fixture.componentInstance.groups().length).toBe(1);
    const [group] = fixture.componentInstance.groups();
    expect(group.title).toBe('For the crust');
    expect(group.ingredients.map((row) => row.matchState)).toEqual(['matched', 'unmatched']);
  });

  it('renders match confidence as a status pill with required text, never color alone', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();
    openEveryLine(fixture);

    const pills = fixture.nativeElement.querySelectorAll('cp-status-pill');
    expect(pills.length).toBeGreaterThanOrEqual(2);
    // Every status pill's tone is backed by required text content — not merely a background color.
    pills.forEach((pill: HTMLElement) => expect(pill.textContent?.trim().length).toBeGreaterThan(0));
    expect(pills[0].textContent).toContain('Matched');
    expect(pills[1].textContent).toContain('No match');
  });

  /** A list reads as its ingredients; the creator opens the one line they want to change. */
  describe('lines closed to their words', () => {
    async function loaded(): Promise<ComponentFixture<RecipeIngredientEditorComponent>> {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();
      return fixture;
    }

    const element = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) =>
      fixture.nativeElement as HTMLElement;
    const editButton = (fixture: ComponentFixture<RecipeIngredientEditorComponent>, n: number) =>
      element(fixture).querySelector<HTMLButtonElement>(`button[aria-label="Edit ingredient ${n}"]`)!;

    it('opens with every line closed to its words, its buttons beside them and no fields', async () => {
      const fixture = await loaded();

      const lines = element(fixture).querySelectorAll('.row');
      expect(lines.length).toBe(2);
      expect(lines[0].querySelector('.line-text')?.textContent?.trim()).toBe('2 cups flour');
      expect(lines[1].querySelector('.line-text')?.textContent?.trim()).toBe('1 tsp salt');
      expect(element(fixture).querySelector('[id^="row-quantity-"]')).toBeNull();
      expect(element(fixture).querySelector('.row-status')).toBeNull();

      for (const label of ['Move ingredient 1', 'Edit ingredient 1', 'Remove ingredient 1']) {
        expect(element(fixture).querySelector(`button[aria-label="${label}"]`))
          .withContext(label)
          .not.toBeNull();
      }
      expect(editButton(fixture, 1).getAttribute('aria-expanded')).toBe('false');
      expect(editButton(fixture, 1).textContent?.trim()).toBe('✎');
    });

    it('opens one line’s fields with its Edit button, and closes them again keeping what was typed', async () => {
      const fixture = await loaded();

      editButton(fixture, 1).click();
      fixture.detectChanges();
      expect(editButton(fixture, 1).getAttribute('aria-expanded')).toBe('true');
      expect(editButton(fixture, 1).textContent?.trim()).toBe('✓');
      expect(element(fixture).querySelector('#row-quantity-ing1')).not.toBeNull();
      // Only that line: the other stays closed.
      expect(element(fixture).querySelector('#row-quantity-ing2')).toBeNull();
      expect(editButton(fixture, 2).getAttribute('aria-expanded')).toBe('false');

      const prep = element(fixture).querySelector<HTMLTextAreaElement>('#row-prep-ing1')!;
      prep.value = 'sifted';
      prep.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      editButton(fixture, 1).click();
      fixture.detectChanges();
      expect(element(fixture).querySelector('#row-quantity-ing1')).toBeNull();
      expect(editButton(fixture, 1).getAttribute('aria-expanded')).toBe('false');
      expect(fixture.componentInstance.groups()[0].ingredients[0].preparationNote).toBe('sifted');
    });

    it('opens a new line, since it has no words yet and the creator is about to fill it in', async () => {
      const fixture = await loaded();
      fixture.componentInstance.addRow(fixture.componentInstance.groups()[0].key);
      fixture.detectChanges();

      expect(editButton(fixture, 3).getAttribute('aria-expanded')).toBe('true');
      const newKey = fixture.componentInstance.groups()[0].ingredients[2].key;
      expect(element(fixture).querySelector(`#row-quantity-${newKey}`)).not.toBeNull();
      expect(element(fixture).querySelectorAll('.row')[2].querySelector('.line-text')?.textContent).toContain(
        'not filled in yet',
      );
    });

    it('opens a line the server refused, so its sentence and its fields are never folded away', async () => {
      const fixture = await loaded();
      fixture.componentRef.setInput('rowErrors', new Map([['ing2', ['Give this line an ingredient.']]]));
      fixture.detectChanges();

      expect(editButton(fixture, 2).getAttribute('aria-expanded')).toBe('true');
      expect(element(fixture).querySelector('.row-problem')?.textContent).toContain('Give this line an ingredient.');
      expect(element(fixture).querySelector('#row-quantity-ing2')).not.toBeNull();
      expect(editButton(fixture, 1).getAttribute('aria-expanded')).toBe('false');
    });

    it('keeps saying on a closed line what still needs settling, and nothing about a line that is fine', async () => {
      const fixture = await loaded();
      const lines = element(fixture).querySelectorAll('.row');

      expect(lines[0].querySelector('.row-attention')).toBeNull();
      expect(lines[1].querySelector('.row-attention')?.textContent).toContain('No match — review needed');
    });
  });

  describe('units CreatorPantry does not recognise', () => {
    /** An AI draft's line whose unit words matched nothing in the catalogue: words, and no unit behind them. */
    const UNRECOGNISED_GROUP: RecipeIngredientGroup = {
      ...SEEDED_GROUP,
      ingredients: [
        ...SEEDED_GROUP.ingredients,
        {
          ...SEEDED_GROUP.ingredients[0],
          id: 'ing3',
          sortOrder: 2,
          displayText: '1 knob butter',
          ingredientNameText: 'butter',
          unitText: 'knob',
          quantity: 1,
          measurementUnitId: null,
        },
        {
          ...SEEDED_GROUP.ingredients[0],
          id: 'ing4',
          sortOrder: 3,
          displayText: '3 eggs',
          ingredientNameText: 'eggs',
          unitText: null,
          quantity: 3,
          measurementUnitId: null,
        },
      ],
    };

    async function seeded(): Promise<ComponentFixture<RecipeIngredientEditorComponent>> {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [UNRECOGNISED_GROUP]);
      fixture.detectChanges();
      openEveryLine(fixture);
      return fixture;
    }

    function warningFor(fixture: ComponentFixture<RecipeIngredientEditorComponent>, key: string): string {
      const element: HTMLElement | null = fixture.nativeElement.querySelector(`#row-unit-${key}-warning`);
      return element?.textContent?.trim() ?? '';
    }

    // The warning under the Unit field is folded away with the line, so the closed line says it for itself.
    it('says so on a closed line too, without opening it', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [UNRECOGNISED_GROUP]);
      fixture.detectChanges();

      const lines = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.row'));
      const butter = lines.find((line) => line.textContent?.includes('1 knob butter'))!;
      const eggs = lines.find((line) => line.textContent?.includes('3 eggs'))!;
      expect(butter.querySelector('#row-unit-ing3')).toBeNull();
      expect(butter.querySelector('.row-attention')?.textContent).toContain('Unit not recognised');
      expect(eggs.querySelector('.row-attention')).toBeNull();
    });

    it('marks the line under its Unit field, quoting the word and saying what it costs', async () => {
      const fixture = await seeded();

      const warning = warningFor(fixture, 'ing3');
      expect(warning).toContain('“knob” isn’t a CreatorPantry unit');
      expect(warning).toContain('won’t scale or convert');
      // Described by the warning, so a screen reader hears it with the field — not colour or position alone.
      const input: HTMLElement = fixture.nativeElement.querySelector('#row-unit-ing3');
      expect(input.getAttribute('aria-describedby')).toContain('row-unit-ing3-warning');
    });

    it('leaves recognised units and lines with no unit alone', async () => {
      const fixture = await seeded();

      expect(warningFor(fixture, 'ing1')).toBe('');
      expect(warningFor(fixture, 'ing4')).toBe('');
    });

    it('counts every such line once, at the top of the list', async () => {
      const fixture = await seeded();

      const notice: HTMLElement = fixture.nativeElement.querySelector('cp-notice.unit-notice');
      expect(notice.textContent).toContain('1 ingredient uses a unit CreatorPantry doesn’t');
    });

    it('clears the mark once the creator picks a catalogue unit for the line', async () => {
      const fixture = await seeded();
      const group = fixture.componentInstance.groups()[0];

      fixture.componentInstance.onUnitPicked(group.key, 'ing3', {
        measurementUnitId: 'unit-tbsp',
        displayName: 'tbsp',
        kind: 'DisplayName',
      });
      fixture.detectChanges();

      expect(warningFor(fixture, 'ing3')).toBe('');
      expect(fixture.nativeElement.querySelector('cp-notice.unit-notice')).toBeNull();
    });
  });

  it('adds a group and an ingredient row, each keyboard-operable via a real button', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.addGroup();
    fixture.detectChanges();
    expect(fixture.componentInstance.groups().length).toBe(1);

    const groupKey = fixture.componentInstance.groups()[0].key;
    fixture.componentInstance.addRow(groupKey);
    fixture.detectChanges();
    expect(fixture.componentInstance.groups()[0].ingredients.length).toBe(1);
  });

  it('edits a row field in place', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    const [group] = fixture.componentInstance.groups();
    const [row] = group.ingredients;
    fixture.componentInstance.updateRow(group.key, row.key, { ingredientNameText: 'all-purpose flour' });
    fixture.detectChanges();

    expect(fixture.componentInstance.groups()[0].ingredients[0].ingredientNameText).toBe('all-purpose flour');
  });

  it('removes a row', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    const [group] = fixture.componentInstance.groups();
    fixture.componentInstance.removeRow(group.key, group.ingredients[0].key);
    fixture.detectChanges();

    expect(fixture.componentInstance.groups()[0].ingredients.length).toBe(1);
  });

  /** Moving a line has two paths that do the same: a drag by its handle, and its handle's keys (SC 2.5.7). */
  describe('moving a line by its handle', () => {
    const SECOND_GROUP: RecipeIngredientGroup = {
      id: 'group2',
      title: 'For the filling',
      sortOrder: 1,
      ingredients: [{ ...SEEDED_GROUP.ingredients[0], id: 'ing5', sortOrder: 0, displayText: '3 apples' }],
    };

    async function loaded(
      groups: readonly RecipeIngredientGroup[] = [SEEDED_GROUP],
    ): Promise<ComponentFixture<RecipeIngredientEditorComponent>> {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', groups);
      fixture.detectChanges();
      document.body.appendChild(fixture.nativeElement);
      return fixture;
    }

    const handleOf = (fixture: ComponentFixture<RecipeIngredientEditorComponent>, rowKey: string) =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(`#ingredient-handle-${rowKey}`)!;

    async function press(fixture: ComponentFixture<RecipeIngredientEditorComponent>, rowKey: string, key: string) {
      const handle = handleOf(fixture, rowKey);
      handle.focus();
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
      handle.dispatchEvent(event);
      fixture.detectChanges();
      await fixture.whenStable();
      return event;
    }

    const keysOf = (fixture: ComponentFixture<RecipeIngredientEditorComponent>, groupIndex = 0) =>
      fixture.componentInstance.groups()[groupIndex].ingredients.map((row) => row.key);

    afterEach(() => document.querySelectorAll('cp-recipe-ingredient-editor').forEach((host) => host.remove()));

    it('gives every line one handle, named for it and told how it moves, and no up and down buttons', async () => {
      const fixture = await loaded();
      const element = fixture.nativeElement as HTMLElement;

      const handle = handleOf(fixture, 'ing1');
      expect(handle.getAttribute('aria-label')).toBe('Move ingredient 1');
      expect(handle.classList).toContain('cdk-drag-handle');
      expect(document.getElementById(handle.getAttribute('aria-describedby')!)?.textContent).toContain('arrow keys');
      expect(element.querySelectorAll('.move-handle').length).toBe(2);
      expect(element.querySelector('.row button[aria-label$=" up"], .row button[aria-label$=" down"]')).toBeNull();
    });

    it('moves a line a place with Up and Down, keeps focus on its handle, and says where it went', async () => {
      const fixture = await loaded();

      const event = await press(fixture, 'ing1', 'ArrowDown');
      expect(event.defaultPrevented).withContext('the page does not scroll as well').toBeTrue();
      expect(keysOf(fixture)).toEqual(['ing2', 'ing1']);
      expect(fixture.componentInstance.moveAnnouncement()).toBe(
        'Moved ingredient to position 2 of 2 in For the crust.',
      );
      expect(document.activeElement).toBe(handleOf(fixture, 'ing1'));

      await press(fixture, 'ing1', 'ArrowUp');
      expect(keysOf(fixture)).toEqual(['ing1', 'ing2']);
    });

    it('takes a line to the top or the bottom with Home and End, and says when it is already there', async () => {
      const fixture = await loaded();

      await press(fixture, 'ing1', 'End');
      expect(keysOf(fixture)).toEqual(['ing2', 'ing1']);
      await press(fixture, 'ing1', 'Home');
      expect(keysOf(fixture)).toEqual(['ing1', 'ing2']);

      await press(fixture, 'ing1', 'ArrowUp');
      expect(keysOf(fixture)).toEqual(['ing1', 'ing2']);
      expect(fixture.componentInstance.moveAnnouncement()).toBe('Already at the top.');
    });

    it('leaves every other key to the browser', async () => {
      const fixture = await loaded();

      const event = await press(fixture, 'ing1', 'Tab');
      expect(event.defaultPrevented).toBeFalse();
      expect(keysOf(fixture)).toEqual(['ing1', 'ing2']);
    });

    it('moves a line into the next group from the end of its own, as a drag can', async () => {
      const fixture = await loaded([SEEDED_GROUP, SECOND_GROUP]);

      await press(fixture, 'ing2', 'ArrowDown');
      expect(keysOf(fixture, 0)).toEqual(['ing1']);
      expect(keysOf(fixture, 1)).toEqual(['ing2', 'ing5']);
      expect(fixture.componentInstance.moveAnnouncement()).toBe(
        'Moved ingredient to position 1 of 2 in For the filling.',
      );

      await press(fixture, 'ing2', 'ArrowUp');
      expect(keysOf(fixture, 0)).toEqual(['ing1', 'ing2']);
    });

    it('puts a dropped line where it was dropped, in its own group or another, and keeps what it holds', async () => {
      const fixture = await loaded([SEEDED_GROUP, SECOND_GROUP]);
      const changed = jasmine.createSpy('groupsChanged');
      fixture.componentInstance.groupsChanged.subscribe(changed);
      const list = (groupKey: string) => ({ data: groupKey });

      fixture.componentInstance.onRowDropped({
        previousContainer: list('group1'),
        container: list('group2'),
        previousIndex: 0,
        currentIndex: 1,
        item: { data: 'ing1' },
      } as unknown as CdkDragDrop<string, string, string>);
      fixture.detectChanges();

      expect(keysOf(fixture, 0)).toEqual(['ing2']);
      expect(keysOf(fixture, 1)).toEqual(['ing5', 'ing1']);
      expect(fixture.componentInstance.groups()[1].ingredients[1].displayText).toBe('2 cups flour');
      expect(changed).toHaveBeenCalled();

      // Dropped back where it was picked up: nothing to do, and nothing said.
      changed.calls.reset();
      const said = fixture.componentInstance.moveAnnouncement();
      const ownList = list('group2');
      fixture.componentInstance.onRowDropped({
        previousContainer: ownList,
        container: ownList,
        previousIndex: 1,
        currentIndex: 1,
        item: { data: 'ing1' },
      } as unknown as CdkDragDrop<string, string, string>);
      expect(changed).not.toHaveBeenCalled();
      expect(fixture.componentInstance.moveAnnouncement()).toBe(said);
    });
  });

  it('presents a candidate picker for an ambiguous/unresolved match instead of auto-selecting one', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.onLinesAccepted({
      rows: [
        {
          key: 'row1',
          id: null,
          displayText: 'cilantro',
          displayTextIsComposed: false,
          ingredientNameText: 'cilantro',
          quantityText: '',
          quantityUpper: null,
          detectedQuantityText: null,
          unitId: null,
          unitLabel: '',
          ingredientId: null,
          matchState: 'ambiguous',
          preparationNote: '',
          isOptional: false,
          scalingBehavior: 'Proportional',
          ingredientCandidates: [
            { ingredientId: 'ing2', canonicalName: 'Cilantro', kind: 'CanonicalName' },
            { ingredientId: 'ing3', canonicalName: 'Coriander', kind: 'Alias' },
          ],
          unitCandidates: [],
        },
      ],
      // No groups seeded here, so "a new group at the end" is the same destination this test always used.
      targetGroupKey: null,
    });
    fixture.detectChanges();
    openEveryLine(fixture);

    const groupKey = fixture.componentInstance.groups()[0].key;
    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select[id^="ingredient-pick-"]');
    expect(select).toBeTruthy();
    expect(select.options.length).toBe(3); // placeholder + two candidates
    expect(fixture.componentInstance.groups()[0].ingredients[0].ingredientId).toBeNull(); // nothing auto-chosen

    fixture.componentInstance.onIngredientChoiceSelected(groupKey, 'row1', 'ing2');
    fixture.detectChanges();

    const row = fixture.componentInstance.groups()[0].ingredients[0];
    expect(row.ingredientId).toBe('ing2');
    expect(row.matchState).toBe('matched');
    expect(row.ingredientCandidates.length).toBe(0);
  });

  it('lets the creator explicitly leave an ambiguous match unmatched', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.onLinesAccepted({
      rows: [
        {
          key: 'row1',
          id: null,
          displayText: 'cilantro',
          displayTextIsComposed: false,
          ingredientNameText: 'cilantro',
          quantityText: '',
          quantityUpper: null,
          detectedQuantityText: null,
          unitId: null,
          unitLabel: '',
          ingredientId: null,
          matchState: 'ambiguous',
          preparationNote: '',
          isOptional: false,
          scalingBehavior: 'Proportional',
          ingredientCandidates: [{ ingredientId: 'ing2', canonicalName: 'Cilantro', kind: 'CanonicalName' }],
          unitCandidates: [],
        },
      ],
      targetGroupKey: null,
    });
    fixture.detectChanges();

    const groupKey = fixture.componentInstance.groups()[0].key;
    fixture.componentInstance.onIngredientChoiceSelected(groupKey, 'row1', '');
    fixture.detectChanges();

    const row = fixture.componentInstance.groups()[0].ingredients[0];
    expect(row.ingredientId).toBeNull();
    expect(row.matchState).toBe('unmatched');
  });

  it('emits groupsChanged on every mutation', async () => {
    const fixture = await createFixture();
    const emitted: unknown[] = [];
    fixture.componentInstance.groupsChanged.subscribe((groups) => emitted.push(groups));

    fixture.componentInstance.addGroup();

    expect(emitted.length).toBe(1);
  });

  it('preserves the creator-entered display text verbatim on a seeded row', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('2 cups flour');
  });

  it('marks a line optional through the shared checkbox, not a bare native one', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();
    openEveryLine(fixture);

    const row = fixture.componentInstance.groups()[0].ingredients[0];
    expect(row.isOptional).toBeFalse();

    // The library's checkbox, which draws the control: a bare native one is painted from the theme's
    // color-scheme and reads as filled-in-dark when unchecked.
    const checkbox = (fixture.nativeElement as HTMLElement).querySelector('cp-checkbox');
    expect(checkbox).withContext('cp-checkbox for Optional').toBeTruthy();
    expect(checkbox!.textContent).toContain('Optional');

    // Clicking the label is what a creator does, and it has to reach the row through the rewired binding.
    checkbox!.querySelector<HTMLLabelElement>('label')!.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.groups()[0].ingredients[0].isOptional).toBeTrue();
    expect(checkbox!.querySelector('.box')?.textContent?.trim()).toBe('✓');
  });

  it('offers a "Fixed amount (e.g. garnish)" scaling option rather than a separate garnish field', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();
    openEveryLine(fixture);

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select[id^="row-scaling-"]');
    const optionLabels = Array.from(select.options).map((option) => option.textContent);
    expect(optionLabels).toContain('Fixed amount (e.g. garnish)');
  });

  it('opens the paste-and-parse dialog from the toolbar', async () => {
    const fixture = await createFixture();
    expect(fixture.componentInstance.pasteDialogOpen()).toBeFalse();

    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    button.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.pasteDialogOpen()).toBeTrue();
    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeTruthy();
  });

  describe('where a pasted batch lands', () => {
    /** A row shaped the way the paste dialog hands them over; only the fields these tests read matter. */
    function acceptedRow(displayText: string) {
      return {
        key: 'row-' + displayText,
        id: null,
        displayText,
        displayTextIsComposed: false,
        ingredientNameText: '',
        quantityText: '',
        quantityUpper: null,
        detectedQuantityText: null,
        unitId: null,
        unitLabel: '',
        ingredientId: null,
        matchState: 'matched' as const,
        preparationNote: '',
        isOptional: false,
        scalingBehavior: 'Proportional' as const,
        ingredientCandidates: [],
        unitCandidates: [],
      };
    }

    it('labels each group for the selector, naming an untitled one by its position', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.componentInstance.addGroup();
      fixture.componentInstance.updateGroupTitle(fixture.componentInstance.groups()[0].key, 'For the crust');
      fixture.detectChanges();

      expect(fixture.componentInstance.pasteTargets().map((target) => target.label)).toEqual([
        'For the crust',
        'Group 2 (untitled)',
      ]);
    });

    it('preselects the first existing group when the dialog opens, so an ignored selector changes nothing', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();

      fixture.componentInstance.openPasteDialog();

      expect(fixture.componentInstance.pasteTargetGroupKey()).toBe(fixture.componentInstance.groups()[0].key);
    });

    it('targets a new group when there are none to choose from yet', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.openPasteDialog();

      expect(fixture.componentInstance.pasteTargetGroupKey()).toBeNull();
    });

    it('adds a batch to the chosen group and leaves every other group untouched', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.componentInstance.addGroup();
      fixture.componentInstance.addGroup();
      const [first, second, third] = fixture.componentInstance.groups();

      fixture.componentInstance.onLinesAccepted({
        rows: [acceptedRow('2 cups flour'), acceptedRow('1 tsp salt')],
        targetGroupKey: second.key,
      });

      const groups = fixture.componentInstance.groups();
      expect(groups.length).toBe(3);
      expect(groups.find((group) => group.key === second.key)!.ingredients.map((row) => row.displayText)).toEqual([
        '2 cups flour',
        '1 tsp salt',
      ]);
      expect(groups.find((group) => group.key === first.key)!.ingredients).toEqual([]);
      expect(groups.find((group) => group.key === third.key)!.ingredients).toEqual([]);
    });

    it('creates exactly one group for a "new group" target and writes its key back for the next batch', async () => {
      const fixture = await createFixture();
      const changes: number[] = [];
      fixture.componentInstance.groupsChanged.subscribe((groups) => changes.push(groups.length));

      fixture.componentInstance.onLinesAccepted({ rows: [acceptedRow('2 cups flour')], targetGroupKey: null });
      const createdKey = fixture.componentInstance.pasteTargetGroupKey();
      expect(createdKey).not.toBeNull();

      // The dialog's selector is bound to that signal, so the second accept arrives with the created key —
      // which is what stops a second empty group being made.
      fixture.componentInstance.onLinesAccepted({ rows: [acceptedRow('1 tsp salt')], targetGroupKey: createdKey });

      expect(fixture.componentInstance.groups().length).toBe(1);
      expect(fixture.componentInstance.groups()[0].ingredients.map((row) => row.displayText)).toEqual([
        '2 cups flour',
        '1 tsp salt',
      ]);
      expect(changes).toEqual([1, 1]);
    });

    it('falls back to a new group rather than dropping rows when the chosen group has since been removed', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      const removed = fixture.componentInstance.groups()[0].key;
      fixture.componentInstance.removeGroup(removed);

      fixture.componentInstance.onLinesAccepted({ rows: [acceptedRow('2 cups flour')], targetGroupKey: removed });

      expect(fixture.componentInstance.groups().length).toBe(1);
      expect(fixture.componentInstance.groups()[0].ingredients.map((row) => row.displayText)).toEqual(['2 cups flour']);
      expect(fixture.componentInstance.pasteTargetGroupKey()).toBe(fixture.componentInstance.groups()[0].key);
    });

    it('emits groupsChanged once for a whole batch, not once per row', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      const target = fixture.componentInstance.groups()[0].key;

      let emissions = 0;
      fixture.componentInstance.groupsChanged.subscribe(() => emissions++);

      fixture.componentInstance.onLinesAccepted({
        rows: Array.from({ length: 12 }, (_, index) => acceptedRow('line ' + index)),
        targetGroupKey: target,
      });

      expect(emissions).toBe(1);
      expect(fixture.componentInstance.groups()[0].ingredients.length).toBe(12);
    });
  });
  /**
   * The nav down the side of a sectioned ingredient list. Ordinary navigation over a list that is always
   * rendered in full: it scrolls, it never mounts, which is what keeps an edit in a group scrolled out of view
   * a real edit (the recipe editor's own spec proves the dirty pill and the request).
   */
  describe('the group nav', () => {
    const host = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) => fixture.nativeElement as HTMLElement;

    /** The shared component's host — what this editor positions, and what it styles through. */
    const navHost = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) =>
      host(fixture).querySelector<HTMLElement>('cp-anchor-nav.group-nav');

    /** The landmark the shared component renders inside it. Null when there is no nav at all. */
    const nav = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) =>
      host(fixture).querySelector<HTMLElement>('nav[aria-label="Ingredient groups"]');

    const navLinks = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) =>
      Array.from(host(fixture).querySelectorAll<HTMLAnchorElement>('nav[aria-label="Ingredient groups"] a'));

    const buttonLabelled = (fixture: ComponentFixture<RecipeIngredientEditorComponent>, label: string) =>
      Array.from(host(fixture).querySelectorAll<HTMLButtonElement>('button')).find(
        (button) => button.textContent?.trim() === label,
      );

    /** Three groups: two the creator named, one they did not, each with a different number of lines. */
    async function threeGroups(): Promise<ComponentFixture<RecipeIngredientEditorComponent>> {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [
        SEEDED_GROUP,
        {
          id: 'group2',
          title: null,
          sortOrder: 1,
          ingredients: [{ ...SEEDED_GROUP.ingredients[0], id: 'ing3' }],
        },
        { id: 'group3', title: 'For the glaze', sortOrder: 2, ingredients: [] },
      ]);
      fixture.detectChanges();
      return fixture;
    }

    it('lists every group, named and counted, in the order they appear', async () => {
      const fixture = await threeGroups();

      expect(navLinks(fixture).map((link) => link.querySelector('.label')?.textContent?.trim())).toEqual([
        'For the crust',
        'Group 2 (untitled)',
        'For the glaze',
      ]);
      expect(navLinks(fixture).map((link) => link.querySelector('.detail')?.textContent?.trim())).toEqual([
        '2 lines',
        '1 line',
        '0 lines',
      ]);

      // One implementation, not two that look alike: this nav is the shared library component, which the
      // instruction editor renders too.
      expect(nav(fixture)!.closest('cp-anchor-nav')).withContext('rendered by CpAnchorNavComponent').toBeTruthy();

      // Ordinary tabbable links, not a roving-tabindex widget, and every one points at a group that exists.
      expect(navLinks(fixture).some((link) => link.hasAttribute('tabindex'))).toBeFalse();
      for (const link of navLinks(fixture)) {
        const id = link.getAttribute('href')!.slice(1);
        expect(host(fixture).querySelector('#' + id))
          .withContext(id)
          .toBeTruthy();
      }
    });

    it('reaches every group: each item scrolls to its own group and focuses its heading', async () => {
      const fixture = await threeGroups();
      const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');
      const keys = fixture.componentInstance.groups().map((group) => group.key);

      for (const [index, link] of navLinks(fixture).entries()) {
        link.click();
        fixture.detectChanges();

        expect((scrollSpy.calls.mostRecent().object as Element).id)
          .withContext(`group ${index + 1}`)
          .toBe(`ingredient-group-${keys[index]}`);
        expect(document.activeElement)
          .withContext(`group ${index + 1} heading`)
          .toBe(host(fixture).querySelector(`#ingredient-group-title-${keys[index]}`));
        expect(link.getAttribute('aria-current')).toBe('true');
      }

      // One at a time: the group just jumped to is the only one marked.
      expect(navLinks(fixture).filter((link) => link.getAttribute('aria-current') === 'true').length).toBe(1);
    });

    it('renders no nav for an ungrouped recipe, nor for a single group', async () => {
      const fixture = await createFixture();

      // Nothing at all yet.
      expect(nav(fixture)).toBeNull();

      // One ungrouped list: R.10's flat list, with nothing to navigate.
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      expect(fixture.componentInstance.groups().length).toBe(1);
      expect(nav(fixture)).toBeNull();

      // One titled group is still not navigation.
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();
      expect(fixture.componentInstance.showGroups()).toBeTrue();
      expect(nav(fixture)).toBeNull();

      // A second group is.
      buttonLabelled(fixture, 'Add ingredient group')!.click();
      fixture.detectChanges();
      expect(navLinks(fixture).length).toBe(2);
    });

    /**
     * The gutter belongs to the nav. Without one — an ungrouped recipe, or a single group — the list must have
     * the whole width: the two-column layout applied unconditionally pinned the entire ingredient list into the
     * nav's 12rem track, which stacked every row's four fields into a narrow vertical strip.
     */
    it('gives the lines the full width when no nav sits beside them', async () => {
      const trackCount = (element: Element) => {
        const value = getComputedStyle(element).gridTemplateColumns;
        return value === 'none' || value === '' ? 0 : value.split(/\s+/).length;
      };

      const fixture = await createFixture();
      const element = host(fixture);
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();

      // Wide enough for a gutter, if there were anything to put in one. The showcase test target loads no token
      // sheet, so give the row the one token its column arithmetic reads.
      element.style.setProperty('--cp-space-4', '1rem');
      element.style.width = '70rem';
      fixture.detectChanges();

      expect(nav(fixture)).withContext('no nav for an ungrouped recipe').toBeNull();

      const layout = element.querySelector('.grouped-layout')!;
      const groups = element.querySelector('.groups')!;
      expect(trackCount(layout)).withContext('columns with no nav').toBe(1);
      expect(groups.getBoundingClientRect().width).toBeGreaterThan(element.getBoundingClientRect().width * 0.8);

      // Which is what lets a row's own fields sit side by side instead of stacking. Asserted by where they
      // actually land rather than by counting tracks: auto-fit reports collapsed tracks too, so a count says
      // less than "all four of these are on one line".
      // One row's fields, not every row's: a seeded group has several lines, and each of those legitimately
      // sits on a line of its own.
      const fieldTops = (root: HTMLElement) =>
        Array.from(root.querySelector('.row-fields')!.querySelectorAll<HTMLElement>(':scope > cp-field')).map((field) =>
          Math.round(field.getBoundingClientRect().top),
        );
      // Quantity, unit and ingredient are one above the other, and the preparation note is in the column beside them.
      const stackOf = (tops: number[]) => ({
        distinct: new Set(tops).size,
        inOrder: tops.every((top, index) => index === 0 || top > tops[index - 1]),
      });
      const noteBeside = (root: HTMLElement) => {
        const fields = root.querySelector('.row-fields')!.getBoundingClientRect();
        const note = root.querySelector('.row-side cp-field')!.getBoundingClientRect();
        return note.left >= fields.right - 1 && Math.abs(note.top - fields.top) < 40;
      };
      expect(fieldTops(element).length).withContext('quantity, unit and ingredient').toBe(3);
      expect(stackOf(fieldTops(element)))
        .withContext('quantity, unit and ingredient one above the other')
        .toEqual({ distinct: 3, inOrder: true });
      expect(noteBeside(element)).withContext('the note in the second column').toBeTrue();

      // Same for a single titled group: still no nav, still the full width.
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();
      openEveryLine(fixture);
      expect(nav(fixture)).toBeNull();
      expect(trackCount(element.querySelector('.grouped-layout')!))
        .withContext('columns for one group')
        .toBe(1);
      expect(stackOf(fieldTops(element)))
        .withContext('the same for one group')
        .toEqual({ distinct: 3, inOrder: true });
      expect(noteBeside(element)).withContext('and the note beside them for one group').toBeTrue();
    });

    it('lays a line out in two columns when the row is wide, with the note, scaling and Optional on the right', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      element.style.setProperty('--cp-space-4', '1rem');
      element.style.setProperty('--cp-space-6', '1.5rem');
      element.style.width = '70rem';
      fixture.detectChanges();

      const box = (selector: string) => element.querySelector(selector)!.getBoundingClientRect();
      const fields = box('.row-fields');
      const note = element.querySelector('.row-side textarea')!.getBoundingClientRect();
      expect(element.querySelector('.row-side textarea')).withContext('the note is a text area').toBeTruthy();
      expect(note.left)
        .withContext('the note is in the right-hand column')
        .toBeGreaterThanOrEqual(fields.right - 1);
      // The first column is a vertical stack: three fields, one left edge, each beneath the one before.
      const stacked = Array.from(element.querySelectorAll('.row-fields > cp-field')).map((field) =>
        field.getBoundingClientRect(),
      );
      expect(stacked.length).toBe(3);
      expect(stacked.every((box) => Math.abs(box.left - stacked[0].left) < 1.5))
        .withContext('one left edge')
        .toBeTrue();
      expect(stacked[1].top).toBeGreaterThanOrEqual(stacked[0].bottom - 1);
      expect(stacked[2].top).toBeGreaterThanOrEqual(stacked[1].bottom - 1);

      // Scaling under the note, its label at the column's left edge; Optional at the right end of the same line.
      const scaling = element.querySelector('.row-toggles cp-field')!.getBoundingClientRect();
      const optional = element.querySelector('.row-toggles cp-checkbox')!.getBoundingClientRect();
      expect(scaling.top).toBeGreaterThanOrEqual(note.bottom - 1);
      expect(Math.abs(scaling.left - note.left))
        .withContext('Scaling is left-aligned with the note')
        .toBeLessThan(1.5);
      // Optional is to the right of Scaling, on the same line: after the select, not in front of its label.
      expect(optional.left)
        .withContext('Optional is to the right of Scaling')
        .toBeGreaterThanOrEqual(scaling.right - 1);
      expect(optional.bottom).withContext('on the same line').toBeGreaterThan(scaling.top);
      expect(optional.top).withContext('on the same line').toBeLessThan(scaling.bottom);
    });

    it('keeps the toolbar on screen under the topbar while a long list scrolls, on a wide editor only', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      for (let count = 0; count < 12; count += 1) {
        buttonLabelled(fixture, 'Add ingredient')!.click();
        fixture.detectChanges();
      }
      element.style.setProperty('--cp-topbar-height', '4.25rem');
      element.style.setProperty('--cp-space-2', '0.5rem');
      // The showcase test target loads no token sheet, so give the toolbar the surface it paints itself with.
      element.style.setProperty('--cp-surface', '#ffffff');
      element.style.width = '70rem';
      fixture.detectChanges();
      const toolbar = () => element.querySelector<HTMLElement>('.toolbar')!;

      expect(getComputedStyle(toolbar()).position).toBe('sticky');
      // Scrolled well down the list, the toolbar is still there, just under the fixed topbar.
      window.scrollTo(0, element.getBoundingClientRect().top + window.scrollY + 900);
      fixture.detectChanges();
      expect(toolbar().getBoundingClientRect().top)
        .withContext('stuck beneath the topbar')
        .toBeCloseTo(4.25 * 16, 0);
      expect(getComputedStyle(toolbar()).backgroundColor)
        .withContext('opaque, not see-through')
        .not.toBe('rgba(0, 0, 0, 0)');

      element.style.width = '30rem';
      fixture.detectChanges();
      expect(getComputedStyle(toolbar()).position).withContext('a phone does not give up the room').toBe('static');
      window.scrollTo(0, 0);
    });

    it('offers one "Add ingredient" at a time: the toolbar one while empty, then one beneath the last line of each group', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      const adds = () =>
        Array.from(element.querySelectorAll<HTMLButtonElement>('button')).filter(
          (button) => button.textContent?.trim() === 'Add ingredient',
        );
      const toolbar = () => element.querySelector('.toolbar')!;
      const follows = (earlier: Element, later: Element) =>
        (earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;

      expect(adds().length).withContext('empty: the toolbar one, and no second one in the empty state').toBe(1);
      expect(toolbar().contains(adds()[0])).toBeTrue();

      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      expect(adds().length).withContext('a plain list: just the one').toBe(1);
      expect(toolbar().contains(adds()[0])).withContext('no longer in the toolbar').toBeFalse();
      const rows = element.querySelectorAll('.row');
      expect(rows.length).toBe(2);
      expect(follows(rows[rows.length - 1], adds()[0]))
        .withContext('beneath the last line')
        .toBeTrue();

      buttonLabelled(fixture, 'Add ingredient group')!.click();
      fixture.detectChanges();
      expect(adds().length)
        .withContext('grouped: one beneath each group')
        .toBe(fixture.componentInstance.groups().length);
      expect(adds().some((button) => toolbar().contains(button)))
        .withContext('none left in the toolbar')
        .toBeFalse();
    });

    // A long list used to mean scrolling back to the top for every new line, then down again to fill it in.
    it('takes the creator to the line it adds, its quantity focused', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      document.body.appendChild(element);

      try {
        for (let count = 0; count < 3; count += 1) {
          buttonLabelled(fixture, 'Add ingredient')!.click();
          fixture.detectChanges();
          await fixture.whenStable();
        }

        const quantities = element.querySelectorAll<HTMLInputElement>('input[id^="row-quantity-"]');
        expect(quantities.length).toBe(3);
        expect(document.activeElement).withContext('the newest line, not the button').toBe(quantities[2]);
      } finally {
        element.remove();
      }
    });

    it('focuses the heading of a group it adds', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      document.body.appendChild(element);

      try {
        buttonLabelled(fixture, 'Add ingredient group')!.click();
        fixture.detectChanges();
        await fixture.whenStable();

        const key = fixture.componentInstance.groups().at(-1)!.key;
        expect(document.activeElement?.id).toBe(`ingredient-group-title-${key}`);
      } finally {
        element.remove();
      }
    });

    it('puts edit, move and remove at the top right of the line, level with its words', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      element.style.setProperty('--cp-space-4', '1rem');
      element.style.width = '70rem';
      fixture.detectChanges();

      const row = element.querySelector('.row')!.getBoundingClientRect();
      const words = element.querySelector('.line-text')!.getBoundingClientRect();
      const status = element.querySelector('.row-status')!.getBoundingClientRect();
      const actions = element.querySelector('.row-actions')!.getBoundingClientRect();
      const fields = element.querySelector('.row-fields')!.getBoundingClientRect();

      // On the words' line, at the far right of the row, and above the line's status and its fields.
      expect(actions.top).toBeLessThan(words.bottom);
      expect(actions.bottom).toBeGreaterThan(words.top);
      expect(row.right - actions.right)
        .withContext("against the right edge, inside the row's padding")
        .toBeLessThan(40);
      expect(actions.bottom).toBeLessThanOrEqual(status.top + 1);
      expect(actions.bottom).toBeLessThanOrEqual(fields.top + 1);
      expect(element.querySelector('button[aria-label="Edit ingredient 1"]')).toBeTruthy();
      expect(element.querySelector('button[aria-label="Remove ingredient 1"]')).toBeTruthy();
    });

    /** As on a step: icons, never the loudest thing on the line, and each named for what it acts on. */
    it('moves and removes a line and a group with quiet icon buttons that say what they act on', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      buttonLabelled(fixture, 'Add ingredient group')!.click();
      fixture.detectChanges();
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();

      const icons: Record<string, string> = {
        'Edit ingredient 1': '✓',
        'Move ingredient 1': '⠿',
        'Remove ingredient 1': 'trash',
        'Move group 1 up': '↑',
        'Move group 1 down': '↓',
        'Remove group 1': 'trash',
      };
      for (const [label, icon] of Object.entries(icons)) {
        const button = element.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`);
        expect(button).withContext(label).not.toBeNull();
        expect(button!.classList).withContext(label).toContain('cp-button--ghost');
        // Removing is a drawn bin; the rest are glyphs. Either way the picture is hidden and the label names it.
        if (icon === 'trash') {
          expect(button!.querySelector('cp-icon[aria-hidden="true"]')?.getAttribute('data-name'))
            .withContext(label)
            .toBe('trash');
          expect(button!.textContent?.trim()).withContext(label).toBe('');
        } else {
          expect(button!.textContent?.trim()).withContext(label).toBe(icon);
          expect(button!.querySelector('[aria-hidden="true"]')?.textContent).withContext(label).toBe(icon);
        }
      }
    });

    it('stacks a line in one column when the row is narrow: fields, then the note, then scaling', async () => {
      const fixture = await createFixture();
      const element = host(fixture);
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      element.style.setProperty('--cp-space-4', '1rem');
      element.style.width = '30rem';
      fixture.detectChanges();

      const fields = element.querySelector('.row-fields')!.getBoundingClientRect();
      const note = element.querySelector('.row-side textarea')!.getBoundingClientRect();
      const scaling = element.querySelector('.row-toggles cp-field')!.getBoundingClientRect();
      expect(note.top).toBeGreaterThanOrEqual(fields.bottom - 1);
      expect(scaling.top).toBeGreaterThanOrEqual(note.bottom - 1);
    });

    it('collapses to one column at a narrow width, with every item still a full target', async () => {
      const fixture = await threeGroups();
      const element = host(fixture);

      // A phone, or a desktop at 200% zoom — both shrink this editor's CSS-px inline size.
      element.style.width = '22rem';
      fixture.detectChanges();

      const layout = element.querySelector('.grouped-layout')!;
      expect(getComputedStyle(layout).gridTemplateColumns.split(/\s+/).length).withContext('columns at 22rem').toBe(1);
      expect(getComputedStyle(navHost(fixture)!).position)
        .withContext('not a sticky gutter when narrow')
        .toBe('static');

      for (const link of navLinks(fixture)) {
        expect(link.getBoundingClientRect().height).withContext(link.textContent?.trim()).toBeGreaterThanOrEqual(40);
      }

      // Nothing spills sideways (WCAG 2.2 SC 1.4.10, Reflow).
      const limit = element.getBoundingClientRect().right + 1;
      const overflowing = Array.from(element.querySelectorAll('*'))
        .filter((node) => node.getBoundingClientRect().right > limit)
        .map((node) => node.tagName.toLowerCase() + (node.id ? '#' + node.id : ''));
      expect(overflowing).withContext('overflowing the editor at 22rem').toEqual([]);

      // Wide enough, and the nav becomes the gutter beside the groups.
      element.style.width = '70rem';
      fixture.detectChanges();
      expect(getComputedStyle(element.querySelector('.grouped-layout')!).gridTemplateColumns.split(/\s+/).length)
        .withContext('columns at 70rem')
        .toBe(2);
      expect(getComputedStyle(navHost(fixture)!).position).toBe('sticky');
      // And stacked in the gutter, through the one property the shared nav exposes for it.
      expect(getComputedStyle(navHost(fixture)!.querySelector('ul')!).flexDirection).toBe('column');
    });
  });

  /**
   * Sections are opt-in. A recipe has one ingredient list until a creator says otherwise, so nothing here may
   * ask them to make a group before they can type a line, and nothing may show them a heading they did not ask
   * for. The group the lines travel in still exists — the wire contract needs one (R.1) — it is simply not
   * presented as a section.
   */
  describe('ungrouped by default', () => {
    /** `fixture.nativeElement` is `any`, so each query goes through the host as an element. */
    const host = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) => fixture.nativeElement as HTMLElement;

    const groupTitleInputs = (fixture: ComponentFixture<RecipeIngredientEditorComponent>) =>
      Array.from(host(fixture).querySelectorAll<HTMLInputElement>('input[id^="ingredient-group-title-"]'));

    const buttonLabelled = (fixture: ComponentFixture<RecipeIngredientEditorComponent>, label: string) =>
      Array.from(host(fixture).querySelectorAll<HTMLButtonElement>('button')).find(
        (button) => button.textContent?.trim() === label,
      );

    it('takes a line from the empty state with no group made first, and shows no heading for the one it needs', async () => {
      const fixture = await createFixture();

      // The empty state asks for an ingredient, not for a group.
      expect(fixture.nativeElement.textContent).toContain('add them one at a time');
      expect(fixture.nativeElement.textContent).not.toContain('add a group');

      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();

      // One line, in the one list it needs, and no section furniture anywhere: no heading field to leave
      // blank, no "Remove group" for a group the creator never made.
      expect(fixture.componentInstance.groups().length).toBe(1);
      expect(fixture.componentInstance.groups()[0].ingredients.length).toBe(1);
      expect(fixture.componentInstance.groups()[0].title).toBe('');
      expect(fixture.componentInstance.showGroups()).toBeFalse();
      expect(groupTitleInputs(fixture)).toEqual([]);
      expect(buttonLabelled(fixture, 'Remove group')).toBeUndefined();

      // The row's own controls are all there — it is a line like any other.
      const rowKey = fixture.componentInstance.groups()[0].ingredients[0].key;
      expect(fixture.nativeElement.querySelector(`#row-ingredient-${rowKey}`)).toBeTruthy();
    });

    it('shows the headings of a recipe that arrives with sections rather than flattening it', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [
        SEEDED_GROUP,
        { ...SEEDED_GROUP, id: 'group2', title: 'For the filling', sortOrder: 1, ingredients: [] },
      ]);
      fixture.detectChanges();
      // ngModel writes a value into the view on a microtask, so the headings' text is there a tick later.
      await fixture.whenStable();

      expect(fixture.componentInstance.showGroups()).toBeTrue();
      expect(groupTitleInputs(fixture).map((input) => input.value)).toEqual(['For the crust', 'For the filling']);
    });

    it('shows a heading for a single group that does have a title', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();
      await fixture.whenStable();

      expect(fixture.componentInstance.showGroups()).toBeTrue();
      expect(groupTitleInputs(fixture).map((input) => input.value)).toEqual(['For the crust']);
    });

    it('presents a single untitled group as the one list it is', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [{ ...SEEDED_GROUP, title: null }]);
      fixture.detectChanges();

      // What a save of an ungrouped recipe stores, read back: one group, no title. It must not come back as
      // "Group 1" with an empty heading, or ungrouped would not survive its own round trip.
      expect(fixture.componentInstance.showGroups()).toBeFalse();
      expect(groupTitleInputs(fixture)).toEqual([]);
      expect(fixture.nativeElement.textContent).toContain('2 cups flour');
    });

    it('keeps existing lines in the first group when the creator asks for sections', async () => {
      const fixture = await createFixture();
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();
      const firstGroupKey = fixture.componentInstance.groups()[0].key;
      const firstRowKey = fixture.componentInstance.groups()[0].ingredients[0].key;
      fixture.componentInstance.updateRow(firstGroupKey, firstRowKey, { ingredientNameText: 'flour' });
      fixture.componentInstance.addRow();
      fixture.detectChanges();
      const secondRowKey = fixture.componentInstance.groups()[0].ingredients[1].key;
      fixture.componentInstance.updateRow(firstGroupKey, secondRowKey, { ingredientNameText: 'salt' });
      fixture.detectChanges();

      buttonLabelled(fixture, 'Add ingredient group')!.click();
      fixture.detectChanges();

      const groups = fixture.componentInstance.groups();
      expect(groups.length).toBe(2);
      // The lines did not move: same group, same order, same content. The new group is the empty one.
      expect(groups[0].key).toBe(firstGroupKey);
      expect(groups[0].ingredients.map((row) => row.ingredientNameText)).toEqual(['flour', 'salt']);
      expect(groups[1].ingredients).toEqual([]);

      // And the first group's heading is now there to name, empty rather than invented.
      const headings = groupTitleInputs(fixture);
      expect(headings.length).toBe(2);
      expect(headings[0].value).toBe('');
    });

    it('names the one destination for what it is while there are no sections', async () => {
      const fixture = await createFixture();
      buttonLabelled(fixture, 'Add ingredient')!.click();
      fixture.detectChanges();

      expect(fixture.componentInstance.pasteTargets().map((target) => target.label)).toEqual(['The ingredient list']);

      buttonLabelled(fixture, 'Add ingredient group')!.click();
      fixture.detectChanges();

      // Numbered only once there is something to number.
      expect(fixture.componentInstance.pasteTargets().map((target) => target.label)).toEqual([
        'Group 1 (untitled)',
        'Group 2 (untitled)',
      ]);
    });
  });

  /**
   * The line a hand-entered row saves as. These drive the row's real inputs rather than calling `updateRow`
   * with a `displayText` no control in this editor sets — which is exactly how a creator filling in every
   * visible field ended up with a line that never reached the server.
   */
  describe('the line a row saves as', () => {
    function clickAddIngredient(fixture: ComponentFixture<RecipeIngredientEditorComponent>): string {
      const buttons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button'));
      const add = buttons.find((button) => button.textContent?.trim() === 'Add ingredient');
      expect(add).withContext('the "Add ingredient" button').toBeTruthy();
      add!.click();
      fixture.detectChanges();

      const rows = fixture.componentInstance.groups()[0].ingredients;
      return rows[rows.length - 1].key;
    }

    /** Types into one of the row's four real inputs, the way a creator does. */
    function typeInto(
      fixture: ComponentFixture<RecipeIngredientEditorComponent>,
      field: string,
      rowKey: string,
      value: string,
    ): void {
      openEveryLine(fixture);
      // Not `input[...]`: the preparation note is a textarea, and the others are inputs.
      const input: HTMLInputElement = fixture.nativeElement.querySelector(`[id="row-${field}-${rowKey}"]`);
      expect(input).withContext(`the row's ${field} input`).toBeTruthy();
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    function rowFor(fixture: ComponentFixture<RecipeIngredientEditorComponent>, rowKey: string) {
      return fixture.componentInstance.groups()[0].ingredients.find((row) => row.key === rowKey)!;
    }

    it('composes a hand-added row’s line from the fields the creator actually types into', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.detectChanges();
      const rowKey = clickAddIngredient(fixture);

      typeInto(fixture, 'quantity', rowKey, '2');
      typeInto(fixture, 'unit', rowKey, 'cups');
      typeInto(fixture, 'ingredient', rowKey, 'all-purpose flour');
      typeInto(fixture, 'prep', rowKey, 'sifted');

      expect(rowFor(fixture, rowKey).displayText).toBe('2 cups all-purpose flour, sifted');
    });

    it('shows the composed line as this editor’s own reading, not as text the creator wrote', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.detectChanges();
      const rowKey = clickAddIngredient(fixture);

      typeInto(fixture, 'ingredient', rowKey, 'flaky sea salt');

      const line: HTMLElement = fixture.nativeElement.querySelector('.line-text');
      expect(line.textContent).toContain('Will be saved as:');
      expect(line.textContent).toContain('flaky sea salt');
    });

    it('keeps the composed line in step as a field is edited again', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.detectChanges();
      const rowKey = clickAddIngredient(fixture);

      typeInto(fixture, 'quantity', rowKey, '2');
      typeInto(fixture, 'ingredient', rowKey, 'flour');
      expect(rowFor(fixture, rowKey).displayText).toBe('2 flour');

      typeInto(fixture, 'quantity', rowKey, '3');

      expect(rowFor(fixture, rowKey).displayText).toBe('3 flour');
    });

    it('leaves a row nobody filled in with an empty line, so an abandoned row is still dropped on save', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.detectChanges();
      const rowKey = clickAddIngredient(fixture);

      expect(rowFor(fixture, rowKey).displayText).toBe('');
      // No words to show, so it says it is empty rather than showing a line the recipe would read.
      expect(fixture.nativeElement.querySelector('.line-text').textContent).toContain('not filled in yet');
      expect(fixture.nativeElement.querySelector('.line-text').textContent).not.toContain('Will be saved as:');

      // Filled in and then cleared again is the same abandoned row.
      typeInto(fixture, 'ingredient', rowKey, 'flour');
      typeInto(fixture, 'ingredient', rowKey, '  ');

      expect(rowFor(fixture, rowKey).displayText).toBe('');
    });

    it('never rewrites a pasted line when the parsed reading beside it is edited', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.onLinesAccepted({
        rows: [
          {
            key: 'row1',
            id: null,
            displayText: '2 cups (240 g) all-purpose flour, sifted',
            displayTextIsComposed: false,
            ingredientNameText: 'all-purpose flour',
            quantityText: '2',
            quantityUpper: null,
            detectedQuantityText: '2',
            unitId: 'unit1',
            unitLabel: 'cups',
            ingredientId: 'ref1',
            matchState: 'matched',
            preparationNote: 'sifted',
            isOptional: false,
            scalingBehavior: 'Proportional',
            ingredientCandidates: [],
            unitCandidates: [],
          },
        ],
        targetGroupKey: null,
      });
      fixture.detectChanges();

      typeInto(fixture, 'quantity', 'row1', '4');
      typeInto(fixture, 'ingredient', 'row1', 'bread flour');

      const row = rowFor(fixture, 'row1');
      expect(row.displayText).toBe('2 cups (240 g) all-purpose flour, sifted');
      expect(row.quantityText).toBe('4');
      expect(fixture.nativeElement.querySelector('.line-text').textContent).not.toContain('Will be saved as:');
    });

    it('never rewrites a line loaded from the server when a field is edited', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();

      const rowKey = fixture.componentInstance.groups()[0].ingredients[0].key;
      typeInto(fixture, 'quantity', rowKey, '5');

      expect(rowFor(fixture, rowKey).displayText).toBe('2 cups flour');
    });

    it('hands the line over for good once something patches it directly', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.addGroup();
      fixture.detectChanges();
      const rowKey = clickAddIngredient(fixture);
      const groupKey = fixture.componentInstance.groups()[0].key;

      typeInto(fixture, 'ingredient', rowKey, 'flour');
      fixture.componentInstance.updateRow(groupKey, rowKey, { displayText: 'a good handful of flour' });
      fixture.detectChanges();

      typeInto(fixture, 'quantity', rowKey, '2');

      expect(rowFor(fixture, rowKey).displayText).toBe('a good handful of flour');
      expect(rowFor(fixture, rowKey).displayTextIsComposed).toBeFalse();
    });

    it('composes from the creator’s words alone, in the order the row reads', () => {
      const parts = {
        quantityText: ' 1 1/2 ',
        unitLabel: ' tablespoons ',
        ingredientNameText: ' olive oil ',
        preparationNote: ' warmed ',
      };
      expect(composeDisplayText(parts)).toBe('1 1/2 tablespoons olive oil, warmed');

      // Whatever the creator left out is simply absent — no placeholder, and no wording of this editor's own.
      expect(composeDisplayText({ ...parts, quantityText: '', unitLabel: '' })).toBe('olive oil, warmed');
      expect(composeDisplayText({ ...parts, quantityText: '', unitLabel: '', ingredientNameText: '' })).toBe('warmed');
      expect(
        composeDisplayText({ quantityText: '', unitLabel: '', ingredientNameText: 'salt', preparationNote: '' }),
      ).toBe('salt');
    });
  });
});

/**
 * The unit and ingredient boxes as comboboxes over the shared platform catalogue (R.14).
 *
 * Two rules are load-bearing here and are what most of these tests are about. Reference data is global, so
 * nothing in these requests may carry a workspace (tenancy.md). And a catalogue match only ever *enriches* a
 * line — the creator's own wording stays the record of what they wrote, and an entry that matches nothing is
 * still saved rather than refused (recipes.md).
 */
describe('RecipeIngredientEditorComponent reference pickers', () => {
  const GATEWAY = 'https://gateway.example';
  const UNITS_URL = `${GATEWAY}/api/v1/reference/units`;
  const INGREDIENTS_URL = `${GATEWAY}/api/v1/reference/ingredients`;

  const CUPS = {
    id: 'unit1',
    code: 'cup',
    displayName: 'Cups',
    pluralName: 'Cups',
    abbreviation: 'c',
    dimension: 'Volume',
    system: 'UsCustomary',
    baseUnitFactor: 0.2366,
    displayPrecision: 2,
  };

  const GRAMS = {
    ...CUPS,
    id: 'unit9',
    code: 'gram',
    displayName: 'Grams',
    pluralName: 'Grams',
    abbreviation: 'g',
    dimension: 'Mass',
    system: 'Metric',
  };

  let http: HttpTestingController;

  function ingredientRow(id: string, canonicalName: string, aliases: readonly string[] = []): Record<string, unknown> {
    return { id, canonicalName, foodCategoryCode: null, defaultCountUnitCode: null, aliases };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RecipeIngredientEditorComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /**
   * An editor whose gateway is resolved, so the catalogue read actually leaves the browser.
   *
   * `fakeAsync` throughout this block: the search is debounced, and a debounce is only observable if the test
   * can move the clock. The runtime config settles on a microtask, which is what the bare `tick()` is for.
   */
  function createEditor(
    groups: readonly RecipeIngredientGroup[] = [SEEDED_GROUP],
  ): ComponentFixture<RecipeIngredientEditorComponent> {
    void TestBed.inject(RuntimeConfigService).load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    tick();

    const fixture = TestBed.createComponent(RecipeIngredientEditorComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('initialGroups', groups);
    fixture.detectChanges();
    openEveryLine(fixture);
    return fixture;
  }

  function settleUnits(
    fixture: ComponentFixture<RecipeIngredientEditorComponent>,
    units: readonly Record<string, unknown>[] = [CUPS],
  ): void {
    http.expectOne((request) => request.url === UNITS_URL).flush({ items: units, nextCursor: null });
    tick();
    fixture.detectChanges();
  }

  function failUnits(fixture: ComponentFixture<RecipeIngredientEditorComponent>): void {
    http.expectOne((request) => request.url === UNITS_URL).flush({}, { status: 500, statusText: 'Server Error' });
    tick();
    fixture.detectChanges();
  }

  /** Lets the debounce expire, then answers the one request it allowed through. */
  function settleSearch(
    fixture: ComponentFixture<RecipeIngredientEditorComponent>,
    items: readonly Record<string, unknown>[],
    nextCursor: string | null = null,
  ): void {
    tick(INGREDIENT_SEARCH_DEBOUNCE_MS);
    http.expectOne((request) => request.url === INGREDIENTS_URL).flush({ items, nextCursor });
    fixture.detectChanges();
  }

  function boxFor(
    fixture: ComponentFixture<RecipeIngredientEditorComponent>,
    field: 'unit' | 'ingredient',
    rowKey: string,
  ): HTMLInputElement {
    const input: HTMLInputElement = fixture.nativeElement.querySelector(`[id="row-${field}-${rowKey}"]`);
    expect(input).withContext(`the row's ${field} box`).toBeTruthy();
    return input;
  }

  function typeInto(
    fixture: ComponentFixture<RecipeIngredientEditorComponent>,
    field: 'unit' | 'ingredient',
    rowKey: string,
    text: string,
  ): void {
    const input = boxFor(fixture, field, rowKey);
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** `mousedown`, as the combobox listens for — a click would blur the box before it landed. */
  function pick(fixture: ComponentFixture<RecipeIngredientEditorComponent>, inputId: string, optionId: string): void {
    const option: HTMLElement = fixture.nativeElement.querySelector(`[id="${inputId}-option-${optionId}"]`);
    expect(option).withContext(`the "${optionId}" option`).toBeTruthy();
    option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  /** What the popup says when it has nothing to list — which is not always "there is no such thing". */
  function emptyText(fixture: ComponentFixture<RecipeIngredientEditorComponent>, inputId: string): string {
    const empty: HTMLElement | null = fixture.nativeElement.querySelector(`[id="${inputId}-listbox"] .empty`);
    return empty?.textContent?.trim() ?? '';
  }

  /** What the combobox's live region is offering to a screen reader right now. */
  function announcement(fixture: ComponentFixture<RecipeIngredientEditorComponent>, inputId: string): string {
    const listbox: HTMLElement = fixture.nativeElement.querySelector(`[id="${inputId}-listbox"]`);
    return listbox.parentElement?.querySelector('.sr-only')?.textContent?.trim() ?? '';
  }

  function rowFor(fixture: ComponentFixture<RecipeIngredientEditorComponent>, rowKey: string) {
    return fixture.componentInstance.groups()[0].ingredients.find((row) => row.key === rowKey)!;
  }

  function clickAddIngredient(fixture: ComponentFixture<RecipeIngredientEditorComponent>): string {
    const buttons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button'));
    const add = buttons.find((button) => button.textContent?.trim() === 'Add ingredient');
    expect(add).withContext('the "Add ingredient" button').toBeTruthy();
    add!.click();
    fixture.detectChanges();

    const rows = fixture.componentInstance.groups()[0].ingredients;
    return rows[rows.length - 1].key;
  }

  // The rule this whole change has to keep: a reference id enriches the line, it does not replace it.
  /**
   * An AI draft wrote "2 lb potatoes" and the line is linked to the catalogue's unit. The box shows the unit's
   * CreatorPantry name, and the line's own word is said under it, because a save still keeps that word.
   */
  it('shows a linked unit by its CreatorPantry name and says what the line itself says', fakeAsync(() => {
    const linked: RecipeIngredientGroup = {
      ...SEEDED_GROUP,
      ingredients: [{ ...SEEDED_GROUP.ingredients[0], displayText: '2 c flour', unitText: 'c' }],
    };
    const fixture = createEditor([linked]);
    settleUnits(fixture);

    const box: HTMLInputElement = fixture.nativeElement.querySelector('#row-unit-ing1');
    expect(box.value).toBe('Cups');
    expect(fixture.nativeElement.querySelector('#row-unit-ing1-hint')?.textContent).toContain('The line says “c”.');
    // Showing the name is not an edit: the line keeps its unit and its word.
    expect(rowFor(fixture, 'ing1').unitId).toBe('unit1');
    expect(rowFor(fixture, 'ing1').unitLabel).toBe('c');
  }));

  it('says nothing more when the line already uses the unit’s own name', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    expect(fixture.nativeElement.querySelector('#row-unit-ing1-hint')).toBeNull();
  }));

  /**
   * A draft line linked to the catalogue's pound, by a word that is not its name. Closed, the list reads it by the
   * unit's CreatorPantry name — the name the open line's box shows — so a recognised unit looks recognised.
   */
  const POUNDS = {
    ...CUPS,
    id: 'unit5',
    code: 'pound',
    displayName: 'pound',
    pluralName: 'pounds',
    abbreviation: 'lb',
    dimension: 'Mass',
  };
  const DRAFT_LINE: RecipeIngredientGroup = {
    ...SEEDED_GROUP,
    ingredients: [
      {
        ...SEEDED_GROUP.ingredients[0],
        displayText: '2 lb potatoes, peeled',
        ingredientNameText: 'potatoes',
        unitText: 'lb',
        measurementUnitId: 'unit5',
      },
    ],
  };

  function closeLine(fixture: ComponentFixture<RecipeIngredientEditorComponent>, rowKey: string): void {
    fixture.componentInstance.toggleRow(rowKey);
    fixture.detectChanges();
  }

  function lineText(fixture: ComponentFixture<RecipeIngredientEditorComponent>): string {
    return fixture.nativeElement.querySelector('.line-text')?.textContent?.trim() ?? '';
  }

  it('reads a closed line by its CreatorPantry unit, and keeps the line as written', fakeAsync(() => {
    const fixture = createEditor([DRAFT_LINE]);
    settleUnits(fixture, [CUPS, POUNDS]);
    closeLine(fixture, 'ing1');

    expect(lineText(fixture)).toBe('2 pounds potatoes, peeled');
    expect(fixture.nativeElement.querySelector('.row-attention')).toBeNull();
    // Only the reading: what a save keeps is still the line and the word it was written with.
    expect(rowFor(fixture, 'ing1').displayText).toBe('2 lb potatoes, peeled');
    expect(rowFor(fixture, 'ing1').unitLabel).toBe('lb');
  }));

  it('shows the line as written again once it is opened', fakeAsync(() => {
    const fixture = createEditor([DRAFT_LINE]);
    settleUnits(fixture, [CUPS, POUNDS]);

    // createEditor leaves every line open: the words are the saved line, and the box and its hint say the rest.
    expect(lineText(fixture)).toBe('2 lb potatoes, peeled');
    expect((fixture.nativeElement.querySelector('#row-unit-ing1') as HTMLInputElement).value).toBe('pound');
    expect(fixture.nativeElement.querySelector('#row-unit-ing1-hint')?.textContent).toContain('The line says “lb”.');
  }));

  it('reads a closed line as written until the unit list can name its unit', fakeAsync(() => {
    const fixture = createEditor([DRAFT_LINE]);
    closeLine(fixture, 'ing1');
    expect(lineText(fixture)).toBe('2 lb potatoes, peeled');

    failUnits(fixture);
    expect(lineText(fixture)).toBe('2 lb potatoes, peeled');
  }));

  it('reads a closed line whose word already names its unit as written', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);
    closeLine(fixture, 'ing1');

    expect(lineText(fixture)).toBe('2 cups flour');
  }));

  it('keeps the creator own line untouched when an ingredient is picked', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'flo');
    settleSearch(fixture, [ingredientRow('i9', 'All-purpose flour', ['plain flour'])]);
    pick(fixture, 'row-ingredient-ing1', 'i9');

    const row = rowFor(fixture, 'ing1');
    expect(row.ingredientId).toBe('i9');
    // The name follows the pick, because picking is the creator's own act — the same thing the "Which
    // ingredient?" confirmation has always done. The line they wrote is a different matter.
    expect(row.ingredientNameText).toBe('All-purpose flour');
    expect(row.displayText).toBe('2 cups flour');
    expect(row.displayTextIsComposed).toBeFalse();
  }));

  it('sets a picked unit without rewriting the line either', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture, [CUPS, GRAMS]);

    typeInto(fixture, 'unit', 'ing1', 'gra');
    pick(fixture, 'row-unit-ing1', 'unit9');

    const row = rowFor(fixture, 'ing1');
    expect(row.unitId).toBe('unit9');
    expect(row.unitLabel).toBe('Grams');
    expect(row.displayText).toBe('2 cups flour');
  }));

  // Nothing in the vocabulary says "glugs" or "zaatar blend", and a creator who writes them is not wrong.
  it('saves an unmatched unit and ingredient exactly as the creator wrote them', fakeAsync(() => {
    const fixture = createEditor([]);
    settleUnits(fixture);
    const rowKey = clickAddIngredient(fixture);

    typeInto(fixture, 'unit', rowKey, 'glugs');
    typeInto(fixture, 'ingredient', rowKey, 'zaatar blend');
    settleSearch(fixture, []);

    const row = rowFor(fixture, rowKey);
    expect(row.unitLabel).toBe('glugs');
    expect(row.unitId).toBeNull();
    expect(row.ingredientNameText).toBe('zaatar blend');
    expect(row.ingredientId).toBeNull();
    // Composed out of their own words, which is what keeps the line out of the blank-row filter on save.
    expect(row.displayText).toBe('glugs zaatar blend');
    expect(emptyText(fixture, `row-ingredient-${rowKey}`)).toContain('it will be saved as you wrote it');
  }));

  it('makes one ingredient request per pause, never one per keystroke, and cancels what it typed past', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'f');
    typeInto(fixture, 'ingredient', 'ing1', 'fl');
    typeInto(fixture, 'ingredient', 'ing1', 'flo');
    http.expectNone((request) => request.url === INGREDIENTS_URL);

    tick(INGREDIENT_SEARCH_DEBOUNCE_MS);
    const first = http.expectOne((request) => request.url === INGREDIENTS_URL);
    expect(first.request.params.get('search')).toBe('flo');
    // Reference data is global (tenancy.md): neither the route nor the query may name a workspace.
    expect(first.request.urlWithParams).not.toContain('cozy-fall');

    // Typed past before the answer came back. Abandoned rather than raced, so a slow "flo" cannot land on top
    // of the answer for "flour".
    typeInto(fixture, 'ingredient', 'ing1', 'flour');
    tick(INGREDIENT_SEARCH_DEBOUNCE_MS);
    expect(first.cancelled).toBeTrue();

    const second = http.expectOne((request) => request.url === INGREDIENTS_URL);
    expect(second.request.params.get('search')).toBe('flour');
    second.flush({ items: [ingredientRow('i9', 'All-purpose flour')], nextCursor: null });
    fixture.detectChanges();
  }));

  // A term the server would ignore is not worth a request, and answering it with the first page of the whole
  // catalogue would read as "these are your matches".
  it('asks nothing for a term too short to search', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'f');
    tick(INGREDIENT_SEARCH_DEBOUNCE_MS);

    http.expectNone((request) => request.url === INGREDIENTS_URL);
    expect(emptyText(fixture, 'row-ingredient-ing1')).toContain('Type at least 2 characters');
  }));

  /**
   * The failure mode this guards against: the combobox recomputes its selection from whatever list it holds
   * whenever the field is left, so a Tab through an untouched field must not be able to wipe a stored match.
   */
  it('keeps a match when the field is only visited', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    const emissions: (readonly EditableIngredientGroup[])[] = [];
    fixture.componentInstance.groupsChanged.subscribe((groups) => emissions.push(groups));

    boxFor(fixture, 'unit', 'ing1').dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(rowFor(fixture, 'ing1').unitId).toBe('unit1');

    // And the harder case: this line's unit is not in the flushed catalogue at all and "tsp" names nothing in
    // it, so the combobox has nothing to resolve the text to and answers with no selection.
    boxFor(fixture, 'unit', 'ing2').dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(rowFor(fixture, 'ing2').unitId).toBe('unit2');
    expect(rowFor(fixture, 'ing2').unitLabel).toBe('tsp');
    expect(emissions.length).withContext('visiting a field is not an edit').toBe(0);
  }));

  // The other half of that: text that no longer names the matched unit does give the match up. A
  // measurementUnitId still pointing at cups would leave the line saying "glugs" and meaning cups.
  it('gives up a match once the text stops naming it', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture, [CUPS, GRAMS]);

    typeInto(fixture, 'unit', 'ing1', 'glugs');

    const row = rowFor(fixture, 'ing1');
    expect(row.unitId).toBeNull();
    expect(row.unitLabel).toBe('glugs');
    expect(row.matchState).toBe('unmatched');
  }));

  it('lets a creator keep typing when the unit catalogue cannot be read, and offers a retry', fakeAsync(() => {
    const fixture = createEditor();
    failUnits(fixture);

    expect(fixture.nativeElement.querySelector('.reference-notice')).withContext('the degraded notice').toBeTruthy();
    expect(emptyText(fixture, 'row-unit-ing1')).toContain('Unit list unavailable');

    typeInto(fixture, 'unit', 'ing1', 'glugs');
    expect(rowFor(fixture, 'ing1').unitLabel).toBe('glugs');

    const retry: HTMLButtonElement = fixture.nativeElement.querySelector('.reference-notice button');
    retry.click();
    settleUnits(fixture, [CUPS]);

    expect(fixture.nativeElement.querySelector('.reference-notice')).toBeNull();
    // The list is readable again, so "glugs" is simply not in it — a different message from an outage, and the
    // one the row can only give once the catalogue arrived.
    expect(emptyText(fixture, 'row-unit-ing1')).toContain('No unit by that name');
  }));

  it('says when the ingredient search itself failed rather than reporting no matches', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'zaatar');
    tick(INGREDIENT_SEARCH_DEBOUNCE_MS);
    http.expectOne((request) => request.url === INGREDIENTS_URL).flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();

    expect(emptyText(fixture, 'row-ingredient-ing1')).toContain('Ingredient search is unavailable');
    expect(rowFor(fixture, 'ing1').ingredientNameText).toBe('zaatar');
    expect(rowFor(fixture, 'ing1').displayText).toBe('2 cups flour');
  }));

  // One search stream serves every row, so the answers have to be tagged with the row that asked.
  it('never offers one row matches to another', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'flo');
    settleSearch(fixture, [ingredientRow('i9', 'All-purpose flour')]);

    expect(fixture.nativeElement.querySelector('[id="row-ingredient-ing1-option-i9"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[id="row-ingredient-ing2-option-i9"]')).toBeNull();
  }));

  // A search reads the first page only, so "2 results" would read as "there are 2" when it means "at least 2".
  it('announces a truncated match list as the first page of more', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'on');
    settleSearch(fixture, [ingredientRow('i1', 'Onion'), ingredientRow('i2', 'Spring onion')], 'page-2');

    expect(announcement(fixture, 'row-ingredient-ing1')).toBe('2 results, more available — keep typing to narrow');

    // And an exhausted list is reported as what it is.
    typeInto(fixture, 'ingredient', 'ing1', 'onio');
    settleSearch(fixture, [ingredientRow('i1', 'Onion')]);
    expect(announcement(fixture, 'row-ingredient-ing1')).toBe('1 result');
  }));

  // The aliases are why a search matched; without them a correct answer looks like a bug.
  it('shows the aliases that made a match', fakeAsync(() => {
    const fixture = createEditor();
    settleUnits(fixture);

    typeInto(fixture, 'ingredient', 'ing1', 'scall');
    settleSearch(fixture, [ingredientRow('i5', 'Green onion', ['scallion', 'spring onion'])]);

    const option: HTMLElement = fixture.nativeElement.querySelector('[id="row-ingredient-ing1-option-i5"]');
    expect(option.textContent).toContain('Green onion');
    expect(option.textContent).toContain('also scallion, spring onion');
  }));
});
