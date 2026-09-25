import { TestBed } from '@angular/core/testing';

import { LandingTrustComponent } from './landing-trust.component';

describe('LandingTrustComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingTrustComponent],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingTrustComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the mission, audience, stage, and proof-points copy', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('.mission')?.textContent).toContain('real production workspace');
    expect(el.querySelector('.audience')?.textContent).toContain('food bloggers and content creators');
    expect(el.querySelector('.stage')?.textContent).toContain('early development');
    expect(el.querySelector('.proof-note')?.textContent).toContain("don’t have customer stories");
  });

  it('contains no testimonial, rating, or customer-count markup', async () => {
    const fixture = await createFixture();
    const text = fixture.nativeElement.textContent as string;

    expect(text).not.toMatch(/★|⭐|customers|users trust|rated/i);
  });

  it('renders custom copy supplied via inputs', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('mission', 'Custom mission');
    fixture.componentRef.setInput('stage', 'Custom stage');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.mission')?.textContent).toContain('Custom mission');
    expect(el.querySelector('.stage')?.textContent).toContain('Custom stage');
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
