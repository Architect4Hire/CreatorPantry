// The answers a working surface keeps on a creative context: what the picture is of, the channel, the day,
// that day's theme and the picture the creator has in mind (AF.3.1), and the brief they chose to work from
// (AF.3.2).
//
// A view of `CreativeContext`, not another API shape. The Content Pipeline and Image Studio each ask for one
// channel, so the surface's channel is the context's *first* one; everything else a context holds — further
// channels, its sources — is read and left as it is.

import {
  CreativeContext,
  CreativeContextBriefSource,
  CreativeContextDay,
  CreativeContextDraft,
  CreativeContextPatch,
} from './creative-context.models';
import { decodeEnum, isRecord } from './recipe.models';

export interface CreativeContextFields {
  /**
   * What the picture is of, in the creator's own words. Empty for none.
   *
   * The context's `workingTitle`, which until now no screen wrote. It is a dish name a creator can type when
   * they have no recipe in the library yet — and it is what the looks, the prompt and the idea are built
   * around when nothing else says what the subject is. A *linked* recipe always wins: it is real source
   * material with a cuisine, a course and a method behind it, where this is a name and nothing more.
   */
  readonly workingTitle: string;
  /** The context's first channel key, or null for none. */
  readonly channelKey: string | null;
  readonly day: CreativeContextDay | null;
  /** The creator's own words, exactly as typed. Empty for none. */
  readonly pictureBrief: string;
  readonly weeklyThemeKey: string | null;
  /** What the creator chose to work from, or null until they have chosen. */
  readonly briefSource: CreativeContextBriefSource | null;
  /** The brief they chose, as they last left it. Empty for none. Never the same thing as the picture. */
  readonly workingBrief: string;
}

export type CreativeContextFieldName = keyof CreativeContextFields;

export const CREATIVE_CONTEXT_FIELD_NAMES: readonly CreativeContextFieldName[] = [
  'workingTitle',
  'channelKey',
  'day',
  'pictureBrief',
  'weeklyThemeKey',
  'briefSource',
  'workingBrief',
];

export const EMPTY_CREATIVE_CONTEXT_FIELDS: CreativeContextFields = {
  workingTitle: '',
  channelKey: null,
  day: null,
  pictureBrief: '',
  weeklyThemeKey: null,
  briefSource: null,
  workingBrief: '',
};

const BRIEF_SOURCES: ReadonlySet<string> = new Set<CreativeContextBriefSource>(['Description', 'Idea', 'Combined']);

/** The three fields that are free text, which are compared and cleared the same way. */
const TEXT_FIELDS: ReadonlySet<CreativeContextFieldName> = new Set<CreativeContextFieldName>([
  'workingTitle',
  'pictureBrief',
  'workingBrief',
]);

const DAYS: ReadonlySet<string> = new Set<CreativeContextDay>([
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
]);

export function creativeContextFieldsOf(context: CreativeContext): CreativeContextFields {
  return {
    workingTitle: context.workingTitle ?? '',
    channelKey: context.channelKeys[0] ?? null,
    day: context.day,
    pictureBrief: context.pictureBrief ?? '',
    weeklyThemeKey: context.weeklyThemeKey,
    briefSource: context.briefSource,
    workingBrief: context.workingBrief ?? '',
  };
}

/**
 * Whether two values of one field are the same answer.
 *
 * Text is compared trimmed because the server stores it trimmed: a trailing space the creator is still typing
 * after must not read as an edit that never finishes saving.
 */
export function sameCreativeContextField(
  name: CreativeContextFieldName,
  left: CreativeContextFields,
  right: CreativeContextFields,
): boolean {
  const [mine, theirs] = [left[name], right[name]];

  return TEXT_FIELDS.has(name) && typeof mine === 'string' && typeof theirs === 'string'
    ? mine.trim() === theirs.trim()
    : mine === theirs;
}

/** The fields whose answer differs between the two, in declaration order. */
export function changedCreativeContextFields(
  from: CreativeContextFields,
  to: CreativeContextFields,
): readonly CreativeContextFieldName[] {
  return CREATIVE_CONTEXT_FIELD_NAMES.filter((name) => !sameCreativeContextField(name, from, to));
}

/** Only the named fields of `fields`. */
export function pickCreativeContextFields(
  fields: CreativeContextFields,
  names: readonly CreativeContextFieldName[],
): Partial<CreativeContextFields> {
  const picked: { -readonly [K in CreativeContextFieldName]?: CreativeContextFields[K] } = {};
  for (const name of names) Object.assign(picked, { [name]: fields[name] });

  return picked;
}

