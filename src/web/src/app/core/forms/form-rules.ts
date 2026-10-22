import { IssueSeverity } from './form-issue';

/**
 * Declarative rules for one form: a path pattern to the rules that apply to what it finds.
 *
 * A pattern is the server's key syntax with `[]` standing for "each": `title`, `servings`,
 * `ingredientGroups[].ingredients[].quantity`. A pattern with `[]` is checked once per row that exists and
 * reports the concrete path (`ingredientGroups[1].ingredients[0].quantity`), which is what a field looks up.
 *
 * **These are conveniences, not the rules.** The server validates everything and stays the authority; a client
 * rule exists to answer sooner, or to say something the server does not (a warning). Do not mirror a whole
 * server validator here — two copies of a rule drift.
 */
export type FormRules<TModel> = Readonly<Record<string, readonly FormRule<TModel>[]>>;

/** What a rule is given about the value it is checking. */
export interface RuleContext<TModel> {
  /** The whole model, for a rule that compares one field with another. */
  readonly model: TModel;
  /** The concrete path being checked. */
  readonly path: string;
}

/** What a rule reports when the value does not pass. `severity` defaults to `error`. */
export interface RuleFailure {
  readonly code: string;
  readonly message: string;
  readonly severity?: IssueSeverity;
}

/** A rule returns nothing when the value passes, and one failure when it does not. */
export type FormRule<TModel = unknown> = (value: unknown, context: RuleContext<TModel>) => RuleFailure | null;

/** Nothing the creator entered: unset, empty, only whitespace, or an empty list. */
export function isBlank(value: unknown): boolean {
  if (value === undefined || value === null) return true;
  if (typeof value === 'string') return value.trim() === '';
  if (Array.isArray(value)) return value.length === 0;

  return false;
}

/** The value must be entered. */
export function required(message: string): FormRule {
  return (value) => (isBlank(value) ? { code: 'required', message } : null);
}

/** At most `max` characters. A blank value passes — `required` is the rule about presence. */
export function maxLength(max: number, message: string): FormRule {
  return (value) => (typeof value === 'string' && value.length > max ? { code: 'maxLength', message } : null);
}

/** At least `min` characters once something is entered. */
export function minLength(min: number, message: string): FormRule {
  return (value) =>
    typeof value === 'string' && !isBlank(value) && value.trim().length < min ? { code: 'minLength', message } : null;
}

/** A number between `min` and `max`, either end optional. A blank or non-numeric value passes. */
export function range(bounds: { readonly min?: number; readonly max?: number }, message: string): FormRule {
  return (value) => {
    if (isBlank(value)) return null;

    const number = typeof value === 'number' ? value : Number(value);
    if (Number.isNaN(number)) return null;

    const below = bounds.min !== undefined && number < bounds.min;
    const above = bounds.max !== undefined && number > bounds.max;

    return below || above ? { code: 'range', message } : null;
  };
}

/** The entered text must match `expression`. A blank value passes. */
export function pattern(expression: RegExp, message: string): FormRule {
  return (value) =>
    typeof value === 'string' && !isBlank(value) && !new RegExp(expression.source, expression.flags.replace('g', '')).test(value)
      ? { code: 'pattern', message }
      : null;
}

/** A rule of the form's own — typically one that compares fields. Return a message to fail, null to pass. */
export function custom<TModel = unknown>(
  code: string,
  check: (value: unknown, context: RuleContext<TModel>) => string | null,
): FormRule<TModel> {
  return (value, context) => {
    const message = check(value, context);

    return message === null ? null : { code, message };
  };
}

/** The same rule, reported as a warning: it says something and does not stop a save. */
export function warn<TModel = unknown>(rule: FormRule<TModel>): FormRule<TModel> {
  return (value, context) => {
    const failure = rule(value, context);

    return failure === null ? null : { ...failure, severity: 'warning' };
  };
}

/** The same rule, reported as information. */
export function inform<TModel = unknown>(rule: FormRule<TModel>): FormRule<TModel> {
  return (value, context) => {
    const failure = rule(value, context);

    return failure === null ? null : { ...failure, severity: 'info' };
  };
}
