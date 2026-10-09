import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';

import { CreativeContext, CreativeContextDraft, CreativeContextSource } from '../../models/creative-context.models';
import { CreativeContextCreateOutcome, CreativeContextService } from '../../services/creative-context.service';
import { HANDOFF_ROUTES, HandoffDestination } from './handoff-destinations';
import { HandoffCompleted, HandoffFailed, HandoffSeed, UseThisInComponent } from './use-this-in.component';

const SLUG = 'cozy-fall';
const CONTEXT = '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a11';
const CONCEPT: CreativeContextSource = {
  kind: 'RecipeConcept',
  conceptRequestId: '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a12',
  conceptId: '5b0f6a3e-2f0c-4d0f-9a35-3d5b6c1f0a13',
};

const DESTINATIONS: readonly HandoffDestination[] = [
  { key: 'draft', label: 'Draft this recipe', primary: true, route: HANDOFF_ROUTES.recipeDraft },
  { key: 'picture', label: 'Make a picture', detail: 'Opens Image Studio with this idea.', route: HANDOFF_ROUTES.imageStudio },
  { key: 'pipeline', label: 'Start a content run', route: HANDOFF_ROUTES.contentPipeline },
];

@Component({ standalone: true, template: 'somewhere else' })
class ElsewhereComponent {}

function context(id = CONTEXT): CreativeContext {
  return {
    id,
    workingTitle: null,
    pictureBrief: null,
    briefSource: null,
    workingBrief: null,
    channelKeys: [],
    day: null,
    weeklyThemeKey: null,
    references: [],
    createdAt: '2026-10-09T12:00:00+00:00',
    updatedAt: '2026-10-09T12:00:00+00:00',
    archivedAt: null,
    concurrencyToken: 'AAAAAAAAB9E=',
  };
}

