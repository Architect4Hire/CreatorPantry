import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  RecipeComparisonSectionResult,
  RecipeFieldChange,
  RecipeItemChange,
  RecipeVersionComparison,
} from '../../models/recipe-version.models';
import { RecipeVersionComparisonComponent } from './recipe-version-comparison.component';

function side(versionNumber: number): RecipeVersionComparison['from'] {
  return {
    versionId: `v${versionNumber}`,
    versionNumber,
    source: 'CreatorEdit',
    readiness: 'Draft',
    createdAt: '2026-01-01T00:00:00Z',
  };
}

function section(
  name: string,
  fieldChanges: readonly RecipeFieldChange[] = [],
  itemChanges: readonly RecipeItemChange[] = [],
): RecipeComparisonSectionResult {
  return {
    section: name,
    fieldChanges,
    itemChanges,
    hasChanges: fieldChanges.length > 0 || itemChanges.length > 0,
  };
}

function item(overrides: Partial<RecipeItemChange> = {}): RecipeItemChange {
  return {
    id: 'i1',
    presence: 'Retained',
    moved: false,
    fromParentId: 'g1',
    toParentId: 'g1',
    fromRank: 0,
    toRank: 0,
    fieldChanges: [],
    ...overrides,
  };
}

function comparison(sections: readonly RecipeComparisonSectionResult[]): RecipeVersionComparison {
  return {
    from: side(1),
    to: side(2),
    comparison: { sections, hasChanges: sections.some((entry) => entry.hasChanges) },
  };
}

