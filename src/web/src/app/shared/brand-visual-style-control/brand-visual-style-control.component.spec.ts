import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BrandGuideChoice, BrandGuideControlStatus, BrandGuideSelection } from '../../models/brand-guide-control.models';
import { BrandVisualReference, BrandVisualStyleLine } from '../../models/brand-visual-style-control.models';
import { BrandVisualStyleControlComponent } from './brand-visual-style-control.component';

function guide(overrides: Partial<BrandGuideChoice> = {}): BrandGuideChoice {
  return { guideId: 'g1', name: 'Bright table', versionNumber: 3, isStale: false, ...overrides };
}

const lines: BrandVisualStyleLine[] = [
  { label: 'Visual identity', summary: 'Warm, honest, never staged.' },
  { label: 'Photography direction', summary: 'Soft window light from the left.' },
];

const usable: BrandVisualReference = { documentId: 'd1', title: 'Spring moodboard', usable: true };
const unreadable: BrandVisualReference = { documentId: 'd2', title: 'Scanned board', usable: false };

interface Setup {
  status?: BrandGuideControlStatus;
  activeGuide?: BrandGuideChoice | null;
  choices?: BrandGuideChoice[];
  selection?: BrandGuideSelection;
  staleAcknowledged?: boolean;
  hasVisualGuidance?: boolean;
  styleLines?: BrandVisualStyleLine[];
  negativeGuidance?: string | null;
  references?: BrandVisualReference[];
  selectedReferenceIds?: string[];
  referencesStatus?: 'loading' | 'error' | 'ready';
}

