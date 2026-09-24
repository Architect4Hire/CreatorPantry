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

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select');
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
