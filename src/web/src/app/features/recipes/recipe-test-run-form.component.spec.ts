import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  CreateRecipeTestRunRequest,
  RecipeTestRun,
  TestIssueInput,
  TestObservationInput,
  UpdateRecipeTestRunRequest,
} from '../../models/recipe-test-run.models';
import { MeasurementUnit } from '../../models/reference.models';
import { ListUnitsOutcome, ReferenceService } from '../../services/reference.service';
import { CreateTestRunOutcome, RecipeTestRunService, UpdateTestRunOutcome } from '../../services/recipe-test-run.service';
import { RecipeTestRunFormComponent, TestRunSaved } from './recipe-test-run-form.component';

const UNITS: readonly MeasurementUnit[] = [
  {
    id: 'u-loaf',
    code: 'loaf',
    displayName: 'loaf',
    pluralName: 'loaves',
    abbreviation: 'loaf',
    dimension: 'Count',
    system: 'Neutral',
    baseUnitFactor: 1,
    displayPrecision: 0,
  },
  {
    id: 'u-gram',
    code: 'gram',
    displayName: 'gram',
    pluralName: 'grams',
    abbreviation: 'g',
    dimension: 'Mass',
    system: 'Metric',
    baseUnitFactor: 1,
    displayPrecision: 0,
  },
];

function createdOutcome(overrides: Partial<{ observationIds: readonly string[]; issueIds: readonly string[] }> = {}): CreateTestRunOutcome {
  return {
    status: 'created',
    replayed: false,
    testRun: {
      testRunId: 't1',
      recipeId: 'r1',
      recipeVersionId: 'v3',
      sourceVersionNumber: 3,
      outcome: 'Succeeded',
      testedAt: '2026-09-28T18:00:00Z',
      createdAt: '2026-09-28T19:00:00Z',
      concurrencyToken: 'token-1',
      observationIds: overrides.observationIds ?? [],
      issueIds: overrides.issueIds ?? [],
    },
  };
}

function savedRun(overrides: Partial<RecipeTestRun> = {}): RecipeTestRun {
  return {
    id: 't1',
    recipeId: 'r1',
    recipeVersionId: 'v3',
    testedAt: '2026-09-28T18:00:00Z',
    testedByMembershipId: 'm1',
    outcome: 'SucceededWithIssues',
    rating: 4,
    environmentNotes: 'Fan oven, humid day',
    equipmentNotes: 'Dark metal tin',
    summaryNotes: 'Good crumb',
    actualYieldText: 'Got 10, not 12',
    actualYieldQuantity: 10,
    actualYieldUnitId: 'u-loaf',
    actualPrepTimeMinutes: 20,
    actualCookTimeMinutes: 35,
    actualRestTimeMinutes: 10,
    actualTotalTimeMinutes: 55,
    createdByMembershipId: 'm1',
    updatedByMembershipId: 'm1',
    createdAt: '2026-09-28T19:00:00Z',
    updatedAt: '2026-09-28T19:00:00Z',
    concurrencyToken: 'token-1',
    observations: [
      { id: 'o1', kind: 'Texture', text: 'Dense crumb', sortOrder: 0 },
      { id: 'o2', kind: 'Timing', text: 'Needed five more minutes', sortOrder: 1 },
    ],
    issues: [
      {
        id: 'i1',
        severity: 'Major',
        title: 'Bake time too short',
        description: 'The middle was still wet at 35 minutes.',
        testObservationId: 'o2',
        sortOrder: 0,
        resolution: null,
      },
    ],
    ...overrides,
  };
}

interface CreateCall {
  readonly request: CreateRecipeTestRunRequest;
  readonly idempotencyKey: string | undefined;
}

interface UpdateCall {
  readonly testRunId: string;
  readonly request: UpdateRecipeTestRunRequest;
  readonly idempotencyKey: string | undefined;
}

class StubTestRunService {
  createOutcome: CreateTestRunOutcome = createdOutcome();
  updateOutcome: UpdateTestRunOutcome = { status: 'updated', replayed: false, testRun: savedRun() };

  createCalls: CreateCall[] = [];
  updateCalls: UpdateCall[] = [];

