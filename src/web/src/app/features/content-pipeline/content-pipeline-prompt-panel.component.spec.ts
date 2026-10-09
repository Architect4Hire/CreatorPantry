import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { AiProposalStatus, AiProposalWarning } from '../../models/ai-proposal.models';
import {
  ContentPipelineConfig,
  ContentPipelinePromptState,
  emptyContentPipelineConfig,
  emptyContentPipelinePromptState,
} from '../../models/content-pipeline.models';
import { RequestImagePromptRequest } from '../../models/image-prompt.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { ImagePromptService } from '../../services/image-prompt.service';
import { LinkedRecipe } from '../../models/creative-context.models';
import { ContentPipelinePromptPanelComponent } from './content-pipeline-prompt-panel.component';

const CHOSEN = { conceptRequestId: 'r-concept', conceptId: 'k-1', label: 'Warm morning', shotKind: 'Hero' as const };

function readyOperation(
  prompt: string,
  avoid: readonly string[] = [],
  warnings: readonly AiProposalWarning[] = [],
): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-prompt',
    status: 'Proposed',
    taskType: 'ImagePrompt',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:05Z',
    failureCategory: null,
    proposal: {
      proposalId: 'p-1',
      outputSchemaVersion: '1',
      promptTemplateId: 'img-002',
      promptTemplateVersion: '1',
      promptTemplateBodyChecksum: 'abc',
      providerName: 'provider',
      modelName: 'model',
      createdAt: '2026-10-07T12:00:05Z',
      changes: [
        {
          changeId: 'c-add',
          changeKind: 'Add',
          targetKind: 'ImagePrompt',
          targetId: 't-1',
          fieldName: null,
          beforeValue: null,
          afterValue: prompt,
          proposedPosition: 0,
          disposition: 'Pending',
        },
        ...(avoid.length > 0
          ? [
              {
                changeId: 'c-avoid',
                changeKind: 'Set' as const,
                targetKind: 'ImagePrompt' as const,
                targetId: 't-1',
                fieldName: 'avoid',
                beforeValue: null,
                afterValue: avoid.join('; '),
                proposedPosition: null,
                disposition: 'Pending' as const,
              },
            ]
          : []),
      ],
      warnings,
    },
  };
}

@Component({
  imports: [ContentPipelinePromptPanelComponent],
  template: `<cp-content-pipeline-prompt-panel
    workspaceSlug="cozy-fall"
    [config]="config()"
    [prompt]="prompt()"
    [recipe]="recipe()"
    (changed)="apply($event)"
  />`,
})
class HostComponent {
  readonly recipe = signal<LinkedRecipe | null>(null);
  readonly config = signal<ContentPipelineConfig>(emptyContentPipelineConfig());
  readonly prompt = signal<ContentPipelinePromptState>({ ...emptyContentPipelinePromptState(), chosen: CHOSEN });

  apply(next: ContentPipelinePromptState): void {
    this.prompt.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let requests: RequestImagePromptRequest[];
let watches: Subject<AiWatchOperationOutcome>[];
let requestOutcome: AiRequestOutcome;
let confirmAnswer: boolean;
let confirmCalls: number;

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
  host.prompt.set({ ...emptyContentPipelinePromptState(), chosen: CHOSEN, ...prompt });
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

const box = (): HTMLTextAreaElement => el.querySelector<HTMLTextAreaElement>('#cp-pipeline-final-prompt')!;

async function type(text: string): Promise<void> {
  const control = box();
  control.value = text;
  control.dispatchEvent(new Event('input'));
  await settle();
}

async function answerWatch(operation: AiProposalStatus, index = watches.length - 1): Promise<void> {
  watches[index].next({ status: 'found', operation });
  await settle();
}

describe('ContentPipelinePromptPanelComponent', () => {
  beforeEach(() => {
    requests = [];
    watches = [];
    confirmAnswer = true;
    confirmCalls = 0;
    requestOutcome = {
      status: 'accepted',
      operation: { ...readyOperation(''), status: 'Requested', proposal: null },
      replayed: false,
    };

    TestBed.configureTestingModule({
      providers: [
        {
          provide: ImagePromptService,
          useValue: {
            request: (_slug: string, request: RequestImagePromptRequest) => {
              requests.push(request);
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
        {
          provide: ConfirmService,
          useValue: {
            confirm: () => {
              confirmCalls += 1;
              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });
  });

  it('offers the prompt box before anything has been written, so a creator can write their own', async () => {
    await mount();

    expect(box()).not.toBeNull();
    expect(box().value).toBe('');
    expect(buttonWith('Write the prompt')).not.toBeNull();
  });

  it('asks for a prompt for the picked shot, carrying the brief and the setup lines', async () => {
    await mount(
      { brief: { documentId: 'd-brief', title: 'Autumn brief' } },
      { channelKey: 'instagram', scene: ['marble slab'], style: ['soft light'] },
    );

    await click('Write the prompt');

    expect(requests[0]).toEqual({
      conceptRequestId: 'r-concept',
      conceptId: 'k-1',
      shotKind: 'Hero',
      channelKey: 'instagram',
      recipeId: null,
      recipeVersionId: null,
      briefDocumentId: 'd-brief',
      sceneOverrides: ['marble slab'],
      styleOverrides: ['soft light'],
    });
  });

  it('names the linked recipe and its pinned version on the request', async () => {
    await mount();
    host.recipe.set({ recipeId: 'r-soda', recipeVersionId: 'v-soda-2' });
    await settle();

    await click('Write the prompt');

    expect(requests[0].recipeId).toBe('r-soda');
    expect(requests[0].recipeVersionId).toBe('v-soda-2');
  });

  it('sends neither id when no recipe is linked', async () => {
    await mount();

    await click('Write the prompt');

    expect(requests[0].recipeId).toBeNull();
    expect(requests[0].recipeVersionId).toBeNull();
  });

  it('seeds the empty box with what was written, and keeps the wording beside it', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('A three-quarter view of a sourdough loaf.', ['hands']));

    expect(box().value).toBe('A three-quarter view of a sourdough loaf.');
    expect(host.prompt().generated?.text).toBe('A three-quarter view of a sourdough loaf.');
    expect(host.prompt().generated?.avoid).toEqual(['hands']);
    expect(el.textContent).toContain('Written to avoid: hands');
    // Seeded text is the model's, which is what the screen has to say — not the creator's.
    expect(host.prompt().promptSource).toBe('composed');
  });

  it('never writes over words the creator has already put in the box', async () => {
    await mount();
    await type('My own prompt.');
    expect(host.prompt().promptSource).toBe('creator');

    await click('Write the prompt');
    await answerWatch(readyOperation('Something the model wrote.'));

    expect(box().value).toBe('My own prompt.');
    // Kept beside it, so they can see what was written and take it if they want.
    expect(host.prompt().generated?.text).toBe('Something the model wrote.');
  });

  it('asks before writing again once the creator has edited, and does nothing when the answer is no', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('First wording.'));

    await type('My own prompt.');
    confirmAnswer = false;
    await click('Write it again');

    expect(confirmCalls).toBe(1);
    expect(requests.length).toBe(1);
    expect(box().value).toBe('My own prompt.');
  });

  it('writes again when the creator says so, and still does not replace their text', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('First wording.'));
    await type('My own prompt.');

    confirmAnswer = true;
    await click('Write it again');
    await answerWatch(readyOperation('Second wording.'));

    expect(requests.length).toBe(2);
    expect(box().value).toBe('My own prompt.');
    expect(host.prompt().generated?.text).toBe('Second wording.');
  });

  it('offers the written wording, titled by what it is, and asks before replacing the edit', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('First wording.'));
    await type('My own prompt.');

    // Titled by what it holds, not as an undo: after writing again it is the newer text.
    expect(el.textContent).toContain('The prompt we wrote');
    expect(el.textContent).not.toContain('before your changes');

    confirmAnswer = false;
    await click('Use this wording instead');
    expect(box().value).toBe('My own prompt.');

    confirmAnswer = true;
    await click('Use this wording instead');
    expect(box().value).toBe('First wording.');
    expect(host.prompt().promptSource).toBe('composed');
  });

  it('does not offer the written wording while it is still what the box says', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('First wording.'));

    expect(el.textContent).not.toContain('The prompt we wrote');
  });

