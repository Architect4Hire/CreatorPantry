import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';

import { PlaceholderSectionComponent } from './placeholder-section.component';

describe('PlaceholderSectionComponent', () => {
  async function createFixture(data: Record<string, unknown>) {
    await TestBed.configureTestingModule({
      imports: [PlaceholderSectionComponent],
      providers: [{ provide: ActivatedRoute, useValue: { snapshot: { data } } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(PlaceholderSectionComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders the title from route data', async () => {
    const fixture = await createFixture({ title: 'Recipes' });
    expect(fixture.nativeElement.textContent).toContain('Recipes');
  });

  it('falls back to a generic title/description when route data is missing', async () => {
    const fixture = await createFixture({});
    expect(fixture.nativeElement.textContent).toContain('Coming soon');
  });
});
