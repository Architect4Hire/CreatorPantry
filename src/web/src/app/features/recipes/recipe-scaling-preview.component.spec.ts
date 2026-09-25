import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import { RecipeScalingPreviewComponent } from './recipe-scaling-preview.component';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const SCALE_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/calculations/scale`;

/** The saved recipe's own lines — the only source of the "before" column. */
const SAVED_GROUPS: readonly RecipeIngredientGroup[] = [
  {
    id: 'group-1',
    title: null,
    sortOrder: 0,
    ingredients: [
      {
        id: 'line-1',
        sortOrder: 0,
        displayText: '250 g all-purpose flour',
        ingredientNameText: 'all-purpose flour',
        quantity: 250,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'Proportional',
      },
      {
        id: 'line-2',
        sortOrder: 1,
        displayText: '2 large eggs',
        ingredientNameText: 'eggs',
        quantity: 2,
        quantityUpper: null,
        measurementUnitId: null,
        ingredientId: null,
        matchStatus: 'NotAttempted',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'ReviewRequired',
      },
    ],
  },
];

function scaledLine(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'line-1',
    displayText: '250 g all-purpose flour',
    wasScaled: true,
    scaledQuantity: { numerator: '500', denominator: '1' },
    scaledQuantityUpper: null,
    scaledDisplayQuantity: 500,
    scaledDisplayQuantityUpper: null,
    warnings: [],
    ...overrides,
  };
}

function scalingPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    sourceVersionNumber: 3,
    preview: {
      factor: { numerator: '2', denominator: '1' },
      requestedTargetYieldQuantity: null,
      scaledYieldQuantity: { numerator: '24', denominator: '1' },
      scaledYieldDisplayQuantity: 24,
      lines: [scaledLine()],
      recipeWarnings: [],
      ...overrides,
    },
  };
}

interface Harness {
  readonly fixture: ComponentFixture<RecipeScalingPreviewComponent>;
  readonly http: HttpTestingController;
}

async function createFixture(inputs: Record<string, unknown> = {}): Promise<Harness> {
  await TestBed.configureTestingModule({
    imports: [RecipeScalingPreviewComponent],
    providers: [provideHttpClient(), provideHttpClientTesting()],
  }).compileComponents();

  const http = TestBed.inject(HttpTestingController);
  const runtimeConfig = TestBed.inject(RuntimeConfigService);
  const loading = runtimeConfig.load();
  http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
  await loading;

  const fixture = TestBed.createComponent(RecipeScalingPreviewComponent);
  fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
  fixture.componentRef.setInput('recipeId', RECIPE_ID);
  fixture.componentRef.setInput('sourceVersionNumber', 3);
  fixture.componentRef.setInput('savedIngredientGroups', SAVED_GROUPS);
  fixture.componentRef.setInput('recipeYieldQuantity', 12);
  fixture.componentRef.setInput('recipeYieldText', '12 servings');

  for (const [name, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(name, value);
  }

  fixture.detectChanges();
  return { fixture, http };
}

/** Runs one scale request end to end and flushes the response the test wants. */
async function scale(
  harness: Harness,
  respond: (request: ReturnType<HttpTestingController['expectOne']>) => void,
): Promise<void> {
  const call = harness.fixture.componentInstance.scale();
  respond(harness.http.expectOne(SCALE_URL));
  await call;
  harness.fixture.detectChanges();
}

function text(fixture: ComponentFixture<RecipeScalingPreviewComponent>): string {
  return (fixture.nativeElement as HTMLElement).textContent ?? '';
}

function rowCells(fixture: ComponentFixture<RecipeScalingPreviewComponent>, index: number): string[] {
  const rows = (fixture.nativeElement as HTMLElement).querySelectorAll('.scaled-lines tbody tr');
  return Array.from(rows[index].querySelectorAll('th, td')).map((cell) => (cell.textContent ?? '').trim());
}

describe('RecipeScalingPreviewComponent', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  // ---- Idle ----

  it('starts with nothing scaled and says so rather than showing an empty table', async () => {
    const { fixture } = await createFixture();

    expect(fixture.nativeElement.querySelector('.scaled-lines')).toBeNull();
    expect(text(fixture)).toContain('Choose a factor or a target yield');
  });

  // The whole panel's premise. A creator reading a table of new numbers has every reason to assume
  // something changed, so this is stated in words and not left to the heading.
  it('says that nothing is saved before any preview exists', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain('nothing here is saved');
  });

  // A scaled recipe is accepted whole or not at all: saving only the yield would leave a recipe stating a
  // yield its own ingredients do not make. Said in words rather than left as a missing button.
  it('says why a scaled preview cannot be applied yet', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    expect(text(harness.fixture)).toContain('needs ingredient saving');
    expect(text(harness.fixture)).toContain('stays a proposal');
    expect(harness.fixture.nativeElement.querySelector('.apply')).toBeNull();
  });

  // ---- Requesting ----

  it('sends the factor with the saved source version and renders the result', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);

    await scale(harness, (req) => {
      expect(req.request.body).toEqual({ sourceVersionNumber: 3, multiplier: 2 });
      req.flush(scalingPayload());
    });

    expect(text(harness.fixture)).toContain('Scaled by ×2, from version 3');
    expect(text(harness.fixture)).toContain('Yield 12 becomes 24');
  });

  it('sends a target yield instead when that mode is chosen', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.setMode('yield');
    harness.fixture.componentInstance.targetYieldValue.set(24);

    await scale(harness, (req) => {
      expect(req.request.body).toEqual({ sourceVersionNumber: 3, targetYieldQuantity: 24 });
      req.flush(scalingPayload());
    });

    expect(text(harness.fixture)).toContain('Scaled by ×2');
  });

  it('binds the factor input, so a typed value is what gets sent', async () => {
    const harness = await createFixture();
    const input: HTMLInputElement = harness.fixture.nativeElement.querySelector('input[name="factor"]');

    input.value = '1.5';
    input.dispatchEvent(new Event('input'));
    await harness.fixture.whenStable();

    // A number, not the text of one: the box is `type="number"`, so nothing downstream ever parses a string.
    expect(harness.fixture.componentInstance.factorValue()).toBe(1.5);
  });

  // The server accepts exactly one of the two, so a value left behind in the other box would be silently
  // ignored — a filled field that does nothing is a lie about what was asked for.
  it('clears the other input when the mode changes', async () => {
    const { fixture } = await createFixture();
    fixture.componentInstance.factorValue.set(2);

    fixture.componentInstance.setMode('yield');

    expect(fixture.componentInstance.factorValue()).toBeNull();
  });

  it('sets the factor from a preset without calculating anything', async () => {
    const { fixture } = await createFixture();

    const preset: HTMLButtonElement = fixture.nativeElement.querySelector(
      'button[aria-label="Set the factor to one half"]',
    );
    preset.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.factorValue()).toBe(0.5);
  });

  // An empty box, or one the browser could not read as a number, is the client's own business — it has no
  // request to send. Everything else goes to the server.
  it('refuses an empty box without calling the server', async () => {
    const { fixture, http } = await createFixture();
    expect(fixture.componentInstance.factorValue()).toBeNull();

    await fixture.componentInstance.scale();
    fixture.detectChanges();

    http.expectNone(SCALE_URL);
    expect(text(fixture)).toContain('Enter a number.');
  });

  // Zero and negative are the server's own rule, and it says so in its own words rather than in a second
  // copy of them here.
  it('sends a zero factor and shows the refusal in the server’s own words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(0);

    await scale(harness, (req) =>
      req.flush(
        {
          code: 'recipes.scaling.invalid_request',
          errors: { multiplier: ['The multiplier must be greater than zero.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(text(harness.fixture)).toContain('The multiplier must be greater than zero.');
  });

  // ---- Before and after ----

  it('takes the before column from the saved recipe, matched by line id', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    const cells = rowCells(harness.fixture, 0);
    expect(cells[0]).toContain('250 g all-purpose flour');
    expect(cells[1]).toBe('250');
    expect(cells[2]).toContain('500');
  });

  // A line the page is not showing means the preview is of content the page does not have. Borrowing a
  // number from somewhere else would hide that.
  it('shows no before amount for a line the saved recipe does not carry', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush(scalingPayload({ lines: [scaledLine({ id: 'line-unknown' })] })),
    );

    expect(rowCells(harness.fixture, 0)[1]).toContain('No saved amount');
  });

  it('keeps the exact fraction beside a rounded amount', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(0.5);
    await scale(harness, (req) =>
      req.flush(
        scalingPayload({
          lines: [
            scaledLine({
              scaledQuantity: { numerator: '1', denominator: '3' },
              scaledDisplayQuantity: 0.33,
            }),
          ],
        }),
      ),
    );

    expect(text(harness.fixture)).toContain('exact 1/3');
  });

  it('renders a scaled range as two bounds', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush(
        scalingPayload({
          lines: [
            scaledLine({
              scaledQuantity: { numerator: '4', denominator: '1' },
              scaledQuantityUpper: { numerator: '6', denominator: '1' },
              scaledDisplayQuantity: 4,
              scaledDisplayQuantityUpper: 6,
              warnings: ['ReviewRequiredRange'],
            }),
          ],
        }),
      ),
    );

    expect(rowCells(harness.fixture, 0)[2]).toContain('4 to 6');
  });

  // ---- Warnings ----

  it('marks a line that was not scaled and says why, in words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush(
        scalingPayload({
          lines: [
            scaledLine({
              id: 'line-2',
              displayText: '2 large eggs',
              wasScaled: false,
              scaledQuantity: { numerator: '2', denominator: '1' },
              scaledDisplayQuantity: 2,
              warnings: ['ReviewRequiredDiscreteCount'],
            }),
          ],
        }),
      ),
    );

    const cells = rowCells(harness.fixture, 0);
    expect(cells[2]).toContain('Kept as written');
    expect(cells[3]).toContain('Whole items — pick a sensible count yourself.');
  });

  it('shows the non-linear caution for an extreme factor', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(40);
    await scale(harness, (req) => req.flush(scalingPayload({ recipeWarnings: ['ExtremeScaleFactor'] })));

    expect(text(harness.fixture)).toContain("don't scale linearly");
  });

  it('explains a recipe with no numeric yield rather than showing a blank scaled yield', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush(
        scalingPayload({
          scaledYieldQuantity: null,
          scaledYieldDisplayQuantity: null,
          recipeWarnings: ['RecipeYieldNotStructured'],
        }),
      ),
    );

    expect(text(harness.fixture)).toContain("no numeric yield");
  });

  // A caution added to the server after this build shipped is still a caution.
  it('shows a generic note for a warning code it does not recognise', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush(scalingPayload({ lines: [scaledLine({ warnings: ['SomethingAddedLater'] })] })),
    );

    expect(text(harness.fixture)).toContain('Flagged for review — check this line.');
  });

  // ---- Yield mode availability ----

  it('offers but disables the yield mode when the recipe has no yield quantity, and says why', async () => {
    const { fixture } = await createFixture({ recipeYieldQuantity: null, recipeYieldText: null });

    const yieldRadio: HTMLInputElement = fixture.nativeElement.querySelector('input[value="yield"]');
    expect(yieldRadio.disabled).toBeTrue();
    expect(text(fixture)).toContain('can only be scaled by a factor');
  });

  it('names what the recipe currently makes as the target hint', async () => {
    const { fixture } = await createFixture();
    fixture.componentInstance.setMode('yield');
    fixture.detectChanges();

    expect(text(fixture)).toContain('This recipe makes 12 — 12 servings.');
  });

  // ---- Unsaved and stale ----

  it('says that unsaved edits are not in the preview', async () => {
    const { fixture } = await createFixture({ editorIsDirty: true });

    expect(text(fixture)).toContain("Unsaved edits aren't included");
  });

  it('always says that unsaved ingredient edits cannot be scaled', async () => {
    const { fixture } = await createFixture();

    expect(text(fixture)).toContain("Ingredient lines added or edited on the Ingredients tab aren't saved yet");
  });

  it('marks a preview out of date when the recipe moves past the version it was computed from', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    expect(harness.fixture.componentInstance.isStale()).toBeFalse();

    // A save, a restore or an unarchive while the preview is on screen.
    harness.fixture.componentRef.setInput('sourceVersionNumber', 4);
    harness.fixture.detectChanges();

    expect(harness.fixture.componentInstance.isStale()).toBeTrue();
    expect(text(harness.fixture)).toContain('These amounts are for version 3');
    // The numbers stay: they were true of the version they name.
    expect(harness.fixture.nativeElement.querySelector('.scaled-lines')).not.toBeNull();
  });

  it('refuses to scale a recipe with no saved version, and says what to do', async () => {
    const { fixture, http } = await createFixture({ sourceVersionNumber: null });

    const submit: HTMLButtonElement = fixture.nativeElement.querySelector('button[type="submit"]');
    expect(submit.disabled).toBeTrue();
    expect(text(fixture)).toContain('no saved version yet');

    fixture.componentInstance.factorValue.set(2);
    await fixture.componentInstance.scale();
    http.expectNone(SCALE_URL);
  });

  // ---- Refusals ----

  it('explains a version that no longer exists', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush({ code: 'recipes.version.not_found' }, { status: 404, statusText: 'Not Found' }),
    );

    expect(text(harness.fixture)).toContain('That version no longer exists');
  });

  it('explains an unreadable recipe without disclosing more', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) =>
      req.flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' }),
    );

    expect(text(harness.fixture)).toContain("This recipe couldn't be found");
  });

  it('offers a retry when the calculation could not be reached', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush({}, { status: 500, statusText: 'Server Error' }));

    expect(text(harness.fixture)).toContain('try again');
  });

  // A typo in the box is no reason to take away a table the creator was reading. The preview states its own
  // factor and version, so it cannot be mistaken for the answer to the attempt that just failed.
  it('keeps the previous preview when a later attempt is refused', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    harness.fixture.componentInstance.factorValue.set(0);
    await scale(harness, (req) =>
      req.flush(
        { code: 'recipes.scaling.invalid_request', errors: { multiplier: ['Greater than zero.'] } },
        { status: 400, statusText: 'Bad Request' },
      ),
    );

    expect(harness.fixture.nativeElement.querySelector('.scaled-lines')).not.toBeNull();
    expect(text(harness.fixture)).toContain('Greater than zero.');
    expect(text(harness.fixture)).toContain('from version 3');
  });

  // ---- Accessibility ----

  it('names the mode chooser with a legend rather than a bare label', async () => {
    const { fixture } = await createFixture();
    const legend: HTMLLegendElement = fixture.nativeElement.querySelector('fieldset.mode legend');

    expect(legend.textContent).toContain('How do you want to scale?');
  });

  it('gives the results table scoped headers and a caption', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    const headers: HTMLElement[] = Array.from(
      harness.fixture.nativeElement.querySelectorAll('.scaled-lines thead th'),
    );
    expect(headers.map((header) => header.getAttribute('scope'))).toEqual(['col', 'col', 'col', 'col']);
    expect(headers.map((header) => header.textContent?.trim())).toEqual([
      'Ingredient',
      'Before',
      'After',
      'Notes',
    ]);

    const rowHeader: HTMLElement = harness.fixture.nativeElement.querySelector('.scaled-lines tbody th');
    expect(rowHeader.getAttribute('scope')).toBe('row');
    expect(harness.fixture.nativeElement.querySelector('.scaled-lines caption')).not.toBeNull();
  });

  it('announces a landed preview politely, without moving focus', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    const summary: HTMLElement = harness.fixture.nativeElement.querySelector('.scaling-summary');
    expect(summary.getAttribute('role')).toBe('status');
    expect(summary.getAttribute('aria-live')).toBe('polite');
  });

  it('marks the results busy while a re-scale is in flight, keeping what is already there', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(2);
    await scale(harness, (req) => req.flush(scalingPayload()));

    harness.fixture.componentInstance.factorValue.set(3);
    const second = harness.fixture.componentInstance.scale();
    harness.fixture.detectChanges();

    const results: HTMLElement = harness.fixture.nativeElement.querySelector('.scaling-result');
    expect(results.getAttribute('aria-busy')).toBe('true');
    expect(harness.fixture.nativeElement.querySelector('.scaled-lines')).not.toBeNull();

    harness.http.expectOne(SCALE_URL).flush(scalingPayload());
    await second;
    harness.fixture.detectChanges();

    expect(
      (harness.fixture.nativeElement.querySelector('.scaling-result') as HTMLElement).getAttribute('aria-busy'),
    ).toBeNull();
  });

  // Status must not depend on recognising a colour or a symbol (WCAG 2.2 AA, 1.4.1).
  it('pairs every warning glyph with its own words', async () => {
    const harness = await createFixture();
    harness.fixture.componentInstance.factorValue.set(40);
    await scale(harness, (req) =>
      req.flush(
        scalingPayload({
          recipeWarnings: ['ExtremeScaleFactor'],
          lines: [scaledLine({ wasScaled: false, warnings: ['FixedQuantityNotScaled'] })],
        }),
      ),
    );

    const glyphs: HTMLElement[] = Array.from(harness.fixture.nativeElement.querySelectorAll('.warning-glyph'));
    expect(glyphs.length).toBeGreaterThan(0);
    for (const glyph of glyphs) {
      expect(glyph.getAttribute('aria-hidden')).toBe('true');
      expect((glyph.parentElement?.textContent ?? '').trim().length).toBeGreaterThan(1);
    }
  });

  it('gives each preset button words a screen reader can read', async () => {
    const { fixture } = await createFixture();
    const presets: HTMLButtonElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.presets button'),
    );

    expect(presets.map((button) => button.getAttribute('aria-label'))).toEqual([
      'Set the factor to one half',
      'Set the factor to two',
      'Set the factor to three',
    ]);
  });
});
