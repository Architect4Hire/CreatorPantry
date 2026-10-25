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

  it('links to the three legal pages, in order', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const links = Array.from(el.querySelectorAll<HTMLAnchorElement>('a.legal-link'));

    expect(links.map((link) => link.textContent?.trim())).toEqual([
      'Privacy Policy',
      'Terms of Service',
      'Acceptable Use Policy',
    ]);
    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/privacy', '/terms', '/acceptable-use']);
    expect(el.querySelector('button')).toBeNull();
  });

  it('renders custom product name and legal links supplied via inputs', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('productName', 'Custom Co');
    fixture.componentRef.setInput('legalLinks', [{ label: 'Cookie Policy', path: '/cookies' }]);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.brand')?.textContent).toContain('Custom Co');
    expect(el.querySelectorAll('.legal-link').length).toBe(1);
    expect(el.querySelector('.legal-link')?.textContent).toContain('Cookie Policy');
    expect(el.querySelector('.legal-link')?.getAttribute('href')).toBe('/cookies');
  });

  /**
   * The empty `<nav aria-label="Footer">` this once rendered was a landmark holding nothing (LP.1 finding 8).
   * The one navigation it has now is named for what is in it, and is never empty.
   */
  it('has one navigation landmark, named Legal, with links in it', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const navs = el.querySelectorAll('nav');

    expect(navs.length).toBe(1);
    expect(navs[0].getAttribute('aria-label')).toBe('Legal');
    expect(navs[0].querySelectorAll('a').length).toBeGreaterThan(0);
    expect(navs[0].closest('footer')).not.toBeNull();
  });
});
