import { FormIssue } from './form-issue';
import { FormRules } from './form-rules';

type Segment = { readonly kind: 'key'; readonly name: string } | { readonly kind: 'each' };

const SEGMENT_PATTERN = /\[\]|[^.[\]]+/g;

function parsePattern(pattern: string): readonly Segment[] {
  return (pattern.match(SEGMENT_PATTERN) ?? []).map((token): Segment => (token === '[]' ? { kind: 'each' } : { kind: 'key', name: token }));
}

interface Target {
  readonly path: string;
  readonly value: unknown;
}

/**
 * Every place in `model` a pattern names, with the concrete path of each.
 *
 * A `[]` expands to the rows that exist, so a pattern about each ingredient quantity is checked once per line.
 * A `[]` over something that is not a list finds nothing — a rule about rows has no rows to speak of. A key
 * that is missing yields `undefined` rather than nothing, because a missing value is exactly what `required`
 * has to see.
 */
function resolve(model: unknown, segments: readonly Segment[]): readonly Target[] {
  let targets: Target[] = [{ path: '', value: model }];

  for (const segment of segments) {
    const next: Target[] = [];

    for (const target of targets) {
      if (segment.kind === 'key') {
        const value = isRecord(target.value) ? target.value[segment.name] : undefined;
        next.push({ path: target.path === '' ? segment.name : `${target.path}.${segment.name}`, value });
      } else if (Array.isArray(target.value)) {
        target.value.forEach((value: unknown, index: number) => next.push({ path: `${target.path}[${index}]`, value }));
      }
    }

    targets = next;
  }

  return targets;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Checks `model` against `rules` and returns what is wrong, in rule order.
 *
 * Pure: it reads the model and returns data. Nothing about where a field is on the screen, how a message is
 * drawn or whether it should be shown yet lives here — `FormValidator` and the components own those.
 */
export function validateModel<TModel>(model: TModel, rules: FormRules<TModel>): readonly FormIssue[] {
  const issues: FormIssue[] = [];

  for (const [pattern, patternRules] of Object.entries(rules)) {
    for (const { path, value } of resolve(model, parsePattern(pattern))) {
      for (const rule of patternRules) {
        const failure = rule(value, { model, path });
        if (failure === null) continue;

        issues.push({
          path,
          severity: failure.severity ?? 'error',
          code: failure.code,
          message: failure.message,
          source: 'client',
        });
      }
    }
  }

  return issues;
}