describe('RecipeVersionComparisonComponent', () => {
  let fixture: ComponentFixture<RecipeVersionComparisonComponent>;

  async function render(value: RecipeVersionComparison): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeVersionComparisonComponent);
    fixture.componentRef.setInput('comparison', value);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [RecipeVersionComparisonComponent] }));

  function rows(element: HTMLElement): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('.row'));
  }

  /**
   * In words rather than with an arrow: "→" is announced inconsistently, so "Version 1 → version 2" can
   * reach a screen reader as "Version 1 version 2".
   */
  it('names both versions in its heading, and names the section by that heading', async () => {
    const element = await render(comparison([section('Metadata')]));

    const heading = element.querySelector('.comparison-title')!;
    expect(heading.textContent).toContain('Version 1 compared with version 2');

    expect(element.querySelector('.comparison')!.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(element.querySelector('.comparison')!.hasAttribute('aria-label')).toBeFalse();
  });

  /**
   * The panel walks the sections it was given, in the order it was given them. It imposes no order of its
   * own — the server publishes every section whether or not it changed, and reordering here would be the
   * beginning of a second opinion about the diff.
   */
  it('renders every section in the order it received them, including unchanged ones', async () => {
    const element = await render(
      comparison([
        section('Metadata', [{ field: 'Title', from: 'Chili', to: 'Chilli' }]),
        section('Timing'),
        section('Ingredients'),
      ]),
    );

    const headings = Array.from(element.querySelectorAll('.section-heading')).map((node) => node.textContent?.trim());
    expect(headings).toEqual(['Metadata', 'Timing', 'Ingredients']);
    expect(element.querySelectorAll('.section-empty').length).toBe(2);
  });

  it('says so plainly when the two versions are identical, and stays quiet after that', async () => {
    const element = await render(comparison([section('Metadata'), section('Timing')]));

    expect(element.querySelector('.comparison-summary')!.textContent).toContain('identical');
    expect(rows(element).length).toBe(0);

    // The summary has already said it. Nine "No changes" headings after it are only noise to read through.
    expect(element.querySelectorAll('.section').length).toBe(0);
  });

  /**
   * The requirement the whole panel turns on: a reader must be able to tell one kind of change from another
   * without seeing colour. Every row prints the legend's own word beside the glyph.
   */
  it('states every change kind in words, not only by glyph and colour', async () => {
    const element = await render(
      comparison([
        section(
          'Ingredients',
          [],
          [
            item({ id: 'a', presence: 'Added', fieldChanges: [{ field: 'IngredientDisplayText', from: null, to: '3 eggs' }] }),
            item({ id: 'r', presence: 'Removed', fieldChanges: [{ field: 'IngredientDisplayText', from: '2 eggs', to: null }] }),
            item({ id: 'c', fieldChanges: [{ field: 'IngredientQuantity', from: '240', to: '260' }] }),
            item({ id: 'm', moved: true, fromRank: 2, toRank: 0 }),
          ],
        ),
      ]),
    );

    expect(rows(element).map((row) => row.querySelector('.row-kind')!.textContent?.trim())).toEqual([
      'Added',
      'Removed',
      'Changed',
      'Moved',
    ]);

    // The glyph is decorative; the word carries the meaning.
    expect(rows(element).every((row) => row.querySelector('.row-glyph')!.getAttribute('aria-hidden') === 'true')).toBeTrue();
  });

  it('reports a move as a move and not as a replacement', async () => {
    const element = await render(
      comparison([section('Ingredients', [], [item({ moved: true, fromRank: 2, toRank: 0 })])]),
    );

    const row = rows(element)[0];
    expect(row.getAttribute('data-kind')).toBe('moved');

    // Positions are 1-based to a reader, who counts from the first line rather than from zero.
    expect(row.querySelector('.row-movement')!.textContent).toContain('from position 3 to 1');

    // The kind label already says "Moved"; repeating it in the note reads as "Moved · Moved from position 3".
    expect(row.querySelector('.row-movement')!.textContent).not.toContain('Moved');

    // Nothing was rewritten, so no values are printed at all.
    expect(row.querySelector('.row-values')).toBeNull();
  });

  it('reports an item that was both reworded and moved as both', async () => {
    const element = await render(
      comparison([
        section(
          'Ingredients',
          [],
          [
            item({
              moved: true,
              fromRank: 0,
              toRank: 2,
              fieldChanges: [{ field: 'IngredientDisplayText', from: '2 eggs', to: '3 eggs' }],
            }),
          ],
        ),
      ]),
    );

    const row = rows(element)[0];
    expect(row.querySelector('.row-kind')!.textContent?.trim()).toBe('Changed');
    expect(row.querySelector('.row-movement')!.textContent).toContain('Moved');
    expect(row.querySelector('.row-value--to')!.textContent).toContain('3 eggs');
  });

  it('names a cross-group move as one, rather than printing two unrelated positions', async () => {
    const element = await render(
      comparison([section('Ingredients', [], [item({ moved: true, fromParentId: 'g1', toParentId: 'g2', toRank: 0 })])]),
    );

    expect(rows(element)[0].querySelector('.row-movement')!.textContent).toContain('another group');
  });

  it('repeats the movement note once per item, not once per field it changed', async () => {
    const element = await render(
      comparison([
        section(
          'Ingredients',
          [],
          [
            item({
              moved: true,
              fromRank: 0,
              toRank: 1,
              fieldChanges: [
                { field: 'IngredientDisplayText', from: '2 eggs', to: '3 eggs' },
                { field: 'IngredientQuantity', from: '2', to: '3' },
              ],
            }),
          ],
        ),
      ]),
    );

    expect(rows(element).length).toBe(2);
    expect(element.querySelectorAll('.row-movement').length).toBe(1);
  });

  it('humanises a field name it has no label for, so a newly added field still reads', async () => {
    const element = await render(
      comparison([section('Timing', [{ field: 'PrepTimeMinutes', from: '20', to: '25' }])]),
    );

    expect(rows(element)[0].querySelector('.row-label')!.textContent?.trim()).toBe('Prep time minutes');
  });

  /**
   * content.md is explicit that editorial status does not mean anything was delivered to any provider, so
   * the section a creator reads is headed "Status" rather than with a word that implies publication.
   */
  it('heads the publication section as Status', async () => {
    const element = await render(comparison([section('Publication', [{ field: 'Status', from: 'Draft', to: 'Ready' }])]));

    expect(element.querySelector('.section-heading')!.textContent?.trim()).toBe('Status');
  });

  it('prints both values for a replacement, tagged so neither is mistaken for the other', async () => {
    const element = await render(comparison([section('Metadata', [{ field: 'Title', from: 'Chili', to: 'Chilli' }])]));

    const row = rows(element)[0];
    expect(row.querySelector('.row-value--from')!.textContent).toContain('Chili');
    expect(row.querySelector('.row-value--to')!.textContent).toContain('Chilli');
    expect(row.querySelector('.row-value--from')!.textContent).toContain('was');
    expect(row.querySelector('.row-value--to')!.textContent).toContain('now');
  });

  it('names an item that carries no field changes from its section', async () => {
    const element = await render(
      comparison([
        section('Ingredients', [], [item({ id: 'g', moved: true, fromParentId: null, toParentId: null, fromRank: 1, toRank: 0 })]),
        section('Instructions', [], [item({ id: 's', moved: true, fromRank: 1, toRank: 0 })]),
      ]),
    );

    // A null parent inside a nested section means the item is a group, which is what the contract states.
    expect(rows(element).map((row) => row.querySelector('.row-label')!.textContent?.trim())).toEqual([
      'Ingredient group',
      'Step',
    ]);
  });

  it('shows a legend for exactly the kinds it can render', async () => {
    const element = await render(comparison([section('Metadata')]));

    expect(element.querySelectorAll('cp-diff-legend li').length).toBe(4);
  });
});
