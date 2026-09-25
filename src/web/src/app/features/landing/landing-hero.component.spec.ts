import { Component } from '@angular/core';
import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { LandingHeroComponent } from './landing-hero.component';

@Component({
  standalone: true,
  imports: [LandingHeroComponent],
  template: `<cp-landing-hero><img cpLandingHeroMedia src="hero.png" alt="A finished recipe card" /></cp-landing-hero>`,
})
class HostWithMediaComponent {}

describe('LandingHeroComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingHeroComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingHeroComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the default approved copy', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('h1')?.textContent).toContain('The workspace where your recipes become everything else.');
    expect(el.querySelector('.subheadline')?.textContent).toContain('Develop and version recipes');
  });

  it('renders custom copy supplied via inputs', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('headline', 'Custom headline');
    fixture.componentRef.setInput('subheadline', 'Custom subheadline');
    fixture.componentRef.setInput('primaryCtaLabel', 'Join now');
    fixture.componentRef.setInput('secondaryCtaLabel', 'Log in');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('h1')?.textContent).toContain('Custom headline');
    expect(el.querySelector('.subheadline')?.textContent).toContain('Custom subheadline');
    expect(el.querySelector('.ctas a')?.textContent).toContain('Join now');
    expect(el.querySelectorAll('.ctas a')[1]?.textContent).toContain('Log in');
  });

  it('links the primary CTA to /sign-up and the secondary CTA to /sign-in', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll<HTMLAnchorElement>('.ctas a');

    expect(links[0].getAttribute('href')).toBe('/sign-up');
    expect(links[1].getAttribute('href')).toBe('/sign-in');
  });

  it("labels the section by the page's h1, giving it an accessible name", async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const section = el.querySelector('section')!;
    const heading = el.querySelector('h1')!;

    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(heading.id).toBeTruthy();
  });

  it('renders nothing in the media slot when none is projected', async () => {
    const fixture = await createFixture();
    const media = fixture.nativeElement.querySelector('.media');

    expect(media?.textContent?.trim()).toBe('');
    expect(media?.children.length).toBe(0);
  });

  it('renders projected content into the media slot when provided', async () => {
    await TestBed.configureTestingModule({
      imports: [HostWithMediaComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(HostWithMediaComponent);
    fixture.detectChanges();

    const media = fixture.nativeElement.querySelector('.media');
    expect(media?.querySelector('img')).toBeTruthy();
  });
});
