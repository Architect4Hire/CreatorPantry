import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  WorkflowIncompleteStep,
  WorkflowResourcePointer,
  WorkflowRun,
  decodeIncompleteSteps,
  decodeMissing,
  decodeWorkflowRun,
} from '../models/workflow-run.models';

export type WorkflowRunReadOutcome =
  | { readonly status: 'found'; readonly run: WorkflowRun }
  /** An unknown run and another workspace's read alike, on purpose. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

/**
 * What a command on a run came to. Every refusal the server can give has its own case, so the shell says the
 * right thing without reading status codes; anything else is `unavailable`, with whether the browser was offline.
 */
export type WorkflowRunCommandOutcome =
  | { readonly status: 'done'; readonly run: WorkflowRun }
  /** 409 `workflows.run.conflict`: the run changed since it was read. Nothing was written. */
  | { readonly status: 'conflict' }
  /** 409 `workflows.run.not_in_progress.conflict`: finished or stopped elsewhere. Nothing was written. */
  | { readonly status: 'not_in_progress' }
  /** 409 on a step that is no longer the current one, or one already done. Nothing was written. */
  | { readonly status: 'step_moved' }
  /** 409 `workflows.run.at_first_step.conflict`. */
  | { readonly status: 'at_first_step' }
  /** 422 `workflows.step.incomplete.unprocessable`: what the step still needs. */
  | { readonly status: 'step_incomplete'; readonly missing: readonly string[] }
  /** 422 `workflows.run.incomplete.unprocessable`: which required steps are still in the way. */
  | { readonly status: 'run_incomplete'; readonly incompleteSteps: readonly WorkflowIncompleteStep[] }
  /** 422 `workflows.step.required.unprocessable`: a required step cannot be skipped. */
  | { readonly status: 'step_required' }
  /** Any other 422, said in the server's own words. */
  | { readonly status: 'unprocessable'; readonly message: string }
  | { readonly status: 'invalid' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

/** Start over answers with the new run — or the one a repeat of this request already started. */
export type WorkflowRunRestartOutcome =
  | { readonly status: 'started'; readonly run: WorkflowRun }
  | Exclude<WorkflowRunCommandOutcome, { readonly status: 'done' }>;

const CODES = {
  conflict: 'workflows.run.conflict',
  notInProgress: 'workflows.run.not_in_progress.conflict',
  atFirstStep: 'workflows.run.at_first_step.conflict',
  stepIncomplete: 'workflows.step.incomplete.unprocessable',
  runIncomplete: 'workflows.run.incomplete.unprocessable',
  stepRequired: 'workflows.step.required.unprocessable',
} as const;

/**
 * The typed client for one guided-workflow run (13A.5–13A.10b). Every command quotes the run's
 * `concurrencyToken` as last read; the server refuses a stale one rather than overwriting, and treats a repeat of
 * a command that already landed as the original — so retrying any of these is safe. Components never inject
 * `HttpClient`.
 */
@Injectable({ providedIn: 'root' })
export class WorkflowRunService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async get(workspaceSlug: string, runId: string): Promise<WorkflowRunReadOutcome> {
    const url = this.url(workspaceSlug, runId);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const run = decodeWorkflowRun(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));
      return run ? { status: 'found', run } : { status: 'unavailable', offline: false };
    } catch (error) {
      return error instanceof HttpErrorResponse && error.status === 404
        ? { status: 'not_found' }
        : { status: 'unavailable', offline: isOffline() };
    }
  }

  /** Points the current step at the record its unfinished work is kept in (13A.7). */
  saveDraft(
    workspaceSlug: string,
    run: WorkflowRun,
    stepKey: string,
    draft: WorkflowResourcePointer,
  ): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, `/steps/${encodeURIComponent(stepKey)}/draft`, 'put', {
      kind: draft.kind,
      id: draft.id,
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  /** Keeps the step's draft and which of its actions the creator was in, to carry on later (13A.10). */
  saveAndExit(
    workspaceSlug: string,
    run: WorkflowRun,
    stepKey: string,
    leaving: { readonly draft?: WorkflowResourcePointer | null; readonly capabilityKey?: string | null },
  ): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, `/steps/${encodeURIComponent(stepKey)}/save-and-exit`, 'post', {
      draft: leaving.draft ? { kind: leaving.draft.kind, id: leaving.draft.id } : null,
      capabilityKey: leaving.capabilityKey ?? null,
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  /** Asks the server to complete the current step; it decides, from the records, whether it is done (13A.8). */
  completeStep(
    workspaceSlug: string,
    run: WorkflowRun,
    stepKey: string,
    results: readonly WorkflowResourcePointer[] = [],
  ): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, `/steps/${encodeURIComponent(stepKey)}/completion`, 'post', {
      results: results.length === 0 ? null : results.map((result) => ({ kind: result.kind, id: result.id })),
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  back(workspaceSlug: string, run: WorkflowRun, stepKey: string): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, `/steps/${encodeURIComponent(stepKey)}/back`, 'post', {
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  skip(
    workspaceSlug: string,
    run: WorkflowRun,
    stepKey: string,
    reason: string | null = null,
  ): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, `/steps/${encodeURIComponent(stepKey)}/skip`, 'post', {
      reason,
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  /** Finishes the run, once the server finds every required step done (13A.10a). */
  completeRun(workspaceSlug: string, run: WorkflowRun): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, '/completion', 'post', {
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  /** Stops the run for good, keeping it and everything it linked (13A.10b). Confirmed by the caller first. */
  abandon(workspaceSlug: string, run: WorkflowRun, reason: string | null): Promise<WorkflowRunCommandOutcome> {
    return this.command(workspaceSlug, run.id, '/abandonment', 'post', {
      confirmed: true,
      reason,
      expectedConcurrencyToken: run.concurrencyToken,
    });
  }

  /**
   * Stops the run and starts the same journey afresh (13A.9b). Confirmed by the caller first. `idempotencyKey` is
   * the caller's for this one attempt: a retry of it must send the same key, so a lost answer never starts two runs.
   */
  async restart(workspaceSlug: string, run: WorkflowRun, idempotencyKey: string): Promise<WorkflowRunRestartOutcome> {
    const outcome = await this.command(
      workspaceSlug,
      run.id,
      '/restart',
      'post',
      { confirmed: true, expectedConcurrencyToken: run.concurrencyToken },
      new HttpHeaders({ 'Idempotency-Key': idempotencyKey }),
    );

    return outcome.status === 'done' ? { status: 'started', run: outcome.run } : outcome;
  }

  /**
   * Starts a fresh run of a workflow (13A.5) — "Start this again" after a run was stopped, which Start over cannot
   * do. `idempotencyKey` belongs to this one attempt, as for {@link restart}.
   */
  async start(
    workspaceSlug: string,
    definitionKey: string,
    idempotencyKey: string,
    startingResource: WorkflowResourcePointer | null = null,
  ): Promise<WorkflowRunRestartOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/workflow-runs`);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const run = decodeWorkflowRun(
        await firstValueFrom(
          this.http.post<unknown>(url, startingResource ? { definitionKey, startingResource } : { definitionKey }, {
            withCredentials: true,
            headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }),
          }),
        ),
      );
      return run ? { status: 'started', run } : { status: 'unavailable', offline: false };
    } catch (error) {
      const outcome = refusal(error);
      return outcome.status === 'done' ? { status: 'started', run: outcome.run } : outcome;
    }
  }

  private url(workspaceSlug: string, runId: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/workflow-runs/${encodeURIComponent(runId)}${suffix}`,
    );
  }

  private async command(
    workspaceSlug: string,
    runId: string,
    suffix: string,
    method: 'post' | 'put',
    body: unknown,
    headers?: HttpHeaders,
  ): Promise<WorkflowRunCommandOutcome> {
    const url = this.url(workspaceSlug, runId, suffix);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    const options = { withCredentials: true, ...(headers ? { headers } : {}) };
    const request: Observable<unknown> =
      method === 'put' ? this.http.put<unknown>(url, body, options) : this.http.post<unknown>(url, body, options);

    try {
      const run = decodeWorkflowRun(await firstValueFrom(request));
      return run ? { status: 'done', run } : { status: 'unavailable', offline: false };
    } catch (error) {
      return refusal(error);
    }
  }
}

function isOffline(): boolean {
  return typeof navigator !== 'undefined' && navigator.onLine === false;
}

function refusal(error: unknown): WorkflowRunCommandOutcome {
  if (!(error instanceof HttpErrorResponse)) return { status: 'unavailable', offline: isOffline() };

  const body: unknown = error.error;
  const code = typeof body === 'object' && body !== null ? (body as Record<string, unknown>)['code'] : undefined;

  switch (error.status) {
    case 400:
      return { status: 'invalid' };
    case 403:
      return { status: 'forbidden' };
    case 404:
      return { status: 'not_found' };
    case 409:
      if (code === CODES.conflict) return { status: 'conflict' };
      if (code === CODES.notInProgress) return { status: 'not_in_progress' };
      if (code === CODES.atFirstStep) return { status: 'at_first_step' };
      return { status: 'step_moved' };
    case 422:
      if (code === CODES.stepIncomplete) return { status: 'step_incomplete', missing: decodeMissing(body) };
      if (code === CODES.runIncomplete)
        return { status: 'run_incomplete', incompleteSteps: decodeIncompleteSteps(body) };
      if (code === CODES.stepRequired) return { status: 'step_required' };
      // The server puts its plain-language sentence in `title`.
      return {
        status: 'unprocessable',
        message:
          typeof (body as Record<string, unknown> | null)?.['title'] === 'string'
            ? ((body as Record<string, unknown>)['title'] as string)
            : "That couldn't be done right now.",
      };
    default:
      return { status: 'unavailable', offline: isOffline() };
  }
}
