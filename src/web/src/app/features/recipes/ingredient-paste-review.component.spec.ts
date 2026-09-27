import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { IngredientPasteReviewComponent } from './ingredient-paste-review.component';

async function createFixture(): Promise<{ fixture: ComponentFixture<IngredientPasteReviewComponent>; http: HttpTestingController }> {
  await TestBed.configureTestingModule({
    imports: [IngredientPasteReviewComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(IngredientPasteReviewComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.detectChanges();

  return { fixture, http };
}

const MATCHED_LINE = {
  tokens: {
    originalText: '2 tsp kosher salt',
    isGroupMarker: false,
    quantity: { span: { start: 0, length: 1, text: '2' }, value: { numerator: '2', denominator: '1' }, range: null, isInvalid: false },
    packageQuantity: null,
    unitCandidate: { start: 2, length: 3, text: 'tsp' },
    ingredientText: { start: 6, length: 11, text: 'kosher salt' },
    preparationText: [],
    isOptional: false,
    ambiguities: [],
  },
  ingredientMatch: {
    inputText: 'kosher salt',
    resolved: { ingredientId: 'ing1', canonicalName: 'kosher salt', kind: 'CanonicalName' },
    alternates: [],
    isAmbiguous: false,
  },
  unitMatch: {
    inputText: 'tsp',
    resolved: { measurementUnitId: 'unit1', displayName: 'teaspoon', kind: 'Code' },
    alternates: [],
    isAmbiguous: false,
  },
};

const AMBIGUOUS_LINE = {
  tokens: {
    originalText: 'cilantro',
    isGroupMarker: false,
    quantity: null,
    packageQuantity: null,
    unitCandidate: null,
    ingredientText: { start: 0, length: 8, text: 'cilantro' },
    preparationText: [],
    isOptional: false,
    ambiguities: [{ kind: 'NoQuantityDetected', span: { start: 0, length: 8, text: 'cilantro' }, message: 'No quantity found.' }],
  },
  ingredientMatch: {
    inputText: 'cilantro',
    resolved: null,
    alternates: [
      { ingredientId: 'ing2', canonicalName: 'Cilantro', kind: 'CanonicalName' },
      { ingredientId: 'ing3', canonicalName: 'Coriander', kind: 'Alias' },
    ],
    isAmbiguous: true,
  },
  unitMatch: null,
};

/** A confidently matched line, with whatever needs varying for one eligibility case patched over it. */
function lineLike(overrides: Record<string, unknown>): unknown {
  return {
    ...MATCHED_LINE,
    ...overrides,
    tokens: { ...MATCHED_LINE.tokens, ...((overrides['tokens'] as Record<string, unknown>) ?? {}) },
  };
}

const GROUP_MARKER_LINE = lineLike({
  tokens: { originalText: 'For the filling', isGroupMarker: true, ingredientText: null, unitCandidate: null },
  ingredientMatch: null,
  unitMatch: null,
});

/** "kosher salt, to taste" — matched with confidence, simply no number to find. */
const NO_QUANTITY_LINE = lineLike({
  tokens: {
    originalText: 'kosher salt, to taste',
    quantity: null,
    ambiguities: [{ kind: 'NoQuantityDetected', span: { start: 0, length: 11, text: 'kosher salt' }, message: 'No quantity found.' }],
  },
});

const INVALID_QUANTITY_LINE = lineLike({
  tokens: {
    originalText: '1/x cups flour',
    quantity: { span: { start: 0, length: 3, text: '1/x' }, value: null, range: null, isInvalid: true },
    ambiguities: [{ kind: 'InvalidQuantity', span: { start: 0, length: 3, text: '1/x' }, message: 'Could not read that quantity.' }],
  },
});

/** The matcher offered a candidate but flagged itself unsure — the line's pill is a warning, not a success. */
const RESOLVED_BUT_AMBIGUOUS_LINE = lineLike({
  ingredientMatch: {
    inputText: 'pepper',
    resolved: { ingredientId: 'ing9', canonicalName: 'black pepper', kind: 'CanonicalName' },
    alternates: [{ ingredientId: 'ing10', canonicalName: 'white pepper', kind: 'CanonicalName' }],
    isAmbiguous: true,
  },
});

/** Free text the matcher had nothing to say about at all — 'unreviewed', which is not 'matched'. */
const NO_MATCH_ATTEMPTED_LINE = lineLike({
  tokens: { originalText: 'a pinch of something', ingredientText: null, unitCandidate: null, quantity: null },
  ingredientMatch: null,
  unitMatch: null,
});

const TWO_GROUPS = [
  { key: 'g1', label: 'For the crust' },
  { key: 'g2', label: 'For the filling' },
];

/** Parses a fixed set of lines and returns the fixture ready to assert against. */
async function parseLines(lines: readonly unknown[], groups: readonly { key: string; label: string }[] = TWO_GROUPS) {
  const { fixture, http } = await createFixture();
  fixture.componentRef.setInput('groups', groups);
  fixture.detectChanges();

  const accepted: { rows: readonly unknown[]; targetGroupKey: string | null }[] = [];
  fixture.componentInstance.linesAccepted.subscribe((event) => accepted.push(event));

  fixture.componentInstance.pastedText.set(lines.map((_, index) => 'line ' + index).join('\n'));
  const parseCall = fixture.componentInstance.parse();
  http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse').flush({ lines });
  await parseCall;
  fixture.detectChanges();

  return { fixture, http, accepted };
}

describe('IngredientPasteReviewComponent batch actions', () => {
  it('wires the paste hint to the textarea, so it is announced rather than only drawn', async () => {
    const { fixture } = await createFixture();
    const textarea = fixture.nativeElement.querySelector('#paste-ingredients-text') as HTMLTextAreaElement;

    const describedBy = textarea.getAttribute('aria-describedby');
    expect(describedBy).toBe('paste-ingredients-text-hint');

    const hint = fixture.nativeElement.querySelector('#' + describedBy);
    expect(hint).withContext('aria-describedby must resolve to a real element').toBeTruthy();
    expect(hint.textContent).toContain('One ingredient per line');
  });

  it('offers every group as a destination, plus a new one, and preselects what the host chose', async () => {
    const { fixture } = await parseLines([MATCHED_LINE]);
    fixture.componentInstance.targetGroupKey.set('g2');
    fixture.detectChanges();

    const select = fixture.nativeElement.querySelector('#paste-target-group') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
      'For the crust',
      'For the filling',
      'A new group at the end',
    ]);
    expect(select.value).toBe('g2');

    // The label is wired to the control, like every other field in this dialog.
    const label = fixture.nativeElement.querySelector('label[for="paste-target-group"]');
    expect(label?.textContent).toContain('Add to');
  });

  it('sends a single accepted line to the chosen group, not to the first one', async () => {
    const { fixture, accepted } = await parseLines([MATCHED_LINE]);
    fixture.componentInstance.targetGroupKey.set('g2');
    fixture.detectChanges();

    fixture.componentInstance.accept(0, MATCHED_LINE as never);

    expect(accepted.length).toBe(1);
    expect(accepted[0].targetGroupKey).toBe('g2');
  });

  it('reads the selector back out of the DOM, so choosing a group is what actually changes the target', async () => {
    const { fixture, accepted } = await parseLines([MATCHED_LINE]);
    const select = fixture.nativeElement.querySelector('#paste-target-group') as HTMLSelectElement;

    select.value = 'g2';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.componentInstance.targetGroupKey()).toBe('g2');

    // The empty option value is the sentinel for "a new group", and must arrive as null rather than ''.
    select.value = '';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.componentInstance.targetGroupKey()).toBeNull();

    fixture.componentInstance.acceptAllMatched();
    expect(accepted[0].targetGroupKey).toBeNull();
  });

  describe('what "Add all matched" will and will not take', () => {
    const cases: readonly { readonly name: string; readonly line: unknown; readonly eligible: boolean }[] = [
      { name: 'a confidently matched line', line: MATCHED_LINE, eligible: true },
      { name: 'a matched line with no quantity to find', line: NO_QUANTITY_LINE, eligible: true },
      { name: 'a section heading', line: GROUP_MARKER_LINE, eligible: false },
      { name: 'a quantity the tokenizer could not read', line: INVALID_QUANTITY_LINE, eligible: false },
      { name: 'a match the matcher flagged ambiguous even though it resolved one', line: RESOLVED_BUT_AMBIGUOUS_LINE, eligible: false },
      { name: 'an unresolved ambiguous match', line: AMBIGUOUS_LINE, eligible: false },
      { name: 'a line the matcher never attempted', line: NO_MATCH_ATTEMPTED_LINE, eligible: false },
    ];

    for (const { name, line, eligible } of cases) {
      it(`${eligible ? 'takes' : 'leaves'} ${name}`, async () => {
        const { fixture, accepted } = await parseLines([line]);

        expect(fixture.componentInstance.bulkAcceptableCount()).toBe(eligible ? 1 : 0);

        fixture.componentInstance.acceptAllMatched();
        expect(accepted.length).toBe(eligible ? 1 : 0);
      });
    }

    it('does not count a line that was already accepted on its own', async () => {
      const { fixture } = await parseLines([MATCHED_LINE, NO_QUANTITY_LINE]);
      expect(fixture.componentInstance.bulkAcceptableCount()).toBe(2);

      fixture.componentInstance.accept(0, MATCHED_LINE as never);
      fixture.detectChanges();

      expect(fixture.componentInstance.bulkAcceptableCount()).toBe(1);
    });
  });

  it('takes a line once the creator has resolved it by hand, because a choice is review', async () => {
    const { fixture, accepted } = await parseLines([AMBIGUOUS_LINE]);
    expect(fixture.componentInstance.bulkAcceptableCount()).toBe(0);

    fixture.componentInstance.onIngredientChoiceChange(0, AMBIGUOUS_LINE as never, 'ing2');
    fixture.detectChanges();

    expect(fixture.componentInstance.bulkAcceptableCount()).toBe(1);

    fixture.componentInstance.acceptAllMatched();
    expect(accepted.length).toBe(1);
    expect((accepted[0].rows[0] as { ingredientId: string }).ingredientId).toBe('ing2');
  });

  it('still leaves out a line the creator deliberately left unmatched, without disabling its own button', async () => {
    const { fixture } = await parseLines([AMBIGUOUS_LINE]);

    // The explicit "leave unmatched" choice — a decision, but not a match, and this button says matched.
    fixture.componentInstance.onIngredientChoiceChange(0, AMBIGUOUS_LINE as never, '');
    fixture.detectChanges();

    expect(fixture.componentInstance.bulkAcceptableCount()).toBe(0);
    const perLineButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find((button) =>
      (button as HTMLButtonElement).textContent?.includes('Add to recipe'),
    ) as HTMLButtonElement;
    expect(perLineButton.disabled).toBeFalse();
  });

  it('disables the bulk button and says what is left when nothing qualifies', async () => {
    const { fixture } = await parseLines([AMBIGUOUS_LINE, GROUP_MARKER_LINE]);

    const bulkButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find((button) =>
      (button as HTMLButtonElement).textContent?.includes('Add all matched'),
    ) as HTMLButtonElement;

    expect(bulkButton.textContent).toContain('Add all matched (0)');
    expect(bulkButton.disabled).toBeTrue();
    // Never "all matched" without saying what that leaves behind.
    expect(fixture.nativeElement.textContent).toContain('1 line needs review');
    expect(fixture.nativeElement.textContent).toContain('1 heading skipped');
  });

  it('announces the batch, its destination, and what still needs review', async () => {
    const { fixture } = await parseLines([MATCHED_LINE, NO_QUANTITY_LINE, AMBIGUOUS_LINE]);
    fixture.componentInstance.targetGroupKey.set('g2');
    fixture.detectChanges();

    fixture.componentInstance.acceptAllMatched();
    fixture.detectChanges();

    const region = fixture.nativeElement.querySelector('[role="status"][aria-live="polite"]');
    expect(region.textContent).toContain('2 ingredients added to For the filling.');
    expect(region.textContent).toContain('1 line still need');
  });

  it('sends one batch rather than one emission per line', async () => {
    const lines = [MATCHED_LINE, NO_QUANTITY_LINE, MATCHED_LINE, GROUP_MARKER_LINE, AMBIGUOUS_LINE];
    const { fixture, accepted } = await parseLines(lines);

    fixture.componentInstance.acceptAllMatched();

    expect(accepted.length).toBe(1);
    expect(accepted[0].rows.length).toBe(3);
  });

  it('marks every accepted line as Added and cannot take the same line twice', async () => {
    const { fixture, accepted } = await parseLines([MATCHED_LINE, NO_QUANTITY_LINE]);

    fixture.componentInstance.acceptAllMatched();
    fixture.detectChanges();

    const labels = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .map((button) => (button as HTMLButtonElement).textContent?.trim())
      .filter((text) => text === 'Added' || text === 'Add to recipe');
    expect(labels).toEqual(['Added', 'Added']);

    fixture.componentInstance.acceptAllMatched();
    expect(accepted.length).withContext('a second click must not re-send the same rows').toBe(1);
  });

  it('handles a long mixed paste, taking only the confident lines', async () => {
    // 19 lines: 12 eligible (10 plain + 2 with no quantity), 3 headings, 2 ambiguous, 1 invalid quantity,
    // 1 never attempted.
    const lines = [
      GROUP_MARKER_LINE,
      ...Array.from({ length: 10 }, () => MATCHED_LINE),
      NO_QUANTITY_LINE,
      NO_QUANTITY_LINE,
      GROUP_MARKER_LINE,
      AMBIGUOUS_LINE,
      AMBIGUOUS_LINE,
      INVALID_QUANTITY_LINE,
      NO_MATCH_ATTEMPTED_LINE,
      GROUP_MARKER_LINE,
    ];
    expect(lines.length).toBe(19);

    const { fixture, accepted } = await parseLines(lines);

    expect(fixture.componentInstance.bulkAcceptableCount()).toBe(12);
    expect(fixture.componentInstance.needsReviewCount()).toBe(4);
    expect(fixture.componentInstance.groupMarkerCount()).toBe(3);

    fixture.componentInstance.acceptAllMatched();

    expect(accepted.length).toBe(1);
    expect(accepted[0].rows.length).toBe(12);
    // Nothing a heading contributed came through as an ingredient.
    expect(accepted[0].rows.some((row) => (row as { displayText: string }).displayText === 'For the filling')).toBeFalse();

    // The four that need review are still offered, one at a time.
    expect(fixture.componentInstance.bulkAcceptableCount()).toBe(0);
    expect(fixture.componentInstance.needsReviewCount()).toBe(4);
  });
});

