import { Component, Type, input, output, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of } from 'rxjs';

import { WorkspaceRole } from '../../../models/auth.models';
import { ContentCard, ContentCardDetail } from '../../../models/content-board.models';
import { PublishingRecord, PublishingStatusValue } from '../../../models/publishing.models';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { ContentBoardService } from '../../../services/content-board.service';
import { PublishingService } from '../../../services/publishing.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { ContentCardPublishingComponent } from '../../content-board/content-card-publishing.component';
import { PublishCheckStepComponent } from './publish-check-step.component';
import { PublishChooseStepComponent } from './publish-choose-step.component';
import { PublishConfirmStepComponent } from './publish-confirm-step.component';
import { PublishScheduleStepComponent } from './publish-schedule-step.component';

const SLUG = 'sams-kitchen';

/**
 * The card's publishing panel, stood in for: the step composes the card page's own panel (13.10d), so what this
 * spec checks is that the step hosts it for the chosen card and watches what it reports.
 */
@Component({
  selector: 'cp-content-card-publishing',
  standalone: true,
  template: `<p class="stub-panel">publishing panel for {{ cardId() }}</p>`,
})
class StubPublishingPanelComponent {
  readonly workspaceSlug = input.required<string>();
  readonly cardId = input.required<string>();
  readonly hasPicture = input(false);
  readonly summary = output<{ label: string; tone: string }>();
}

function card(overrides: Partial<ContentCard> = {}): ContentCard {
  return {
    id: 'card-1',
    title: 'Soda bread, Friday',
    descriptionPreview: 'Warm from the oven.',
    descriptionLength: 19,
    column: 'ReadyForReview',
    channelKey: 'instagram',
    plannedDate: '2026-10-16',
    plannedDay: null,
    cuisineCode: null,
    dishTypeCode: null,
    methodCode: null,
    lineage: {
      creativeContextId: null,
      recipeId: null,
      recipeVersionId: null,
      promptRecordId: null,
      mediaAssetId: null,
      mediaAssetVersionNumber: null,
      socialPackageId: null,
    },
    moveCount: 1,
    hasPackage: false,
    publishing: null,
    createdAt: '2026-10-14T09:00:00Z',
    updatedAt: '2026-10-15T09:00:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

function detail(overrides: Partial<ContentCardDetail> = {}): ContentCardDetail {
  return {
    card: card(),
    description: 'Warm from the oven.',
    lineage: [],
    history: [],
    historyTruncated: false,
    ...overrides,
  };
}

function record(overrides: Partial<PublishingRecord> = {}): PublishingRecord {
  return {
    id: 'rec-1',
    contentCardId: 'card-1',
    publishingProfileId: 'prof-1',
    platform: 'Instagram',
    status: 'Scheduled' as PublishingStatusValue,
    text: 'Warm from the oven.',
    scheduledAt: '2026-10-16T14:00:00Z',
    scheduleTimeZoneId: 'America/Chicago',
    scheduledLocal: '2026-10-16T09:00:00',
    outcomeUncertain: false,
    failureCategory: null,
    failureSummary: null,
    submittedAt: null,
    completedAt: null,
    submitAttempts: 0,
    confirmedAt: '2026-10-15T10:00:00Z',
    concurrencyToken: 'AAAAAAAAB9F=',
    ...overrides,
  };
}

/** A run of "Plan and publish content", holding whatever records it has been given. */
function publishRun(links: readonly { kind: string; resourceId: string; stepKey: string | null }[] = []): WorkflowRun {
  return decodeWorkflowRun(
    wireRun({
      definitionKey: 'publishing.plan-and-publish',
      goal: 'Plan and publish content',
      steps: [
        { key: 'choose', title: 'Choose what to publish', requirement: 'Required', status: 'NotStarted' },
        { key: 'check', title: "Check it's ready", requirement: 'Optional', status: 'NotStarted' },
        { key: 'schedule', title: 'Pick a time and schedule it', requirement: 'Required', status: 'NotStarted' },
        { key: 'confirm', title: 'Check it went through', requirement: 'Required', status: 'NotStarted' },
      ],
      links,
    }),
  )!;
}

let port: WorkflowStepPort;
let keepDraft: jasmine.Spy<(pointer: WorkflowResourcePointer) => Promise<boolean>>;
let list: jasmine.Spy;
let cardDetail: jasmine.Spy;
let forCard: jasmine.Spy;
let readRecord: jasmine.Spy;
let role: WorkspaceRole;

/** The card's publishing panel, stood in for: the step composes it rather than redrawing publishing. */
function configure(): void {
  role = 'Editor';
  port = new WorkflowStepPort();
  keepDraft = jasmine.createSpy('keepDraft').and.resolveTo(true);
  port.connect(keepDraft);
  list = jasmine
    .createSpy('list')
    .and.returnValue(of({ status: 'found', page: { items: [card()], nextCursor: null, columns: [] } }));
  cardDetail = jasmine.createSpy('detail').and.returnValue(of({ status: 'found', detail: detail() }));
  forCard = jasmine.createSpy('forCard').and.returnValue(of({ status: 'read', list: { items: [], complete: true } }));
  readRecord = jasmine.createSpy('record').and.returnValue(of({ status: 'read', record: record() }));

  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: ContentBoardService, useValue: { list, detail: cardDetail } },
      { provide: PublishingService, useValue: { forCard, record: readRecord } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: signal({
            status: 'ready',
            memberships: [
              {
                workspaceId: 'w1',
                workspaceSlug: SLUG,
                workspaceName: 'Sam',
                membershipId: 'm1',
                get role() {
                  return role;
                },
                status: 'Active',
              },
            ],
          }),
          ensureLoaded: () => Promise.resolve(),
        },
      },
    ],
  });

  // The panel is the card page's own and reads through its own services; the step's job is to host it.
  TestBed.overrideComponent(PublishScheduleStepComponent, {
    remove: { imports: [ContentCardPublishingComponent] },
    add: { imports: [StubPublishingPanelComponent] },
  });
}

