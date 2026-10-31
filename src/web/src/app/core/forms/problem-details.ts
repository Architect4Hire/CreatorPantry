import { HttpErrorResponse } from '@angular/common/http';

/**
 * Reading what the API said when it refused a request.
 *
 * Every refusal is `ProblemDetails`: a `title` with the server's own sentence, a stable `code`, and — for a
 * shape the server validates — `errors`, the field messages keyed by camelCase request path. This is the one
 * reader of that body; a service that decodes it by hand is a second copy to keep in step.
 */
export function problemBodyOf(error: unknown): Record<string, unknown> | null {
  if (!(error instanceof HttpErrorResponse)) return null;

  const body: unknown = error.error;

  return typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : null;
}

/** The HTTP status of a failed request, or null when `error` is not one. */
export function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body — see `ProblemResults.StatusFor` server-side. */
export function problemCodeOf(error: unknown): string | null {
  const code = problemBodyOf(error)?.['code'];

  return typeof code === 'string' ? code : null;
}

/** `ProblemDetails.title`, which carries the server's own sentence about a refusal. */
export function problemMessageOf(error: unknown): string | null {
  const title = problemBodyOf(error)?.['title'];

  return typeof title === 'string' && title.trim() !== '' ? title : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name. Anything malformed is dropped. */
export function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const errors = problemBodyOf(error)?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }

  return result;
}