  /** Held open so a test can assert what the form looks like while a save is in flight. */
  holdNext = false;
  private release: (() => void) | null = null;

  createTestRun(
    _workspaceSlug: string,
    _recipeId: string,
    request: CreateRecipeTestRunRequest,
    idempotencyKey?: string,
  ): Promise<CreateTestRunOutcome> {
    this.createCalls.push({ request, idempotencyKey });
    return this.answer(() => this.createOutcome);
  }

  updateTestRun(
    _workspaceSlug: string,
    _recipeId: string,
    testRunId: string,
    request: UpdateRecipeTestRunRequest,
    idempotencyKey?: string,
  ): Promise<UpdateTestRunOutcome> {
    this.updateCalls.push({ testRunId, request, idempotencyKey });
    return this.answer(() => this.updateOutcome);
  }

  finish(): void {
    this.release?.();
    this.release = null;
  }

  private answer<T>(outcome: () => T): Promise<T> {
    if (!this.holdNext) return Promise.resolve(outcome());

    this.holdNext = false;
    return new Promise<T>((resolve) => {
      this.release = () => resolve(outcome());
    });
  }
}

class StubReferenceService {
  outcome: ListUnitsOutcome = { status: 'found', units: UNITS };

  listUnits(): Promise<ListUnitsOutcome> {
    return Promise.resolve(this.outcome);
  }
}

class StubConfirmService {
  answer = true;
  requests: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.requests.push(request);
    return Promise.resolve(this.answer);
  }
}