async function render<T>(type: Type<T>, run: WorkflowRun): Promise<ComponentFixture<T>> {
  const fixture = TestBed.createComponent(type);
  fixture.componentRef.setInput('run', run);
  fixture.componentRef.setInput('workspaceSlug', SLUG);
  fixture.detectChanges();
  await settle(fixture);

  return fixture;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i += 1) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('"Plan and publish content" step screens (13A.22)', () => {
  beforeEach(configure);

  describe('choosing what to publish', () => {
    it('offers the board’s cards as one answer each, and keeps the one chosen on the run', async () => {
      const fixture = await render(PublishChooseStepComponent, publishRun());
      const group = (fixture.nativeElement as HTMLElement).querySelector('cp-choice-group')!;

      // One answer, not several: a run publishes one card.
      expect(group.querySelector('[role="radiogroup"]')).not.toBeNull();
      expect(text(fixture)).toContain('Soda bread, Friday');
      expect(text(fixture)).toContain('Ready for review');
      expect(port.registration()!.ready!()).toBe(false);

      const radio = group.querySelector<HTMLInputElement>('input')!;
      radio.click();
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledWith({ kind: 'ContentCard', id: 'card-1' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'ContentCard', id: 'card-1' }]);
      expect(port.registration()!.ready!()).toBe(true);
      expect(port.announcement()!.text).toContain('Nothing is scheduled yet');
    });

    it('starts on the card the run was started from, without being chosen again', async () => {
      const fixture = await render(
        PublishChooseStepComponent,
        publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: null }]),
      );

      expect(port.registration()!.results!()).toEqual([{ kind: 'ContentCard', id: 'card-1' }]);
      expect(port.registration()!.ready!()).toBe(true);
      expect(keepDraft).not.toHaveBeenCalled();
    });

    it('says what to do when there is nothing on the board, and offers the board itself', async () => {
      list.and.returnValue(of({ status: 'found', page: { items: [], nextCursor: null, columns: [] } }));
      const fixture = await render(PublishChooseStepComponent, publishRun());

      expect(text(fixture)).toContain('There is nothing on your board yet');
      expect((fixture.nativeElement as HTMLElement).querySelector('cp-notice a')!.getAttribute('href')).toBe(
        `/${SLUG}/content-board`,
      );
      expect(port.registration()!.ready!()).toBe(false);
    });

    it('says a board it could not read could not be read, and offers to try again', async () => {
      list.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(PublishChooseStepComponent, publishRun());

      expect(text(fixture)).toContain('couldn’t be read just now');
      expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')).not.toBeNull();
    });
  });

  describe('checking it is ready', () => {
    const chosen = () => publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' }]);

    it('shows the card’s own words and says nothing is scheduled yet', async () => {
      const fixture = await render(PublishCheckStepComponent, chosen());

      expect(text(fixture)).toContain('Soda bread, Friday');
      expect(text(fixture)).toContain('Warm from the oven.');
      expect(text(fixture)).toContain('Nothing is scheduled yet');
      expect((fixture.nativeElement as HTMLElement).querySelector('a')!.getAttribute('href')).toBe(
        `/${SLUG}/content-board/card-1`,
      );
      expect(port.registration()!.ready!()).toBe(true);
    });

    it('says a card with no words can still be given a time', async () => {
      cardDetail.and.returnValue(of({ status: 'found', detail: detail({ description: null }) }));
      const fixture = await render(PublishCheckStepComponent, chosen());

      expect(text(fixture)).toContain('no words on it yet');
    });

    it('says a card that has gone is gone, rather than offering to publish it', async () => {
      cardDetail.and.returnValue(of({ status: 'not_found' }));
      const fixture = await render(PublishCheckStepComponent, chosen());

      expect(text(fixture)).toContain('can’t be found any more');
      expect(port.registration()!.ready!()).toBe(false);
    });

    it('sends the creator back a step when no card has been chosen', async () => {
      const fixture = await render(PublishCheckStepComponent, publishRun());

      expect(text(fixture)).toContain('Go back a step and choose the card');
      expect(cardDetail).not.toHaveBeenCalled();
    });
  });

  describe('picking a time', () => {
    const chosen = () => publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' }]);

    it('is not ready until there is a booking, and then names it on the run', async () => {
      const fixture = await render(PublishScheduleStepComponent, chosen());

      expect(port.registration()!.ready!()).toBe(false);
      expect(keepDraft).not.toHaveBeenCalled();

      forCard.and.returnValue(of({ status: 'read', list: { items: [record()], complete: true } }));
      await fixture.componentInstance.refresh();
      await settle(fixture);

      expect(keepDraft).toHaveBeenCalledWith({ kind: 'PublishingRecord', id: 'rec-1' });
      expect(port.registration()!.results!()).toEqual([{ kind: 'PublishingRecord', id: 'rec-1' }]);
      expect(port.registration()!.ready!()).toBe(true);
      // The zone is named, not shown as the IANA id it is stored and sent as.
      expect(text(fixture)).toContain('Booked for 2026-10-16T09:00:00 (Central Daylight Time)');
      expect(text(fixture)).not.toContain('America/Chicago');

      // The card page's own panel, hosted for the card this run chose: publishing is not redrawn here.
      expect(text(fixture)).toContain('publishing panel for card-1');
    });

    it('reports the booking this run made, not another the card happens to hold', async () => {
      forCard.and.returnValue(
        of({
          status: 'read',
          list: { items: [record({ id: 'rec-other' }), record({ id: 'rec-mine' })], complete: true },
        }),
      );
      await render(
        PublishScheduleStepComponent,
        publishRun([
          { kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' },
          { kind: 'PublishingRecord', resourceId: 'rec-mine', stepKey: 'schedule' },
        ]),
      );

      expect(port.registration()!.results!()).toEqual([{ kind: 'PublishingRecord', id: 'rec-mine' }]);
    });

    it('tells someone who cannot book that an Editor makes the booking', async () => {
      role = 'Contributor';
      const fixture = await render(PublishScheduleStepComponent, chosen());

      expect(text(fixture)).toContain('An Editor or Owner makes the booking');
    });

    it('says it cannot tell whether there is a booking when that could not be read', async () => {
      forCard.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(PublishScheduleStepComponent, chosen());

      expect(text(fixture)).toContain('can’t tell whether there is a booking');
      expect(port.registration()!.ready!()).toBe(false);
    });

    it('sends the creator back when no card has been chosen', async () => {
      const fixture = await render(PublishScheduleStepComponent, publishRun());

      expect(text(fixture)).toContain('Go back and choose the card');
      expect(forCard).not.toHaveBeenCalled();
    });
  });

  describe('checking it went through', () => {
    const booked = (id = 'rec-1') =>
      publishRun([
        { kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' },
        { kind: 'PublishingRecord', resourceId: id, stepKey: 'schedule' },
      ]);

    it('reads the run’s own booking and says where it stands, in the card page’s words', async () => {
      const fixture = await render(PublishConfirmStepComponent, booked());

      expect(readRecord).toHaveBeenCalledWith(SLUG, 'rec-1');
      expect(text(fixture)).toContain('Scheduled');
      expect(text(fixture)).toContain('Central Daylight Time');
      expect(text(fixture)).not.toContain('America/Chicago');
      expect(port.registration()!.ready!()).toBe(true);
    });

    it('never rounds an uncertain outcome to success or failure', async () => {
      readRecord.and.returnValue(
        of({ status: 'read', record: record({ status: 'Submitting', outcomeUncertain: true }) }),
      );
      const fixture = await render(PublishConfirmStepComponent, booked());

      const said = text(fixture);
      expect(said).not.toContain('Published');
      expect(said).not.toContain('Not published');
      expect((fixture.nativeElement as HTMLElement).querySelector('cp-status-pill')!.textContent).not.toBe('');

      // The one state whose advice is to wait: a second booking could publish the same post twice.
      expect((fixture.nativeElement as HTMLElement).querySelector('cp-notice')).not.toBeNull();
      expect(port.registration()!.ready!()).toBe(false);
    });

    it('says a failure is a failure, and the step is not done', async () => {
      readRecord.and.returnValue(
        of({ status: 'read', record: record({ status: 'Failed', failureSummary: 'The provider refused it.' }) }),
      );
      const fixture = await render(PublishConfirmStepComponent, booked());

      expect(port.registration()!.ready!()).toBe(false);
      expect(text(fixture)).toContain('Not published');
    });

    it('falls back to the card’s newest booking when the run holds none', async () => {
      forCard.and.returnValue(of({ status: 'read', list: { items: [record({ id: 'rec-newest' })], complete: true } }));
      const fixture = await render(
        PublishConfirmStepComponent,
        publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' }]),
      );

      expect(readRecord).not.toHaveBeenCalled();
      expect(forCard).toHaveBeenCalledWith(SLUG, 'card-1');
      expect(text(fixture)).toContain('Scheduled');
    });

    it('says nothing has been booked when nothing has, and sends the creator back a step', async () => {
      const fixture = await render(
        PublishConfirmStepComponent,
        publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' }]),
      );

      expect(text(fixture)).toContain('Nothing has been booked for this card yet');
      expect(port.registration()!.ready!()).toBe(false);
    });

    it('says a booking it could not read could not be read, and changes nothing', async () => {
      readRecord.and.returnValue(of({ status: 'unavailable' }));
      const fixture = await render(PublishConfirmStepComponent, booked());

      expect(text(fixture)).toContain('couldn’t be read just now');
      expect(text(fixture)).toContain('Nothing has changed because of that');
      expect(port.registration()!.ready!()).toBe(false);
    });
  });

  // AU.7: what is on its way is drawn as lines and said once, from a status that is always there.
  describe('while something is read', () => {
    const root = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
    const statuses = (fixture: ComponentFixture<unknown>) =>
      Array.from(root(fixture).querySelectorAll('p.cp-sr-only[role="status"]')).map((each) => each.textContent!.trim());
    const chosen = () => publishRun([{ kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' }]);
    const booked = () =>
      publishRun([
        { kind: 'ContentCard', resourceId: 'card-1', stepKey: 'choose' },
        { kind: 'PublishingRecord', resourceId: 'rec-1', stepKey: 'schedule' },
      ]);

    const cases: readonly [string, Type<unknown>, () => WorkflowRun, () => jasmine.Spy, string][] = [
      ['the board', PublishChooseStepComponent, () => publishRun(), () => list, 'Reading your board…'],
      ['the card', PublishCheckStepComponent, chosen, () => cardDetail, 'Reading your card…'],
      ['what happened', PublishConfirmStepComponent, booked, () => readRecord, 'Reading what happened…'],
    ];

    for (const [what, step, run, spy, sentence] of cases) {
      it(`draws lines while ${what} is read, says it once, and empties the status once it is`, async () => {
        spy().and.returnValue(NEVER);
        const held = await render(step, run());
        expect(statuses(held)).toEqual([sentence]);
        expect(root(held).querySelector('[aria-busy="true"] cp-skeleton')).not.toBeNull();
        expect(
          Array.from(root(held).querySelectorAll('p:not(.cp-sr-only)')).some((each) =>
            each.textContent?.includes(sentence),
          ),
        ).toBeFalse();
        held.destroy();

        TestBed.resetTestingModule();
        configure();
        const read = await render(step, run());
        expect(statuses(read)).toEqual(['']);
        expect(root(read).querySelector('[aria-busy="true"] cp-skeleton')).toBeNull();
      });
    }
  });
});
