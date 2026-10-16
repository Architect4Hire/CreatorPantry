import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CaptureWorkTaskRequest,
  MoveWorkTaskRequest,
  PlanningDates,
  TodayWorkTasks,
  WeekWorkTasks,
  WorkTask,
  decodePlanningDates,
  decodeWeekWorkTasks,
  decodeTodayWorkTasks,
  decodeTopThree,
  decodeWorkTask,
  encodeCaptureWorkTaskRequest,
  encodeMoveWorkTaskRequest,
} from '../models/work-task.models';
import { decodeFieldErrors, problemCodeOf, statusCodeOf } from './ai-request';

export type CaptureWorkTaskOutcome =
  | { readonly status: 'captured'; readonly task: WorkTask; readonly replayed: boolean }
  /** Shape problems, by the request field each is about. */
  | { readonly status: 'invalid'; readonly fieldErrors: Record<string, readonly string[]> }
  /** A label this workspace does not offer — absent, retired or another workspace's, which read the same. */
  | { readonly status: 'label_refused' }
  /** A linked record this workspace cannot point a task at, by the field that named it. */
  | { readonly status: 'link_refused'; readonly fields: readonly string[] }
  /** The key was used for a different request: send again with a fresh key. */
  | { readonly status: 'key_reused' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

export type PlanningDatesOutcome =
  | { readonly status: 'found'; readonly dates: PlanningDates }
  /** The workspace keeps no calendar, so there is no honest "today" to plan by. */
  | { readonly status: 'no_time_zone' }
  | { readonly status: 'unavailable' };

export type WeekOutcome =
  | { readonly status: 'found'; readonly week: WeekWorkTasks }
  /** The workspace keeps no calendar, so a week has no honest edges. */
  | { readonly status: 'no_time_zone' }
  /** The week asked for is not a Monday written as yyyy-MM-dd. */
  | { readonly status: 'invalid' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

export type TodayOutcome =
  | { readonly status: 'found'; readonly day: TodayWorkTasks }
  /** The workspace keeps no calendar, so there is no honest "today" to show. */
  | { readonly status: 'no_time_zone' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

/** What became of a move, a completion or a reopen (13A.28, 13A.28b). */
export type WorkTaskChangeOutcome =
  | { readonly status: 'saved'; readonly task: WorkTask }
  /** The task changed somewhere else first. Nothing was written: read the day again. */
  | { readonly status: 'stale' }
  /** Gone, handed to someone else, or never the caller's — the server says these one way. */
  | { readonly status: 'not_found' }
  /** Already where this asked for it — finished, or not finished — so nothing was written. */
  | { readonly status: 'already' }
  /** The place asked for — before a task — is not in that list any more. Nothing was written (13A.33). */
  | { readonly status: 'place_refused' }
  /** The server could not read the request as asked — the list it is already in, say. Nothing was written. */
  | { readonly status: 'invalid' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

export type TopThreeOutcome =
  | { readonly status: 'saved'; readonly taskIds: readonly string[] }
  /** A task that is no longer on the creator's day. Nothing was written. */
  | { readonly status: 'refused' }
  | { readonly status: 'no_time_zone' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable'; readonly offline: boolean };

const CODES = {
  labelRefused: 'planning.work_task.label.unprocessable',
  linkRefused: 'planning.work_task.link.unprocessable',
  keyReused: 'idempotency.key_reused',
  weekNoZone: 'planning.week.time_zone_not_set.unprocessable',
  todayNoZone: 'planning.today.time_zone_not_set.unprocessable',
  stale: 'planning.work_task.stale.conflict',
  completed: 'planning.work_task.completed.conflict',
  open: 'planning.work_task.open.conflict',
  topThreeRefused: 'planning.top_three.unprocessable',
  placeRefused: 'planning.work_task.move.unprocessable',
} as const;

/**
 * The typed client for one person's work tasks.
 *
 * The caller owns the `Idempotency-Key` across retries of one capture, and sends the same key only with the same
 * request: a retry after a failure that may in fact have saved then still makes one task. No component may
 * inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class WorkTaskService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Captures a task for the caller in the workspace (13A.24, 13A.31). */
  async capture(workspaceSlug: string, request: CaptureWorkTaskRequest, idempotencyKey: string): Promise<CaptureWorkTaskOutcome> {
    const url = this.url(workspaceSlug, '/work-tasks');
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeCaptureWorkTaskRequest(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const task = decodeWorkTask(response.body);

      return task
        ? { status: 'captured', task, replayed: response.headers.get('Idempotent-Replayed') === 'true' }
        : { status: 'unavailable', offline: false };
    } catch (error) {
      switch (statusCodeOf(error)) {
        case 400:
          return { status: 'invalid', fieldErrors: decodeFieldErrors(error) };
        case 403:
          return { status: 'forbidden' };
        case 422:
          switch (problemCodeOf(error)) {
            case CODES.labelRefused:
              return { status: 'label_refused' };
            case CODES.linkRefused:
              return { status: 'link_refused', fields: Object.keys(decodeFieldErrors(error)) };
            case CODES.keyReused:
              return { status: 'key_reused' };
            default:
              return { status: 'unavailable', offline: false };
          }
        default:
          return { status: 'unavailable', offline: isOffline() };
      }
    }
  }

  /**
   * Today and this week's Monday where the workspace keeps its calendar — read from the week route, so the days
   * a capture offers are the ones My Day and My Week mean, even near midnight.
   */
  async planningDates(workspaceSlug: string): Promise<PlanningDatesOutcome> {
    const url = this.url(workspaceSlug, '/work-tasks/week');
    if (!url) return { status: 'unavailable' };

    try {
      const dates = decodePlanningDates(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return dates ? { status: 'found', dates } : { status: 'unavailable' };
    } catch (error) {
      return statusCodeOf(error) === 422 && problemCodeOf(error) === CODES.weekNoZone
        ? { status: 'no_time_zone' }
        : { status: 'unavailable' };
    }
  }

  /** The caller's own day (13A.25), with the top three they chose (13A.32). Suggestions only when asked for. */
  async today(workspaceSlug: string, includeSuggestions = false): Promise<TodayOutcome> {
    const url = this.url(workspaceSlug, `/work-tasks/today${includeSuggestions ? '?includeSuggestions=true' : ''}`);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const day = decodeTodayWorkTasks(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return day ? { status: 'found', day } : { status: 'unavailable', offline: false };
    } catch (error) {
      return statusCodeOf(error) === 422 && problemCodeOf(error) === CODES.todayNoZone
        ? { status: 'no_time_zone' }
        : { status: 'unavailable', offline: isOffline() };
    }
  }

  /** The caller's own week (13A.26): the one starting on `weekStart`, a Monday, or the current one when null. */
  async week(workspaceSlug: string, weekStart: string | null): Promise<WeekOutcome> {
    const query = weekStart ? `?weekStart=${encodeURIComponent(weekStart)}` : '';
    const url = this.url(workspaceSlug, `/work-tasks/week${query}`);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const week = decodeWeekWorkTasks(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return week ? { status: 'found', week } : { status: 'unavailable', offline: false };
    } catch (error) {
      switch (statusCodeOf(error)) {
        case 400:
          return { status: 'invalid' };
        case 422:
          return problemCodeOf(error) === CODES.weekNoZone
            ? { status: 'no_time_zone' }
            : { status: 'unavailable', offline: false };
        default:
          return { status: 'unavailable', offline: isOffline() };
      }
    }
  }

  /** Moves one of the caller's tasks to another of their lists, at the bottom of it (13A.28). */
  move(workspaceSlug: string, task: WorkTask, request: MoveWorkTaskRequest): Promise<WorkTaskChangeOutcome> {
    return this.change(workspaceSlug, task, 'move', encodeMoveWorkTaskRequest(task, request));
  }

  /**
   * Gives one of the caller's tasks a new place in the list it is already in (13A.28a): before `beforeTaskId`, or at
   * the bottom. A change of list is a move; the server refuses one sent here.
   */
  reorder(workspaceSlug: string, task: WorkTask, beforeTaskId: string | null): Promise<WorkTaskChangeOutcome> {
    return this.change(workspaceSlug, task, 'reorder', {
      expectedConcurrencyToken: task.concurrencyToken,
      ...(beforeTaskId ? { beforeTaskId } : {}),
    });
  }

  /** Finishes one of the caller's tasks (13A.28b). */
  complete(workspaceSlug: string, task: WorkTask): Promise<WorkTaskChangeOutcome> {
    return this.change(workspaceSlug, task, 'complete', { expectedConcurrencyToken: task.concurrencyToken });
  }

  /** Takes one of the caller's finished tasks up again, where it stood (13A.28b). */
  reopen(workspaceSlug: string, task: WorkTask): Promise<WorkTaskChangeOutcome> {
    return this.change(workspaceSlug, task, 'reopen', { expectedConcurrencyToken: task.concurrencyToken });
  }

  /** Replaces the caller's top three for today with these tasks, in this order; an empty list clears it (13A.32). */
  async setTopThree(workspaceSlug: string, taskIds: readonly string[]): Promise<TopThreeOutcome> {
    const url = this.url(workspaceSlug, '/work-tasks/top-three');
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const ids = decodeTopThree(
        await firstValueFrom(this.http.put<unknown>(url, { taskIds }, { withCredentials: true })),
      );

      return ids ? { status: 'saved', taskIds: ids } : { status: 'unavailable', offline: false };
    } catch (error) {
      switch (statusCodeOf(error)) {
        case 403:
          return { status: 'forbidden' };
        case 422:
          if (problemCodeOf(error) === CODES.topThreeRefused) return { status: 'refused' };
          if (problemCodeOf(error) === CODES.todayNoZone) return { status: 'no_time_zone' };
          return { status: 'unavailable', offline: false };
        default:
          return { status: 'unavailable', offline: isOffline() };
      }
    }
  }

  /**
   * One command on one task, quoting the token of the read it was composed against. No idempotency key: a repeat
   * quotes a token the first one used, so the server refuses it as stale rather than doing it twice.
   */
  private async change(
    workspaceSlug: string,
    task: WorkTask,
    action: 'move' | 'reorder' | 'complete' | 'reopen',
    body: Record<string, unknown>,
  ): Promise<WorkTaskChangeOutcome> {
    const url = this.url(workspaceSlug, `/work-tasks/${encodeURIComponent(task.id)}/${action}`);
    if (!url) return { status: 'unavailable', offline: isOffline() };

    try {
      const saved = decodeWorkTask(await firstValueFrom(this.http.post<unknown>(url, body, { withCredentials: true })));

      return saved ? { status: 'saved', task: saved } : { status: 'unavailable', offline: false };
    } catch (error) {
      switch (statusCodeOf(error)) {
        case 400:
          return { status: 'invalid' };
        case 403:
          return { status: 'forbidden' };
        case 404:
          return { status: 'not_found' };
        case 422:
          return problemCodeOf(error) === CODES.placeRefused
            ? { status: 'place_refused' }
            : { status: 'unavailable', offline: false };
        case 409:
          switch (problemCodeOf(error)) {
            case CODES.stale:
              return { status: 'stale' };
            case CODES.completed:
            case CODES.open:
              return { status: 'already' };
            default:
              // Not saved, and safe to send again.
              return { status: 'unavailable', offline: false };
          }
        default:
          return { status: 'unavailable', offline: isOffline() };
      }
    }
  }

  private url(workspaceSlug: string, path: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}${path}`);
  }
}

function isOffline(): boolean {
  return typeof navigator !== 'undefined' && navigator.onLine === false;
}
