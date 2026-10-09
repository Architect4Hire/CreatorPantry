import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import {
  ContentPipelineDraft,
  emptyContentPipelineDraft,
} from '../../models/content-pipeline.models';
import { LinkedRecipe } from '../../models/creative-context.models';
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
    [recipe]="recipe()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  readonly recipe = signal<LinkedRecipe | null>(null);
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

  it('lays the work out as named sections, the brief first, in the order it happens', async () => {
    await mount();

    const headings = Array.from(el.querySelectorAll('cp-form-section h3, cp-form-section h2')).map((heading) =>
      heading.textContent?.trim(),
    );

    expect(headings).toEqual(['The brief', 'The look', 'Extras', 'The prompt']);
  });

  describe('a linked recipe', () => {
    const LINKED: LinkedRecipe = { recipeId: 'recipe-1', recipeVersionId: 'version-1' };

    function ideaAround(recipeVersionId: string): ContentPipelineDraft['seed'] {
      return {
        lastToken: 'abc-123',
        keep: {},
        accepted: {
          token: 'abc-123',
          cuisine: null,
          dishType: null,
          method: null,
          photographyStyle: null,
          channel: null,
          day: { day: 'Wednesday', pinned: false, theme: null },
          occasion: null,
          description: 'Plan a post about "Lemon Tart".',
          recipe: { recipeId: 'recipe-1', recipeVersionId, title: 'Lemon Tart' },
          subject: null,
        },
      };
    }

    const brief = (): string => el.querySelector('#cp-pipeline-brief')?.textContent ?? '';

    it('says nothing about a recipe when none is linked', async () => {
      await mount();

      expect(brief()).not.toContain('recipe you linked');
    });

    it('says the looks and the prompt are planned around it, by name when the idea was built around it', async () => {
      await mount({ seed: ideaAround('version-1') });
      host.recipe.set(LINKED);
      await settle();

      expect(brief()).toContain('planned around the recipe you linked, Lemon Tart.');
    });

    it('does not borrow a name from an idea built around another version', async () => {
      await mount({ seed: ideaAround('version-0') });
      host.recipe.set(LINKED);
      await settle();

      expect(brief()).toContain('planned around the recipe you linked on the first step');
      expect(brief()).not.toContain('Lemon Tart');
    });
  });

  describe('the brief', () => {
    function briefBox(): HTMLTextAreaElement {
      return el.querySelector<HTMLTextAreaElement>('#cp-pipeline-brief-text')!;
    }

    async function mountWith(config: Partial<ContentPipelineDraft['config']>): Promise<void> {
      const base = emptyContentPipelineDraft();
      await mount({ config: { ...base.config, ...config } });
    }

    it('shows the chosen brief at the head of the step, with where it came from', async () => {
      const base = emptyContentPipelineDraft();
      // With the idea it was combined from still picked, so the brief reads as exactly what the choice makes.
      await mount({
        config: { ...base.config, concept: 'Mine.', briefSource: 'Combined', brief: 'Mine.\n\nAn idea.' },
        seed: {
          lastToken: 'abc-123',
          keep: {},
          accepted: {
            token: 'abc-123',
            cuisine: null,
            dishType: null,
            method: null,
            photographyStyle: null,
            channel: null,
            day: { day: 'Wednesday', pinned: false, theme: null },
            occasion: null,
            description: 'An idea.',
            recipe: null,
            subject: null,
          },
        },
      });

      expect(briefBox().value).toBe('Mine.\n\nAn idea.');
      expect(el.querySelector('#cp-pipeline-brief')?.textContent).toContain('Combine them');
      expect(el.querySelector('#cp-pipeline-brief')?.textContent).not.toContain('Edited since you chose it');
    });

    it('lets the creator edit it, changing the brief and neither of the things it was made from', async () => {
      await mountWith({ concept: 'Mine.', briefSource: 'Description', brief: 'Mine.' });

      briefBox().value = 'Mine, and steam.';
      briefBox().dispatchEvent(new Event('input'));
      await settle();

      expect(host.draft().config.brief).toBe('Mine, and steam.');
      expect(host.draft().config.concept).toBe('Mine.');
      expect(host.draft().config.briefSource).toBe('Description');
      expect(el.querySelector('#cp-pipeline-brief')?.textContent).toContain('Edited since you chose it');
    });

    it('says when it is too long, beside the box, and cuts nothing', async () => {
      const long = 'a'.repeat(1001);
      await mountWith({ briefSource: 'Idea', brief: long });

      expect(el.querySelector('#cp-pipeline-brief')?.textContent).toContain('Keep the brief to 1000 or fewer');
      expect(briefBox().value.length).toBe(1001);
    });

    it('says so when none was chosen, and still lets one be written', async () => {
      await mountWith({ concept: 'A description nobody chose to work from.' });

      expect(el.querySelector('#cp-pipeline-brief')?.textContent).toContain('No brief has been chosen yet');
      expect(briefBox().value).withContext('the description is not read in its place').toBe('');
    });
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
