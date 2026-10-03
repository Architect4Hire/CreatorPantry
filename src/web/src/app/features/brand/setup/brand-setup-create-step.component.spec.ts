import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { AiAllowanceState, AiUsageService, AllowanceFigures } from '../../../services/ai-usage.service';
import { BrandSourceDetailOutcome, BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { BrandSetupCreateStepComponent } from './brand-setup-create-step.component';
import { decodeCreateChoice, usedSummary } from './brand-setup-create';

const SLUG = 'sams-kitchen';

function detail(id: string, title: string, status = 'Active'): BrandSourceDocumentDetail {
  return {
    id,
    title,
    documentType: 'WritingSample',
    purpose: 'Voice',
    status,
    fileName: `${id}.docx`,
    sizeBytes: 2048,
    mediaType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    versionNumber: 1,
    concurrencyToken: 'token-1',
    channelKey: null,
    audience: null,
    tags: [],
    extraction: { state: 'NotExtracted', origin: null, at: null },
    contentChecksum: 'sha256:0',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
  };
}

/** Stands in for the document reads, so "what gets used" is driven by real outcomes rather than mocked out. */
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

class StubUsage {
  readonly allowance = signal<AiAllowanceState>({ kind: 'unknown' });
  loads = 0;

  ensureLoaded(): Promise<void> {
    this.loads += 1;
    return Promise.resolve();
  }
}

function figures(remaining: number): AllowanceFigures {
  return {
    unit: 'Credits',
    allowance: 1000,
    remaining,
    consumed: 1000 - remaining,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-11-01T00:00:00Z',
    timeZoneId: 'Europe/London',
    usedPercent: Math.round(((1000 - remaining) / 1000) * 100),
  };
}

@Component({
  imports: [BrandSetupCreateStepComponent],
  template: `<cp-brand-setup-create-step
    [step]="step"
    [draft]="draft()"
    [sources]="sources()"
    [checked]="checked()"
    [workspaceSlug]="slug"
    (reported)="reports.push($event)"
  />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS.find((each) => each.slug === 'create')!;
  readonly slug = SLUG;
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly sources = signal<Record<string, unknown> | null>(null);
  readonly checked = signal<Record<string, unknown> | null>(null);
  readonly reports: BrandSetupStepReport[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let documents: StubDocuments;
let usage: StubUsage;

interface MountOptions {
  readonly draft?: Record<string, unknown> | null;
  readonly sources?: Record<string, unknown> | null;
  readonly checked?: Record<string, unknown> | null;
  readonly allowance?: AiAllowanceState;
}

async function mount(options: MountOptions = {}): Promise<void> {
  usage.allowance.set(options.allowance ?? { kind: 'unknown' });
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(options.draft ?? null);
  host.sources.set(options.sources ?? null);
  host.checked.set(options.checked ?? null);
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
const control = (id: string): HTMLInputElement | null => el.querySelector<HTMLInputElement>('#' + id);
const text = (): string => el.textContent ?? '';

async function click(id: string): Promise<void> {
  control(id)!.click();
  await settle();
}

function button(label: string): HTMLButtonElement | null {
  return Array.from(el.querySelectorAll('button')).find((each) => each.textContent?.trim() === label) ?? null;
}

const examples = (...items: { documentId: string; kind: string }[]): Record<string, unknown> => ({ items });

describe('BrandSetupCreateStepComponent', () => {
  beforeEach(() => {
    documents = new StubDocuments();
    usage = new StubUsage();
    TestBed.configureTestingModule({
      providers: [
        { provide: BrandSourceDocumentService, useValue: documents },
        { provide: AiUsageService, useValue: usage },
      ],
    });
  });

  describe('the choice', () => {
    it('starts with nothing picked, nothing to save and Continue off', async () => {
      await mount();
      expect(control('cp-create-method-draft')!.checked).toBeFalse();
      expect(control('cp-create-method-myself')!.checked).toBeFalse();
      expect(last().canContinue).toBeFalse();
      expect(last().isDirty).toBeFalse();
      expect(last().draft).toBeNull();
      expect(text()).toContain('Pick one of the two');
    });

    it('can continue as soon as the creator says they will write it themselves', async () => {
      await mount();
      await click('cp-create-method-myself');
      expect(last().canContinue).toBeTrue();
      expect(last().isDirty).toBeTrue();
      expect(last().draft).toEqual({ method: 'myself', costAcknowledged: false });
      expect(text()).toContain('No AI is used');
      expect(text()).toContain("Next: you'll see your guide's sections");
    });

    it('holds Continue until the cost of a draft is acknowledged in so many words', async () => {
      await mount({ allowance: { kind: 'healthy', period: figures(900) } });
      await click('cp-create-method-draft');

      expect(last().canContinue).toBeFalse();
      expect(last().draft).toEqual({ method: 'draft', costAcknowledged: false });
      expect(text()).toContain('uses a little of your monthly AI allowance');
      expect(text()).toContain('Tick the box above');

      await click('cp-create-cost-ack');
      expect(last().canContinue).toBeTrue();
      expect(last().draft).toEqual({ method: 'draft', costAcknowledged: true });
      expect(text()).not.toContain('Tick the box above');
    });

    it('takes the acknowledgement back when it is unticked', async () => {
      await mount();
      await click('cp-create-method-draft');
      await click('cp-create-cost-ack');
      await click('cp-create-cost-ack');
      expect(control('cp-create-cost-ack')!.checked).toBeFalse();
      expect(last().canContinue).toBeFalse();
    });

    it('does not ask for the acknowledgement twice when the choice comes back from the draft', async () => {
      await mount({ draft: { method: 'draft', costAcknowledged: true } });
      expect(control('cp-create-method-draft')!.checked).toBeTrue();
      expect(control('cp-create-cost-ack')!.checked).toBeTrue();
      expect(last().canContinue).toBeTrue();
      expect(last().isDirty).toBeFalse();
      expect(last().draft).toEqual({ method: 'draft', costAcknowledged: true });
    });

    it('reads a malformed saved choice as no choice at all', async () => {
      expect(decodeCreateChoice({ method: 'magic', costAcknowledged: 'yes' })).toEqual({ method: null, costAcknowledged: false });
      await mount({ draft: { method: 42 } });
      expect(last().canContinue).toBeFalse();
      expect(text()).toContain('Pick one of the two');
    });
  });

  describe('a spent allowance', () => {
    it('offers no draft but still lets setup finish', async () => {
      await mount({ allowance: { kind: 'exhausted', period: figures(0) } });
      expect(control('cp-create-method-draft')!.disabled).toBeTrue();
      expect(control('cp-create-method-myself')!.disabled).toBeFalse();
      expect(text()).toContain('Your AI allowance is spent');

      await click('cp-create-method-myself');
      expect(last().canContinue).toBeTrue();
      expect(last().draft).toEqual({ method: 'myself', costAcknowledged: false });
    });

    it('says a switched-off account is not something that comes back on its own', async () => {
      await mount({ allowance: { kind: 'suspended', period: figures(500) } });
      expect(control('cp-create-method-draft')!.disabled).toBeTrue();
      expect(text()).toContain('switched off');
    });

    it('holds a draft chosen earlier and names the way forward', async () => {
      await mount({ draft: { method: 'draft', costAcknowledged: true }, allowance: { kind: 'exhausted', period: figures(0) } });
      expect(last().canContinue).toBeFalse();
      expect(text()).toContain("choose \"I'll write it myself\"");
      // The acknowledgement is not asked again for a draft that cannot be written anyway.
      expect(control('cp-create-cost-ack')).toBeNull();
    });

    it('blocks nothing on an allowance nobody has read, or one we are no longer sure of', async () => {
      await mount({ allowance: { kind: 'unknown' } });
      expect(control('cp-create-method-draft')!.disabled).toBeFalse();

      usage.allowance.set({ kind: 'degraded', period: figures(10) });
      await settle();
      expect(control('cp-create-method-draft')!.disabled).toBeFalse();
      expect(usage.loads).toBe(1);
    });

    it('warns before the limit without stopping anything', async () => {
      await mount({ allowance: { kind: 'nearly-spent', period: figures(80) } });
      expect(text()).toContain('nearly spent');
      expect(control('cp-create-method-draft')!.disabled).toBeFalse();
      await click('cp-create-method-draft');
      await click('cp-create-cost-ack');
      expect(last().canContinue).toBeTrue();
    });
  });

  describe('what the guide starts from', () => {
    it('names each example and says which text was checked', async () => {
      documents.details.set('d1', detail('d1', 'My best banana bread post'));
      documents.details.set('d2', detail('d2', 'A caption I liked'));
      await mount({
        sources: examples({ documentId: 'd1', kind: 'sounds-like-me' }, { documentId: 'd2', kind: 'social' }),
        checked: { confirmed: [{ documentId: 'd1', extractionId: 'x1' }] },
      });

      expect(text()).toContain('My best banana bread post');
      expect(text()).toContain('A caption I liked');
      expect(text()).toContain('Checked by you');
      expect(text()).toContain('Not checked');
      expect(text()).toContain('2 examples will be used.');
    });

    it('counts a look you like separately from the words', async () => {
      documents.details.set('d1', detail('d1', 'A post'));
      documents.details.set('d2', detail('d2', 'A photo I love'));
      await mount({ sources: examples({ documentId: 'd1', kind: 'blog' }, { documentId: 'd2', kind: 'visual' }) });
      expect(text()).toContain('1 example and 1 look will be used.');
      expect(text()).toContain('Used for your photo style');
      expect(usedSummary(0, 0)).toBe('');
    });

    it('shows an example left out earlier as one that will not be used', async () => {
      documents.details.set('d1', detail('d1', 'An old draft', 'Archived'));
      await mount({ sources: examples({ documentId: 'd1', kind: 'sounds-like-me' }) });
      expect(text()).toContain('Left out');
      expect(text()).not.toContain('will be used.');
    });

    it('says plainly when an example has gone', async () => {
      await mount({ sources: examples({ documentId: 'gone', kind: 'blog' }) });
      expect(text()).toContain('Not available');
      expect(text()).toContain("can't find this one any more");
    });

    it('offers a retry when an example cannot be reached, and keeps the choice available', async () => {
      documents.details.set('d1', detail('d1', 'A post'));
      documents.unavailable.add('d1');
      await mount({ sources: examples({ documentId: 'd1', kind: 'blog' }) });

      expect(text()).toContain("couldn't show one of your examples");
      await click('cp-create-method-myself');
      expect(last().canContinue).toBeTrue();

      documents.unavailable.delete('d1');
      button('Try again')!.click();
      await settle();
      expect(text()).toContain('A post');
      expect(button('Try again')).toBeNull();
    });

    it('says so when there are no examples at all, and still lets the creator choose', async () => {
      await mount();
      expect(text()).toContain("didn't add any examples");
      expect(text()).toContain('your answers alone');
      expect(documents.reads).toEqual([]);
      await click('cp-create-method-draft');
      await click('cp-create-cost-ack');
      expect(last().canContinue).toBeTrue();
    });

    it('lists what gets used by name behind a disclosure, and nothing about how', async () => {
      documents.details.set('d1', detail('d1', 'My best banana bread post'));
      await mount({ sources: examples({ documentId: 'd1', kind: 'sounds-like-me' }) });

      const details = el.querySelector('details.cp-disclosure')!;
      expect(details.hasAttribute('open')).toBeFalse();
      expect(details.textContent).toContain('My best banana bread post');
      expect(details.textContent).toContain('Nothing else from your workspace is used');
    });
  });

  describe('accessibility and words', () => {
    it('names its groups and every control, and offers no model or prompt vocabulary', async () => {
      documents.details.set('d1', detail('d1', 'A post'));
      await mount({ sources: examples({ documentId: 'd1', kind: 'blog' }), allowance: { kind: 'healthy', period: figures(900) } });
      await click('cp-create-method-draft');

      const group = el.querySelector('[role="radiogroup"]')!;
      expect(group.getAttribute('aria-label')).toBe('How should your guide get written?');
      for (const each of Array.from(el.querySelectorAll<HTMLInputElement>('input'))) {
        expect(each.labels?.length || each.getAttribute('aria-label')).withContext(each.id).toBeTruthy();
      }
      expect(el.querySelectorAll('cp-form-section').length).toBe(2);
      expect(text().toLowerCase()).not.toMatch(/\b(prompt|model|token|provider|embedding|deployment|context window)\b/);
    });

    it('announces the two things that hold Continue, where they happen', async () => {
      await mount();
      const pick = Array.from(el.querySelectorAll('p')).find((each) => each.textContent?.includes('Pick one of the two'))!;
      expect(pick.getAttribute('role')).toBeNull();

      await click('cp-create-method-draft');
      const tick = Array.from(el.querySelectorAll('p')).find((each) => each.textContent?.includes('Tick the box above'))!;
      expect(tick.getAttribute('role')).toBe('status');
    });

    it('starts nothing on its own: no draft is requested and nothing is activated', async () => {
      documents.details.set('d1', detail('d1', 'A post'));
      await mount({ sources: examples({ documentId: 'd1', kind: 'blog' }) });
      await click('cp-create-method-draft');
      await click('cp-create-cost-ack');

      // The only reads are the examples this step names. Choosing a draft writes nothing and asks for nothing.
      expect(documents.reads).toEqual(['d1']);
      expect(text()).toContain('nothing is switched on');
    });
  });
});