describe('IngredientPasteReviewComponent', () => {
  it('does nothing to the recipe until a line is explicitly accepted — parsing alone changes nothing', async () => {
    const { fixture, http } = await createFixture();
    const accepted: unknown[] = [];
    fixture.componentInstance.linesAccepted.subscribe((event) => accepted.push(event));

    fixture.componentInstance.pastedText.set('2 tsp kosher salt');
    const parseCall = fixture.componentInstance.parse();
    http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse').flush({ lines: [MATCHED_LINE] });
    await parseCall;
    fixture.detectChanges();

    expect(accepted.length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('2 tsp kosher salt');
  });

  it('emits an editable row only once "Add to recipe" is clicked, then disables that line\'s button', async () => {
    const { fixture, http } = await createFixture();
    const accepted: { rows: readonly unknown[] }[] = [];
    fixture.componentInstance.linesAccepted.subscribe((event) => accepted.push(event));

    fixture.componentInstance.pastedText.set('2 tsp kosher salt');
    const parseCall = fixture.componentInstance.parse();
    http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse').flush({ lines: [MATCHED_LINE] });
    await parseCall;
    fixture.detectChanges();

    fixture.componentInstance.accept(0, MATCHED_LINE as never);
    fixture.detectChanges();

    expect(accepted.length).toBe(1);
    expect(fixture.componentInstance.isAccepted(0)).toBeTrue();

    // The accept button for an already-accepted line is disabled, not just relabeled — a second click cannot add a duplicate.
    const acceptButtons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('.review-line button'));
    expect(acceptButtons[0].disabled).toBeTrue();
    expect(acceptButtons[0].textContent).toContain('Added');
  });

  it('shows a picker for an ambiguous match and requires an explicit choice before accepting resolves it', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.pastedText.set('cilantro');
    const parseCall = fixture.componentInstance.parse();
    http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse').flush({ lines: [AMBIGUOUS_LINE] });
    await parseCall;
    fixture.detectChanges();

    // By id, not the first select on the page: the batch actions bar now has a group selector above this one.
    const select: HTMLSelectElement = fixture.nativeElement.querySelector('#ingredient-choice-0');
    expect(select).toBeTruthy();
    expect(select.options.length).toBe(3); // placeholder + two alternates

    const accepted: { rows: { ingredientId: string | null }[] }[] = [];
    fixture.componentInstance.linesAccepted.subscribe((event) => accepted.push(event as never));

    // Accepting without choosing leaves it unresolved rather than guessing among the alternates.
    fixture.componentInstance.accept(0, AMBIGUOUS_LINE as never);
    expect(accepted[0].rows[0].ingredientId).toBeNull();
  });

  it('applies the creator\'s picked candidate when accepted', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.pastedText.set('cilantro');
    const parseCall = fixture.componentInstance.parse();
    http.expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse').flush({ lines: [AMBIGUOUS_LINE] });
    await parseCall;
    fixture.detectChanges();

    fixture.componentInstance.onIngredientChoiceChange(0, AMBIGUOUS_LINE as never, 'ing3');

    const accepted: { rows: { ingredientId: string | null; ingredientNameText: string }[] }[] = [];
    fixture.componentInstance.linesAccepted.subscribe((event) => accepted.push(event as never));
    fixture.componentInstance.accept(0, AMBIGUOUS_LINE as never);

    expect(accepted[0].rows[0].ingredientId).toBe('ing3');
    expect(accepted[0].rows[0].ingredientNameText).toBe('Coriander');
  });

  it('surfaces a validation failure message rather than a silent no-op', async () => {
    const { fixture, http } = await createFixture();
    fixture.componentInstance.pastedText.set('x'.repeat(500));
    const parseCall = fixture.componentInstance.parse();
    http
      .expectOne('https://gateway.example/api/v1/workspaces/cozy-fall/ingredient-tools/parse')
      .flush({ code: 'recipes.ingredient-lines.invalid_request', errors: { lines: ['Too long.'] } }, { status: 400, statusText: 'Bad Request' });
    await parseCall;
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain('Too long.');
  });

  it('does not parse a blank paste', async () => {
    const { fixture } = await createFixture();
    fixture.componentInstance.pastedText.set('   \n  ');
    await fixture.componentInstance.parse();

    expect(fixture.componentInstance.state().status).toBe('idle');
  });
});
