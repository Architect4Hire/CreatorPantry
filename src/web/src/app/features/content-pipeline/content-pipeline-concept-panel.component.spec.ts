import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { AiProposalDetail, AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import {
  ContentPipelineConfig,
  ContentPipelinePromptState,
  emptyContentPipelineConfig,
  emptyContentPipelinePromptState,
} from '../../models/content-pipeline.models';
import { RequestPhotographyConceptsRequest } from '../../models/photography-concept.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { ContentPipelineConceptPanelComponent } from './content-pipeline-concept-panel.component';

function row(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'PhotographyConcept',
    targetId: 't-1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposal(changes: readonly AiProposedChange[], warnings: AiProposalDetail['warnings'] = []): AiProposalDetail {
  return {
    proposalId: 'p-1',
    outputSchemaVersion: '1',
    promptTemplateId: 'img-001',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-07T12:00:05Z',
    changes,
    warnings,
  };
}

function operation(detail: AiProposalDetail | null, status: AiProposalStatus['status'] = 'Proposed'): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-concept',
    status,
    taskType: 'PhotographyConcept',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:05Z',
    failureCategory: null,
    proposal: detail,
  };
}

const TWO_LOOKS = proposal([
  row({ changeId: 'a1', changeKind: 'Add', targetId: 't-1', afterValue: 'Warm morning', proposedPosition: 0 }),
  row({ targetId: 't-1', fieldName: 'mood', afterValue: 'Unhurried' }),
  row({ targetId: 't-1', fieldName: 'shot.Hero.framing', afterValue: 'Three-quarter' }),
  row({ targetId: 't-1', fieldName: 'shot.DetailShot.framing', afterValue: 'Macro on the crumb' }),
  row({ changeId: 'a2', changeKind: 'Add', targetId: 't-2', afterValue: 'Blue hour', proposedPosition: 1 }),
  row({ targetId: 't-2', fieldName: 'shot.Hero.framing', afterValue: 'Overhead' }),
]);