  it('shows a warning about the composition, including one that arrived with no prompt', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(
      readyOperation('A loaf.', [], [{ kind: 'Limitation', message: 'Your brief was too long to read.', changeId: null }]),
    );

    expect(el.textContent).toContain('Your brief was too long to read.');
  });

  it('shows a safety caution as a warning rather than as an ordinary note', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(
      readyOperation('A loaf.', [], [{ kind: 'SafetyCaution', message: 'Follow tested guidance.', changeId: null }]),
    );

    const notice = Array.from(el.querySelectorAll('cp-notice')).find((each) =>
      (each.textContent ?? '').includes('Follow tested guidance'),
    );
    expect(notice?.classList.contains('cp-notice--warning')).toBeTrue();
  });

  it('says the words in the box were written for the creator, until they change them', async () => {
    await mount();
    await click('Write the prompt');
    await answerWatch(readyOperation('A loaf.'));

    expect(el.textContent).toContain('written for you');

    await type('My own prompt.');

    expect(el.textContent).not.toContain('written for you');
  });

  it('keeps the request id so the prompt comes back after a refresh, and reads it rather than asking again', async () => {
    await mount();
    await click('Write the prompt');

    expect(host.prompt().promptRequestId).toBe('r-prompt');

    // A fresh mount with that id watches it instead of spending the allowance on a second ask.
    const kept = host.prompt();
    requests = [];
    watches = [];
    await mount(kept);

    expect(requests.length).toBe(0);
    expect(watches.length).toBe(1);
  });

  it('says the task is switched off and still lets the creator write their own prompt', async () => {
    requestOutcome = { status: 'task_not_enabled' };
    await mount();
    await click('Write the prompt');

    expect(el.textContent).toContain('switched off');
    expect(box()).not.toBeNull();

    await type('I will write it myself.');
    expect(host.prompt().finalPrompt).toBe('I will write it myself.');
  });

  it('says what a refusal was about, in the server’s own words', async () => {
    requestOutcome = { status: 'refused', code: 'ai.imagePromptConcept.not_found', message: 'That look is gone.' };
    await mount();
    await click('Write the prompt');

    expect(el.textContent).toContain('That look is gone.');
  });

  it('offers to stop checking rather than claiming to cancel, since no route cancels a generation', async () => {
    await mount();
    await click('Write the prompt');

    expect(buttonWith('Stop checking')).not.toBeNull();
    expect(buttonWith('Cancel')).toBeNull();

    await click('Stop checking');
    expect(watches[0].observed).toBeFalse();
    expect(buttonWith('Check again')).not.toBeNull();
  });

  it('says nothing can be written until a shot is picked', async () => {
    await mount({ chosen: null });

    expect(el.textContent).toContain('Pick a look and a shot');
    expect(buttonWith('Write the prompt')).toBeNull();
    // The box is still there: a creator who wants to write their own is not blocked on picking a look.
    expect(box()).not.toBeNull();
  });
});
