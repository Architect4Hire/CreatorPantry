import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Component } from '@angular/core';
import { Router, TitleStrategy, provideRouter } from '@angular/router';

import { PageTitleStrategy } from './page-title.strategy';

@Component({ selector: 'cp-title-stub', template: '' })
class StubComponent {}

describe('PageTitleStrategy', () => {
  let router: Router;
  let title: Title;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'plain', component: StubComponent },
          {
            path: 'section',
            data: { title: 'Section' },
            children: [
              { path: '', pathMatch: 'full', component: StubComponent },
              { path: 'child', component: StubComponent, data: { title: 'Child page' } },
              { path: 'untitled', component: StubComponent },
            ],
          },
          { path: 'native', component: StubComponent, title: 'Native title', data: { title: 'Ignored' } },
          { path: 'blank', component: StubComponent, data: { title: '   ' } },
        ]),
        { provide: TitleStrategy, useExisting: PageTitleStrategy },
      ],
    });
    router = TestBed.inject(Router);
    title = TestBed.inject(Title);
  });

  it('falls back to the app name when no route names itself', async () => {
    await router.navigateByUrl('/plain');
    expect(title.getTitle()).toBe('CreatorPantry');
  });

  it('uses the data title of the matched route', async () => {
    await router.navigateByUrl('/section');
    expect(title.getTitle()).toBe('Section · CreatorPantry');
  });

  it('prefers the deepest route that names itself', async () => {
    await router.navigateByUrl('/section/child');
    expect(title.getTitle()).toBe('Child page · CreatorPantry');
  });

  it('inherits the nearest titled ancestor when a child has no title', async () => {
    await router.navigateByUrl('/section/untitled');
    expect(title.getTitle()).toBe('Section · CreatorPantry');
  });

  it('updates on every navigation, not only the first', async () => {
    await router.navigateByUrl('/section/child');
    await router.navigateByUrl('/plain');
    expect(title.getTitle()).toBe('CreatorPantry');
  });

  it("lets Angular's native route title take precedence", async () => {
    await router.navigateByUrl('/native');
    expect(title.getTitle()).toBe('Native title · CreatorPantry');
  });

  it('ignores a blank title', async () => {
    await router.navigateByUrl('/blank');
    expect(title.getTitle()).toBe('CreatorPantry');
  });
});
