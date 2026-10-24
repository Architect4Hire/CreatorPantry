import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CpThemeService } from '@creator-pantry/ui';

import { CpLogoComponent, LOGO_HEIGHT, LOGO_WIDTH } from './logo.component';

describe('CpLogoComponent', () => {
  let fixture: ComponentFixture<CpLogoComponent>;
  let resolvedTheme: ReturnType<typeof signal<'light' | 'dark'>>;

  function img(): HTMLImageElement {
    return fixture.nativeElement.querySelector('img') as HTMLImageElement;
  }

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
    expect(img().getAttribute('alt')).toBe('CreatorPantry');
  });

  it('reserves its box with the intrinsic width and height, so it shifts nothing as it loads', () => {
    expect(img().getAttribute('width')).toBe(String(LOGO_WIDTH));
    expect(img().getAttribute('height')).toBe(String(LOGO_HEIGHT));
  });

  it('offers the 200w and 400w WebP renditions for the light theme', () => {
    expect(img().getAttribute('src')).toBe('/images/logo-400.webp');
    expect(img().getAttribute('srcset')).toBe('/images/logo-200.webp 200w, /images/logo-400.webp 400w');
    expect(img().getAttribute('sizes')).toBe('12.5rem');
  });

  it('swaps to the dark renditions when the resolved theme is dark', () => {
    resolvedTheme.set('dark');
    fixture.detectChanges();

    expect(img().getAttribute('src')).toBe('/images/logodark-400.webp');
    expect(img().getAttribute('srcset')).toBe('/images/logodark-200.webp 200w, /images/logodark-400.webp 400w');
  });

  it('asks for the image first only when it is the priority', () => {
    expect(img().getAttribute('fetchpriority')).toBeNull();
    expect(img().getAttribute('loading')).toBeNull();

    fixture.componentRef.setInput('priority', true);
    fixture.detectChanges();

    expect(img().getAttribute('fetchpriority')).toBe('high');
    expect(img().getAttribute('loading')).toBe('eager');
  });

  it('passes the consumer’s shown width through as sizes', () => {
    fixture.componentRef.setInput('sizes', '6.5rem');
    fixture.detectChanges();

    expect(img().getAttribute('sizes')).toBe('6.5rem');
  });
});
