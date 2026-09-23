import { TestBed } from '@angular/core/testing';

import { CpStatusPillComponent, CpStatusPillTone } from './status-pill.component';

describe('CpStatusPillComponent', () => {
  const tones: { tone: CpStatusPillTone; icon: string }[] = [
    { tone: 'neutral', icon: '○' },
    { tone: 'progress', icon: '◐' },
    { tone: 'success', icon: '✓' },
    { tone: 'warning', icon: '▲' },
    { tone: 'error', icon: '✕' },
    { tone: 'stale', icon: '↻' },
  ];

  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [CpStatusPillComponent] }).compileComponents();
    return TestBed.createComponent(CpStatusPillComponent);
  }

  it('renders projected text content', async () => {
    const fixture = await createFixture();
    fixture.nativeElement.textContent = '';
    const host = fixture.nativeElement as HTMLElement;
    host.append('Published');
    fixture.detectChanges();

    expect(host.textContent).toContain('Published');
  });

  for (const { tone, icon } of tones) {
    it(`renders the default aria-hidden glyph for the '${tone}' tone`, async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('tone', tone);
      fixture.detectChanges();

      const iconEl = fixture.nativeElement.querySelector('.icon') as HTMLElement;
      expect(iconEl).toBeTruthy();
      expect(iconEl.getAttribute('aria-hidden')).toBe('true');
      expect(iconEl.textContent).toBe(icon);
    });

    it(`reflects the '${tone}' tone on the host data-tone attribute`, async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('tone', tone);
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).getAttribute('data-tone')).toBe(tone);
    });
  }

  it('suppresses the glyph when iconHidden is true but keeps the projected text', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    host.append('Needs review');
    fixture.componentRef.setInput('iconHidden', true);
    fixture.detectChanges();

    expect(host.querySelector('.icon')).toBeNull();
    expect(host.textContent).toContain('Needs review');
  });

  it('uses the icon input override instead of the tone default glyph', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('tone', 'success');
    fixture.componentRef.setInput('icon', '★');
    fixture.detectChanges();

    const iconEl = fixture.nativeElement.querySelector('.icon') as HTMLElement;
    expect(iconEl.textContent).toBe('★');
  });
});