describe('BrandVisualStyleControlComponent', () => {
  let fixture: ComponentFixture<BrandVisualStyleControlComponent>;

  async function render(setup: Setup = {}): Promise<HTMLElement> {
    fixture = TestBed.createComponent(BrandVisualStyleControlComponent);
    const values: Required<Setup> = {
      status: 'ready',
      activeGuide: guide(),
      choices: [],
      selection: { kind: 'default' },
      staleAcknowledged: false,
      hasVisualGuidance: true,
      styleLines: [],
      negativeGuidance: null,
      references: [],
      selectedReferenceIds: [],
      referencesStatus: 'ready',
      ...setup,
    };
    for (const [key, value] of Object.entries(values)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  const text = (host: Element) => host.textContent?.replace(/\s+/g, ' ') ?? '';
  const button = (host: HTMLElement, name: string) =>
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === name)!;

  beforeEach(() => TestBed.configureTestingModule({ imports: [BrandVisualStyleControlComponent] }));

  it('names the active visual style and its exact version by default', async () => {
    const host = await render();

    expect(text(host)).toContain('Using your visual style: Bright table, version 3.');
    expect(text(host)).toContain('Visual style on');
  });

  it('is a named region', async () => {
    const host = await render();
    const region = host.querySelector('section')!;

    expect(host.querySelector(`#${region.getAttribute('aria-labelledby')}`)?.textContent).toContain('Visual style');
  });

  it('describes an override as applying to this piece only', async () => {
    const other = guide({ guideId: 'g2', name: 'Winter set', versionNumber: 2 });
    const host = await render({ choices: [other], selection: { kind: 'override', guideId: 'g2', versionNumber: 2 } });

    expect(text(host)).toContain('Using Winter set, version 2, for this piece only.');
    expect(text(host)).toContain('is not your active visual style');
  });

  describe('no visual guide', () => {
    it('tells an active guide with no visual sections apart from having no guide, and offers the guide', async () => {
      const host = await render({ hasVisualGuidance: false });
      let opened = 0;
      fixture.componentInstance.reviewGuide.subscribe(() => opened++);

      expect(text(host)).toContain('does not describe a visual style yet');
      expect(text(host)).toContain('No visual style');
      expect(host.querySelector('[role="alert"]')).toBeNull();
      button(host, 'Open guide').click();

      expect(opened).toBe(1);
    });

    it('offers set-up when there is no guide at all', async () => {
      const host = await render({ activeGuide: null });
      let setUp = 0;
      fixture.componentInstance.setUpGuide.subscribe(() => setUp++);

      expect(text(host)).toContain('You have not set up a brand style guide yet');
      button(host, 'Set up my brand style guide').click();

      expect(setUp).toBe(1);
    });

    it('treats the no-style choice as a choice, not a warning', async () => {
      const host = await render({ selection: { kind: 'none' } });

      expect(text(host)).toContain('Making this without your visual style.');
      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(host.querySelector('.block')).toBeNull();
    });
  });

  describe('stale guide', () => {
    const stale = guide({ isStale: true, staleReason: 'A reference document was replaced.' });

    it('blocks with an alert until the creator continues explicitly', async () => {
      const host = await render({ activeGuide: stale });
      const events: string[] = [];
      fixture.componentInstance.reviewGuide.subscribe(() => events.push('review'));
      fixture.componentInstance.staleAcknowledgedChange.subscribe((v) => events.push(`ack:${v}`));

      expect(host.querySelector('[role="alert"]')?.textContent).toContain('A reference document was replaced.');
      button(host, 'Review guide').click();
      button(host, 'Continue anyway').click();

      expect(events).toEqual(['review', 'ack:true']);
    });

    it('stays visibly stale once accepted, and can be undone', async () => {
      const host = await render({ activeGuide: stale, staleAcknowledged: true });

      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(text(host)).toContain('You are continuing with an out-of-date guide.');
      expect(text(host)).toContain('Out of date');
      expect(button(host, 'Undo')).toBeTruthy();
    });
  });

  it('reports a chosen guide that has gone instead of using another', async () => {
    const host = await render({ selection: { kind: 'override', guideId: 'gone', versionNumber: 1 } });

    expect(text(host)).toContain('The guide you chose is no longer available.');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Choose a guide to continue');
  });

  describe('the look and what is kept out', () => {
    it('shows the look and the avoid summary in the creator\'s words', async () => {
      const host = await render({ styleLines: lines, negativeGuidance: 'No flash, no neon, no hands in frame.' });

      expect(text(host)).toContain('Visual identity');
      expect(text(host)).toContain('Soft window light from the left.');
      expect(text(host)).toContain('Kept out of the picture');
      expect(text(host)).toContain('No flash, no neon, no hands in frame.');
    });

    it('omits the avoid section when the guide names nothing to avoid', async () => {
      const host = await render({ styleLines: lines });

      expect(text(host)).not.toContain('Kept out of the picture');
    });
  });

  describe('reference examples', () => {
    it('is a labelled group with a count announced politely', async () => {
      const host = await render({ references: [usable], selectedReferenceIds: ['d1'] });
      const fieldset = host.querySelector('fieldset')!;

      expect(fieldset.querySelector('legend')?.textContent).toContain('Reference examples');
      expect(fieldset.getAttribute('aria-describedby')).toBeTruthy();
      expect(host.querySelector(`#${fieldset.getAttribute('aria-describedby')}`)?.textContent).toContain('1 of 10 chosen.');
      expect(host.querySelector(`#${fieldset.getAttribute('aria-describedby')}`)?.getAttribute('role')).toBe('status');
    });

    it('emits the new selection when one is ticked and unticked', async () => {
      const host = await render({ references: [usable, { ...usable, documentId: 'd3', title: 'Autumn board' }], selectedReferenceIds: ['d1'] });
      const events: (readonly string[])[] = [];
      fixture.componentInstance.selectedReferenceIdsChange.subscribe((ids) => events.push(ids));

      const boxes = host.querySelectorAll<HTMLInputElement>('cp-checkbox input');
      boxes[1].click();
      boxes[0].click();

      expect(events).toEqual([['d1', 'd3'], []]);
    });

    it('says plainly when a reference has no readable text, and warns before submit', async () => {
      const host = await render({ references: [usable, unreadable], selectedReferenceIds: ['d2'] });

      expect(text(host)).toContain('Scanned board (no readable text)');
      expect(host.querySelector('[data-kind="conflict"]')?.textContent).toContain('Its image is never sent.');
    });

    it('lets the creator remove an unusable reference from the warning', async () => {
      const host = await render({ references: [unreadable], selectedReferenceIds: ['d2'] });
      const events: (readonly string[])[] = [];
      fixture.componentInstance.selectedReferenceIdsChange.subscribe((ids) => events.push(ids));

      button(host, 'Remove').click();

      expect(events).toEqual([[]]);
    });

    it('stops at the limit and says so, while leaving chosen ones untickable', async () => {
      const many = Array.from({ length: 11 }, (_, i) => ({ documentId: `d${i}`, title: `Ref ${i}`, usable: true }));
      const host = await render({ references: many, selectedReferenceIds: many.slice(0, 10).map((r) => r.documentId) });
      const boxes = [...host.querySelectorAll<HTMLInputElement>('cp-checkbox input')];

      expect(boxes[10].disabled).toBeTrue();
      expect(boxes[0].disabled).toBeFalse();
      expect(text(host)).toContain('That is the most you can use at once.');
    });

    it('is switched off, and says why, when making this without a visual style', async () => {
      const host = await render({ selection: { kind: 'none' }, references: [usable] });

      expect(host.querySelector('fieldset')?.disabled).toBeTrue();
      expect(text(host)).toContain('References are not used when you make this without your visual style.');
      expect(host.querySelector('[data-kind="conflict"]')).toBeNull();
    });

    it('asks the host to go to the library rather than uploading anything itself', async () => {
      const host = await render();
      let asked = 0;
      fixture.componentInstance.addReference.subscribe(() => asked++);

      button(host, 'Add a reference').click();

      expect(asked).toBe(1);
      expect(host.querySelector('input[type="file"]')).toBeNull();
    });

    it('says so when there are none, and degrades without blocking when they cannot load', async () => {
      expect(text(await render())).toContain('You have no visual references yet.');
      expect(text(await render({ referencesStatus: 'error' }))).toContain('You can still continue without them.');
      expect(text(await render({ referencesStatus: 'loading' }))).toContain('Loading your references…');
    });
  });

  describe('change selection', () => {
    const other = guide({ guideId: 'g2', name: 'Winter set', versionNumber: 2 });

    it('keeps the picker closed until asked, and says so with aria-expanded', async () => {
      const host = await render({ choices: [other] });

      expect(host.querySelector('select')).toBeNull();
      expect(button(host, 'Change').getAttribute('aria-expanded')).toBe('false');

      button(host, 'Change').click();
      fixture.detectChanges();

      expect(button(host, 'Change').getAttribute('aria-expanded')).toBe('true');
      expect(host.querySelector('select')).not.toBeNull();
    });

    it('labels the select and lists default, others and no visual style', async () => {
      const host = await render({ choices: [other] });
      button(host, 'Change').click();
      fixture.detectChanges();

      const select = host.querySelector('select')!;

      expect(host.querySelector(`label[for="${select.id}"]`)?.textContent).toContain('Visual style for this piece');
      expect([...select.options].map((o) => o.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        'My visual style: Bright table, version 3 (default)',
        'Winter set, version 2',
        'No visual style',
      ]);
    });

    it('emits an override, clears stale acceptance, and closes', async () => {
      const host = await render({ choices: [other], staleAcknowledged: true });
      const events: unknown[] = [];
      fixture.componentInstance.selectionChange.subscribe((s) => events.push(s));
      fixture.componentInstance.staleAcknowledgedChange.subscribe((v) => events.push(`ack:${v}`));

      button(host, 'Change').click();
      fixture.detectChanges();
      const select = host.querySelector('select')!;
      select.value = 'g2@2';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(events).toEqual(['ack:false', { kind: 'override', guideId: 'g2', versionNumber: 2 }]);
      expect(host.querySelector('select')).toBeNull();
    });
  });

  it('keeps provenance behind a collapsed disclosure showing the exact version', async () => {
    const host = await render({ activeGuide: guide({ appliedSections: ['Visual identity', 'Photography direction'] }) });
    const details = host.querySelector('details')!;

    expect(details.open).toBeFalse();
    const terms = [...details.querySelectorAll('dt')].map((dt) => [dt.textContent, dt.nextElementSibling?.textContent]);

    expect(terms).toContain(['Version', '3']);
    expect(terms).toContain(['Used for this piece', 'Visual identity, Photography direction']);
  });

  it('never shows storage, retrieval or provider vocabulary', async () => {
    const host = await render({
      styleLines: lines,
      negativeGuidance: 'No flash.',
      references: [usable, unreadable],
      selectedReferenceIds: ['d1', 'd2'],
    });

    expect(text(host)).not.toMatch(/blob|embedding|chunk|prompt|token|checksum|provider|bucket|\.png|\.jpg|https?:/i);
  });

  describe('loading and error', () => {
    it('is busy while loading and makes no claim about a guide', async () => {
      const host = await render({ status: 'loading' });

      expect(host.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
      expect(text(host)).toContain('Checking your visual style');
      expect(text(host)).not.toContain('Using your visual style');
    });

    it('offers a retry on error', async () => {
      const host = await render({ status: 'error' });
      let retried = 0;
      fixture.componentInstance.retry.subscribe(() => retried++);

      expect(host.querySelector('[role="alert"]')?.textContent).toContain('Nothing you have typed has been lost');
      button(host, 'Try again').click();

      expect(retried).toBe(1);
    });
  });

  it('gives every button a name and an explicit type', async () => {
    const host = await render({
      activeGuide: guide({ isStale: true }),
      references: [unreadable],
      selectedReferenceIds: ['d2'],
    });

    for (const b of host.querySelectorAll('button')) {
      expect(b.textContent?.trim().length).toBeGreaterThan(0);
      expect(b.getAttribute('type')).toBe('button');
    }
  });
});
