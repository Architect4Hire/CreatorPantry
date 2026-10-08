import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CpThemeService } from '@creator-pantry/ui';

import { CpLogoComponent } from './logo';

describe('CpLogoComponent', () => {
  let fixture: ComponentFixture<CpLogoComponent>;
  let resolvedTheme: ReturnType<typeof signal<'light' | 'dark'>>;

  beforeEach(async () => {
    resolvedTheme = signal<'light' | 'dark'>('light');

    await TestBed.configureTestingModule({
      imports: [CpLogoComponent],
      providers: [{ provide: CpThemeService, useValue: { resolved: resolvedTheme } }],
    }).compileComponents();

    fixture = TestBed.createComponent(CpLogoComponent);
    fixture.detectChanges();
  });

  it('names the brand for assistive technology', () => {
    const img = fixture.nativeElement.querySelector('img') as HTMLImageElement;

    expect(img.getAttribute('alt')).toBe('CreatorPantry');
  });

  it('swaps to the dark logo when the resolved theme is dark', () => {
    const img = fixture.nativeElement.querySelector('img') as HTMLImageElement;

    expect(img.getAttribute('src')).toBe('/images/logo.png');

    resolvedTheme.set('dark');
    fixture.detectChanges();

    expect(img.getAttribute('src')).toBe('/images/logodark.png');
  });

  it('applies the supplied inline styles to the image', () => {
    fixture.componentRef.setInput('styles', 'height: 500px');
    fixture.detectChanges();

    const img = fixture.nativeElement.querySelector('img') as HTMLImageElement;

    expect(img.style.height).toBe('500px');
  });
});
