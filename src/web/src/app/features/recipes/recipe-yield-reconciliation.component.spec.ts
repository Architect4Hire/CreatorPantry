import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { RecipeYieldReconciliationComponent } from './recipe-yield-reconciliation.component';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const YIELD_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/recalculate-yield`;
const UNITS_URL = 'https://gateway.example/api/v1/reference/units';

const CATALOGUE = [
  {
    id: 'unit-cup',
    code: 'cup-us',
    displayName: 'cup',
    pluralName: 'cups',
    abbreviation: 'c',
    dimension: 'Volume',
    system: 'UsCustomary',
    baseUnitFactor: 236.588,
    displayPrecision: 2,
  },
];

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      status: 'Reconciled',
      batchYield: 12,
      servingCount: 6,
      servingSize: 2,
      solvedField: null,
      // The calculator's own string, verbatim. This fixture used to carry an invented one, which is why the
      // panel's formula gloss could be keyed to strings the server never emits and still pass.
      formula: 'servingCount × servingSize = batchYield',
      panVolume: null,
      panFillRatio: null,
      panComparable: null,
      ...overrides,
    },
  };
}

interface Harness {
  readonly fixture: ComponentFixture<RecipeYieldReconciliationComponent>;
  readonly http: HttpTestingController;
}

async function createFixture(
  options: { yieldUnitId?: string | null; sourceVersionNumber?: number | null } = {},
): Promise<Harness> {
  await TestBed.configureTestingModule({
    imports: [RecipeYieldReconciliationComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(RecipeYieldReconciliationComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.componentRef.setInput('recipeId', RECIPE_ID);
  fixture.componentRef.setInput(
    'sourceVersionNumber',
    options.sourceVersionNumber === undefined ? 3 : options.sourceVersionNumber,
  );
  fixture.componentRef.setInput(
    'yieldUnitId',
    options.yieldUnitId === undefined ? 'unit-cup' : options.yieldUnitId,
  );
  fixture.detectChanges();

  http.expectOne((request) => request.url === UNITS_URL).flush({ items: CATALOGUE, nextCursor: null });
  await fixture.whenStable();
  fixture.detectChanges();

  return { fixture, http };
}

async function reconcile(
  harness: Harness,
  respond: (request: ReturnType<HttpTestingController['expectOne']>) => void,
): Promise<void> {
  const call = harness.fixture.componentInstance.reconcile();
  respond(harness.http.expectOne(YIELD_URL));
  await call;
  harness.fixture.detectChanges();
}

function text(fixture: ComponentFixture<RecipeYieldReconciliationComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

function detailPairs(fixture: ComponentFixture<RecipeYieldReconciliationComponent>): string[] {
  return Array.from(fixture.nativeElement.querySelectorAll('.result-detail dt, .result-detail dd')).map(
    (node) => ((node as HTMLElement).textContent ?? '').replace(/\s+/g, ' ').trim(),
  );
}

describe('RecipeYieldReconciliationComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  // ---- Assumptions ----

  it('says that nothing is saved and that only what is typed is used', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain('nothing here is saved');
    expect(text(fixture)).toContain("The recipe's own yield quantity is never filled in or assumed");
  });

  // The server resolves the dimension from the version's yield unit, which the form cannot show on its own.
  it('names the unit the amounts are read in', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain("read in cups — this recipe's yield unit");
  });

  // A recipe with no yield unit is read as a plain count, which also silently rules out a pan comparison.
  it('says what happens when the recipe records no yield unit, including for the pan', async () => {
    const { fixture } = await createFixture({ yieldUnitId: null });

    expect(text(fixture)).toContain('records no yield unit');
    expect(text(fixture)).toContain('read as plain counts');
    expect(text(fixture)).toContain('pan comparison needs a volume yield');
  });

  // ---- The four statuses ----

  it('reports three values that agree, with the formula in symbols and in words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);

    await reconcile(harness, (req) => {
      expect(req.request.body).toEqual({
        sourceVersionNumber: 3,
        batchYield: 12,
        servingCount: 6,
        servingSize: 2,
        displayPrecision: 2,
      });
      req.flush(payload());
    });

    const card = text(harness.fixture);
    expect(card).toContain('These agree');
    expect(card).toContain('servingCount × servingSize = batchYield');
    expect(card).toContain('which matches the batch yield you gave');
  });

  it('marks exactly the computed value as computed and the rest as entered', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload({ status: 'Solved', solvedField: 'BatchYield' })));

    const pairs = detailPairs(harness.fixture);
    expect(pairs).toContain('Batch yield');
    expect(pairs).toContain('12 computed');
    expect(pairs).toContain('6 as you entered');
    expect(pairs).toContain('2 as you entered');
    expect(text(harness.fixture)).toContain('Worked out the missing one');
  });

  // recipes.md: none of the three is assumed to be the wrong one, and nothing is overridden.
  it('reports a contradiction with all three values unchanged, and says nothing was overridden', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(99);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload({ status: 'Contradictory', batchYield: 99 })));

    const card = text(harness.fixture);
    expect(card).toContain("These don't agree");
    expect(card).toContain('Nothing was changed');
    expect(card).toContain('none of the three is assumed to be the wrong one');

    const pairs = detailPairs(harness.fixture);
    expect(pairs).toContain('99 as you entered');
    expect(pairs).toContain('6 as you entered');
    expect(pairs).toContain('2 as you entered');
  });

  // Too few values is an answer, not a failure — and it must not be filled in from the recipe.
  it('reports too few values as a conclusion, with no formula', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);

    await reconcile(harness, (req) => {
      expect(req.request.body).toEqual({ sourceVersionNumber: 3, batchYield: 12, displayPrecision: 2 });
      req.flush(payload({ status: 'InsufficientInput', servingCount: null, servingSize: null, formula: null }));
    });

    const card = text(harness.fixture);
    expect(card).toContain('Not enough to work from');
    expect(card).toContain('Nothing was assumed in place of the missing ones');
    expect(detailPairs(harness.fixture)).not.toContain('Formula');
  });

  it('shows a value that was never given as blank rather than as zero', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    await reconcile(harness, (req) =>
      req.flush(payload({ status: 'InsufficientInput', servingCount: null, servingSize: null, formula: null })),
    );

    // The em dash sits between them for sighted readers and is aria-hidden, so a screen reader hears
    // "No value not given" — the placeholder is never read as a character.
    expect(detailPairs(harness.fixture)).toContain('No value— not given');

    const placeholder: HTMLElement = harness.fixture.nativeElement.querySelector('.row-value [aria-hidden]');
    expect(placeholder.textContent).toBe('—');
  });

  // ---- Pan comparison ----

  it('shows no pan block when no capacity was given', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    await reconcile(harness, (req) => req.flush(payload({ status: 'Solved', solvedField: 'ServingSize' })));

    expect(text(harness.fixture)).not.toContain('Pan comparison');
  });

  it('shows the fill ratio as an exact fraction, with the not-a-verdict caution', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    harness.fixture.componentInstance.panVolume.set(9);

    await reconcile(harness, (req) =>
      req.flush(
        payload({ panVolume: 9, panComparable: true, panFillRatio: { numerator: '4', denominator: '3' } }),
      ),
    );

    const card = text(harness.fixture);
    expect(card).toContain('Pan comparison');
    expect(card).toContain('4/3');
    expect(card).toContain('not whether it fits');
    expect(card).toContain("does not know your pan's shape");
  });

  // The server does not say which of the two reasons applies, so the message covers both honestly.
  it('explains a pan capacity that could not be compared', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.panVolume.set(9);
    await reconcile(harness, (req) => req.flush(payload({ panVolume: 9, panComparable: false })));

    const card = text(harness.fixture);
    expect(card).toContain('could not be compared');
    expect(card).toContain("needs the recipe's yield to be a volume and a batch yield to be known");
    expect(card).not.toContain('Fill ratio');
  });

  // ---- Refusals ----

  it('shows a non-positive value against its own field', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(0);

    await reconcile(harness, (req) =>
      req.flush(
        {
          code: 'recipes.yield-recalculation.invalid_request',
          errors: { batchYield: ['The batch yield must be greater than zero.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(text(harness.fixture)).toContain('The batch yield must be greater than zero.');
    const input: HTMLInputElement = harness.fixture.nativeElement.querySelector(
      `#${harness.fixture.componentInstance.batchYieldFieldId}`,
    );
    expect(input.getAttribute('aria-invalid')).toBe('true');
  });

  it('clears a previous card when a later request is refused', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload()));
    expect(harness.fixture.nativeElement.querySelector('.result-card')).not.toBeNull();

    harness.fixture.componentInstance.batchYield.set(0);
    await reconcile(harness, (req) =>
      req.flush(
        {
          code: 'recipes.yield-recalculation.invalid_request',
          errors: { batchYield: ['The batch yield must be greater than zero.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('clears a card when the recipe moves past the version it was computed against', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload()));

    harness.fixture.componentRef.setInput('sourceVersionNumber', 4);
    harness.fixture.detectChanges();

    expect(harness.fixture.nativeElement.querySelector('.result-card')).toBeNull();
  });

  it('refuses to reconcile a recipe with no saved version, and says what to do', async () => {
    const { fixture, http } = await createFixture({ sourceVersionNumber: null });

    expect(text(fixture)).toContain('no saved version yet');
    const submit: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    expect(submit.disabled).toBeTrue();

    await fixture.componentInstance.reconcile();
    http.expectNone(YIELD_URL);
  });

  it('explains a version that no longer exists', async () => {
    const harness = await createFixture();
    await reconcile(harness, (req) =>
      req.flush({ code: 'recipes.version.not_found' }, { status: 404, statusText: 'Not Found' }),
    );

    expect(text(harness.fixture)).toContain('That version no longer exists');
  });

  it('offers a retry when the calculation could not be reached', async () => {
    const harness = await createFixture();
    await reconcile(harness, (req) => req.flush({}, { status: 500, statusText: 'Server Error' }));

    expect(text(harness.fixture)).toContain('try again');
  });

  // ---- Applying ----

  async function reconciled(
    overrides: Record<string, unknown> = {},
    options: { editorIsDirty?: boolean; savedYieldQuantity?: number | null } = {},
  ): Promise<Harness> {
    const harness = await createFixture();
    harness.fixture.componentRef.setInput(
      'savedYieldQuantity',
      options.savedYieldQuantity === undefined ? 10 : options.savedYieldQuantity,
    );
    if (options.editorIsDirty) harness.fixture.componentRef.setInput('editorIsDirty', true);

    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload(overrides)));
    return harness;
  }

  it('offers to record a reconciled batch yield, showing what would change', async () => {
    const harness = await reconciled();

    expect(harness.fixture.componentInstance.canApply()).toBeTrue();
    expect(text(harness.fixture)).toContain('What would change');
    expect(text(harness.fixture)).toContain('Recipe yield quantity');
  });

  it('raises the batch yield and how the change reads', async () => {
    const harness = await reconciled();
    const raised: unknown[] = [];
    harness.fixture.componentInstance.applyRequested.subscribe((event) => raised.push(event));

    harness.fixture.componentInstance.requestApply();

    expect(raised).toEqual([jasmine.objectContaining({ yieldQuantity: 12, beforeLabel: '10', afterLabel: '12' })]);
  });

  it('offers a solved batch yield too', async () => {
    const harness = await reconciled({ status: 'Solved', solvedField: 'BatchYield' });

    expect(harness.fixture.componentInstance.canApply()).toBeTrue();
  });

  // The point of a contradiction is that nothing decided which number is right. Offering to save one would
  // make the choice the calculation refused to make.
  it('refuses to record anything from a contradiction, and says why', async () => {
    const harness = await reconciled({ status: 'Contradictory', batchYield: 99 });

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(text(harness.fixture)).toContain('nothing here decided which one is right');
  });

  it('refuses to record anything when there was too little to work from', async () => {
    const harness = await reconciled({
      status: 'InsufficientInput',
      servingCount: null,
      servingSize: null,
      formula: null,
    });

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(text(harness.fixture)).toContain('no reconciled yield to record');
  });

  // Writing a number the recipe already holds would create a version in which nothing changed.
  it('refuses to record a yield the recipe already has', async () => {
    const harness = await reconciled({}, { savedYieldQuantity: 12 });

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(text(harness.fixture)).toContain("already the recipe's yield quantity");
  });

  it('will not record while the editor has unsaved changes, and says why', async () => {
    const harness = await reconciled({}, { editorIsDirty: true });

    expect(harness.fixture.componentInstance.canApply()).toBeFalse();
    expect(text(harness.fixture)).toContain('Save or discard your changes first');
  });

  it('raises nothing when the control is asked while blocked', async () => {
    const harness = await reconciled({}, { editorIsDirty: true });
    const raised: unknown[] = [];
    harness.fixture.componentInstance.applyRequested.subscribe((event) => raised.push(event));

    harness.fixture.componentInstance.requestApply();

    expect(raised).toEqual([]);
  });

  it('says the recipe had no yield recorded when it had none', async () => {
    const harness = await reconciled({}, { savedYieldQuantity: null });

    expect(text(harness.fixture)).toContain('not recorded');
    expect(harness.fixture.componentInstance.canApply()).toBeTrue();
  });

  // ---- Accessibility ----

  it('announces a landed card politely, without moving focus', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload()));

    // The announcement is a persistent region whose text changes, not the card itself: a live region
    // inserted already holding its content is not reliably announced.
    const region: HTMLElement = harness.fixture.nativeElement.querySelector('[role="status"][aria-live="polite"]');
    expect(region.getAttribute('aria-live')).toBe('polite');
    expect(region.textContent?.trim().length).toBeGreaterThan(0);
    expect(harness.fixture.nativeElement.querySelector('.result-card').getAttribute('role')).toBeNull();
  });

  // The pill's tone must never be the only thing carrying the conclusion (WCAG 2.2 AA, 1.4.1).
  it('states each status in words beside the pill, not only by its tone', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(99);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload({ status: 'Contradictory', batchYield: 99 })));

    const pill: HTMLElement = harness.fixture.nativeElement.querySelector('cp-status-pill');
    expect(pill.textContent).toContain("These don't agree");

    const explanation: HTMLElement = harness.fixture.nativeElement.querySelector('.result-explanation');
    expect((explanation.textContent ?? '').length).toBeGreaterThan(40);
  });

  it('presents the reconciliation as term-and-value pairs', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.batchYield.set(12);
    harness.fixture.componentInstance.servingCount.set(6);
    harness.fixture.componentInstance.servingSize.set(2);
    await reconcile(harness, (req) => req.flush(payload()));

    const terms: HTMLElement[] = Array.from(
      harness.fixture.nativeElement.querySelectorAll('.result-detail dt'),
    );
    expect(terms.map((term) => term.textContent?.trim())).toEqual([
      'Batch yield',
      'Servings',
      'Serving size',
      'Formula',
    ]);
  });
});
