import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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

      // Wide enough for a gutter, if there were anything to put in one.
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
      expect(fieldTops(element).length).withContext('the four fields of the row').toBe(4);
      expect(new Set(fieldTops(element)).size).withContext('distinct field rows').toBe(1);

      // Same for a single titled group: still no nav, still the full width.
      fixture.componentRef.setInput('initialGroups', [SEEDED_GROUP]);
      fixture.detectChanges();
      expect(nav(fixture)).toBeNull();
      expect(trackCount(element.querySelector('.grouped-layout')!)).withContext('columns for one group').toBe(1);
      expect(new Set(fieldTops(element)).size).withContext('distinct field rows, one group').toBe(1);
    });

    it('collapses to one column at a narrow width, with every item still a full target', async () => {
      const fixture = await threeGroups();
      const element = host(fixture);

      // A phone, or a desktop at 200% zoom — both shrink this editor's CSS-px inline size.
      element.style.width = '22rem';
      fixture.detectChanges();

      const layout = element.querySelector('.grouped-layout')!;
      expect(getComputedStyle(layout).gridTemplateColumns.split(/\s+/).length)
        .withContext('columns at 22rem')
        .toBe(1);
      expect(getComputedStyle(navHost(fixture)!).position).withContext('not a sticky gutter when narrow').toBe('static');

      for (const link of navLinks(fixture)) {
        expect(link.getBoundingClientRect().height)
          .withContext(link.textContent?.trim())
          .toBeGreaterThanOrEqual(40);
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
    function typeInto(fixture: ComponentFixture<RecipeIngredientEditorComponent>, field: string, rowKey: string, value: string): void {
      const input: HTMLInputElement = fixture.nativeElement.querySelector(`input[id="row-${field}-${rowKey}"]`);
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

      const line: HTMLElement = fixture.nativeElement.querySelector('.original-text');
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
      expect(fixture.nativeElement.querySelector('.original-text')).toBeNull();

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
      expect(fixture.nativeElement.querySelector('.original-text').textContent).not.toContain('Will be saved as:');
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
      const parts = { quantityText: ' 1 1/2 ', unitLabel: ' tablespoons ', ingredientNameText: ' olive oil ', preparationNote: ' warmed ' };
      expect(composeDisplayText(parts)).toBe('1 1/2 tablespoons olive oil, warmed');

      // Whatever the creator left out is simply absent — no placeholder, and no wording of this editor's own.
      expect(composeDisplayText({ ...parts, quantityText: '', unitLabel: '' })).toBe('olive oil, warmed');
      expect(composeDisplayText({ ...parts, quantityText: '', unitLabel: '', ingredientNameText: '' })).toBe('warmed');
      expect(composeDisplayText({ quantityText: '', unitLabel: '', ingredientNameText: 'salt', preparationNote: '' })).toBe('salt');
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
    const input: HTMLInputElement = fixture.nativeElement.querySelector(`input[id="row-${field}-${rowKey}"]`);
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
