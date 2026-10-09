import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Route, Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';

import { routes } from '../../app.routes';
import { CreativeContextSource } from '../../models/creative-context.models';
import { CreativeContextService } from '../../services/creative-context.service';
import { UseThisInComponent } from '../../shared/use-this-in/use-this-in.component';
import { CONTENT_PIPELINE_ROUTES } from '../content-pipeline/content-pipeline.routes';
import { CONCEPT_HANDOFF_DESTINATIONS } from './recipe-concept-studio.component';

const SLUG = 'cozy-fall';
const CONTEXT = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11';
const CONCEPT: CreativeContextSource = {
  kind: 'RecipeConcept',
  conceptRequestId: '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a12',
  conceptId: '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a13',
};

/** What the destination's own route was given, read the way a page there would read it. */
let arrived: { contextId: string | null; step: string | null } | null = null;

/**
 * Stands where Image Studio and the Content Pipeline shell stand.
 *
 * Those pages do not read the context until Phase 3, so rendering them would prove nothing about the id and
 * would drag every one of their dependencies into a test about an address. This reads the route the way they
 * will: the parameter, from the activated route or an ancestor of it.
 */
@Component({ standalone: true, template: 'destination' })
class DestinationProbeComponent {
  constructor() {
    const read = (name: string): string | null => {
      for (let node: ActivatedRoute | null = inject(ActivatedRoute); node; node = node.parent) {
        const value = node.snapshot.paramMap.get(name);
        if (value) return value;
      }

      return null;
    };

    arrived = { contextId: read('contextId'), step: read('step') };
  }
}

/** The studio's two destinations on the control, with nothing else of the studio around them. */
@Component({
  standalone: true,
  imports: [UseThisInComponent],
  template: `<cp-use-this-in [workspaceSlug]="slug" [source]="source" [destinations]="destinations" />`,
})
class HostComponent {
  readonly slug = SLUG;
  readonly source = CONCEPT;
  readonly destinations = CONCEPT_HANDOFF_DESTINATIONS;
}

/**
 * The same route table with each page swapped for the probe. Paths, redirects, `pathMatch` and order are the
 * real ones — those are what is under test — and only what gets rendered at the end is replaced.
 */
function withProbe(table: Routes): Routes {
  return table.map((route): Route => {
    const { loadComponent, children, ...rest } = route;

    return {
      ...rest,
      ...(loadComponent ? { component: DestinationProbeComponent } : {}),
      ...(children ? { children: withProbe(children) } : {}),
    };
  });
}

/** Image Studio's own routes, lifted out of the real application table rather than restated here. */
function imageStudioRoutes(): Routes {
  const workspace = routes.find((route) => route.path === ':workspaceSlug');
  const imageStudio = workspace?.children?.find((route) => route.path === 'image-studio');

  if (!imageStudio?.children) throw new Error('The application routes no longer have image-studio children.');

  return imageStudio.children;
}

/**
 * AF.2.3: a concept handed to Image Studio or the Content Pipeline arrives there with its creative context's id
 * in the route.
 *
 * The control's own spec proves it navigates to an address; the route specs prove the tables have the right
 * shape. This is the join between them — the studio's real destinations, driven through the real route tables,
 * landing on a route that actually receives the parameter.
 */
describe('handing a chosen concept to another surface', () => {
  let harness: RouterTestingHarness;
  let created: { slug: string; from: unknown }[];

  beforeEach(async () => {
    arrived = null;
    created = [];

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: ':workspaceSlug',
            children: [
              { path: 'ai-recipe-studio', component: HostComponent },
              { path: 'image-studio', children: withProbe(imageStudioRoutes()) },
              { path: 'workflows', children: [{ path: 'content-pipeline', children: withProbe(CONTENT_PIPELINE_ROUTES) }] },
            ],
          },
        ]),
        {
          provide: CreativeContextService,
          useValue: {
            create: (slug: string, draft: { from?: unknown }) => {
              created.push({ slug, from: draft.from });

              return of({ status: 'created', context: { id: CONTEXT } });
            },
          },
        },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${SLUG}/ai-recipe-studio`, HostComponent);
    harness.detectChanges();
  });

  async function choose(label: string): Promise<void> {
    const host = harness.routeNativeElement as HTMLElement;
    const button = Array.from(host.querySelectorAll('button')).find((item) => item.textContent?.trim() === label);
    if (!button) throw new Error(`no destination "${label}"`);

    button.click();

    for (let turn = 0; turn < 4; turn += 1) {
      await harness.fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve, 0));
      harness.detectChanges();
    }
  }

  it('opens Image Studio on the context, and its route receives the id', async () => {
    await choose('Make a picture');

    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/image-studio/context/${CONTEXT}`);
    expect(arrived).toEqual({ contextId: CONTEXT, step: null });
    expect(created).toEqual([{ slug: SLUG, from: CONCEPT }]);
  });

  it('opens the Content Pipeline on the context at its first step, and its route receives the id', async () => {
    await choose('Start a content run');

    // The destination names no step; the pipeline's own redirect supplies the first one and keeps the id.
    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/workflows/content-pipeline/context/${CONTEXT}/setup`);
    expect(arrived).toEqual({ contextId: CONTEXT, step: 'setup' });
  });

  it('still opens each destination without a context, as it always has', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl(`/${SLUG}/image-studio`);
    expect(arrived).toEqual({ contextId: null, step: null });

    await router.navigateByUrl(`/${SLUG}/workflows/content-pipeline`);
    expect(router.url).toBe(`/${SLUG}/workflows/content-pipeline/setup`);
    expect(arrived).toEqual({ contextId: null, step: 'setup' });
  });

  it('does not mistake a context id for a pipeline step', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl(`/${SLUG}/workflows/content-pipeline/context/${CONTEXT}/idea`);

    expect(arrived).toEqual({ contextId: CONTEXT, step: 'idea' });
  });
});
