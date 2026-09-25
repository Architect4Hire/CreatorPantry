import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { LandingComponent } from './landing.component';

describe('LandingComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('composes all five landing sections, in order, inside <main>', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const main = el.querySelector('main')!;
    const mainChildren = Array.from(main.children).map((child) => child.tagName.toLowerCase());

    expect(mainChildren).toEqual([
      'cp-landing-hero',
      'cp-landing-value-props',
      'cp-landing-how-it-works',
      'cp-landing-trust',
      'cp-landing-final-cta',
    ]);
  });

  it('keeps the footer outside <main> so it stays a top-level landmark', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const main = el.querySelector('main')!;

    expect(main.querySelector('footer')).toBeFalsy();
    expect(el.querySelector('footer')).toBeTruthy();
    expect(el.querySelector('cp-landing-footer')?.parentElement?.tagName.toLowerCase()).not.toBe('main');
  });

  it('renders exactly one page heading (h1) from the hero section', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelectorAll('h1').length).toBe(1);
  });
});
