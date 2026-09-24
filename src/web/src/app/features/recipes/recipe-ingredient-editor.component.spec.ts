import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeIngredientEditorComponent } from './recipe-ingredient-editor.component';

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

const SEEDED_GROUP: RecipeIngredientGroup = {
  id: 'group1',
  title: 'For the crust',
  sortOrder: 0,
  ingredients: [
    {
      id: 'ing1',
      sortOrder: 0,
      displayText: '2 cups flour',
      ingredientNameText: 'flour',
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
      ingredientNameText: 'salt',
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

    const pills = fixture.nativeElement.querySelectorAll('cp-status-pill');
    expect(pills.length).toBeGreaterThanOrEqual(2);
    // Every status pill's tone is backed by required text content — not merely a background color.
    pills.forEach((pill: HTMLElement) => expect(pill.textContent?.trim().length).toBeGreaterThan(0));
    expect(pills[0].textContent).toContain('Matched');
    expect(pills[1].textContent).toContain('No match');
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

  it('reorders rows with moveRow and announces the move for screen readers', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    const [group] = fixture.componentInstance.groups();
    const firstKey = group.ingredients[0].key;
    fixture.componentInstance.moveRow(group.key, firstKey, 1);
    fixture.detectChanges();

    expect(fixture.componentInstance.groups()[0].ingredients[1].key).toBe(firstKey);
    expect(fixture.componentInstance.moveAnnouncement()).toContain('position 2 of 2');
  });

  it('renders up/down buttons disabled at the boundaries of the row list', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

    const upButtons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button[aria-label^="Move ingredient"][aria-label$="up"]'));
    const downButtons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('button[aria-label^="Move ingredient"][aria-label$="down"]'));

    expect(upButtons[0].disabled).toBeTrue();
    expect(downButtons[downButtons.length - 1].disabled).toBeTrue();
    expect(upButtons[upButtons.length - 1].disabled).toBeFalse();
    expect(downButtons[0].disabled).toBeFalse();
  });

  it('presents a candidate picker for an ambiguous/unresolved match instead of auto-selecting one', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.onLinesAccepted({
      rows: [
        {
          key: 'row1',
          id: null,
          displayText: 'cilantro',
          ingredientNameText: 'cilantro',
          quantityText: '',
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
    });
    fixture.detectChanges();

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
          ingredientNameText: 'cilantro',
          quantityText: '',
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

  it('offers a "Fixed amount (e.g. garnish)" scaling option rather than a separate garnish field', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
    fixture.detectChanges();

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
});