describe('UseThisInComponent', () => {
  let fixture: ComponentFixture<UseThisInComponent>;
  let router: Router;

  /** Every create the control asked for, and the stream each one is waiting on. */
  let calls: { slug: string; draft: CreativeContextDraft; key: string; answer: Subject<CreativeContextCreateOutcome> }[];
  let handedOff: HandoffCompleted[];
  let failed: HandoffFailed[];

  async function render(
    options: {
      destinations?: readonly HandoffDestination[];
      source?: CreativeContextSource;
      seed?: HandoffSeed | null;
      disabled?: boolean;
      heading?: string;
    } = {},
  ): Promise<void> {
    fixture = TestBed.createComponent(UseThisInComponent);
    fixture.componentRef.setInput('workspaceSlug', SLUG);
    fixture.componentRef.setInput('source', options.source ?? CONCEPT);
    fixture.componentRef.setInput('destinations', options.destinations ?? DESTINATIONS);
    if (options.seed !== undefined) fixture.componentRef.setInput('seed', options.seed);
    if (options.disabled !== undefined) fixture.componentRef.setInput('disabled', options.disabled);
    if (options.heading !== undefined) fixture.componentRef.setInput('heading', options.heading);

    fixture.componentInstance.handedOff.subscribe((event) => handedOff.push(event));
    fixture.componentInstance.failed.subscribe((event) => failed.push(event));

    fixture.detectChanges();
    await fixture.whenStable();
  }

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function buttons(): HTMLButtonElement[] {
    return Array.from(host().querySelectorAll<HTMLButtonElement>('button'));
  }

  function button(key: string): HTMLButtonElement {
    return host().querySelector<HTMLButtonElement>(`button[data-destination="${key}"]`)!;
  }

  function alertText(): string | null {
    return host().querySelector('[role="alert"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
  }

  /** Answers the most recent create and lets the navigation that follows finish. */
  async function answer(outcome: CreativeContextCreateOutcome): Promise<void> {
    const call = calls[calls.length - 1];
    call.answer.next(outcome);
    call.answer.complete();

    for (let turn = 0; turn < 4; turn += 1) {
      await fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve, 0));
      fixture.detectChanges();
    }
  }

  beforeEach(async () => {
    calls = [];
    handedOff = [];
    failed = [];

    await TestBed.configureTestingModule({
      imports: [UseThisInComponent],
      providers: [
        provideRouter([
          { path: ':workspaceSlug/image-studio/context/:contextId', component: ElsewhereComponent },
          { path: ':workspaceSlug/workflows/content-pipeline/context/:contextId', component: ElsewhereComponent },
          { path: ':workspaceSlug/ai-recipe-studio/draft/context/:contextId', component: ElsewhereComponent },
          { path: ':workspaceSlug/blocked/:contextId', component: ElsewhereComponent, canActivate: [() => false] },
          { path: '**', component: ElsewhereComponent },
        ]),
        {
          provide: CreativeContextService,
          useValue: {
            create: (slug: string, draft: CreativeContextDraft, key: string) => {
              const subject = new Subject<CreativeContextCreateOutcome>();
              calls.push({ slug, draft, key, answer: subject });

              return subject.asObservable();
            },
          },
        },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    await router.navigateByUrl(`/${SLUG}/ai-recipe-studio`);
  });

  describe('what it offers', () => {
    it('offers exactly the destinations it was given, in order, as a named group', async () => {
      await render();

      const group = host().querySelector('[role="group"]')!;
      const heading = host().querySelector(`#${group.getAttribute('aria-labelledby')}`);

      expect(heading?.textContent).toBe('Use this in…');
      expect(buttons().map((item) => item.textContent?.trim())).toEqual([
        'Draft this recipe',
        'Make a picture',
        'Start a content run',
      ]);
      expect(host().querySelectorAll('li').length).toBe(3);
    });

    it('offers one destination when given one, and no others', async () => {
      await render({ destinations: [DESTINATIONS[1]] });

      expect(buttons().map((item) => item.textContent?.trim())).toEqual(['Make a picture']);
    });

    it('renders nothing at all when given none', async () => {
      await render({ destinations: [] });

      expect(host().textContent?.trim()).toBe('');
      expect(host().querySelector('[role="group"]')).toBeNull();
    });

    it('makes the primary destination the primary button and the rest secondary', async () => {
      await render();

      expect(button('draft').classList).toContain('cp-button--primary');
      expect(button('picture').classList).toContain('cp-button--secondary');
      expect(button('pipeline').classList).toContain('cp-button--secondary');
    });

    it('ties a destination’s explanation to its own button', async () => {
      await render();

      const described = button('picture').getAttribute('aria-describedby');

      expect(host().querySelector(`#${described}`)?.textContent).toBe('Opens Image Studio with this idea.');
      expect(button('draft').hasAttribute('aria-describedby')).toBeFalse();
    });

    it('uses the heading it was given', async () => {
      await render({ heading: 'Take this idea further' });

      expect(host().querySelector('.heading')?.textContent).toBe('Take this idea further');
    });

    it('gives two controls on one page different ids', async () => {
      await render();
      const first = host().querySelector('[role="group"]')!.getAttribute('aria-labelledby');

      const second = TestBed.createComponent(UseThisInComponent);
      second.componentRef.setInput('workspaceSlug', SLUG);
      second.componentRef.setInput('source', CONCEPT);
      second.componentRef.setInput('destinations', DESTINATIONS);
      second.detectChanges();

      const other = (second.nativeElement as HTMLElement).querySelector('[role="group"]')!.getAttribute('aria-labelledby');
      expect(other).not.toBe(first);
    });
  });

  describe('keyboard', () => {
    it('is ordinary buttons in tab order, with no roving focus to learn', async () => {
      await render();

      for (const item of buttons()) {
        expect(item.tagName).toBe('BUTTON');
        expect(item.type).toBe('button');
        expect(item.tabIndex).toBe(0);
        expect(item.hasAttribute('role')).toBeFalse();
      }
    });

    for (const key of ['Enter', ' ']) {
      it(`starts the hand-off when ${key === ' ' ? 'Space' : key} activates a button`, async () => {
        await render();

        // A native button turns Enter and Space into a click; dispatching that click is the part a test can
        // drive, and the assertion above is what makes the browser do the rest.
        button('picture').focus();
        button('picture').dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
        button('picture').click();
        fixture.detectChanges();

        expect(calls.length).toBe(1);
        expect(document.activeElement).toBe(button('picture'));
      });
    }

    it('keeps focus on the button used while the request runs, and after it fails', async () => {
      await render();

      button('picture').focus();
      button('picture').click();
      fixture.detectChanges();

      // `aria-disabled`, not `disabled`: a disabled button drops focus and the creator loses their place.
      expect(button('picture').disabled).toBeFalse();
      expect(document.activeElement).toBe(button('picture'));

      await answer({ status: 'unavailable' });

      expect(document.activeElement).toBe(button('picture'));
    });
  });

  describe('handing off', () => {
    it('creates a context from the source and opens the destination with its id in the path', async () => {
      await render();

      button('picture').click();
      fixture.detectChanges();

      expect(calls.length).toBe(1);
      expect(calls[0].slug).toBe(SLUG);
      expect(calls[0].draft).toEqual({ from: CONCEPT });

      await answer({ status: 'created', context: context() });

      expect(router.url).toBe(`/${SLUG}/image-studio/context/${CONTEXT}`);
      expect(handedOff).toEqual([{ destinationKey: 'picture', contextId: CONTEXT }]);
      expect(failed).toEqual([]);
    });

    const routes = [
      ['draft', `/${SLUG}/ai-recipe-studio/draft/context/${CONTEXT}`],
      ['picture', `/${SLUG}/image-studio/context/${CONTEXT}`],
      ['pipeline', `/${SLUG}/workflows/content-pipeline/context/${CONTEXT}`],
    ] as const;

    for (const [key, url] of routes) {
      it(`opens '${key}' at ${url}`, async () => {
        await render();

        button(key).click();
        await answer({ status: 'created', context: context() });

        expect(router.url).toBe(url);
      });
    }

    it('carries what the creator already said into the new context', async () => {
      await render({ seed: { workingTitle: 'Soda bread, autumn', channelKeys: ['blog'], day: 'Monday' } });

      button('pipeline').click();

      expect(calls[0].draft).toEqual({
        workingTitle: 'Soda bread, autumn',
        channelKeys: ['blog'],
        day: 'Monday',
        from: CONCEPT,
      });
    });

    it('says it is working, holds every button, and tells a screen reader which destination', async () => {
      await render();

      button('picture').click();
      fixture.detectChanges();

      expect(button('picture').textContent?.trim()).toBe('Opening…');

      // The visible words shorten; the name a screen reader hears still says which destination.
      expect(button('picture').getAttribute('aria-label')).toBe('Opening Make a picture…');
      expect(button('draft').hasAttribute('aria-label')).toBeFalse();
      expect(buttons().every((item) => item.getAttribute('aria-disabled') === 'true')).toBeTrue();
      expect(host().querySelector('[role="group"]')!.getAttribute('aria-busy')).toBe('true');
      expect(host().querySelector('[role="status"]')?.textContent?.trim()).toBe('Opening Make a picture…');
    });

    it('has its status region on the page before there is anything to say', async () => {
      await render();

      const status = host().querySelector('[role="status"]');

      expect(status).not.toBeNull();
      expect(status!.textContent?.trim()).toBe('');
    });

    it('starts one context however many times a button is pressed while it works', async () => {
      await render();

      button('picture').click();
      button('picture').click();
      button('pipeline').click();
      fixture.detectChanges();

      expect(calls.length).toBe(1);
    });

    it('does nothing while its host has disabled it, and says so to assistive technology', async () => {
      await render({ disabled: true });

      button('picture').click();

      expect(calls.length).toBe(0);
      expect(buttons().every((item) => item.getAttribute('aria-disabled') === 'true')).toBeTrue();
    });

    it('gives the buttons back when a guard declines the destination, without calling it a failure', async () => {
      await render({
        destinations: [{ key: 'blocked', label: 'Go somewhere guarded', route: (id) => ['blocked', id] }],
      });

      button('blocked').click();
      await answer({ status: 'created', context: context() });

      expect(router.url).toBe(`/${SLUG}/ai-recipe-studio`);
      expect(button('blocked').getAttribute('aria-disabled')).toBeNull();
      expect(alertText()).toBeNull();
      expect(failed).toEqual([]);
    });
  });

  describe('when the context cannot be created', () => {
    const failures = [
      [{ status: 'source_unavailable' }, 'source_unavailable', 'could not be used to start new work'],
      [{ status: 'forbidden' }, 'forbidden', 'view this workspace but not start new work'],
      [{ status: 'unavailable' }, 'unavailable', 'could not be started just now'],
      [{ status: 'invalid', errors: {} }, 'unavailable', 'could not be started just now'],
      [{ status: 'key_reused' }, 'unavailable', 'could not be started just now'],
    ] as const;

    for (const [outcome, reason, words] of failures) {
      it(`stays where the creator was and says why on '${outcome.status}'`, async () => {
        await render();

        button('picture').click();
        await answer(outcome as CreativeContextCreateOutcome);

        // Where they were, with nothing navigated and nothing lost.
        expect(router.url).toBe(`/${SLUG}/ai-recipe-studio`);
        expect(alertText()).toContain(words);
        expect(failed).toEqual([{ destinationKey: 'picture', reason }]);
        expect(handedOff).toEqual([]);

        // And able to try again: the buttons are back, with their own words.
        expect(buttons().every((item) => item.getAttribute('aria-disabled') === null)).toBeTrue();
        expect(button('picture').textContent?.trim()).toBe('Make a picture');
        expect(host().querySelector('[role="group"]')!.hasAttribute('aria-busy')).toBeFalse();
        expect(host().querySelector('[role="status"]')?.textContent?.trim()).toBe('');
      });
    }

    it('clears the message as soon as the creator tries again', async () => {
      await render();

      button('picture').click();
      await answer({ status: 'unavailable' });
      expect(alertText()).not.toBeNull();

      button('picture').click();
      fixture.detectChanges();

      expect(alertText()).toBeNull();
    });

    it('conveys the refusal in words and a glyph, not by colour alone', async () => {
      await render();

      button('picture').click();
      await answer({ status: 'forbidden' });

      const notice = host().querySelector('[role="alert"]')!;
      expect(notice.querySelector('[aria-hidden="true"]')?.textContent?.trim()).not.toBe('');
      expect(notice.textContent).toContain('Ask an owner');
    });
  });

  describe('one piece of work per source', () => {
    it('retries under the same key, so a lost response cannot make a second context', async () => {
      await render();

      button('picture').click();
      await answer({ status: 'unavailable' });
      button('picture').click();

      expect(calls.length).toBe(2);
      expect(calls[1].key).toBe(calls[0].key);
    });

    it('uses the same key for a second destination, so the same context is opened there', async () => {
      await render({
        destinations: [
          { key: 'blocked', label: 'Go somewhere guarded', route: (id) => ['blocked', id] },
          DESTINATIONS[1],
        ],
      });

      button('blocked').click();
      await answer({ status: 'created', context: context() });
      button('picture').click();

      expect(calls[1].key).toBe(calls[0].key);
    });

    it('takes a new key when the source changes', async () => {
      await render();

      button('picture').click();
      await answer({ status: 'unavailable' });

      fixture.componentRef.setInput('source', { kind: 'PromptRecord', promptRecordId: CONTEXT });
      fixture.detectChanges();
      button('picture').click();

      expect(calls[1].key).not.toBe(calls[0].key);
    });

    it('takes a new key when what is carried with it changes', async () => {
      await render({ seed: { workingTitle: 'First' } });

      button('picture').click();
      await answer({ status: 'unavailable' });

      fixture.componentRef.setInput('seed', { workingTitle: 'Second' });
      fixture.detectChanges();
      button('picture').click();

      expect(calls[1].key).not.toBe(calls[0].key);
    });

    it('takes a new key after the server says the old one names a different request', async () => {
      await render();

      button('picture').click();
      await answer({ status: 'key_reused' });
      button('picture').click();

      expect(calls[1].key).not.toBe(calls[0].key);
    });
  });
});
