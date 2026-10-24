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

  it('renders the placeholder legal links as visibly disabled, not fabricated real links', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const legalLinks = el.querySelectorAll<HTMLButtonElement>('.legal-link');

    expect(legalLinks.length).toBe(2);
    legalLinks.forEach((link) => {
      expect(link.tagName).toBe('BUTTON');
      expect(link.disabled).toBeTrue();
      expect(link.textContent).toContain('coming soon');
    });
    expect(legalLinks[0].textContent).toContain('Privacy Policy');
    expect(legalLinks[1].textContent).toContain('Terms of Service');
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

  /**
   * The sign-in link was taken out of the footer in 305994b and the empty region was left behind, which a
   * screen reader then announced as a navigation landmark holding nothing (LP.1 finding 8). A landmark comes
   * back when there are links to put in it, not before.
   */
  it('leaves no empty navigation landmark behind', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('nav')).toBeNull();
    expect(el.querySelector('footer')).not.toBeNull();
  });
});
