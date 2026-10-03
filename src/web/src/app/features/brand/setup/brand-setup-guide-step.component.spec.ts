import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { BrandSourceDetailOutcome, BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { BrandSetupGuideStepComponent } from './brand-setup-guide-step.component';
import { decodeGoals } from './brand-setup-goals';
import { decodeStyle } from './brand-setup-style';
import { GuideSection, composeSection, decodeGuideSections, writtenSummary } from './brand-setup-guide';

const SLUG = 'sams-kitchen';

function detail(id: string, title: string): BrandSourceDocumentDetail {
  return {
    id,
    title,
    documentType: 'WritingSample',
    purpose: 'Voice',
    status: 'Active',
    fileName: `${id}.docx`,
    sizeBytes: 1024,
    mediaType: 'text/plain',
    versionNumber: 1,
    concurrencyToken: 'token-1',
  };
}

class StubDocuments {
  readonly details = new Map<string, BrandSourceDocumentDetail>();
  readonly unavailable = new Set<string>();
  readonly reads: string[] = [];

  get(slug: string, documentId: string): Promise<BrandSourceDetailOutcome> {
    expect(slug).toBe(SLUG);
    this.reads.push(documentId);
    if (this.unavailable.has(documentId)) return Promise.resolve({ status: 'unavailable' });
    const found = this.details.get(documentId);
    return Promise.resolve(found ? { status: 'ok', document: found } : { status: 'not_found' });
  }
}

@Component({
  imports: [BrandSetupGuideStepComponent],
  template: `<cp-brand-setup-guide-step
    [step]="step"
    [draft]="draft()"
    [goals]="goals()"
    [style]="style()"
    [choice]="choice()"
    [workspaceSlug]="slug"
    (reported)="reports.push($event)"
    (saveRequested)="saves = saves + 1"
  />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS.find((each) => each.slug === 'edit')!;
  readonly slug = SLUG;
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly goals = signal<Record<string, unknown> | null>(null);
  readonly style = signal<Record<string, unknown> | null>(null);
  readonly choice = signal<Record<string, unknown> | null>(null);
  readonly reports: BrandSetupStepReport[] = [];
  saves = 0;
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let documents: StubDocuments;

interface MountOptions {
  readonly draft?: Record<string, unknown> | null;
  readonly goals?: Record<string, unknown> | null;
  readonly style?: Record<string, unknown> | null;
  readonly choice?: Record<string, unknown> | null;
}

/** Answers that compose nothing, so a spec about emptiness is not fighting the goals step's own defaults. */
const NO_ANSWERS = { purposes: ['blog'], audience: 'other', audienceNote: '', channels: ['blog'], different: '', avoid: '' };

async function mount(options: MountOptions = {}): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(options.draft ?? null);
  host.goals.set(options.goals ?? null);
  host.style.set(options.style ?? null);
  host.choice.set(options.choice ?? null);
  el = fixture.nativeElement;
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const text = (): string => el.textContent ?? '';
const body = (id: string): HTMLTextAreaElement => el.querySelector<HTMLTextAreaElement>(`#cp-guide-body-${id}`)!;
const headings = (): string[] => Array.from(el.querySelectorAll('cp-form-section h2')).map((each) => each.textContent!.trim());

function sectionOf(id: string): GuideSection {
  const slice = last().draft as { sections: readonly GuideSection[] };
  return slice.sections.find((each) => each.id === id)!;
}

async function type(id: string, value: string): Promise<void> {
  const control = body(id);
  control.value = value;
  control.dispatchEvent(new Event('input'));
  await settle();
}

function button(label: string): HTMLButtonElement | null {
  return Array.from(el.querySelectorAll('button')).find((each) => each.textContent?.trim() === label) ?? null;
}

const saved = (...sections: Partial<GuideSection>[]): Record<string, unknown> => ({
  sections: sections.map((each) => ({ origin: 'creator', edited: false, citedDocumentIds: [], body: '', ...each })),
});

describe('BrandSetupGuideStepComponent', () => {
  beforeEach(() => {
    documents = new StubDocuments();
    TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: documents }],
    });
  });

  describe('the parts of the guide', () => {
    it('has one part per thing to say, plus a part for each channel the creator shares on', async () => {
      await mount({ goals: { ...NO_ANSWERS, channels: ['blog', 'newsletter'] } });
      expect(headings()).toContain('How you come across');
      expect(headings()).toContain('Your blog posts');
      expect(headings()).toContain('Your newsletter');
      expect(headings()).not.toContain('Your social posts');
      expect(headings()).toContain('Your photos');
      expect(headings()).toContain('Anything else');
      expect(el.querySelectorAll('cp-form-section').length).toBe(headings().length);
    });

    it('offers a social part when any social channel was picked, and only one', async () => {
      await mount({ goals: { ...NO_ANSWERS, channels: ['instagram', 'tiktok'] } });
      expect(headings().filter((each) => each === 'Your social posts').length).toBe(1);
      expect(headings()).not.toContain('Your blog posts');
    });

    it('counts what is written and holds Continue until something is', async () => {
      await mount({ goals: NO_ANSWERS });
      const total = headings().length;
      expect(text()).toContain(writtenSummary(0, total));
      expect(last().canContinue).toBeFalse();
      expect(text()).toContain('Write at least one part');

      await type('voice', 'I sound like me.');
      expect(text()).toContain(writtenSummary(1, total));
      expect(last().canContinue).toBeTrue();
      expect(text()).not.toContain('Write at least one part');
    });
  });

  describe('where a part came from', () => {
    it('says a part back from the earlier answers and marks it as theirs to change', async () => {
      await mount({ goals: { ...NO_ANSWERS, audience: 'home-cooks' }, style: { choices: { voice: ['warm-friend'] }, notes: {} } });
      expect(body('voice').value).toContain('warm friend');
      expect(body('audience').value).toContain('home cooks');
      expect(text()).toContain('From your answers');
      expect(sectionOf('voice').origin).toBe('answers');
      expect(sectionOf('voice').edited).toBeFalse();
      // Composing is not an unsaved change of the creator's, but it is still handed over to be saved.
      expect(last().isDirty).toBeFalse();
      expect(last().draft).not.toBeNull();
    });

    it('carries the creator own note for a question through word for word', async () => {
      await mount({ goals: NO_ANSWERS, style: { choices: { voice: ['calm-teacher'] }, notes: { voice: 'I swear a little.' } } });
      expect(body('voice').value).toContain('I swear a little.');
    });

    it('becomes the creator words as soon as they type, and reports the change', async () => {
      await mount({ goals: NO_ANSWERS, style: { choices: { voice: ['warm-friend'] }, notes: {} } });
      await type('voice', 'I sound like a tired parent at 6pm.');

      expect(text()).toContain('Your words');
      expect(sectionOf('voice')).toEqual(
        jasmine.objectContaining({ body: 'I sound like a tired parent at 6pm.', origin: 'answers', edited: true }),
      );
      expect(last().isDirty).toBeTrue();
    });

    it('never re-derives a part the creator has touched, however the answers change', async () => {
      await mount({
        draft: saved({ id: 'voice', body: 'My own line.', origin: 'answers', edited: true }),
        style: { choices: { voice: ['expert-pro'] }, notes: {} },
        goals: NO_ANSWERS,
      });
      expect(body('voice').value).toBe('My own line.');
      expect(text()).toContain('Your words');
      expect(last().isDirty).toBeFalse();
    });

    it('does follow a changed answer while the part is still untouched', async () => {
      // Exactly what the answer it was composed from produces, so the spec is about the rule and not a string.
      const before = composeSection('voice', decodeGoals(NO_ANSWERS), decodeStyle({ choices: { voice: ['warm-friend'] }, notes: {} }));
      await mount({
        draft: saved({ id: 'voice', body: before, origin: 'answers', edited: false }),
        style: { choices: { voice: ['expert-pro'] }, notes: {} },
        goals: NO_ANSWERS,
      });
      expect(body('voice').value).toContain('expert');
      expect(body('voice').value).not.toContain('warm friend');
    });

    it('shows a part written for the creator, and the examples it was written from', async () => {
      documents.details.set('d1', detail('d1', 'My best banana bread post'));
      await mount({
        draft: saved({ id: 'voice', body: 'Drafted words.', origin: 'draft', citedDocumentIds: ['d1'] }),
        goals: NO_ANSWERS,
      });
      expect(text()).toContain('Written for you');
      expect(text()).toContain('Written from My best banana bread post.');
      expect(documents.reads).toEqual(['d1']);

      await type('voice', 'Drafted words, but mine now.');
      expect(text()).toContain('Changed by you');
      expect(text()).toContain('stays exactly as you left it');
      expect(sectionOf('voice')).toEqual(jasmine.objectContaining({ origin: 'draft', edited: true }));
    });

    it('says plainly when a drafted part rests on the answers rather than an example', async () => {
      await mount({ draft: saved({ id: 'voice', body: 'Drafted words.', origin: 'draft' }), goals: NO_ANSWERS });
      expect(text()).toContain('not from one of your examples');
      expect(documents.reads).toEqual([]);
    });

    it('names an example that has gone without pretending the part changed', async () => {
      await mount({ draft: saved({ id: 'voice', body: 'Drafted.', origin: 'draft', citedDocumentIds: ['gone'] }), goals: NO_ANSWERS });
      expect(text()).toContain('an example that is no longer there');
      expect(body('voice').value).toBe('Drafted.');
    });

    it('offers a retry when an example cannot be looked up, and says the guide is untouched', async () => {
      documents.details.set('d1', detail('d1', 'My best post'));
      documents.unavailable.add('d1');
      await mount({ draft: saved({ id: 'voice', body: 'Drafted.', origin: 'draft', citedDocumentIds: ['d1'] }), goals: NO_ANSWERS });

      expect(text()).toContain("couldn't look up every example");
      expect(text()).toContain('Your guide is unchanged');

      documents.unavailable.delete('d1');
      button('Try again')!.click();
      await settle();
      expect(text()).toContain('Written from My best post.');
      expect(button('Try again')).toBeNull();
    });

    it('ignores a malformed saved part rather than emptying the editor', async () => {
      expect(decodeGuideSections({ sections: 'nope' })).toEqual([]);
      expect(decodeGuideSections({ sections: [{ id: 'voice', body: 'x', origin: 'magic' }, { id: 7 }] })).toEqual([]);

      await mount({ draft: { sections: [{ id: 'voice' }] }, goals: NO_ANSWERS, style: { choices: { voice: ['warm-friend'] }, notes: {} } });
      expect(body('voice').value).toContain('warm friend');
    });
  });

  describe('saving and the draft that is not written yet', () => {
    it('asks the shell to save rather than saving on its own', async () => {
      await mount({ goals: NO_ANSWERS });
      await type('voice', 'Mine.');
      button('Save draft')!.click();
      await settle();
      expect(host.saves).toBe(1);
      // No second account of whether the work is saved: that line belongs to the shell.
      expect(text()).not.toContain('Saved at');
    });

    it('says so when a draft was asked for and has not been written', async () => {
      await mount({ goals: NO_ANSWERS, choice: { method: 'draft', costAcknowledged: true } });
      expect(text()).toContain("isn't written yet");
    });

    it('says nothing of the sort to a creator writing it themselves', async () => {
      await mount({ goals: NO_ANSWERS, choice: { method: 'myself', costAcknowledged: false } });
      expect(text()).not.toContain("isn't written yet");
    });

    it('says nothing of the sort once a part has been written for them', async () => {
      await mount({
        goals: NO_ANSWERS,
        choice: { method: 'draft', costAcknowledged: true },
        draft: saved({ id: 'voice', body: 'Drafted.', origin: 'draft' }),
      });
      expect(text()).not.toContain("isn't written yet");
    });
  });

  describe('getting around and accessibility', () => {
    it('lists the parts as links that say whether each is written, and moves to the one picked', async () => {
      await mount({ goals: NO_ANSWERS, style: { choices: { voice: ['warm-friend'] }, notes: {} } });
      const nav = el.querySelector('nav[aria-label="Parts of your guide"]')!;
      const links = Array.from(nav.querySelectorAll('a'));
      expect(links.length).toBe(headings().length);
      expect(links[0].textContent).toContain('How you come across');
      expect(links[0].textContent).toContain('Written');
      expect(links[links.length - 1].textContent).toContain('Empty');

      links[1].click();
      await settle();
      const target = el.querySelector('#cp-guide-tone')!;
      expect(document.activeElement).toBe(target);
      expect(links[1].getAttribute('aria-current')).toBe('true');
    });

    it('names every box, caps what one part can hold, and explains each part in a disclosure', async () => {
      await mount({ goals: NO_ANSWERS });
      const boxes = Array.from(el.querySelectorAll<HTMLTextAreaElement>('textarea'));
      expect(boxes.length).toBe(headings().length);
      for (const box of boxes) {
        expect(box.labels?.length).withContext(box.id).toBeTruthy();
        expect(box.getAttribute('aria-describedby')).withContext(box.id).toBeTruthy();
        expect(box.maxLength).toBe(2000);
      }
      expect(el.querySelectorAll('details.cp-disclosure').length).toBe(headings().length);
    });

    it('states optionality once, and never part by part', async () => {
      await mount({ goals: NO_ANSWERS });
      expect(text()).not.toMatch(/\(optional\)/i);
      expect(text()).not.toMatch(/\brequired\b/i);
    });

    it('uses no model, prompt or provider vocabulary', async () => {
      await mount({ goals: NO_ANSWERS, draft: saved({ id: 'voice', body: 'Drafted.', origin: 'draft' }) });
      expect(text().toLowerCase()).not.toMatch(/\b(prompt|model|token|provider|embedding|deployment|chunk|passage)\b/);
    });
  });
});
