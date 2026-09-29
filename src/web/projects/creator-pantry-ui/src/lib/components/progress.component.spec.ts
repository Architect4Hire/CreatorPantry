import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpProgressComponent, CpProgressTone } from './progress.component';

describe('CpProgressComponent', () => {
  let fixture: ComponentFixture<CpProgressComponent>;

  beforeEach(() => TestBed.configureTestingModule({ imports: [CpProgressComponent] }));

  async function render(inputs: { value?: number; label?: string; valueText?: string; tone?: CpProgressTone }) {
    await TestBed.compileComponents();

    fixture = TestBed.createComponent(CpProgressComponent);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    return { host, bar: host.querySelector('[role="progressbar"]') as HTMLElement };
  }

  it('prints a percentage when no value text is supplied', async () => {
    const { host, bar } = await render({ value: 36 });

    expect(host.querySelector('.meta strong')?.textContent?.trim()).toBe('36%');

    // Nothing to override the percentage, so nothing is announced beyond aria-valuenow.
    expect(bar.getAttribute('aria-valuetext')).toBeNull();
  });

  /**
   * The reason the input exists: "640 credits left" is the figure a creator with an allowance wants, and a
   * screen reader should hear the same words the sighted reader sees rather than a fraction.
   */
  it('prints and announces the supplied value text instead of a percentage', async () => {
    const { host, bar } = await render({ value: 36, valueText: '640 credits left' });

    expect(host.querySelector('.meta strong')?.textContent?.trim()).toBe('640 credits left');
    expect(bar.getAttribute('aria-valuetext')).toBe('640 credits left');

    // The bar still fills by the number, so the visual and the text cannot disagree about how full it is.
    expect(bar.getAttribute('aria-valuenow')).toBe('36');
  });

  for (const tone of ['default', 'success', 'warning', 'error'] as const) {
    it(`reflects the '${tone}' tone on the host so the fill can be themed`, async () => {
      const { host } = await render({ value: 50, tone });

      expect(host.getAttribute('data-tone')).toBe(tone);
    });
  }

  /**
   * The not-colour-alone contract, asserted as an absence: a tone changes the fill and <strong>nothing
   * else</strong>. No glyph appears, no text changes, no ARIA changes — so a consumer that relied on the
   * tone to say something would be saying it in colour only, and this test is what makes that visible.
   */
  it('carries no meaning in the tone beyond the fill colour', async () => {
    const quiet = await render({ value: 50, label: 'Allowance', valueText: '5 left' });
    const loud = await render({ value: 50, label: 'Allowance', valueText: '5 left', tone: 'error' });

    expect(loud.host.textContent).toBe(quiet.host.textContent);
    expect(loud.bar.getAttribute('aria-valuetext')).toBe(quiet.bar.getAttribute('aria-valuetext'));
    expect(loud.bar.getAttribute('aria-valuenow')).toBe(quiet.bar.getAttribute('aria-valuenow'));
  });

  it('clamps the fill to the accessible range while printing what it was told', async () => {
    const { host, bar } = await render({ value: 140 });

    expect(bar.getAttribute('aria-valuenow')).toBe('100');
    expect(host.querySelector('.meta strong')?.textContent?.trim()).toBe('140%');
  });
});
