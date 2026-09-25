import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { LandingFinalCtaComponent } from './landing-final-cta.component';

describe('LandingFinalCtaComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingFinalCtaComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingFinalCtaComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('links the primary CTA to /sign-up and the secondary CTA to /sign-in', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll<HTMLAnchorElement>('.ctas a');

    expect(links[0].getAttribute('href')).toBe('/sign-up');
    expect(links[1].getAttribute('href')).toBe('/sign-in');
  });

  it('renders custom copy supplied via inputs', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('heading', 'Custom heading');
    fixture.componentRef.setInput('primaryCtaLabel', 'Join now');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('h2')?.textContent).toContain('Custom heading');
    expect(el.querySelector('.ctas a')?.textContent).toContain('Join now');
  });

  it("labels the section by its own heading, giving it an accessible name", async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const section = el.querySelector('section')!;
    const heading = el.querySelector('h2')!;

    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(heading.id).toBeTruthy();
  });
});
