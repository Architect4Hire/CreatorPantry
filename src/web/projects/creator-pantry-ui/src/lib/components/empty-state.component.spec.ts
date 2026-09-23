import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpEmptyStateComponent } from './empty-state.component';

const FOOD_EMOJI = ['🍲', '🍳', '🥘', '🍽️', '🍕', '🥗', '🍰', '🧑‍🍳'];

@Component({
  standalone: true,
  imports: [CpEmptyStateComponent],
  template: `
    <cp-empty-state [title]="title" [description]="description" [icon]="icon" [variant]="variant">
      <button cpEmptyStateActions type="button">Primary action</button>
      <button cpEmptyStateActions type="button">Secondary action</button>
    </cp-empty-state>
  `,
})
class HostComponent {
  title = 'No recipes yet';
  description = 'Create your first recipe to get started.';
  icon = '✦';
  variant: 'first-use' | 'no-results' = 'first-use';
}

describe('CpEmptyStateComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  it('renders the title as an h3 and the description as supporting text', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const heading = host.querySelector('h3') as HTMLElement;

    expect(heading.textContent).toContain('No recipes yet');
    expect(host.querySelector('p')?.textContent).toContain('Create your first recipe to get started.');
  });

  it('omits the description paragraph when none is provided', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.description = '';
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('p')).toBeNull();
  });

  it('renders the icon decoratively with aria-hidden', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const icon = host.querySelector('.icon') as HTMLElement;

    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.textContent).toContain('✦');
  });

  it('defaults to a generic icon, not a food-specific glyph', async () => {
    const fixture = TestBed.createComponent(CpEmptyStateComponent);
    fixture.componentRef.setInput('title', 'Empty');
    fixture.detectChanges();

    const defaultIcon = fixture.componentInstance.icon();
    expect(FOOD_EMOJI).not.toContain(defaultIcon);
  });

  for (const variant of ['first-use', 'no-results'] as const) {
    it(`reflects a "${variant}" variant input as a data-variant attribute on the host`, () => {
      const fixture = TestBed.createComponent(CpEmptyStateComponent);
      fixture.componentRef.setInput('title', 'Empty');
      fixture.componentRef.setInput('variant', variant);
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).getAttribute('data-variant')).toBe(variant);
    });
  }

  it('renders projected actions and preserves their DOM order', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const buttons = Array.from(host.querySelectorAll('button')) as HTMLButtonElement[];

    expect(buttons.length).toBe(2);
    expect(buttons[0].textContent).toContain('Primary action');
    expect(buttons[1].textContent).toContain('Secondary action');
  });
});
