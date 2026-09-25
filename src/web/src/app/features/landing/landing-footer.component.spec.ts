import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { LandingFooterComponent } from './landing-footer.component';

describe('LandingFooterComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingFooterComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingFooterComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the product name and the current year', async () => {
    const fixture = await createFixture();
    const brand = fixture.nativeElement.querySelector('.brand');

    expect(brand?.textContent).toContain('CreatorPantry');
    expect(brand?.textContent).toContain(String(new Date().getFullYear()));
  });

  it('links to /sign-in', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const footerSignIn = el.querySelector<HTMLAnchorElement>('.footer-links a');

    expect(footerSignIn?.getAttribute('href')).toBe('/sign-in');
  });

  it('gives the sign-in link at least a 40px touch target', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const link = el.querySelector<HTMLAnchorElement>('.footer-links a')!;
    const minHeight = getComputedStyle(link).minHeight;

    // jsdom-free (real Chrome via Karma) computed style; 2.5rem == 40px at the default 16px root.
    expect(parseFloat(minHeight)).toBeGreaterThanOrEqual(40);
  });

  it('renders the placeholder legal links as visibly disabled, not fabricated real links, outside the Footer nav', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const nav = el.querySelector('nav')!;
    const legalLinks = el.querySelectorAll<HTMLButtonElement>('.legal-link');

    expect(legalLinks.length).toBe(2);
    legalLinks.forEach((link) => {
      expect(link.tagName).toBe('BUTTON');
      expect(link.disabled).toBeTrue();
      expect(link.textContent).toContain('coming soon');
    });
    expect(legalLinks[0].textContent).toContain('Privacy Policy');
    expect(legalLinks[1].textContent).toContain('Terms of Service');
    expect(nav.querySelector('.legal-link')).toBeFalsy();
  });

  it('renders custom product name and legal links supplied via inputs', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('productName', 'Custom Co');
    fixture.componentRef.setInput('legalLinks', ['Cookie Policy']);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.brand')?.textContent).toContain('Custom Co');
    expect(el.querySelectorAll('.legal-link').length).toBe(1);
    expect(el.querySelector('.legal-link')?.textContent).toContain('Cookie Policy');
  });

  it('labels the footer navigation region, containing only the real sign-in link', async () => {
    const fixture = await createFixture();
    const nav = fixture.nativeElement.querySelector('nav');

    expect(nav?.getAttribute('aria-label')).toBe('Footer');
    expect(nav?.querySelectorAll('a').length).toBe(1);
  });
});
