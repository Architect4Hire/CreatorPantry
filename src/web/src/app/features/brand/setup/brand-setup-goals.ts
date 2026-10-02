/** What the "What you're making" step asks, and how its saved answers are read back. Plain data, no framework. */

export interface GoalOption {
  readonly key: string;
  readonly label: string;
  readonly hint: string;
}

export const PURPOSE_OPTIONS: readonly GoalOption[] = [
  { key: 'blog', label: 'Grow my food blog', hint: 'Recipes and articles on your own site' },
  { key: 'social', label: 'Build my following', hint: 'Posts and short captions for social media' },
  { key: 'newsletter', label: 'Keep readers coming back', hint: 'A newsletter to the people who already follow you' },
  { key: 'brands', label: 'Work with brands', hint: 'Sponsored posts and partnerships' },
  { key: 'other', label: 'Something else', hint: '' },
];

export const AUDIENCE_OPTIONS: readonly GoalOption[] = [
  { key: 'home-cooks', label: 'Home cooks', hint: 'People cooking for themselves or their family' },
  { key: 'weeknight', label: 'Busy weeknight cooks', hint: 'Quick, no-fuss meals' },
  { key: 'beginners', label: 'Beginners', hint: 'Learning the basics' },
  { key: 'enthusiasts', label: 'Food enthusiasts', hint: 'People who like to go deeper' },
  { key: 'other', label: 'Someone else', hint: '' },
];

export const CHANNEL_OPTIONS: readonly GoalOption[] = [
  { key: 'blog', label: 'Blog', hint: '' },
  { key: 'instagram', label: 'Instagram', hint: '' },
  { key: 'facebook', label: 'Facebook', hint: '' },
  { key: 'pinterest', label: 'Pinterest', hint: '' },
  { key: 'tiktok', label: 'TikTok', hint: '' },
  { key: 'youtube', label: 'YouTube', hint: '' },
  { key: 'newsletter', label: 'Newsletter', hint: '' },
  { key: 'other', label: 'Somewhere else', hint: '' },
];

export const DEFAULT_AUDIENCE = 'home-cooks';
export const DEFAULT_CHANNELS: readonly string[] = ['blog', 'instagram'];

export const NOTE_MAX = 120;
export const DIFFERENT_MAX = 300;
export const AVOID_MAX = 200;
export const WEBSITE_MAX = 300;

export interface GoalsAnswers {
  readonly purposes: readonly string[];
  readonly audience: string;
  readonly channels: readonly string[];
  readonly purposeNote: string;
  readonly audienceNote: string;
  readonly channelNote: string;
  readonly different: string;
  readonly avoid: string;
  readonly website: string;
}

export const DEFAULT_ANSWERS: GoalsAnswers = {
  purposes: [],
  audience: DEFAULT_AUDIENCE,
  channels: DEFAULT_CHANNELS,
  purposeNote: '',
  audienceNote: '',
  channelNote: '',
  different: '',
  avoid: '',
  website: '',
};

function pickKnown(value: unknown, options: readonly GoalOption[]): string[] {
  if (!Array.isArray(value)) return [];
  const known = new Set(options.map((o) => o.key));
  // Keep the order the options are shown in, so the same answers always serialise the same way.
  return options.map((o) => o.key).filter((key) => known.has(key) && value.includes(key));
}

function text(value: unknown, max: number): string {
  return typeof value === 'string' ? value.slice(0, max) : '';
}

/** Reads a saved slice defensively: anything unknown or malformed falls back to the default for that field. */
export function decodeGoals(slice: Readonly<Record<string, unknown>> | null): GoalsAnswers {
  if (slice === null) return DEFAULT_ANSWERS;
  const audience = AUDIENCE_OPTIONS.some((o) => o.key === slice['audience']) ? (slice['audience'] as string) : DEFAULT_AUDIENCE;
  const channels = 'channels' in slice && Array.isArray(slice['channels']) ? pickKnown(slice['channels'], CHANNEL_OPTIONS) : DEFAULT_CHANNELS;
  return {
    purposes: pickKnown(slice['purposes'], PURPOSE_OPTIONS),
    audience,
    channels,
    purposeNote: text(slice['purposeNote'], NOTE_MAX),
    audienceNote: text(slice['audienceNote'], NOTE_MAX),
    channelNote: text(slice['channelNote'], NOTE_MAX),
    different: text(slice['different'], DIFFERENT_MAX),
    avoid: text(slice['avoid'], AVOID_MAX),
    website: text(slice['website'], WEBSITE_MAX),
  };
}

/** Empty means fine (the field is optional); otherwise the address must be a web address. */
export function websiteProblem(value: string): string {
  const trimmed = value.trim();
  if (trimmed === '') return '';
  try {
    const url = new URL(trimmed);
    if (url.protocol === 'http:' || url.protocol === 'https:') return '';
  } catch {
    // fall through
  }
  return 'Enter a web address that starts with https://, for example https://example.com.';
}

export function goalsComplete(answers: GoalsAnswers): boolean {
  return answers.purposes.length > 0 && answers.channels.length > 0 && websiteProblem(answers.website) === '';
}

export function toggled(list: readonly string[], key: string, on: boolean, options: readonly GoalOption[]): string[] {
  const next = new Set(list);
  if (on) next.add(key);
  else next.delete(key);
  return options.map((o) => o.key).filter((k) => next.has(k));
}

export function sameAnswers(a: GoalsAnswers, b: GoalsAnswers): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
