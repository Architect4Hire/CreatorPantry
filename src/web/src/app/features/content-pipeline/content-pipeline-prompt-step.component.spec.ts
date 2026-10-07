import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import {
  ContentPipelineDraft,
  emptyContentPipelineDraft,
} from '../../models/content-pipeline.models';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { AiWatchOperationOutcome } from '../../services/ai-request';
import { ImagePromptService } from '../../services/image-prompt.service';
import { PhotographyConceptService } from '../../services/photography-concept.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { ContentPipelinePromptStepComponent } from './content-pipeline-prompt-step.component';

const CHOSEN = { conceptRequestId: 'r-concept', conceptId: 'k-1', label: 'Warm morning', shotKind: 'Hero' as const };

@Component({
  imports: [ContentPipelinePromptStepComponent],
  template: `<cp-content-pipeline-prompt-step
    workspaceSlug="cozy-fall"
    [draft]="draft()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  readonly announcements: string[] = [];

  apply(next: ContentPipelineDraft): void {
    this.draft.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(draft?: Partial<ContentPipelineDraft>): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set({ ...emptyContentPipelineDraft(), ...draft });
  el = fixture.nativeElement;
  await settle();
}

const neverAnswers = (): Observable<AiWatchOperationOutcome> => of<AiWatchOperationOutcome>();

describe('ContentPipelinePromptStepComponent', () => {
  beforeEach(() => {
    const ai = { request: () => Promise.resolve({ status: 'unavailable' as const }), watch: neverAnswers };

    TestBed.configureTestingModule({
      providers: [
        { provide: PhotographyConceptService, useValue: ai },
        { provide: ImagePromptService, useValue: ai },
        { provide: ReferenceImageService, useValue: ai },
        {
          provide: AiUsageService,
          useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }), ensureLoaded: () => Promise.resolve() },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
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

  it('lays the work out as three named sections, in the order it happens', async () => {
    await mount();

    const headings = Array.from(el.querySelectorAll('cp-form-section h3, cp-form-section h2')).map((heading) =>
      heading.textContent?.trim(),
    );

    expect(headings).toEqual(['The look', 'Extras', 'The prompt']);
  });

  it('names every section to a screen reader, so each is a landmark rather than a run of text', async () => {
    await mount();

    for (const section of Array.from(el.querySelectorAll('cp-form-section section'))) {
      const labelledBy = section.getAttribute('aria-labelledby');
      expect(labelledBy).withContext('aria-labelledby').toBeTruthy();
      expect(el.querySelector('#' + labelledBy)?.textContent?.trim().length).toBeGreaterThan(0);
    }
  });

  it('holds the prompt back until a shot has been picked, and says why', async () => {
    await mount();

    expect(el.textContent).toContain('Pick a look and a shot first');
    expect(el.querySelector('#cp-pipeline-final-prompt')).toBeNull();
  });

  it('shows the prompt once a shot is picked', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ prompt: { ...base.prompt, chosen: CHOSEN } });

    expect(el.querySelector('#cp-pipeline-final-prompt')).not.toBeNull();
    expect(el.textContent).toContain('Warm morning');
  });

  it('offers a brief and a reference, both optional and neither asked for', async () => {
    await mount();

    expect(el.textContent).toContain('Both optional');
    expect(el.querySelector('cp-content-pipeline-reference-panel')).not.toBeNull();
    // Two pickers: the brief's, and the one inside the reference panel.
    expect(el.querySelectorAll('cp-content-pipeline-document-picker').length).toBe(2);
  });

  it('asks the brief picker for documents and the reference picker for photographs', async () => {
    await mount();

    const accepts = Array.from(el.querySelectorAll('cp-uploader input[type="file"]')).map((input) =>
      input.getAttribute('accept'),
    );

    expect(accepts[0]).toContain('.pdf');
    expect(accepts[1]).toBe('.png,.jpg,.jpeg,.webp');
  });

  it('marks no field optional, because the step says it once in its own legend', async () => {
    await mount();

    expect(el.querySelectorAll('[aria-required="true"]').length).toBe(0);
    expect(el.textContent).not.toContain('(optional)');
  });
});