/**
 * The edit that makes the named fields on the server say what `fields` says, and touches nothing else.
 *
 * A channel replaces the context's first and keeps any others it names, so a surface that asks for one channel
 * does not throw away the rest of a piece of work that is for several. No channel clears the set.
 */
export function creativeContextPatchFor(
  names: readonly CreativeContextFieldName[],
  fields: CreativeContextFields,
  currentChannelKeys: readonly string[],
): CreativeContextPatch {
  const patch: { -readonly [K in keyof CreativeContextPatch]?: CreativeContextPatch[K] } = {};

  for (const name of names) {
    if (name === 'channelKey') {
      const key = fields.channelKey;
      patch.channelKeys = key === null ? [] : [key, ...currentChannelKeys.slice(1).filter((other) => other !== key)];
    } else if (name === 'workingTitle') {
      patch.workingTitle = fields.workingTitle.trim() === '' ? null : fields.workingTitle;
    } else if (name === 'pictureBrief') {
      patch.pictureBrief = fields.pictureBrief.trim() === '' ? null : fields.pictureBrief;
    } else if (name === 'workingBrief') {
      patch.workingBrief = fields.workingBrief.trim() === '' ? null : fields.workingBrief;
    } else if (name === 'briefSource') {
      patch.briefSource = fields.briefSource;
    } else if (name === 'day') {
      patch.day = fields.day;
    } else {
      patch.weeklyThemeKey = fields.weeklyThemeKey;
    }
  }

  return patch;
}

/**
 * What a new context starts with, for work that began on a surface rather than from a source.
 *
 * The chosen brief is not among them — the create route takes none — so it follows as the first ordinary edit.
 */
export function creativeContextDraftFor(fields: CreativeContextFields): CreativeContextDraft {
  return {
    ...(fields.workingTitle.trim() !== '' ? { workingTitle: fields.workingTitle } : {}),
    ...(fields.channelKey !== null ? { channelKeys: [fields.channelKey] } : {}),
    ...(fields.day !== null ? { day: fields.day } : {}),
    ...(fields.pictureBrief.trim() !== '' ? { pictureBrief: fields.pictureBrief } : {}),
    ...(fields.weeklyThemeKey !== null ? { weeklyThemeKey: fields.weeklyThemeKey } : {}),
  };
}

/**
 * Some of the fields, read back from this device's storage, or null for a shape this build cannot read.
 *
 * A field that is absent was not kept, which is different from one kept as "none" — so only the fields present
 * come back.
 */
export function decodeCreativeContextFieldsPart(raw: unknown): Partial<CreativeContextFields> | null {
  if (!isRecord(raw)) return null;

  const part: { -readonly [K in CreativeContextFieldName]?: CreativeContextFields[K] } = {};

  if ('workingTitle' in raw) {
    const value = raw['workingTitle'];
    if (typeof value !== 'string') return null;
    part.workingTitle = value;
  }
  if ('channelKey' in raw) {
    const value = raw['channelKey'];
    if (value !== null && typeof value !== 'string') return null;
    part.channelKey = value === null || value.trim() === '' ? null : value.trim();
  }
  if ('day' in raw) {
    const value = raw['day'];
    const day = value === null ? null : decodeEnum<CreativeContextDay>(DAYS, value);
    if (value !== null && day === null) return null;
    part.day = day;
  }
  if ('pictureBrief' in raw) {
    const value = raw['pictureBrief'];
    if (typeof value !== 'string') return null;
    part.pictureBrief = value;
  }
  if ('briefSource' in raw) {
    const value = raw['briefSource'];
    const source = value === null ? null : decodeEnum<CreativeContextBriefSource>(BRIEF_SOURCES, value);
    if (value !== null && source === null) return null;
    part.briefSource = source;
  }
  if ('workingBrief' in raw) {
    const value = raw['workingBrief'];
    if (typeof value !== 'string') return null;
    part.workingBrief = value;
  }
  if ('weeklyThemeKey' in raw) {
    const value = raw['weeklyThemeKey'];
    if (value !== null && typeof value !== 'string') return null;
    part.weeklyThemeKey = value === null || value === '' ? null : value;
  }

  return part;
}

/** Every field from storage, or null when any is missing or unreadable. */
export function decodeCreativeContextFields(raw: unknown): CreativeContextFields | null {
  const part = decodeCreativeContextFieldsPart(raw);
  if (part === null || CREATIVE_CONTEXT_FIELD_NAMES.some((name) => !(name in part))) return null;

  return part as CreativeContextFields;
}
