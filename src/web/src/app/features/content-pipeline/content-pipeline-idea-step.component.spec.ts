import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { ContentPipelineDraft, emptyContentPipelineDraft } from '../../models/content-pipeline.models';
import { ContentSeed, ContentSeedQuery } from '../../models/content-seed.models';
import { ContentSeedService, GenerateContentSeedOutcome } from '../../services/content-seed.service';
import { ContentPipelineIdeaStepComponent } from './content-pipeline-idea-step.component';

function seed(overrides: Partial<ContentSeed> = {}): ContentSeed {
  return {
    token: 'abc-123',
    cuisine: { key: 'thai', displayName: 'Thai', pinned: false },
    dishType: { key: 'main-course', displayName: 'Main course', pinned: false },
    method: { key: 'stir-fry', displayName: 'Stir-fry', pinned: false, requiresSafetyCaution: false },
    photographyStyle: { key: 'overhead-flat-lay', displayName: 'Overhead flat lay', pinned: false },
    channel: { key: 'instagram', displayName: 'Instagram', pinned: false },
    day: { day: 'Wednesday', pinned: false, theme: null },
    occasion: { key: 'weeknight', displayName: 'Weeknight', pinned: false },
    description: 'Develop a Thai main course using the stir-fry method.',
    ...overrides,
  };
}