@Component({
  imports: [ContentPipelineConceptPanelComponent],
  template: `<cp-content-pipeline-concept-panel
    workspaceSlug="cozy-fall"
    [config]="config()"
    [prompt]="prompt()"
    [idea]="idea()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly config = signal<ContentPipelineConfig>(emptyContentPipelineConfig());
  readonly prompt = signal<ContentPipelinePromptState>(emptyContentPipelinePromptState());
  readonly idea = signal<string | null>('Develop a Thai main course using the stir-fry method.');
  readonly announcements: string[] = [];

  apply(next: ContentPipelinePromptState): void {
    this.prompt.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let requests: RequestPhotographyConceptsRequest[];
let keys: string[];
let watches: Subject<AiWatchOperationOutcome>[];
let requestOutcome: AiRequestOutcome;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(
  prompt?: Partial<ContentPipelinePromptState>,
  config?: Partial<ContentPipelineConfig>,
): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.prompt.set({ ...emptyContentPipelinePromptState(), ...prompt });
  host.config.set({ ...emptyContentPipelineConfig(), ...config });
  el = fixture.nativeElement;
  await settle();
}

function buttonWith(text: string): HTMLButtonElement | null {
  return (
    (Array.from(el.querySelectorAll('button')).find((button) =>
      (button.textContent ?? '').trim().startsWith(text),
    ) as HTMLButtonElement | undefined) ?? null
  );
}

async function click(text: string): Promise<void> {
  buttonWith(text)!.click();
  await settle();
}

async function answerWatch(detail: AiProposalDetail | null, status: AiProposalStatus['status'] = 'Proposed'): Promise<void> {
  watches[watches.length - 1].next({ status: 'found', operation: operation(detail, status) });
  await settle();
}

const options = (): readonly HTMLInputElement[] =>
  Array.from(el.querySelectorAll<HTMLInputElement>('cp-choice-group input'));

describe('ContentPipelineConceptPanelComponent', () => {
  beforeEach(() => {
    requests = [];
    keys = [];
    watches = [];
    requestOutcome = { status: 'accepted', operation: operation(null, 'Requested'), replayed: false };

    TestBed.configureTestingModule({
      providers: [
        {
          provide: PhotographyConceptService,
          useValue: {
            request: (_slug: string, request: RequestPhotographyConceptsRequest, key: string) => {
              requests.push(request);
              keys.push(key);
              return Promise.resolve(requestOutcome);
            },
            watch: (): Observable<AiWatchOperationOutcome> => {
              const subject = new Subject<AiWatchOperationOutcome>();
              watches.push(subject);
              return subject.asObservable();
            },
          },
        },
        {
          provide: AiUsageService,
          useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }), ensureLoaded: () => Promise.resolve() },
        },
      ],
    });
  });

  it('asks for nothing until the creator does', async () => {
    await mount();

    expect(requests.length).toBe(0);
    expect(buttonWith('Plan some looks')).not.toBeNull();
  });

  it('asks with the channel, scene, style and wording from the step before', async () => {
    await mount(undefined, {
      channelKey: 'instagram',
      concept: 'A tight crop.',
      scene: ['marble slab'],
      style: ['soft light'],
    });

    await click('Plan some looks');

    expect(requests[0]).toEqual({
      channelKey: 'instagram',
      creatorConcept: 'A tight crop.',
      sceneOverrides: ['marble slab'],
      styleOverrides: ['soft light'],
    });
    expect(host.prompt().conceptRequestId).toBe('r-concept');
  });

  it('plans around the idea the creator picked when they wrote no description of their own', async () => {
    await mount();

    await click('Plan some looks');

    expect(requests[0].creatorConcept).toBe('Develop a Thai main course using the stir-fry method.');
  });

  it('keeps the description the creator wrote over the idea, because those are their words', async () => {
    await mount(undefined, { concept: 'A tight crop.' });

    await click('Plan some looks');

    expect(requests[0].creatorConcept).toBe('A tight crop.');
  });

  it('asks for nothing, and says why, when there is no subject to plan around', async () => {
    await mount(undefined, { channelKey: 'instagram' });
    host.idea.set(null);
    await settle();

    expect(el.textContent).toContain('nothing to plan a look around yet');
    expect(buttonWith('Plan some looks')!.disabled).toBeTrue();

    buttonWith('Plan some looks')!.click();
    await settle();
    expect(requests.length).toBe(0);

    // A scene line is enough to name something, so the same panel then asks.
    host.config.set({ ...host.config(), scene: ['a pan of lasagne on a wooden table'] });
    await settle();
    expect(el.textContent).not.toContain('nothing to plan a look around yet');
    await click('Plan some looks');
    expect(requests.length).toBe(1);
  });

  it('shows each look and offers every shot of every look as one set of choices', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);

    expect(el.textContent).toContain('Warm morning');
    expect(el.textContent).toContain('Unhurried');
    expect(el.textContent).toContain('Blue hour');

    // One group, three shots across two looks — so exactly one answer is possible.
    expect(el.querySelectorAll('cp-choice-group').length).toBe(1);
    expect(options().length).toBe(3);
    expect(options().every((input) => input.type === 'radio')).toBeTrue();
  });

  it('records the look and the shot together, because one without the other is refused', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);

    options()[1].click();
    await settle();

    expect(host.prompt().chosen).toEqual({
      conceptRequestId: 'r-concept',
      conceptId: 't-1',
      label: 'Warm morning',
      shotKind: 'DetailShot',
    });
    expect(host.announcements.some((line) => line.includes('Warm morning'))).toBeTrue();
  });

  it('drops a pick when different looks are asked for, since a look only resolves against its own request', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);
    options()[0].click();
    await settle();
    expect(host.prompt().chosen).not.toBeNull();

    await click('Plan different looks');

    expect(host.prompt().chosen).toBeNull();
  });

  it('shows every shot in full, so styling and props are seen before a pick', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(
      proposal([
        row({ changeId: 'a1', changeKind: 'Add', targetId: 't-1', afterValue: 'Warm morning', proposedPosition: 0 }),
        row({ targetId: 't-1', fieldName: 'shot.Hero.framing', afterValue: 'Three-quarter' }),
        row({ targetId: 't-1', fieldName: 'shot.Hero.styling', afterValue: 'One slice cut away, crumb showing' }),
        row({ targetId: 't-1', fieldName: 'shot.Hero.surface', afterValue: 'Scrubbed oak' }),
        row({ targetId: 't-1', fieldName: 'shot.Hero.props', afterValue: 'linen; bread knife' }),
      ]),
    );

    // Styling is where a garnish nobody wrote down would appear, so a creator has to see it before choosing.
    expect(el.textContent).toContain('One slice cut away, crumb showing');
    expect(el.textContent).toContain('Scrubbed oak');
    expect(el.textContent).toContain('linen, bread knife');
  });

  it('clears a prompt composed for the old shot when a different one is picked', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);
    options()[0].click();
    await settle();

    // As if a prompt had been written for that first shot.
    host.prompt.set({
      ...host.prompt(),
      promptRequestId: 'r-prompt',
      generated: { text: 'For the first shot.', avoid: [] },
      finalPrompt: 'For the first shot.',
      promptSource: 'composed',
    });
    await settle();

    options()[1].click();
    await settle();

    expect(host.prompt().promptRequestId).toBeNull();
    expect(host.prompt().generated).toBeNull();
    expect(host.prompt().finalPrompt).toBe('');
    expect(host.prompt().promptSource).toBe('none');
  });

  it("keeps a prompt the creator wrote when a different shot is picked, and says nothing was theirs to lose", async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);
    options()[0].click();
    await settle();

    host.prompt.set({ ...host.prompt(), finalPrompt: 'My own words.', promptSource: 'creator' });
    await settle();

    options()[1].click();
    await settle();

    // Their words follow them; only the composition, which was for another shot, is dropped.
    expect(host.prompt().finalPrompt).toBe('My own words.');
    expect(host.prompt().promptSource).toBe('creator');
    expect(host.prompt().generated).toBeNull();
  });

  it('picking the same shot again changes nothing', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);
    options()[0].click();
    await settle();

    host.prompt.set({ ...host.prompt(), finalPrompt: 'Written.', promptSource: 'composed' });
    await settle();

    options()[0].click();
    await settle();

    expect(host.prompt().finalPrompt).toBe('Written.');
  });

  it('reads the looks back after a refresh rather than asking for a second set', async () => {
    await mount({ conceptRequestId: 'r-kept' });

    expect(requests.length).toBe(0);
    expect(watches.length).toBe(1);
  });

  it('shows a safety caution as a warning, and an ordinary note as a note', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(
      proposal(TWO_LOOKS.changes, [
        { kind: 'SafetyCaution', message: 'Follow tested guidance for this method.', changeId: null },
        { kind: 'Assumption', message: 'Assumed a round loaf.', changeId: 'a1' },
      ]),
    );

    const notices = Array.from(el.querySelectorAll('cp-notice'));
    const safety = notices.find((notice) => (notice.textContent ?? '').includes('Follow tested guidance'));
    const assumption = notices.find((notice) => (notice.textContent ?? '').includes('Assumed a round loaf'));

    // The tone is a host class on cp-notice, which is also what carries its glyph: status is never a hue alone.
    expect(safety?.classList.contains('cp-notice--warning')).toBeTrue();
    expect(assumption).toBeTruthy();
    expect(assumption?.classList.contains('cp-notice--warning')).toBeFalse();
  });

  it('says so when an answer came back with no shots to choose from', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(
      proposal([row({ changeId: 'a1', changeKind: 'Add', targetId: 't-1', afterValue: 'A look', proposedPosition: 0 })]),
    );

    expect(el.textContent).toContain('No shots came back');
    expect(el.querySelectorAll('cp-choice-group').length).toBe(0);
  });

  it('offers to stop checking rather than to cancel, and to check again once it has stopped', async () => {
    await mount();
    await click('Plan some looks');

    expect(buttonWith('Cancel')).toBeNull();
    await click('Stop checking');

    expect(watches[0].observed).toBeFalse();
    expect(buttonWith('Check again')).not.toBeNull();
  });

  it('retries a failed ask under the same key, so a lost answer is replayed rather than bought twice', async () => {
    requestOutcome = { status: 'unavailable' };
    await mount();
    await click('Plan some looks');
    await click('Try again');

    expect(keys.length).toBe(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('takes a fresh key for a deliberately different question', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);

    await click('Plan different looks');

    expect(keys.length).toBe(2);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('takes a fresh key once the server has accepted one', async () => {
    await mount();
    await click('Plan some looks');
    await answerWatch(TWO_LOOKS);
    await click('Plan different looks');
    await answerWatch(TWO_LOOKS);

    expect(new Set(keys).size).toBe(keys.length);
  });

  it('offers a fresh ask when the key it used had already been spent on something else', async () => {
    requestOutcome = { status: 'idempotency_key_conflict' };
    await mount();
    await click('Plan some looks');

    expect(el.textContent).toContain('details that have since changed');

    requestOutcome = { status: 'accepted', operation: operation(null, 'Requested'), replayed: false };
    await click('Ask again');

    expect(keys.length).toBe(2);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('says a switched-off task is switched off, not broken', async () => {
    requestOutcome = { status: 'task_not_enabled' };
    await mount();
    await click('Plan some looks');

    expect(el.textContent).toContain('switched off');
  });

  it('keeps the setup intact when the allowance refuses the ask', async () => {
    requestOutcome = { status: 'account_suspended', unit: 'Credits' };
    await mount(undefined, { concept: 'A tight crop.' });
    await click('Plan some looks');

    expect(el.textContent).toContain('still here');
    expect(host.prompt().conceptRequestId).toBeNull();
  });
});
