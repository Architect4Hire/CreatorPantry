import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';

import { CpButtonComponent } from './components/button.component';
import { CpProgressComponent } from './components/progress.component';
import { CpThemeService } from './theme.service';

function relativeLuminance(hex: string): number {
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(hex.slice(i + 1, i + 3), 16) / 255);
  const linear = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function contrastRatio(hexA: string, hexB: string): number {
  const [lighter, darker] = [relativeLuminance(hexA), relativeLuminance(hexB)].sort((a, b) => b - a);
  return (lighter + 0.05) / (darker + 0.05);
}

function readTokens(theme: 'light' | 'dark', names: string[]): Record<string, string> {
  const probe = document.createElement('div');
  probe.setAttribute('data-cp-theme', theme);
  document.body.appendChild(probe);
  const computed = getComputedStyle(probe);
  const values = Object.fromEntries(names.map((name) => [name, computed.getPropertyValue(name).trim()]));
  probe.remove();
  return values;
}

describe('CreatorPantry UI library', () => {
  it('applies the configured button variant and size', async () => {
    await TestBed.configureTestingModule({ imports: [CpButtonComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpButtonComponent);
    fixture.componentRef.setInput('variant', 'secondary');
    fixture.componentRef.setInput('size', 'lg');
    fixture.detectChanges();

    const button = fixture.nativeElement as HTMLButtonElement;
    expect(button.classList).toContain('cp-button--secondary');
    expect(button.classList).toContain('cp-button--lg');
  });

  it('clamps progress values to the accessible zero-to-one-hundred range', async () => {
    await TestBed.configureTestingModule({ imports: [CpProgressComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpProgressComponent);
    fixture.componentRef.setInput('value', 140);
    fixture.detectChanges();

    const progress = fixture.nativeElement.querySelector('[role="progressbar"]') as HTMLElement;
    expect(progress.getAttribute('aria-valuenow')).toBe('100');
  });

  it('toggles the documentElement data-cp-theme attribute and persists the choice', () => {
    const service = TestBed.inject(CpThemeService);
    const document = TestBed.inject(DOCUMENT);

    service.set('light');
    expect(document.documentElement.dataset['cpTheme']).toBe('light');

    service.toggle();
    expect(document.documentElement.dataset['cpTheme']).toBe('dark');
    expect(document.defaultView?.localStorage.getItem('creator-pantry-theme')).toBe('dark');
  });
});

describe('CreatorPantry design tokens', () => {
  it('keeps the z-index scale ordered topbar < sidebar < overlay < modal', () => {
    const { '--cp-z-topbar': topbar, '--cp-z-sidebar': sidebar, '--cp-z-overlay': overlay, '--cp-z-modal': modal } =
      readTokens('light', ['--cp-z-topbar', '--cp-z-sidebar', '--cp-z-overlay', '--cp-z-modal']);

    expect(Number(topbar)).toBeLessThan(Number(sidebar));
    expect(Number(sidebar)).toBeLessThan(Number(overlay));
    expect(Number(overlay)).toBeLessThan(Number(modal));
  });

  for (const theme of ['light', 'dark'] as const) {
    it(`meets WCAG AA (4.5:1) for text-on-accent pairs in the ${theme} theme`, () => {
      const tokens = readTokens(theme, [
        '--cp-primary', '--cp-primary-soft', '--cp-pink', '--cp-pink-soft',
        '--cp-orange', '--cp-orange-soft', '--cp-blue', '--cp-blue-soft',
        '--cp-danger', '--cp-ink-on-accent', '--cp-text-faint', '--cp-bg',
        '--cp-text-muted', '--cp-surface-subtle',
      ]);

      expect(contrastRatio(tokens['--cp-primary'], tokens['--cp-primary-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-pink'], tokens['--cp-pink-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-orange'], tokens['--cp-orange-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-blue'], tokens['--cp-blue-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-ink-on-accent'], tokens['--cp-danger'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-text-faint'], tokens['--cp-bg'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-text-muted'], tokens['--cp-surface-subtle'])).toBeGreaterThanOrEqual(4.5);
    });
  }
});
