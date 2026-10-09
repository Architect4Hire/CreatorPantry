import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { AiProposalDetail, AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import { ContentPipelinePromptState, emptyContentPipelinePromptState } from '../../models/content-pipeline.models';
import { RequestReferenceImageRequest } from '../../models/reference-image.models';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { GeneratedImageService } from '../../services/generated-image.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { ContentPipelineReferencePanelComponent } from './content-pipeline-reference-panel.component';

const REFERENCE = { documentId: 'd-ref', title: 'A loaf I like' };

function row(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'ReferenceImageAnalysis',
    targetId: 't-1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposal(changes: readonly AiProposedChange[]): AiProposalDetail {
  return {
    proposalId: 'p-1',
    outputSchemaVersion: '1',
    promptTemplateId: 'img-004',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-07T12:00:05Z',
    changes,
    warnings: [],
  };
}

function operation(detail: AiProposalDetail | null, status: AiProposalStatus['status'] = 'Proposed'): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-ref',
    status,
    taskType: 'ReferenceImageAnalysis',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:05Z',
    failureCategory: null,
    proposal: detail,
  };
}

const READING = proposal([
  row({ changeKind: 'Add', afterValue: 'A loaf on scrubbed oak in side light.', proposedPosition: 0 }),
  row({ fieldName: 'observation.Lighting', afterValue: 'Hard side light from the left' }),
  row({ fieldName: 'observation.Lighting.confidence', afterValue: 'Clear' }),
  row({ fieldName: 'observation.Mood', afterValue: 'Calm' }),
  row({ fieldName: 'observation.Mood.confidence', afterValue: 'Unclear' }),
]);

@Component({
  imports: [ContentPipelineReferencePanelComponent],
  template: `<cp-content-pipeline-reference-panel
    workspaceSlug="cozy-fall"
    [prompt]="prompt()"
    (changed)="apply($event)"
  />`,
})
class HostComponent {
  readonly prompt = signal<ContentPipelinePromptState>(emptyContentPipelinePromptState());

