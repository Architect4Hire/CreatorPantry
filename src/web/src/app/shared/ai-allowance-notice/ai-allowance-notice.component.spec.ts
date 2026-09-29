import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { AiAllowanceState, AllowanceFigures } from '../../services/ai-usage.service';
import { AiAllowanceNoticeComponent } from './ai-allowance-notice.component';

function figures(overrides: Partial<AllowanceFigures> = {}): AllowanceFigures {
  return {
    unit: 'Credits',
    allowance: 1000,
    remaining: 120,
    consumed: 880,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-04-01T00:00:00+01:00',
    timeZoneId: 'Europe/London',
    usedPercent: 88,
    ...overrides,
  };
}

describe('AiAllowanceNoticeComponent', () => {
  let fixture: ComponentFixture<AiAllowanceNoticeComponent>;

  async function render(allowance: AiAllowanceState, refusal: AiQuotaRefusal | null = null): Promise<HTMLElement> {
    fixture = TestBed.createComponent(AiAllowanceNoticeComponent);
    fixture.componentRef.setInput('allowance', allowance);
    fixture.componentRef.setInput('refusal', refusal);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [AiAllowanceNoticeComponent] }));

  /**
   * The states where the app does not know, or knows everything is fine. A warning surface that spoke here
   * would be one a creator learns to ignore by the time it matters.
   */
  for (const allowance of [
    { kind: 'unknown' } as const,
    { kind: 'healthy', period: figures({ remaining: 900 }) } as const,
    { kind: 'degraded', period: figures() } as const,
  ]) {
    it(`says nothing when the allowance is '${allowance.kind}'`, async () => {
      const host = await render(allowance);

      expect(host.textContent?.trim()).toBe('');
      expect(host.querySelector('[role="status"]')).toBeNull();
      expect(host.querySelector('[role="alert"]')).toBeNull();
    });
  }

  it('warns politely when the allowance is nearly spent, and names what is left and when it returns', async () => {
    const host = await render({ kind: 'nearly-spent', period: figures({ remaining: 120 }) });
    const notice = host.querySelector('[role="status"]');

    expect(notice).not.toBeNull();
    expect(host.querySelector('[role="alert"]')).toBeNull();
    expect(notice?.textContent).toContain('120 AI credits');

    // Day and month asserted separately: the order is the browser locale's to choose, and the test should
    // not fail in en-US for printing "April 1" rather than "1 April". Which day it is, is the point.
    expect(notice?.textContent).toContain('April');
    expect(notice?.textContent).toContain('1');
  });

  it('states a spent allowance and what still works', async () => {
    const host = await render({ kind: 'exhausted', period: figures({ remaining: 0 }) });
    const notice = host.querySelector('[role="status"]');

    expect(notice?.textContent).toContain('spent');
    expect(notice?.textContent).toContain('keep writing and editing recipes');
    expect(notice?.textContent).toContain('April');
  });

  /**
   * The distinction that matters: an allowance comes back on its own, a suspension does not. Promising a
   * reset to a suspended creator would leave them waiting for something that never arrives.
   */
  it('tells a suspended account something different, and promises no reset', async () => {
    const host = await render({ kind: 'suspended', period: figures() });
    const notice = host.querySelector('[role="status"]');

    expect(notice?.textContent).toContain('switched off');
    expect(notice?.textContent).toContain('Get in touch');
    expect(notice?.textContent).not.toContain('comes back');
    expect(notice?.textContent).not.toContain('April');
  });

  it('announces an actual refusal assertively, with what the request needed', async () => {
    const host = await render({ kind: 'healthy', period: figures({ remaining: 900 }) }, {
      status: 'quota_exhausted',
      unit: 'Credits',
      allowance: 1000,
      remaining: 5,
      required: 20,
      resetsAt: '2026-04-01T00:00:00+01:00',
    });

    const alert = host.querySelector('[role="alert"]');
    expect(alert).not.toBeNull();
    expect(host.querySelector('[role="status"]')).toBeNull();
    expect(alert?.textContent).toContain('20 AI credits');
    expect(alert?.textContent).toContain('5 AI credits');
    expect(alert?.textContent).toContain('Nothing you have typed has been lost');
  });

  it('prefers the refusal over the pre-flight warning', async () => {
    const host = await render({ kind: 'nearly-spent', period: figures() }, { status: 'account_suspended', unit: 'Credits' });

    expect(host.querySelector('[role="alert"]')?.textContent).toContain('switched off');
    expect(host.querySelector('[role="status"]')).toBeNull();
  });

  /**
   * EASE-004: a creator reads credits and a date. The words below are the ones the plumbing uses, and none
   * of them belongs on screen.
   */
  for (const jargon of ['token', 'provider', 'deployment', 'model', 'quota', 'reservation']) {
    it(`never says '${jargon}'`, async () => {
      const states: AiAllowanceState[] = [
        { kind: 'nearly-spent', period: figures() },
        { kind: 'exhausted', period: figures({ remaining: 0 }) },
        { kind: 'suspended', period: figures() },
      ];

      for (const state of states) {
        const host = await render(state);
        expect(host.textContent?.toLowerCase()).not.toContain(jargon);
      }

      const refused = await render({ kind: 'unknown' }, {
        status: 'quota_exhausted',
        unit: 'Credits',
        allowance: 1000,
        remaining: 5,
        required: 20,
        resetsAt: '2026-04-01T00:00:00+01:00',
      });
      expect(refused.textContent?.toLowerCase()).not.toContain(jargon);
    });
  }

  /**
   * Status is never colour alone: strip the styling and every state still names itself in words, with a
   * glyph beside it.
   */
  it('names every state in words with a glyph, not in colour', async () => {
    for (const [state, label] of [
      [{ kind: 'nearly-spent', period: figures() } as const, 'Nearly spent'],
      [{ kind: 'exhausted', period: figures({ remaining: 0 }) } as const, 'Spent'],
      [{ kind: 'suspended', period: figures() } as const, 'Switched off'],
    ] as const) {
      const host = await render(state);
      const pill = host.querySelector('cp-status-pill');

      expect(pill?.textContent).toContain(label);
      expect(pill?.querySelector('.icon')?.getAttribute('aria-hidden')).toBe('true');
    }
  });

  /**
   * <strong>The reset boundary.</strong> This instant is the first moment of 1 April in Auckland and still
   * 31 March everywhere west of it. The date a creator is shown must be the one their own allowance turns
   * over on, so it is formatted in the stored zone and not in whatever zone the browser happens to sit in.
   */
  it('names the reset day in the account zone, not the browser zone', async () => {
    const auckland = await render({
      kind: 'nearly-spent',
      period: figures({ resetsAt: '2026-03-31T11:00:00Z', timeZoneId: 'Pacific/Auckland' }),
    });

    expect(auckland.textContent).toContain('April');
    expect(auckland.textContent).not.toContain('March');

    // Same instant, a zone still in March. Different day, correctly.
    const london = await render({
      kind: 'nearly-spent',
      period: figures({ resetsAt: '2026-03-31T11:00:00Z', timeZoneId: 'Europe/London' }),
    });

    expect(london.textContent).toContain('March');
    expect(london.textContent).not.toContain('April');
  });

  /** A reset the browser cannot place in the account's calendar is not guessed at. */
  it('falls back to wording that promises no date when the zone is unknown', async () => {
    const host = await render({ kind: 'nearly-spent', period: figures({ timeZoneId: 'Mars/Olympus_Mons' }) });

    expect(host.textContent).toContain('when the period resets');
    expect(host.textContent).not.toContain('Invalid');
  });
});
