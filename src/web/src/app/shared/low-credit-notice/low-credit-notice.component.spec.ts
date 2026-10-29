import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AiLowCreditNotice } from '../../services/ai-usage.service';
import { PACK_PURCHASE_LINK } from '../pack-purchase-link';
import { LowCreditNoticeComponent } from './low-credit-notice.component';

function notice(overrides: Partial<AiLowCreditNotice> = {}): AiLowCreditNotice {
  return {
    spendable: 90,
    unit: 'Credits',
    resetsAt: '2026-04-01T00:00:00+01:00',
    timeZoneId: 'Europe/London',
    allowanceRenews: true,
    ...overrides,
  };
}

describe('LowCreditNoticeComponent', () => {
  let fixture: ComponentFixture<LowCreditNoticeComponent>;

  async function render(value: AiLowCreditNotice | null, offerPack = true): Promise<HTMLElement> {
    fixture = TestBed.createComponent(LowCreditNoticeComponent);
    fixture.componentRef.setInput('notice', value);
    fixture.componentRef.setInput('offerPack', offerPack);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [LowCreditNoticeComponent],
      providers: [provideRouter([])],
    }),
  );

  it('says what is left, when it comes back, and what a pack does', async () => {
    const host = await render(notice());
    const text = host.textContent ?? '';

    expect(text).toContain('Your AI credits are running low');
    expect(text).toContain('90 AI credits left');
    expect(text).toContain('your allowance and your packs together');
    // The month and day as this browser writes them, rather than a fixed order: the allowance page's own
    // formatter is shared, and the point here is that the reset is said at all.
    expect(text).toMatch(/April\s*1|1\s*April/);
    expect(text).toContain('pack credits don’t expire');
  });

  /**
   * The promise the landing page makes, kept by there being no other button: the one action is a link to
   * where a creator buys a pack themselves (B-59).
   */
  it('offers a link to buy a pack and nothing that buys one', async () => {
    const host = await render(notice());
    const link = host.querySelector('a')!;

    expect(link.textContent?.trim()).toBe('Get a credit pack');
    expect(link.getAttribute('href')).toBe(PACK_PURCHASE_LINK);

    const buttons = Array.from(host.querySelectorAll('button'));
    expect(buttons.length).toBe(1);
    expect(buttons[0].textContent?.trim()).toBe('Dismiss the low-credit notice');
  });

  /** Sending somebody to the page they are reading is a loop, not an offer. */
  it('leaves the link out where it would point at the page showing it', async () => {
    const host = await render(notice(), false);

    expect(host.querySelector('a')).toBeNull();
    expect(host.textContent).toContain('90 AI credits left');
  });

  it('asks to be dismissed rather than dismissing itself', async () => {
    let dismissed = 0;
    const host = await render(notice());
    fixture.componentInstance.dismissed.subscribe(() => (dismissed += 1));

    host.querySelector<HTMLButtonElement>('button')!.click();

    expect(dismissed).toBe(1);
    // Still on screen: what happens next is the caller's, and the notice does not decide it.
    expect(host.textContent).toContain('Your AI credits are running low');
  });

  /**
   * A trial's credits are granted once (B-48). Promising them back on a date would tell somebody to wait
   * for something that is not coming.
   */
  it('does not promise a trial’s credits back', async () => {
    const host = await render(notice({ allowanceRenews: false }));

    expect(host.textContent).toContain('granted once and don’t renew');
    expect(host.textContent).not.toContain('comes back on');
  });

  /** A unit this product does not quote figures in is described rather than counted (EASE-004). */
  it('describes rather than counts an allowance it cannot quote', async () => {
    const host = await render(notice({ unit: 'Tokens', spendable: 1500 }));

    expect(host.textContent).toContain('not much left to spend');
    expect(host.textContent).not.toContain('1,500');
  });

  /**
   * The region is in the page before it has anything to say, so a notice arriving part-way through somebody's
   * work is announced rather than inserted with its text already in place.
   */
  it('keeps a quiet live region when there is nothing to say', async () => {
    const host = await render(null);
    const region = host.querySelector('cp-notice')!;

    expect(region).not.toBeNull();
    expect(region.getAttribute('role')).toBe('status');
    expect(region.classList).toContain('cp-notice--quiet');
    expect(host.textContent?.trim()).toBe('');
  });
});