@Component({
  imports: [ContentPipelineIdeaStepComponent],
  template: `<cp-content-pipeline-idea-step
    workspaceSlug="cozy-fall"
    [draft]="draft()"
    (changed)="apply($event)"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft());
  readonly emitted: ContentPipelineDraft[] = [];
  readonly announcements: string[] = [];

  apply(next: ContentPipelineDraft): void {
    this.emitted.push(next);
    this.draft.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let queries: { slug: string; query: ContentSeedQuery }[];
let pending: Subject<GenerateContentSeedOutcome>[];
let confirmAnswer: boolean;
let confirmCalls: number;

function generate(slug: string, query: ContentSeedQuery): Observable<GenerateContentSeedOutcome> {
  queries.push({ slug, query });
  const subject = new Subject<GenerateContentSeedOutcome>();
  pending.push(subject);
  return subject.asObservable();
}

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(draft: ContentPipelineDraft = emptyContentPipelineDraft()): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(draft);
  el = fixture.nativeElement;
  await settle();
}

async function answer(outcome: GenerateContentSeedOutcome, index = pending.length - 1): Promise<void> {
  pending[index].next(outcome);
  pending[index].complete();
  await settle();
}

function buttonWith(text: string): HTMLButtonElement {
  return Array.from(el.querySelectorAll('button')).find((button) =>
    (button.textContent ?? '').trim().startsWith(text),
  ) as HTMLButtonElement;
}

async function click(text: string): Promise<void> {
  buttonWith(text).click();
  await settle();
}

const latest = (): ContentPipelineDraft => host.emitted[host.emitted.length - 1];

describe('ContentPipelineIdeaStepComponent', () => {
  beforeEach(() => {
    queries = [];
    pending = [];
    confirmAnswer = true;
    confirmCalls = 0;

    TestBed.configureTestingModule({
      providers: [
        { provide: ContentSeedService, useValue: { generate } },
        {
          provide: ConfirmService,
          useValue: {
            confirm: () => {
              confirmCalls += 1;
              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });
  });

  it('asks for nothing until the creator does, because an idea is a suggestion', async () => {
    await mount();

    expect(queries.length).toBe(0);
    expect(el.textContent).toContain('No idea yet');
    expect(buttonWith('Suggest an idea')).toBeTruthy();
  });

  it('shows an idea and keeps only the code that reproduces it, never the idea itself', async () => {
    await mount();
    await click('Suggest an idea');

    expect(el.textContent).toContain('Putting an idea together');

    await answer({ status: 'found', seed: seed() });

    expect(el.textContent).toContain('Develop a Thai main course using the stir-fry method.');
    expect(latest().seed.lastToken).toBe('abc-123');
    expect(latest().seed.accepted).toBeNull();
    expect(host.announcements).toContain('A new idea is ready.');
  });

  it('comes back to the same idea after a reload, from the code the draft kept', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: null } });

    expect(queries.length).toBe(1);
    expect(queries[0].query.token).toBe('abc-123');
    expect(queries[0].slug).toBe('cozy-fall');
  });

  it('needs nothing fetched for an idea that was already picked', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: 'abc-123', keep: {}, accepted: seed() } });

    expect(queries.length).toBe(0);
    expect(el.textContent).toContain('This is the idea you picked.');
  });

  it('holds onto a part of the idea and sends it as a pin on the next try', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    const keep = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep cuisine'),
    )!;
    keep.click();
    await settle();

    expect(latest().seed.keep.cuisine).toBe('thai');
    expect(keep.getAttribute('aria-pressed')).toBe('true');

    await click('Try another');
    expect(queries[queries.length - 1].query.cuisine).toBe('thai');
    expect(queries[queries.length - 1].query.token).toBeNull();
  });

  it('writes a kept channel and day back to the setup step, so neither value has two homes', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    const keepChannel = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep channel'),
    )!;
    keepChannel.click();
    await settle();

    expect(latest().config.channelKey).toBe('instagram');
    expect(latest().seed.keep).toEqual({});

    const keepDay = Array.from(el.querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find((button) =>
      (button.getAttribute('aria-label') ?? '').startsWith('Keep day'),
    )!;
    keepDay.click();
    await settle();

    expect(latest().config.day).toBe('Wednesday');
  });

  it('cancels the idea in flight when a second is asked for, so the first cannot land on top', async () => {
    await mount();
    await click('Suggest an idea');
    expect(pending[0].observed).toBeTrue();

    // The code box is on screen throughout, so a second ask really can arrive while the first is in flight.
    const token = el.querySelector<HTMLInputElement>('#cp-pipeline-token')!;
    token.value = 'spring-bakes';
    token.dispatchEvent(new Event('input'));
    await settle();
    await click('Show that idea');

    expect(pending.length).toBe(2);
    expect(pending[0].observed).toBeFalse();

    await answer({ status: 'found', seed: seed({ token: 'second', description: 'The second idea.' }) }, 1);
    expect(el.textContent).toContain('The second idea.');

    // The abandoned answer arriving late changes nothing.
    pending[0].next({ status: 'found', seed: seed({ description: 'The first idea.' }) });
    await settle();
    expect(el.textContent).not.toContain('The first idea.');
  });

  it('shows a caution when the method carries one', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        method: { key: 'pressure-canning', displayName: 'Pressure canning', pinned: false, requiresSafetyCaution: true },
      }),
    });

    expect(el.textContent).toContain('This method needs care');
  });

  it('says nothing at all about safety when no caution is attached, and never that a method is safe', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    expect(el.textContent).not.toContain('This method needs care');
    expect(el.textContent?.toLowerCase()).not.toContain('is safe');
  });

  it('keeps the caution on screen after the idea is picked', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        method: { key: 'pressure-canning', displayName: 'Pressure canning', pinned: false, requiresSafetyCaution: true },
      }),
    });
    await click('Pick this idea');

    expect(latest().seed.accepted?.method?.requiresSafetyCaution).toBeTrue();
    expect(el.textContent).toContain('This method needs care');
  });

  it('shows the parts the creator asked for as theirs', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({ channel: { key: 'instagram', displayName: 'Instagram', pinned: true } }),
    });

    expect(el.textContent).toContain('You asked for this');
  });

  it("shows the workspace's own theme for the day, and does not offer to keep it", async () => {
    await mount();
    await click('Suggest an idea');
    await answer({
      status: 'found',
      seed: seed({
        day: { day: 'Wednesday', pinned: false, theme: { key: 'midweek', displayName: 'Midweek meals', pinned: false } },
      }),
    });

    expect(el.textContent).toContain('Midweek meals');
    const labels = Array.from(el.querySelectorAll('button[aria-pressed]')).map((b) => b.getAttribute('aria-label'));
    expect(labels.some((label) => (label ?? '').includes('theme'))).toBeFalse();
  });

  it('picks an idea, and lets the creator go back to looking without losing it', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });
    await click('Pick this idea');

    expect(latest().seed.accepted?.token).toBe('abc-123');
    expect(host.announcements).toContain('Idea picked.');

    await click('Change the idea');

    expect(latest().seed.accepted).toBeNull();
    // Nothing was re-fetched: the idea is still on screen as a suggestion.
    expect(queries.length).toBe(1);
    expect(el.textContent).toContain('Develop a Thai main course');
  });

  describe('choosing what to work from', () => {
    const IDEA = 'Develop a Thai main course using the stir-fry method.';
    const MINE = 'A tight crop of the first slice.';

    function choices(): HTMLInputElement[] {
      return Array.from(el.querySelectorAll<HTMLInputElement>('#cp-pipeline-brief-choice input[type="radio"]'));
    }

    async function choose(index: number): Promise<void> {
      choices()[index].click();
      await settle();
    }

    async function picked(concept: string): Promise<void> {
      const base = emptyContentPipelineDraft();
      await mount({ ...base, config: { ...base.config, concept } });
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');
    }

    it('offers no way to put the idea in place of the description', async () => {
      await picked(MINE);

      expect(buttonWith('Use this wording as my description')).toBeFalsy();
    });

    it('records the idea as the brief when there is no description to choose against, and asks nothing', async () => {
      await picked('');

      expect(choices().length).toBe(0);
      expect(latest().config.briefSource).toBe('Idea');
      expect(latest().config.brief).toBe(IDEA);
      expect(latest().config.concept).withContext('the description is not filled in for them').toBe('');
    });

    it('asks once there is a description and a picked idea, with nothing chosen for the creator', async () => {
      await picked(MINE);

      expect(choices().length).toBe(3);
      expect(choices().some((choice) => choice.checked)).toBeFalse();
      expect(latest().config.briefSource).toBeNull();
      expect(latest().config.brief).toBe('');
    });

    it('shows, on each choice, exactly the brief it would make', async () => {
      await picked(MINE);

      const section = el.querySelector('#cp-pipeline-brief-choice')!.textContent ?? '';
      expect(section).toContain('Work from my description');
      expect(section).toContain('Work from this idea');
      expect(section).toContain('Combine them');
      expect(section).toContain(MINE);
      expect(section).toContain(IDEA);
    });

    it('works from the description as it was written', async () => {
      await picked(MINE);
      await choose(0);

      expect(latest().config.briefSource).toBe('Description');
      expect(latest().config.brief).toBe(MINE);
    });

    it('works from the idea, and leaves the description exactly as it was', async () => {
      await picked(MINE);
      await choose(1);

      expect(latest().config.briefSource).toBe('Idea');
      expect(latest().config.brief).toBe(IDEA);
      expect(latest().config.concept).toBe(MINE);
    });

    it('combines them by putting the idea below the description, which is not rewritten', async () => {
      await picked(MINE);
      await choose(2);

      expect(latest().config.briefSource).toBe('Combined');
      expect(latest().config.brief).toBe(`${MINE}\n\n${IDEA}`);
      expect(latest().config.concept).toBe(MINE);
      expect(host.announcements).toContain('Combine them. The brief is set.');
    });

    it('asks before a new choice replaces a brief the creator edited, and keeps it on a no', async () => {
      await picked(MINE);
      await choose(0);
      host.draft.set({ ...host.draft(), config: { ...host.draft().config, brief: 'A tight crop, and steam.' } });
      await settle();
      expect(el.textContent).toContain('You have edited the brief');

      confirmAnswer = false;
      await choose(2);

      expect(confirmCalls).toBe(1);
      expect(host.draft().config.briefSource).toBe('Description');
      expect(host.draft().config.brief).toBe('A tight crop, and steam.');
      expect(choices().map((choice) => choice.checked))
        .withContext('the mark goes back to the choice that stands')
        .toEqual([true, false, false]);

      confirmAnswer = true;
      await choose(2);

      expect(host.draft().config.briefSource).toBe('Combined');
      expect(host.draft().config.brief).toBe(`${MINE}\n\n${IDEA}`);
    });

    it('leaves a brief that came with the run alone when an idea is picked', async () => {
      const base = emptyContentPipelineDraft();
      await mount({ ...base, config: { ...base.config, briefSource: 'Idea', brief: 'Chosen on another device.' } });
      await click('Suggest an idea');
      await answer({ status: 'found', seed: seed() });
      await click('Pick this idea');

      expect(latest().config.brief).toBe('Chosen on another device.');
    });
  });

  it('shows a refused pin, keeps it, and offers to let it go', async () => {
    const base = emptyContentPipelineDraft();
    await mount({ ...base, seed: { lastToken: null, keep: { cuisine: 'atlantean' }, accepted: null } });
    await click('Suggest an idea');
    await answer({
      status: 'refused',
      fieldErrors: { Cuisine: ['That cuisine is no longer available.'] },
    });

    expect(el.textContent).toContain('That cuisine is no longer available.');
    expect(el.textContent).toContain('Nothing you chose has been changed');
    // Still kept: nothing was silently dropped to make the request succeed.
    expect(host.draft().seed.keep.cuisine).toBe('atlantean');

    await click('Stop keeping cuisine');

    expect(latest().seed.keep.cuisine).toBeUndefined();
  });

  it('reports an outage as an outage, with a way to try again', async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'unavailable' });

    expect(el.textContent).toContain('could not be put together');
    await click('Try again');
    expect(queries.length).toBe(2);
  });

  it('reproduces an idea from a code the creator pasted, and refuses one that is not a code', async () => {
    await mount();

    const token = el.querySelector<HTMLInputElement>('#cp-pipeline-token')!;
    token.value = 'not a code';
    token.dispatchEvent(new Event('input'));
    await settle();

    expect(el.textContent).toContain('A code is up to 64 letters');
    expect(buttonWith('Show that idea').disabled).toBeTrue();

    token.value = 'spring-bakes';
    token.dispatchEvent(new Event('input'));
    await settle();

    await click('Show that idea');
    expect(queries[queries.length - 1].query.token).toBe('spring-bakes');
  });

  it("shows the idea's own code so it can be kept or shared", async () => {
    await mount();
    await click('Suggest an idea');
    await answer({ status: 'found', seed: seed() });

    expect(el.querySelector('code')?.textContent).toBe('abc-123');
  });
});