  apply(next: ContentPipelinePromptState): void {
    this.prompt.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let requests: RequestReferenceImageRequest[];
let watches: Subject<AiWatchOperationOutcome>[];
let requestOutcome: AiRequestOutcome;
let confirmAnswer: boolean;
let confirmCalls: number;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(prompt?: Partial<ContentPipelinePromptState>): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.prompt.set({ ...emptyContentPipelinePromptState(), ...prompt });
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

describe('ContentPipelineReferencePanelComponent', () => {
  beforeEach(() => {
    requests = [];
    watches = [];
    confirmAnswer = true;
    confirmCalls = 0;
    requestOutcome = { status: 'accepted', operation: operation(null, 'Requested'), replayed: false };

    TestBed.configureTestingModule({
      providers: [
        {
          provide: ReferenceImageService,
          useValue: {
            request: (_slug: string, request: RequestReferenceImageRequest) => {
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
        // The panel reads a run's pictures only when it has a run and a session, and this host gives it neither.
        { provide: GeneratedImageService, useValue: { watch: () => of() } },
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
        {
          provide: BrandSourceDocumentService,
          useValue: {
            searchLibrary: (): Observable<BrandLibraryOutcome> =>
              of({ status: 'ok', page: { items: [], nextCursor: null } }),
            upload: () => Promise.resolve({ status: 'failed' as const, reason: 'unavailable' as const }),
          },
        },
      ],
    });
  });

  it('asks for nothing, and offers no reading, until a photograph is attached', async () => {
    await mount();

    expect(buttonWith('Read this picture')).toBeNull();
    expect(requests.length).toBe(0);
  });

  it('reads the attached photograph, with the creator’s note as untrusted text beside it', async () => {
    await mount({ reference: REFERENCE });

    const note = el.querySelector<HTMLTextAreaElement>('#cp-pipeline-reference-note')!;
    note.value = '  the light  ';
    note.dispatchEvent(new Event('input'));
    await settle();

    await click('Read this picture');

    expect(requests[0]).toEqual({
      picture: { source: 'BrandDocument', referenceDocumentId: 'd-ref' },
      note: '  the light  ',
    });
    expect(host.prompt().referenceRequestId).toBe('r-ref');
  });

  it('shows every observation with how sure it is, in words', async () => {
    await mount({ reference: REFERENCE });
    await click('Read this picture');
    await answerWatch(READING);

    expect(el.textContent).toContain('Hard side light from the left');
    expect(el.textContent).toContain('Clearly visible');
    expect(el.textContent).toContain('Calm');
    expect(el.textContent).toContain('Hard to tell');

    // Every row carries its confidence as text, so nothing depends on a hue or a bar.
    const rows = Array.from(el.querySelectorAll('.observations > li'));
    expect(rows.length).toBe(2);
    for (const row of rows) {
      expect(row.querySelector('.confidence')?.textContent?.trim().length).toBeGreaterThan(0);
    }
  });

  it('says no observations came back, without judging the photograph', async () => {
    await mount({ reference: REFERENCE });
    await click('Read this picture');
    await answerWatch(proposal([row({ changeKind: 'Add', afterValue: 'A loaf.', proposedPosition: 0 })]));

    expect(el.textContent).toContain('No observations came back');
    // Never "nothing could be read with any confidence": that asserts a judgement the server did not make.
    expect(el.textContent).not.toContain('with any confidence');
  });

  it('offers the wording it drew without taking an empty prompt box as permission to ask', async () => {
    await mount({ reference: REFERENCE });
    await click('Read this picture');
    await answerWatch(READING);

    await click('Use this as my prompt');

    expect(confirmCalls).toBe(0);
    expect(host.prompt().finalPrompt).toBe('A loaf on scrubbed oak in side light.');
    // 'reference', not 'creator': they chose the wording, they did not write it.
    expect(host.prompt().promptSource).toBe('reference');
  });

  it('asks before replacing a prompt the creator already has, and leaves it alone on a no', async () => {
    await mount({ reference: REFERENCE, finalPrompt: 'My own prompt.', promptSource: 'creator' });
    await click('Read this picture');
    await answerWatch(READING);

    confirmAnswer = false;
    await click('Use this as my prompt');
    expect(confirmCalls).toBe(1);
    expect(host.prompt().finalPrompt).toBe('My own prompt.');

    confirmAnswer = true;
    await click('Use this as my prompt');
    expect(host.prompt().finalPrompt).toBe('A loaf on scrubbed oak in side light.');
  });

  it('abandons a reading when a different photograph is attached, because it read another picture', async () => {
    await mount({ reference: REFERENCE });
    await click('Read this picture');
    await answerWatch(READING);
    expect(el.textContent).toContain('Hard side light');

    await click('Remove');

    expect(host.prompt().reference).toBeNull();
    expect(host.prompt().referenceRequestId).toBeNull();
    expect(el.textContent).not.toContain('Hard side light');
  });

  it('reads a kept reading back rather than asking for a second one', async () => {
    await mount({ reference: REFERENCE, referenceRequestId: 'r-kept' });

    expect(requests.length).toBe(0);
    expect(watches.length).toBe(1);
  });

  it('says a reference the server will not read cannot be read, and what to do instead', async () => {
    requestOutcome = {
      status: 'refused',
      code: 'ai.referenceImage.not_found',
      message: 'That file cannot be read as a photograph.',
    };
    await mount({ reference: REFERENCE });
    await click('Read this picture');

    expect(el.textContent).toContain('That file cannot be read as a photograph.');
    expect(el.textContent).toContain('Remove this picture and choose a different one');
  });

  it('offers to stop checking rather than to cancel', async () => {
    await mount({ reference: REFERENCE });
    await click('Read this picture');

    expect(buttonWith('Cancel')).toBeNull();
    await click('Stop checking');

    expect(watches[0].observed).toBeFalse();
  });
});
