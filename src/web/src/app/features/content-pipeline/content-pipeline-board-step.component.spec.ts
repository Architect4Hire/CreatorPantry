import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';

import { PipelineBoardCard } from '../../models/content-board.models';
import {
  ContentBoardService,
  PipelineBoardCardCommitOutcome,
  PipelineBoardCardReadOutcome,
} from '../../services/content-board.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { ContentPipelineBoardStepComponent } from './content-pipeline-board-step.component';

const SLUG = 'cozy-fall';
const CTX = 'ctx-1';
const CARD: PipelineBoardCard = { id: 'card-1', title: 'Soda bread, autumn', column: 'ToDo' };

@Component({
  imports: [ContentPipelineBoardStepComponent],
  providers: [CreativeContextSession],
  template: `<cp-content-pipeline-board-step
    [workspaceSlug]="slug()"
    [session]="session"
    (announced)="announcements.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal(SLUG);
  readonly announcements: string[] = [];

  constructor(readonly session: CreativeContextSession) {}
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let server: FakeCreativeContextService;
let reads: string[];
let commits: string[];
let readOutcome: PipelineBoardCardReadOutcome;
let commitAnswer: Observable<PipelineBoardCardCommitOutcome>;

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

/** Mount the step for a piece of work that kept `kept` pictures and has saved `saved` of them. */
async function mount(saved = 1, kept = saved): Promise<void> {
  server = new FakeCreativeContextService();
  server.seed(SLUG, CTX, {
    workingTitle: 'Soda bread, autumn',
    references: [
      ...Array.from({ length: kept }, (_, index) =>
        server.reference({ kind: 'GeneratedImage', purpose: 'Keeper', generatedImageId: `img-${index + 1}` }, index),
      ),
      ...Array.from({ length: saved }, (_, index) =>
        server.reference({ kind: 'DamAsset', purpose: 'Keeper', mediaAssetId: `a${index + 1}` }, kept + index),
      ),
    ],
  });

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: CreativeContextService, useValue: server },
      {
        provide: ContentBoardService,
        useValue: {
          pipelineCard: (slug: string, contextId: string) => {
            reads.push(`${slug}|${contextId}`);

            return of(readOutcome);
          },
          commitPipeline: (slug: string, contextId: string) => {
            commits.push(`${slug}|${contextId}`);

            return commitAnswer;
          },
        },
      },
    ],
  }).compileComponents();

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await host.session.load(SLUG, CTX);
  await settle();
}

function text(): string {
  return (el.textContent ?? '').replace(/\s+/g, ' ');
}

function button(): HTMLButtonElement | null {
  return el.querySelector('button');
}

describe('ContentPipelineBoardStepComponent', () => {
  beforeEach(() => {
    reads = [];
    commits = [];
    readOutcome = { status: 'none' };
    commitAnswer = of({ status: 'on_board', card: CARD, created: true });
  });

  it('asks the server whether the work is on the board, once, and offers to add it when it is not', async () => {
    await mount();

    expect(reads).toEqual([`${SLUG}|${CTX}`]);
    expect(button()?.textContent).toContain('Add to your board');
    expect(button()?.disabled).toBe(false);
    expect(text()).toContain('does not schedule or publish anything');
  });

  it('adds the run with one request naming only the work, and says where it went', async () => {
    await mount();

    button()!.click();
    await settle();

    expect(commits).toEqual([`${SLUG}|${CTX}`]);
    expect(text()).toContain('This run is on your board as “Soda bread, autumn”, in To do.');
    expect(button()).toBeNull();
    expect(host.announcements).toEqual(['Added to your board as “Soda bread, autumn”.']);
  });

  it('shows a run that is already on the board as done, on a resumed visit, without sending anything', async () => {
    readOutcome = { status: 'found', card: { ...CARD, column: 'ReadyForReview' } };

    await mount();

    expect(text()).toContain('This run is on your board as “Soda bread, autumn”, in Ready for review.');
    expect(button()).toBeNull();
    expect(commits).toEqual([]);
  });

  it('says so when the server answers with a card that was already there', async () => {
    commitAnswer = of({ status: 'on_board', card: CARD, created: false });
    await mount();

    button()!.click();
    await settle();

    expect(host.announcements).toEqual(['This run is already on your board as “Soda bread, autumn”.']);
    expect(text()).toContain('This run is on your board');
  });

  it('does not offer the action until a kept picture is in the library', async () => {
    await mount(0, 2);

    expect(button()?.disabled).toBe(true);
    expect(text()).toContain('Save at least one of the pictures you kept to your library');

    button()!.click();
    await settle();
    expect(commits).toEqual([]);
  });

  it('goes ahead with some pictures still to save, and says they make no second card', async () => {
    await mount(1, 3);

    expect(button()?.disabled).toBe(false);
    expect(text()).toContain('2 pictures you kept are not in your library yet');
    expect(text()).toContain('will not make a second card');
  });

  it('holds the button while the request is out, so one press is one request', async () => {
    const answer = new Subject<PipelineBoardCardCommitOutcome>();
    commitAnswer = answer;
    await mount();

    button()!.click();
    await settle();

    expect(button()?.disabled).toBe(true);
    expect(button()?.textContent).toContain('Adding to your board…');
    button()!.click();
    expect(commits.length).toBe(1);

    answer.next({ status: 'on_board', card: CARD, created: true });
    answer.complete();
    await settle();
    expect(text()).toContain('This run is on your board');
  });

  it('keeps the action after a failure, says nothing was added, and the retry is the same request', async () => {
    commitAnswer = of({ status: 'unavailable' });
    await mount();

    button()!.click();
    await settle();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Nothing was added, so it is safe to try again');
    expect(button()?.textContent).toContain('Try again');
    expect(button()?.disabled).toBe(false);

    commitAnswer = of({ status: 'on_board', card: CARD, created: true });
    button()!.click();
    await settle();

    expect(commits).toEqual([`${SLUG}|${CTX}`, `${SLUG}|${CTX}`]);
    expect(el.querySelector('[role="alert"]')).toBeNull();
    expect(text()).toContain('This run is on your board');
  });

  const refusals = [
    ['no_saved_picture', 'Save at least one of the pictures you kept'],
    ['not_found', 'can no longer be found'],
    ['forbidden', 'do not have permission'],
  ] as const;

  for (const [status, sentence] of refusals) {
    it(`explains a ${status} refusal in the creator’s terms`, async () => {
      commitAnswer = of({ status });
      await mount();

      button()!.click();
      await settle();

      expect(el.querySelector('[role="alert"]')?.textContent).toContain(sentence);
    });
  }

  it('says the board could not be read rather than offering to add a run that may already be there', async () => {
    readOutcome = { status: 'unavailable' };
    await mount();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain("couldn't be read");
    expect(text()).not.toContain('Add to your board');

    readOutcome = { status: 'none' };
    button()!.click();
    await settle();

    expect(reads.length).toBe(2);
    expect(button()?.textContent).toContain('Add to your board');
  });

  it('asks nothing and offers nothing while the work in hand is another workspace’s', async () => {
    await mount();
    reads.length = 0;

    // The route has moved to another workspace; the session still holds the first one's work.
    host.slug.set('other-kitchen');
    await settle();

    expect(reads).toEqual([]);
    expect(button()).toBeNull();
    expect(text()).toContain('Checking your board…');
    // Drawn as what is coming, and said once: the sentence is offscreen, the skeleton is what shows.
    expect(el.querySelector('[data-testid="board-loading"]')?.getAttribute('aria-busy')).toBe('true');
    expect(el.querySelector('[data-testid="board-loading"] cp-skeleton')).not.toBeNull();
    expect(text()).not.toContain('Soda bread');
  });
});
