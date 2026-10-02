import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  BrandGuideChannelRule,
  BrandGuideChoice,
  BrandGuideControlStatus,
  BrandGuideRulesStatus,
  BrandGuideSelection,
} from '../../models/brand-guide-control.models';
import { BrandGuideControlComponent } from './brand-guide-control.component';

function guide(overrides: Partial<BrandGuideChoice> = {}): BrandGuideChoice {
  return { guideId: 'g1', name: 'Warm kitchen voice', versionNumber: 3, isStale: false, ...overrides };
}

interface Setup {
  status?: BrandGuideControlStatus;
  activeGuide?: BrandGuideChoice | null;
  choices?: BrandGuideChoice[];
  selection?: BrandGuideSelection;
  staleAcknowledged?: boolean;
  rules?: BrandGuideChannelRule[];
  rulesStatus?: BrandGuideRulesStatus;
}

describe('BrandGuideControlComponent', () => {
  let fixture: ComponentFixture<BrandGuideControlComponent>;

  async function render(setup: Setup = {}): Promise<HTMLElement> {
    fixture = TestBed.createComponent(BrandGuideControlComponent);
    const values: Required<Setup> = {
      status: 'ready',
      activeGuide: guide(),
      choices: [],
      selection: { kind: 'default' },
      staleAcknowledged: false,
      rules: [],
      rulesStatus: 'ready',
      ...setup,
    };
    for (const [key, value] of Object.entries(values)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    await fixture.whenStable();

    return fixture.nativeElement as HTMLElement;
  }

  const text = (host: HTMLElement) => host.textContent?.replace(/\s+/g, ' ') ?? '';

  beforeEach(() => TestBed.configureTestingModule({ imports: [BrandGuideControlComponent] }));

  it('defaults to "Use my brand voice", naming the guide and version', async () => {
    const host = await render();

    expect(text(host)).toContain('Using your brand voice: Warm kitchen voice, version 3.');
    expect(text(host)).toContain('Brand voice on');
  });

  it('is a named region', async () => {
    const host = await render();
    const region = host.querySelector('section')!;

    expect(host.querySelector(`#${region.getAttribute('aria-labelledby')}`)?.textContent).toContain('Brand voice');
  });

  it('describes an override as applying to this piece only', async () => {
    const other = guide({ guideId: 'g2', name: 'Weeknight voice', versionNumber: 2 });
    const host = await render({ choices: [other], selection: { kind: 'override', guideId: 'g2', versionNumber: 2 } });

    expect(text(host)).toContain('Using Weeknight voice, version 2, for this piece only.');
    expect(text(host)).toContain('This piece only');
  });

  it('describes the no-guide choice as a choice, not a warning', async () => {
    const host = await render({ selection: { kind: 'none' } });

    expect(text(host)).toContain('Writing without a brand voice.');
    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(host.querySelector('.rules')).toBeNull();
  });

  it('offers set-up when no guide exists, and says it will write without one', async () => {
    const host = await render({ activeGuide: null, choices: [] });
    let setUp = 0;
    fixture.componentInstance.setUpGuide.subscribe(() => setUp++);

    expect(text(host)).toContain('You have not set up a brand voice yet');
    const button = [...host.querySelectorAll('button')].find((b) => b.textContent?.includes('Set up'))!;
    button.click();

    expect(setUp).toBe(1);
  });

  describe('stale guide', () => {
    const stale = guide({ isStale: true, staleReason: 'A source document was replaced.' });

    it('warns with an alert and blocks until the creator continues explicitly', async () => {
      const host = await render({ activeGuide: stale });
      const alert = host.querySelector('[role="alert"]')!;

      expect(alert.textContent).toContain('may be out of date');
      expect(alert.textContent).toContain('A source document was replaced.');
    });

    it('falls back to a generic reason when none is supplied', async () => {
      const host = await render({ activeGuide: guide({ isStale: true }) });

      expect(host.querySelector('[role="alert"]')?.textContent).toContain('sources behind it have changed');
    });

    it('emits review and continue-anyway as separate, explicit actions', async () => {
      const host = await render({ activeGuide: stale });
      const events: string[] = [];
      fixture.componentInstance.reviewGuide.subscribe(() => events.push('review'));
      fixture.componentInstance.staleAcknowledgedChange.subscribe((v) => events.push(`ack:${v}`));

      const byName = (name: string) => [...host.querySelectorAll('button')].find((b) => b.textContent?.includes(name))!;
      byName('Review guide').click();
      byName('Continue anyway').click();

      expect(events).toEqual(['review', 'ack:true']);
    });

    it('stays visibly stale once accepted, and offers undo', async () => {
      const host = await render({ activeGuide: stale, staleAcknowledged: true });

      expect(host.querySelector('[role="alert"]')).toBeNull();
      expect(text(host)).toContain('You are continuing with an out-of-date guide.');
      expect(text(host)).toContain('Out of date');
      expect(text(host)).toContain('Undo');
    });
  });

  it('reports a chosen guide that has gone missing instead of substituting the active one', async () => {
    const host = await render({ selection: { kind: 'override', guideId: 'gone', versionNumber: 1 } });

    expect(text(host)).toContain('The guide you chose is no longer available.');
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('Choose a guide to continue');
  });

  describe('change selection', () => {
    const other = guide({ guideId: 'g2', name: 'Weeknight voice', versionNumber: 2 });

    function changeButton(host: HTMLElement): HTMLButtonElement {
      return [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Change')!;
    }

    it('keeps the picker closed until asked, with aria-expanded to say so', async () => {
      const host = await render({ choices: [other] });

      expect(host.querySelector('select')).toBeNull();
      expect(changeButton(host).getAttribute('aria-expanded')).toBe('false');

      changeButton(host).click();
      fixture.detectChanges();

      expect(changeButton(host).getAttribute('aria-expanded')).toBe('true');
      expect(host.querySelector('select')).not.toBeNull();
    });

    it('labels the select and lists default, other guides, and no brand voice', async () => {
      const host = await render({ choices: [other] });
      changeButton(host).click();
      fixture.detectChanges();

      const select = host.querySelector('select')!;
      const label = host.querySelector(`label[for="${select.id}"]`);
      const options = [...select.options].map((o) => o.textContent?.replace(/\s+/g, ' ').trim());

      expect(label?.textContent).toContain('Guide for this piece');
      expect(options).toEqual([
        'My brand voice: Warm kitchen voice, version 3 (default)',
        'Weeknight voice, version 2',
        'No brand voice',
      ]);
    });

    it('emits an override, clears any stale acceptance, and closes the picker', async () => {
      const host = await render({ choices: [other], staleAcknowledged: true });
      const events: unknown[] = [];
      fixture.componentInstance.selectionChange.subscribe((s) => events.push(s));
      fixture.componentInstance.staleAcknowledgedChange.subscribe((v) => events.push(`ack:${v}`));

      changeButton(host).click();
      fixture.detectChanges();
      const select = host.querySelector('select')!;
      select.value = 'g2@2';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      expect(events).toEqual(['ack:false', { kind: 'override', guideId: 'g2', versionNumber: 2 }]);
      expect(host.querySelector('select')).toBeNull();
    });

    it('emits the no-guide and default choices', async () => {
      const host = await render({ choices: [other] });
      const events: BrandGuideSelection[] = [];
      fixture.componentInstance.selectionChange.subscribe((s) => events.push(s));

      for (const value of ['none', 'default']) {
        changeButton(host).click();
        fixture.detectChanges();
        const select = host.querySelector('select')!;
        select.value = value;
        select.dispatchEvent(new Event('change'));
        fixture.detectChanges();
      }

      expect(events).toEqual([{ kind: 'none' }, { kind: 'default' }]);
    });

    it('marks a stale alternative in the option text, not by colour', async () => {
      const host = await render({ choices: [guide({ guideId: 'g2', name: 'Old', versionNumber: 1, isStale: true })] });
      changeButton(host).click();
      fixture.detectChanges();

      expect(text(host)).toContain('Old, version 1 (may be out of date)');
    });
  });

  describe('channel rules preview', () => {
    it('lists the rules in plain language', async () => {
      const host = await render({
        rules: [
          { channel: 'Blog', summary: 'Conversational, first person, ends with a call to action.' },
          { channel: 'Instagram', summary: 'Short and playful; up to five hashtags.' },
        ],
      });

      const items = [...host.querySelectorAll('.rules li')].map((li) => [
        li.querySelector('strong')?.textContent,
        li.querySelector('span')?.textContent,
      ]);

      expect(items).toEqual([
        ['Blog', 'Conversational, first person, ends with a call to action.'],
        ['Instagram', 'Short and playful; up to five hashtags.'],
      ]);
    });

    it('says so when there are no channel-specific rules', async () => {
      const host = await render();

      expect(text(host)).toContain('No channel-specific rules, so your general voice applies.');
    });

    it('degrades without hiding the guide when the rules cannot load', async () => {
      const host = await render({ rulesStatus: 'error' });

      expect(text(host)).toContain('We could not load the rules for this channel. Your guide still applies.');
      expect(text(host)).toContain('Using your brand voice');
    });
  });

  describe('details', () => {
    it('keeps provenance behind a collapsed disclosure', async () => {
      const host = await render({
        activeGuide: guide({ approvedAt: '2026-03-14T10:00:00Z', appliedSections: ['Tone', 'Vocabulary'] }),
      });
      const details = host.querySelector('details')!;

      expect(details.open).toBe(false);
      expect(details.querySelector('summary')?.textContent).toContain('Guide details');
      expect(text(details as HTMLElement)).toContain('Tone, Vocabulary');
      expect(text(details as HTMLElement)).toContain('Mar 14, 2026');
    });

    it('never renders raw context, prompt or storage vocabulary', async () => {
      const host = await render({ activeGuide: guide({ appliedSections: ['Tone'] }) });

      expect(text(host)).not.toMatch(/chunk|prompt|embedding|blob|token|checksum|provider/i);
    });
  });

  describe('loading and error', () => {
    it('is busy while loading and shows no guide claims', async () => {
      const host = await render({ status: 'loading' });

      expect(host.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
      expect(text(host)).toContain('Checking your brand voice');
      expect(text(host)).not.toContain('Using your brand voice');
    });

    it('offers a retry on error and keeps the creator reassured', async () => {
      const host = await render({ status: 'error' });
      let retried = 0;
      fixture.componentInstance.retry.subscribe(() => retried++);

      expect(host.querySelector('[role="alert"]')?.textContent).toContain('Nothing you have typed has been lost');
      [...host.querySelectorAll('button')].find((b) => b.textContent?.includes('Try again'))!.click();

      expect(retried).toBe(1);
    });
  });

  it('gives every interactive control an accessible name', async () => {
    const host = await render({ activeGuide: guide({ isStale: true }), choices: [guide({ guideId: 'g2' })] });

    for (const button of host.querySelectorAll('button')) {
      expect(button.textContent?.trim().length).toBeGreaterThan(0);
      expect(button.getAttribute('type')).toBe('button');
    }
  });
});
