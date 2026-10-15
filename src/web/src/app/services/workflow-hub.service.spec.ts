import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { ApiBaseService } from '../core/api-base.service';
import { attentionReasonText, decodeWorkflowDefinitions, decodeWorkflowRunSummaryPage } from '../models/workflow-hub.models';
import { WorkflowHubService } from './workflow-hub.service';

const BASE = 'https://gateway.test';
const WS = `${BASE}/api/v1/workspaces/sams-kitchen`;

const DEFINITION = {
  key: 'recipe.create',
  version: 1,
  goal: 'Create a recipe',
  description: 'Write a recipe yourself or start from a first draft.',
  expectedOutcome: 'A recipe in your library.',
  startsFrom: [],
  available: true,
  unavailableReason: null,
  canStart: false,
  neededRole: 'Editor',
  waitingFor: [{ message: 'Set your workspace’s time zone in brand settings first.', route: 'brand' }],
};

const SUMMARY = {
  id: 'run-1',
  title: 'Soda bread',
  status: 'InProgress',
  definitionKey: 'recipe.create',
  definitionVersion: 1,
  goal: 'Create a recipe',
  owner: { membershipId: 'm1', displayName: 'Sam' },
  assignee: null,
  stepCount: 5,
  completedStepCount: 1,
  skippedStepCount: 1,
  currentStep: { key: 'ingredients', title: 'Ingredients' },
  linkedResource: { kind: 'Recipe', id: 'r-1' },
  nextAction: { stepKey: 'ingredients', title: 'Ingredients', capabilityName: 'Open your recipe', youCanDoIt: true },
  startedAt: '2026-10-14T09:00:00Z',
  updatedAt: '2026-10-14T10:00:00Z',
  completedAt: null,
  abandonedAt: null,
  attentionReasons: ['idle'],
};

describe('workflow hub models', () => {
  it('reads the workflows a workspace offers, with who can start them and what they need', () => {
    const [read] = decodeWorkflowDefinitions([DEFINITION])!;

    expect(read).toEqual({
      key: 'recipe.create',
      goal: 'Create a recipe',
      description: 'Write a recipe yourself or start from a first draft.',
      expectedOutcome: 'A recipe in your library.',
      available: true,
      canStart: false,
      neededRole: 'Editor',
      waitingFor: [{ message: 'Set your workspace’s time zone in brand settings first.', route: 'brand' }],
    });
  });

  it('refuses a list with an entry that is not a workflow', () => {
    expect(decodeWorkflowDefinitions([DEFINITION, { key: 'x' }])).toBeNull();
    expect(decodeWorkflowDefinitions({})).toBeNull();
  });

  it('reads a page of runs as summaries, and nothing that is not one', () => {
    const page = decodeWorkflowRunSummaryPage({ items: [SUMMARY], nextCursor: 'c-1' })!;

    expect(page.nextCursor).toBe('c-1');
    expect(page.items[0].nextStepTitle).toBe('Ingredients');
    expect(page.items[0].currentStepTitle).toBe('Ingredients');
    expect(page.items[0].attentionReasons).toEqual(['idle']);

    expect(decodeWorkflowRunSummaryPage({ items: [{ ...SUMMARY, status: 'Paused' }], nextCursor: null })).toBeNull();
    expect(decodeWorkflowRunSummaryPage({ items: [SUMMARY], nextCursor: 7 })).toBeNull();
  });

  it('says each reason in plain words, and something true for one it does not know', () => {
    expect(attentionReasonText('idle')).toBe('No one has touched this in a week.');
    expect(attentionReasonText('newer_version')).toBe('A newer version of this journey is available.');
    expect(attentionReasonText('not_yours_to_do')).toBe('Someone with more access needs to do the next step.');
    expect(attentionReasonText('something_new')).toBe('This needs a look.');
  });
});

describe('WorkflowHubService', () => {
  let service: WorkflowHubService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ApiBaseService, useValue: { url: (path: string) => new URL(path, BASE).toString() } },
      ],
    });
    service = TestBed.inject(WorkflowHubService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the workflows, and an unknown workspace as not found', async () => {
    const found = service.definitions('sams-kitchen');
    const request = http.expectOne(`${WS}/workflow-definitions`);
    expect(request.request.withCredentials).toBeTrue();
    request.flush([DEFINITION]);
    expect((await found).status).toBe('found');

    const missing = service.definitions('sams-kitchen');
    http.expectOne(`${WS}/workflow-definitions`).flush({}, { status: 404, statusText: 'Not Found' });
    expect(await missing).toEqual({ status: 'not_found' });
  });

  it('asks for a view of runs with its page size and cursor', async () => {
    const page = service.runs('sams-kitchen', { view: 'attention', limit: 10, cursor: 'c-1' });
    const request = http.expectOne((req) => req.url === `${WS}/workflow-runs`);
    expect(request.request.params.get('view')).toBe('attention');
    expect(request.request.params.get('limit')).toBe('10');
    expect(request.request.params.get('cursor')).toBe('c-1');
    request.flush({ items: [SUMMARY], nextCursor: null });

    expect((await page).status).toBe('found');
  });

  it('treats a failure or a body that is not a page as unavailable', async () => {
    const failed = service.runs('sams-kitchen', { view: 'mine' });
    http.expectOne((req) => req.url === `${WS}/workflow-runs`).flush({}, { status: 503, statusText: 'x' });
    expect(await failed).toEqual({ status: 'unavailable' });

    const malformed = service.runs('sams-kitchen', { view: 'mine' });
    http.expectOne((req) => req.url === `${WS}/workflow-runs`).flush({ items: 'no' });
    expect(await malformed).toEqual({ status: 'unavailable' });
  });
});