describe('RecipeTestRunFormComponent', () => {
  let service: StubTestRunService;
  let reference: StubReferenceService;
  let confirm: StubConfirmService;
  let fixture: ComponentFixture<RecipeTestRunFormComponent>;
  let saves: TestRunSaved[];
  let cancels: number;
  let reloadRequests: number;

  async function render(testRun: RecipeTestRun | null = null, versionNumber = 3): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeTestRunFormComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('versionNumber', versionNumber);
    fixture.componentRef.setInput('testRun', testRun);

    saves = [];
    cancels = 0;
    reloadRequests = 0;
    fixture.componentInstance.saved.subscribe((event) => saves.push(event));
    fixture.componentInstance.cancelled.subscribe(() => (cancels += 1));
    fixture.componentInstance.reloadRequested.subscribe(() => (reloadRequests += 1));

    fixture.detectChanges();
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function buttonWith(element: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.trim().includes(text));
  }

  function control<T extends HTMLElement>(element: HTMLElement, suffix: string): T {
    const id = `${fixture.componentInstance.idPrefix}-${suffix}`;
    const found = element.querySelector<T>(`[id="${id}"]`);
    if (found === null) throw new Error(`No control with id ${id}`);
    return found;
  }

  async function save(element: HTMLElement): Promise<void> {
    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    await settle();
  }

  function lastCreate(): CreateRecipeTestRunRequest {
    return service.createCalls[service.createCalls.length - 1].request;
  }

  function lastUpdate(): UpdateRecipeTestRunRequest {
    return service.updateCalls[service.updateCalls.length - 1].request;
  }

  beforeEach(() => {
    service = new StubTestRunService();
    reference = new StubReferenceService();
    confirm = new StubConfirmService();

    TestBed.configureTestingModule({
      imports: [RecipeTestRunFormComponent],
      providers: [
        { provide: RecipeTestRunService, useValue: service },
        { provide: ReferenceService, useValue: reference },
        { provide: ConfirmService, useValue: confirm },
      ],
    });
  });

  // ---- What the form promises -------------------------------------------

  it('names the version under test and says nothing here changes the recipe', async () => {
    const element = await render(null, 4);

    const text = element.textContent ?? '';
    expect(text).toContain('version 4');
    expect(text).toContain('nothing on this form changes the recipe');
  });

  /** A version is shown, never chosen: the PATCH route accepts no source version at all. */
  it('offers no control for choosing the version', async () => {
    const element = await render();

    expect(element.querySelector('[id$="-sourceVersionNumber"]')).toBeNull();
  });

  /**
   * Neither route accepts an asset. An uploader here would collect photographs that a Save silently dropped,
   * so the form has none and says where they arrive instead.
   */
  it('offers no attachment control, because the contract has no attachments', async () => {
    const element = await render();

    expect(element.querySelector('cp-uploader')).toBeNull();
    expect(element.querySelector('input[type="file"]')).toBeNull();
  });

  /** Optionality is stated once, in the legend, rather than field by field. */
  it('states in one place that only the date is required', async () => {
    const element = await render();

    expect(element.textContent).toContain('Only the date is required.');

    // The date and an issue's two required fields are the only `*`s a form with no issues can show.
    const marks = element.querySelectorAll('cp-field label span[aria-hidden="true"]');
    expect(marks.length).toBe(1);
  });

  /** Nothing sums the three parts into a total, and the form says so where somebody would expect it to. */
  it('says the total is recorded as entered rather than derived', async () => {
    const element = await render();

    expect(element.textContent).toContain('Nothing adds the three above together.');
  });

  it('builds its sections from the design system rather than its own markup', async () => {
    const element = await render();

    expect(element.querySelectorAll('cp-form-section').length).toBe(7);
    // A quantity and its unit, and the three durations: the only two rows on the form.
    expect(element.querySelectorAll('cp-field-row').length).toBe(2);
  });

  // ---- Create -----------------------------------------------------------

  it('defaults the date to now and submits it as an instant', async () => {
    const element = await render();

    expect(control<HTMLInputElement>(element, 'testedAt').value.length).toBeGreaterThan(0);

    await save(element);

    expect(service.createCalls.length).toBe(1);
    expect(lastCreate().sourceVersionNumber).toBe(3);
    expect(Number.isNaN(new Date(lastCreate().testedAt).getTime())).toBeFalse();
  });

  it('cannot be saved with no date at all', async () => {
    const element = await render();

    fixture.componentInstance.testedAt.set('');
    fixture.detectChanges();

    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeTrue();
    await save(element);
    expect(service.createCalls.length).toBe(0);
  });

  it('sends what the tester typed, trimming and nulling what they left empty', async () => {
    const element = await render();

    fixture.componentInstance.outcome.set('SucceededWithIssues');
    fixture.componentInstance.rating.set('4');
    fixture.componentInstance.environmentNotes.set('  Fan oven  ');
    fixture.componentInstance.actualYieldText.set('Got 10, not 12');
    fixture.componentInstance.actualCookTimeMinutes.set('35');
    fixture.detectChanges();

    await save(element);

    const request = lastCreate();
    expect(request.outcome).toBe('SucceededWithIssues');
    expect(request.rating).toBe(4);
    expect(request.environmentNotes).toBe('Fan oven');
    expect(request.actualYieldText).toBe('Got 10, not 12');
    expect(request.actualCookTimeMinutes).toBe(35);
    expect(request.equipmentNotes).toBeNull();
    expect(request.actualTotalTimeMinutes).toBeNull();
  });

  /** A row somebody added and never filled in is not an opinion about their notes; it is not sent. */
  it('drops an untouched note row and keeps one with only a kind chosen', async () => {
    const element = await render();

    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a note')?.click();
    fixture.detectChanges();

    const [first, second] = fixture.componentInstance.observations();
    fixture.componentInstance.updateObservation(first.key, { text: 'Dense crumb' });
    fixture.componentInstance.updateObservation(second.key, { kind: 'Timing' });
    fixture.detectChanges();

    await save(element);

    const observations = lastCreate().observations as readonly TestObservationInput[];
    expect(observations.length).toBe(2);
    expect(observations[0].text).toBe('Dense crumb');
    expect(observations[1].text).toBe('');
  });

  it('sends an unanswered severity as null so the refusal can name the row', async () => {
    const element = await render();

    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();
    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, { title: 'Crumb too dense' });
    fixture.detectChanges();

    await save(element);

    const issues = lastCreate().issues as readonly TestIssueInput[];
    expect(issues[0].severity).toBeNull();
    expect(issues[0].title).toBe('Crumb too dense');
  });

  /** Position in the submitted list is what `observationIndex` means, so it is resolved against that list. */
  it('points an issue at its note by position in the list actually submitted', async () => {
    const element = await render();

    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();

    const [blank, written] = fixture.componentInstance.observations();
    fixture.componentInstance.updateObservation(written.key, { text: 'Needed five more minutes' });
    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, {
      severity: 'Major',
      title: 'Bake time too short',
      fromObservationKey: written.key,
    });
    fixture.detectChanges();
    void blank;

    await save(element);

    const request = lastCreate();
    expect((request.observations as readonly TestObservationInput[]).length).toBe(1);
    // Index 0, not 1: the blank row was never submitted, so the written note is the first in the list.
    expect((request.issues as readonly TestIssueInput[])[0].observationIndex).toBe(0);
  });

  it('follows a note when it is reordered rather than the slot it used to sit in', async () => {
    const element = await render();

    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();

    const [first, second] = fixture.componentInstance.observations();
    fixture.componentInstance.updateObservation(first.key, { text: 'Dense crumb' });
    fixture.componentInstance.updateObservation(second.key, { text: 'Needed longer' });
    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, {
      severity: 'Minor',
      title: 'Crumb',
      fromObservationKey: first.key,
    });
    fixture.componentInstance.moveObservation(first.key, 1);
    fixture.detectChanges();

    await save(element);

    expect((lastCreate().observations as readonly TestObservationInput[])[1].text).toBe('Dense crumb');
    expect((lastCreate().issues as readonly TestIssueInput[])[0].observationIndex).toBe(1);
  });

  it('unpoints an issue whose note was removed rather than repointing it at the next one', async () => {
    const element = await render();

    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();

    const [first, second] = fixture.componentInstance.observations();
    fixture.componentInstance.updateObservation(first.key, { text: 'Dense crumb' });
    fixture.componentInstance.updateObservation(second.key, { text: 'Needed longer' });
    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, {
      severity: 'Minor',
      title: 'Crumb',
      fromObservationKey: first.key,
    });
    fixture.componentInstance.removeObservation(first.key);
    fixture.detectChanges();

    await save(element);

    expect((lastCreate().issues as readonly TestIssueInput[])[0].observationIndex).toBeNull();
  });

  it('emits the created test and switches to correcting it', async () => {
    const element = await render();
    service.createOutcome = createdOutcome();

    await save(element);

    expect(saves.length).toBe(1);
    expect(saves[0].kind).toBe('created');
    expect(fixture.componentInstance.testRunId()).toBe('t1');
    expect(element.textContent).toContain('Correct this test');
  });

  /**
   * The ids come back in submitted order, and adopting them is what makes the next save an edit of the same
   * rows rather than a second copy of them.
   */
  it('adopts the row ids a create returned, so a second save edits in place', async () => {
    const element = await render();
    service.createOutcome = createdOutcome({ observationIds: ['o9'], issueIds: ['i9'] });

    buttonWith(element, 'Add a note')?.click();
    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();
    fixture.componentInstance.updateObservation(fixture.componentInstance.observations()[0].key, { text: 'Dense' });
    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, {
      severity: 'Minor',
      title: 'Crumb',
    });
    fixture.detectChanges();

    await save(element);

    expect(fixture.componentInstance.observations()[0].id).toBe('o9');
    expect(fixture.componentInstance.issues()[0].id).toBe('i9');

    // Correcting the note is what puts the adopted ids on the wire, and the id is what makes this an edit of
    // that note rather than a second note beside it.
    fixture.componentInstance.updateObservation(fixture.componentInstance.observations()[0].key, {
      text: 'Dense crumb, not just dense',
    });
    fixture.detectChanges();
    await save(element);

    const observations = lastUpdate().observations;
    const issues = lastUpdate().issues;
    expect(observations.submitted && observations.value[0].id).toBe('o9');
    expect(issues.submitted && issues.value[0].id).toBe('i9');
  });

  /** A save that changed no note and no issue leaves both lists alone rather than resubmitting them. */
  it('leaves both lists absent when only a scalar field changed', async () => {
    const element = await render(savedRun());

    fixture.componentInstance.summaryNotes.set('Second pass');
    fixture.detectChanges();
    await save(element);

    expect(lastUpdate().observations.submitted).toBeFalse();
    expect(lastUpdate().issues.submitted).toBeFalse();
  });

  // ---- Edit -------------------------------------------------------------

  it('seeds every field, note and issue from the test it was given', async () => {
    const element = await render(savedRun());

    expect(control<HTMLSelectElement>(element, 'outcome').value).toBe('SucceededWithIssues');
    expect(control<HTMLSelectElement>(element, 'rating').value).toBe('4');
    expect(control<HTMLTextAreaElement>(element, 'environmentNotes').value).toBe('Fan oven, humid day');
    expect(control<HTMLInputElement>(element, 'actualYieldText').value).toBe('Got 10, not 12');
    expect(control<HTMLInputElement>(element, 'actualTotalTimeMinutes').value).toBe('55');
    expect(fixture.componentInstance.observations().length).toBe(2);
    expect(fixture.componentInstance.issues()[0].title).toBe('Bake time too short');
  });

  it('re-links a seeded issue to the note it came from', async () => {
    await render(savedRun());

    const secondNote = fixture.componentInstance.observations()[1];
    expect(fixture.componentInstance.issues()[0].fromObservationKey).toBe(secondNote.key);
  });

  it('names the seeded unit in the picker once the catalogue is readable', async () => {
    const element = await render(savedRun());

    expect(control<HTMLInputElement>(element, 'actualYieldUnitId').value).toBe('loaf');
  });

  /** A patch sends what changed and nothing else: an untouched field left in would be a value nobody chose. */
  it('sends only the fields that changed, with the token', async () => {
    const element = await render(savedRun());

    fixture.componentInstance.summaryNotes.set('Better on the second bake');
    fixture.detectChanges();

    await save(element);

    const request = lastUpdate();
    expect(request.expectedConcurrencyToken).toBe('token-1');
    expect(request.summaryNotes).toEqual({ submitted: true, value: 'Better on the second bake' });
    expect(request.environmentNotes.submitted).toBeFalse();
    expect(request.rating.submitted).toBeFalse();
    expect(request.observations.submitted).toBeFalse();
  });

  it('clears a field the tester emptied rather than leaving it alone', async () => {
    const element = await render(savedRun());

    fixture.componentInstance.rating.set('');
    fixture.componentInstance.equipmentNotes.set('');
    fixture.detectChanges();

    await save(element);

    expect(lastUpdate().rating).toEqual({ submitted: true, value: null });
    expect(lastUpdate().equipmentNotes).toEqual({ submitted: true, value: null });
  });

  /**
   * An issue may only name a note by position when the notes travel too, and sending the notes without the
   * issues would repoint every stored link against a list the issues were not measured against.
   */
  it('sends both lists together when either changed', async () => {
    const element = await render(savedRun());

    fixture.componentInstance.updateIssue(fixture.componentInstance.issues()[0].key, { title: 'Needs longer' });
    fixture.detectChanges();

    await save(element);

    expect(lastUpdate().observations.submitted).toBeTrue();
    expect(lastUpdate().issues.submitted).toBeTrue();
  });

  it('never sends a source version on an edit', async () => {
    const element = await render(savedRun());

    fixture.componentInstance.summaryNotes.set('Changed');
    fixture.detectChanges();
    await save(element);

    expect('sourceVersionNumber' in lastUpdate()).toBeFalse();
  });

  it('will not save an edit that changed nothing', async () => {
    const element = await render(savedRun());

    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeTrue();
    await save(element);
    expect(service.updateCalls.length).toBe(0);
  });

  it('reports unsaved changes while an edit is pending', async () => {
    const element = await render(savedRun());

    expect(element.textContent).not.toContain('Unsaved changes.');

    fixture.componentInstance.summaryNotes.set('Changed');
    fixture.detectChanges();

    expect(element.textContent).toContain('Unsaved changes.');
  });

  it('rebases on the test the server returned, so the next edit quotes the new token', async () => {
    const element = await render(savedRun());
    service.updateOutcome = {
      status: 'updated',
      replayed: false,
      testRun: savedRun({ concurrencyToken: 'token-2', summaryNotes: 'Better on the second bake' }),
    };

    fixture.componentInstance.summaryNotes.set('Better on the second bake');
    fixture.detectChanges();
    await save(element);

    expect(fixture.componentInstance.isDirty()).toBeFalse();

    fixture.componentInstance.rating.set('5');
    fixture.detectChanges();
    await save(element);

    expect(lastUpdate().expectedConcurrencyToken).toBe('token-2');
  });

  // ---- Resolved issues --------------------------------------------------

  it('marks a resolved issue and offers no way to remove it', async () => {
    const resolved = savedRun({
      issues: [
        {
          id: 'i1',
          severity: 'Major',
          title: 'Bake time too short',
          description: null,
          testObservationId: null,
          sortOrder: 0,
          resolution: {
            id: 'x1',
            kind: 'Fixed',
            notes: 'Added five minutes',
            resolvedByMembershipId: 'm2',
            resolvedAt: '2026-09-29T09:00:00Z',
            resolutionRecipeVersionId: 'v4',
            predatingVersionOverrideReason: null,
          },
        },
      ],
    });

    const element = await render(resolved);

    // Named in words, not only by the pill's colour.
    expect(element.querySelector('cp-status-pill')?.textContent).toContain('Resolved');
    expect(element.textContent).toContain('It stays on the test');

    // By aria-label, not by the word "Remove": the notes above have their own Remove buttons, and a looser
    // match would pass here while the issue's control was still on the page.
    expect(element.querySelector('[aria-label^="Remove issue"]')).toBeNull();
    expect(element.querySelector('[aria-label^="Remove note"]')).not.toBeNull();
  });

  it('refuses to remove a resolved issue even when asked directly', async () => {
    const element = await render(
      savedRun({
        issues: [
          {
            id: 'i1',
            severity: 'Minor',
            title: 'Slightly dry',
            description: null,
            testObservationId: null,
            sortOrder: 0,
            resolution: {
              id: 'x1',
              kind: 'WontFix',
              notes: null,
              resolvedByMembershipId: 'm2',
              resolvedAt: '2026-09-29T09:00:00Z',
              resolutionRecipeVersionId: null,
              predatingVersionOverrideReason: null,
            },
          },
        ],
      }),
    );
    void element;

    fixture.componentInstance.removeIssue(fixture.componentInstance.issues()[0].key);

    expect(fixture.componentInstance.issues().length).toBe(1);
  });

  // ---- Refusals ---------------------------------------------------------

  it('places a refused field beside the field and leaves what was typed alone', async () => {
    const element = await render();
    service.createOutcome = { status: 'validation_failed', fieldErrors: { rating: ['A rating is between 1 and 5.'] } };

    fixture.componentInstance.rating.set('4');
    fixture.detectChanges();
    await save(element);

    expect(element.textContent).toContain('A rating is between 1 and 5.');
    expect(control<HTMLSelectElement>(element, 'rating').value).toBe('4');
    expect(control<HTMLSelectElement>(element, 'rating').getAttribute('aria-invalid')).toBe('true');
  });

  /** An indexed key names a position in the submitted list, which is resolved back to the row that was there. */
  it('places an indexed refusal on the row that was submitted at that position', async () => {
    const element = await render();
    service.createOutcome = {
      status: 'validation_failed',
      fieldErrors: { 'issues[0].severity': ['Say how badly this affects the recipe.'] },
    };

    buttonWith(element, 'Add a problem')?.click();
    fixture.detectChanges();
    const issueKey = fixture.componentInstance.issues()[0].key;
    fixture.componentInstance.updateIssue(issueKey, { title: 'Crumb too dense' });
    fixture.detectChanges();

    await save(element);

    expect(fixture.componentInstance.rowFieldError('issues', issueKey, 'severity')).toBe(
      'Say how badly this affects the recipe.',
    );
    expect(control<HTMLSelectElement>(element, `issues-severity-${issueKey}`).getAttribute('aria-invalid')).toBe('true');
  });

  /** A position naming a row that has since gone is dropped rather than blamed on whatever moved into it. */
  it('drops an indexed refusal whose row no longer exists', async () => {
    const element = await render();
    service.createOutcome = {
      status: 'validation_failed',
      fieldErrors: { 'observations[0].text': ['An observation cannot be blank.'] },
    };

    buttonWith(element, 'Add a note')?.click();
    fixture.detectChanges();
    const noteKey = fixture.componentInstance.observations()[0].key;
    fixture.componentInstance.updateObservation(noteKey, { kind: 'Texture' });
    fixture.detectChanges();
    await save(element);

    expect(fixture.componentInstance.rowFieldError('observations', noteKey, 'text')).toContain('cannot be blank');

    fixture.componentInstance.removeObservation(noteKey);
    fixture.detectChanges();

    expect(element.textContent).not.toContain('An observation cannot be blank.');
  });

  it('shows a list-level refusal as the section problem', async () => {
    const element = await render();
    service.createOutcome = {
      status: 'validation_failed',
      fieldErrors: { observations: ['A test can carry at most 100 observations.'] },
    };

    await save(element);

    expect(element.textContent).toContain('A test can carry at most 100 observations.');
  });

  it('moves focus to the first refused field', async () => {
    const element = await render();
    service.createOutcome = {
      status: 'validation_failed',
      fieldErrors: { summaryNotes: ['Too long.'], rating: ['A rating is between 1 and 5.'] },
    };

    await save(element);

    // Rating comes before the summary in the form, and that order is what decides, not the order of the keys.
    expect(document.activeElement).toBe(control<HTMLSelectElement>(element, 'rating'));
  });

  it('focuses the status line when the refusal named nothing the form renders', async () => {
    const element = await render();
    service.createOutcome = { status: 'validation_failed', fieldErrors: { somethingElse: ['No.'] } };

    await save(element);

    expect(document.activeElement).toBe(control<HTMLParagraphElement>(element, 'status'));
  });

  it('offers a reload and no retry when somebody else saved first', async () => {
    const element = await render(savedRun());
    service.updateOutcome = { status: 'conflict' };

    fixture.componentInstance.summaryNotes.set('Changed');
    fixture.detectChanges();
    await save(element);

    expect(element.textContent).toContain('Nothing you typed has been lost');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeTrue();

    buttonWith(element, 'Reload this test')?.click();
    expect(reloadRequests).toBe(1);
  });

  /** Putting the issue back is the remedy, so retrying stays available — unlike a stale token. */
  it('keeps saving available when a resolved issue was dropped', async () => {
    const element = await render(savedRun());
    service.updateOutcome = { status: 'issue_removal_conflict' };

    fixture.componentInstance.summaryNotes.set('Changed');
    fixture.detectChanges();
    await save(element);

    expect(element.textContent).toContain('cannot be removed');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('says to unarchive rather than to retry when the recipe is archived', async () => {
    const element = await render();
    service.createOutcome = { status: 'archived_conflict' };

    await save(element);

    expect(element.textContent).toContain('This recipe is archived.');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('names the missing version when the history moved on', async () => {
    const element = await render(null, 7);
    service.createOutcome = {
      status: 'version_not_found',
      fieldErrors: { sourceVersionNumber: ['No such version.'] },
    };

    await save(element);

    expect(element.textContent).toContain('no longer has a version 7');
    expect(buttonWith(element, 'Reload this test')).toBeDefined();
  });

  it('says which role is needed when the workspace refuses the write', async () => {
    const element = await render();
    service.createOutcome = { status: 'forbidden' };

    await save(element);

    expect(element.textContent).toContain('Contributor role');
  });

  it('says nothing was written when the save could not be sent', async () => {
    const element = await render();
    service.createOutcome = { status: 'unavailable' };

    await save(element);

    expect(element.textContent).toContain('Nothing has been written');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeFalse();
  });

  // ---- Idempotency ------------------------------------------------------

  it('reuses one key across retries of the identical save', async () => {
    const element = await render();
    service.createOutcome = { status: 'unavailable' };

    await save(element);
    await save(element);

    expect(service.createCalls.length).toBe(2);
    expect(service.createCalls[0].idempotencyKey).toBe(service.createCalls[1].idempotencyKey);
  });

  it('takes a fresh key once the write-up has changed', async () => {
    const element = await render();
    service.createOutcome = { status: 'unavailable' };

    await save(element);
    fixture.componentInstance.summaryNotes.set('Different');
    fixture.detectChanges();
    await save(element);

    expect(service.createCalls[0].idempotencyKey).not.toBe(service.createCalls[1].idempotencyKey);
  });

  /** The key is bound to a different body server-side, so retrying with it could only be refused forever. */
  it('takes a fresh key after the server reports one reused', async () => {
    const element = await render();
    service.createOutcome = { status: 'idempotency_key_conflict' };

    await save(element);
    service.createOutcome = { status: 'unavailable' };
    await save(element);

    expect(service.createCalls[0].idempotencyKey).not.toBe(service.createCalls[1].idempotencyKey);
  });

  // ---- In flight --------------------------------------------------------

  it('says it is saving and refuses a second press while one is in flight', async () => {
    const element = await render();
    service.holdNext = true;

    (element.querySelector('button[type="submit"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const submit = element.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(submit.textContent).toContain('Saving…');
    expect(submit.disabled).toBeTrue();
    expect(submit.getAttribute('aria-busy')).toBe('true');

    submit.click();
    service.finish();
    await settle();

    expect(service.createCalls.length).toBe(1);
  });

  // ---- Leaving ----------------------------------------------------------

  it('asks before discarding a write-up, and stays when the answer is no', async () => {
    const element = await render();
    confirm.answer = false;

    fixture.componentInstance.summaryNotes.set('Half written');
    fixture.detectChanges();

    buttonWith(element, 'Cancel')?.click();
    await settle();

    expect(confirm.requests[0].tone).toBe('danger');
    expect(cancels).toBe(0);
  });

  it('leaves without asking when nothing has been typed', async () => {
    const element = await render();

    buttonWith(element, 'Cancel')?.click();
    await settle();

    expect(confirm.requests.length).toBe(0);
    expect(cancels).toBe(1);
  });

  it('leaves once the discard is confirmed', async () => {
    const element = await render();
    confirm.answer = true;

    fixture.componentInstance.summaryNotes.set('Half written');
    fixture.detectChanges();
    buttonWith(element, 'Cancel')?.click();
    await settle();

    expect(cancels).toBe(1);
  });

  // ---- Unit catalogue ---------------------------------------------------

  it('says the unit list is unreadable and leaves the rest of the form usable', async () => {
    reference.outcome = { status: 'unavailable' };
    const element = await render();

    expect(element.textContent).toContain('The unit list could not be read.');
    expect((element.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('offers a Clear only once a unit has been chosen', async () => {
    const element = await render();

    expect(buttonWith(element, 'Clear')).toBeUndefined();

    fixture.componentInstance.onYieldUnitPicked({ id: 'u-loaf', label: 'loaf' });
    fixture.detectChanges();

    expect(buttonWith(element, 'Clear')).toBeDefined();

    buttonWith(element, 'Clear')?.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.actualYieldUnitId()).toBeNull();
  });

  // ---- Accessibility ----------------------------------------------------

  it('gives every control an accessible name through its field', async () => {
    const element = await render(savedRun());

    for (const field of Array.from(element.querySelectorAll('cp-field'))) {
      const label = field.querySelector('label');
      expect(label?.getAttribute('for')).toBeTruthy();
      expect(element.querySelector(`[id="${label?.getAttribute('for')}"]`)).not.toBeNull();
    }
  });

  it('names each row control by its position rather than by an anonymous icon', async () => {
    const element = await render(savedRun());

    const labels = Array.from(element.querySelectorAll('button[aria-label]')).map((button) =>
      button.getAttribute('aria-label'),
    );

    expect(labels).toContain('Move note 1 later');
    expect(labels).toContain('Remove note 2');
    expect(labels).toContain('Remove issue 1');
  });

  it('cannot move the first row earlier or the last row later', async () => {
    const element = await render(savedRun());

    const up = element.querySelector<HTMLButtonElement>('[aria-label="Move note 1 earlier"]');
    const down = element.querySelector<HTMLButtonElement>('[aria-label="Move note 2 later"]');

    expect(up?.disabled).toBeTrue();
    expect(down?.disabled).toBeTrue();
  });

  it('describes an empty list rather than showing an unexplained blank', async () => {
    const element = await render();

    expect(element.textContent).toContain('No notes yet.');
    expect(element.textContent).toContain('No problems recorded.');
  });

  /** A test with no issues is not a success, and the form does not let the empty list imply one. */
  it('does not let an empty issue list read as a verdict', async () => {
    const element = await render();

    expect(element.textContent).toContain('A test with none is not thereby a success');
  });
});
