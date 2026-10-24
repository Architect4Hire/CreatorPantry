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

    expect(el.querySelector('h1')?.textContent?.trim()).toBe('The workspace where your recipes become everything else.');
    expect(el.querySelector('.subheadline')?.textContent).toContain('Write and version your recipes');
    expect(el.querySelector('.note')?.textContent).toContain('Your workspace’s Owner switches on the features you use.');
  });

  it('keeps the AI-as-proposal wording and claims no self-serve publishing', async () => {
    const fixture = await createFixture();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('nothing changes until you accept it');
    expect(text).not.toMatch(/publish/i);
  });

  it('underlines only the highlighted phrase, and the heading still reads as one sentence', async () => {
    const fixture = await createFixture();
    const heading = (fixture.nativeElement as HTMLElement).querySelector('h1')!;
    const marks = heading.querySelectorAll('.highlight');

    expect(marks.length).toBe(1);
    expect(marks[0].textContent).toBe('everything else.');
    expect(marks[0].getAttribute('aria-hidden')).toBeNull();
    expect(heading.textContent?.replace(/\s+/g, ' ').trim()).toBe('The workspace where your recipes become everything else.');
  });

  it('marks nothing when the headline does not contain the highlighted phrase', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('headline', 'A different headline');
    fixture.detectChanges();

    const heading = (fixture.nativeElement as HTMLElement).querySelector('h1')!;
    expect(heading.querySelector('.highlight')).toBeNull();
    expect(heading.textContent?.trim()).toBe('A different headline');
  });

  it('omits the note when it is cleared', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('note', '');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.note')).toBeNull();
  });

  it('puts the logo first and asks for it first, named once for assistive technology', async () => {
    const fixture = await createFixture();
    const copy = (fixture.nativeElement as HTMLElement).querySelector('.copy')!;
    const logo = copy.firstElementChild!;
    const img = logo.querySelector('img')!;

    expect(logo.tagName.toLowerCase()).toBe('cp-logo');
    expect(img.getAttribute('fetchpriority')).toBe('high');
    expect(img.getAttribute('alt')).toBe('CreatorPantry');
    expect(img.getAttribute('width')).toBeTruthy();
    expect(img.getAttribute('height')).toBeTruthy();
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
