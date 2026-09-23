import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { RecipeLibraryComponent } from './recipe-library.component';
import { RecipeLibraryFixtureService, RecipeLibraryItem } from './recipe-library-fixture.service';

const FIXTURE_ITEMS: readonly RecipeLibraryItem[] = [
  { id: 'r1', title: 'Weeknight Chili', status: 'Ready', updatedAt: '2026-09-18T16:04:00Z' },
  { id: 'r2', title: 'Sheet-Pan Gnocchi', status: 'Draft', updatedAt: '2026-09-20T09:30:00Z' },
];

describe('RecipeLibraryComponent', () => {
  let loadSpy: jasmine.Spy<() => Promise<readonly RecipeLibraryItem[]>>;

  async function createFixture() {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [RecipeLibraryComponent],
      providers: [provideRouter([]), { provide: RecipeLibraryFixtureService, useValue: { load: loadSpy } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(RecipeLibraryComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('shows a loading status while the fixture resolves', async () => {
    loadSpy = jasmine.createSpy('load').and.returnValue(new Promise(() => {}));
    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('[role="status"]')).toBeTruthy();
  });

  it('renders each recipe with its title, status, and a link into the editor', async () => {
    loadSpy = jasmine.createSpy('load').and.resolveTo(FIXTURE_ITEMS);
    const fixture = await createFixture();

    const rows = fixture.nativeElement.querySelectorAll('.recipe-link');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Weeknight Chili');
    expect(rows[0].textContent).toContain('Ready');
    expect(rows[0].getAttribute('href')).toContain('/r1');
  });

  it('shows a first-use empty state with a create action when there are no recipes', async () => {
    loadSpy = jasmine.createSpy('load').and.resolveTo([]);
    const fixture = await createFixture();

    expect(fixture.nativeElement.textContent).toContain('No recipes yet');
    const createLinks = Array.from(fixture.nativeElement.querySelectorAll('a')) as HTMLAnchorElement[];
    expect(createLinks.some((link) => link.textContent?.includes('New recipe'))).toBeTrue();
  });

  it('renders the empty state with a role distinct from the error alert (not itself an alert)', async () => {
    loadSpy = jasmine.createSpy('load').and.resolveTo([]);
    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[role="status"]')).toBeNull();
  });

  it('shows an error state with a working retry', async () => {
    loadSpy = jasmine.createSpy('load').and.rejectWith(new Error('network down'));
    const fixture = await createFixture();

    const alert = fixture.nativeElement.querySelector('[role="alert"]');
    expect(alert).toBeTruthy();
    expect(alert.textContent).toContain("Couldn't load");

    loadSpy.and.resolveTo(FIXTURE_ITEMS);
    const retryButton = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(retryButton.textContent).toContain('Try again');
    retryButton.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.recipe-link').length).toBe(2);
  });

  it('always exposes the create action from the header, even while ready with results', async () => {
    loadSpy = jasmine.createSpy('load').and.resolveTo(FIXTURE_ITEMS);
    const fixture = await createFixture();

    const links = Array.from(fixture.nativeElement.querySelectorAll('a')) as HTMLAnchorElement[];
    expect(links.some((link) => link.textContent?.includes('New recipe'))).toBeTrue();
  });
});
