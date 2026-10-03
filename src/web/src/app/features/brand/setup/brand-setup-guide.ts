import { AUDIENCE_OPTIONS, GoalsAnswers, decodeGoals } from './brand-setup-goals';
import { StyleAnswers, StyleQuestionKey, decodeStyle } from './brand-setup-style';

/**
 * What the "Review your guide" step shows, composes and remembers. Plain data, no framework.
 *
 * The guide lives in the creator's own wizard draft, one entry per part, and the shell saves it the way it
 * saves every other step's answers. Nothing here creates, approves or activates a style guide.
 */

/**
 * The server's `BrandStyleGuideSectionKey`, by name, for the one part of this the wizard does not own: the
 * step that finally writes a guide has to name each part in the server's own vocabulary. Values are never
 * stored in the creator's draft — `id` below is — so renaming one of these cannot invalidate saved work.
 */
export type GuideServerSectionKey =
  | 'Voice'
  | 'Tone'
  | 'Audience'
  | 'WritingStyle'
  | 'Vocabulary'
  | 'PointOfView'
  | 'CallsToAction'
  | 'BlogGuidance'
  | 'SocialGuidance'
  | 'ChannelVariant'
  | 'PhotographyDirection'
  | 'UserNotes';

/** Where a part's words came from. */
export type SectionOrigin =
  /** Written for the creator. Nothing writes this yet; a saved draft carrying it renders as such. */
  | 'draft'
  /** Put together from the creator's own earlier answers. Re-derived only while they have not touched it. */
  | 'answers'
  /** The creator's own words, typed here. */
  | 'creator';

export interface GuideSectionDefinition {
  /** The stable id stored in the draft. Also the suffix of the part's control and anchor ids. */
  readonly id: string;
  readonly label: string;
  /** Plain-language help for the part, in a disclosure. No model, prompt or embedding vocabulary. */
  readonly help: string;
  /** What to write, shown while the part is empty. */
  readonly hint: string;
  readonly serverKey: GuideServerSectionKey;
  /** Set only for a part that is one channel's own variant, which the server keys by channel. */
  readonly channelKey: string | null;
}

/** The parts every guide has, in reading order. */
export const CORE_SECTIONS: readonly GuideSectionDefinition[] = [
  {
    id: 'voice',
    label: 'How you come across',
    help: 'The first thing a reader notices. One or two sentences about the person behind the words.',
    hint: 'A sentence about how you sound to a reader.',
    serverKey: 'Voice',
    channelKey: null,
  },
  {
    id: 'tone',
    label: 'How it feels to read',
    help: 'The mood of your writing, and how much humor belongs in it.',
    hint: 'A sentence about the mood you want.',
    serverKey: 'Tone',
    channelKey: null,
  },
  {
    id: 'audience',
    label: "Who you're talking to",
    help: 'Who you picture reading, and how you speak to them.',
    hint: 'A sentence about your reader.',
    serverKey: 'Audience',
    channelKey: null,
  },
  {
    id: 'writing-style',
    label: 'How you write',
    help: 'How formal you are, and how your sentences run.',
    hint: 'A sentence about your sentences.',
    serverKey: 'WritingStyle',
    channelKey: null,
  },
  {
    id: 'words',
    label: 'Your words',
    help: 'The words you reach for, and the ones you would never use.',
    hint: 'A sentence about the words you use.',
    serverKey: 'Vocabulary',
    channelKey: null,
  },
  {
    id: 'viewpoint',
    label: 'How you refer to yourself and your reader',
    help: 'Whether you write as "I", as "we", or mostly about "you".',
    hint: 'A sentence about how you address your reader.',
    serverKey: 'PointOfView',
    channelKey: null,
  },
  {
    id: 'calls-to-action',
    label: 'How you ask',
    help: 'How you invite a reader to cook, comment, share or subscribe.',
    hint: 'A sentence about how you ask, or that you rarely do.',
    serverKey: 'CallsToAction',
    channelKey: null,
  },
];

/** The channels this step offers a part for, and which of the creator's channel answers ask for it. */
const CHANNEL_SECTIONS: readonly { readonly channels: readonly string[]; readonly definition: GuideSectionDefinition }[] = [
  {
    channels: ['blog'],
    definition: {
      id: 'channel-blog',
      label: 'Your blog posts',
      help: 'Anything that is true of your blog but not of everything else: how you open a post, how long a headnote runs, how you handle the recipe card.',
      hint: 'Anything particular to your blog. You can leave this for later.',
      serverKey: 'BlogGuidance',
      channelKey: null,
    },
  },
  {
    channels: ['instagram', 'facebook', 'pinterest', 'tiktok', 'youtube'],
    definition: {
      id: 'channel-social',
      label: 'Your social posts',
      help: 'How your voice changes when a post is short: the first line, hashtags, emoji, how you sign off.',
      hint: 'Anything particular to your social posts. You can leave this for later.',
      serverKey: 'SocialGuidance',
      channelKey: null,
    },
  },
  {
    channels: ['newsletter'],
    definition: {
      id: 'channel-newsletter',
      label: 'Your newsletter',
      help: 'How you write to people who already follow you: the subject line, the greeting, how personal it gets.',
      hint: 'Anything particular to your newsletter. You can leave this for later.',
      serverKey: 'ChannelVariant',
      channelKey: 'newsletter',
    },
  },
];

/** The parts that come after the channels: pictures, then anything the creator wants to add. */
export const CLOSING_SECTIONS: readonly GuideSectionDefinition[] = [
  {
    id: 'photos',
    label: 'Your photos',
    help: 'How your food should look: the light, the surfaces, the angle, how styled or unstyled it is.',
    hint: 'A sentence about how your photos should look. You can leave this for later.',
    serverKey: 'PhotographyDirection',
    channelKey: null,
  },
  {
    id: 'notes',
    label: 'Anything else',
    help: 'Anything that does not fit above, in your own words.',
    hint: 'Anything else worth saying.',
    serverKey: 'UserNotes',
    channelKey: null,
  },
];

/** How much one part may hold. Generous for a few paragraphs, and small enough that every part together, plus
 * the rest of the wizard's answers, stays well inside the session's own 64 KB ceiling. */
export const SECTION_BODY_MAX = 2000;

/** Which parts this creator's guide has: the core ones, a part per channel they share on, then the closing ones. */
export function sectionsFor(goals: GoalsAnswers): readonly GuideSectionDefinition[] {
  const channels = CHANNEL_SECTIONS.filter((each) => each.channels.some((channel) => goals.channels.includes(channel)));
  return [...CORE_SECTIONS, ...channels.map((each) => each.definition), ...CLOSING_SECTIONS];
}

export interface GuideSection {
  readonly id: string;
  readonly body: string;
  readonly origin: SectionOrigin;
  /** True once the creator has changed these words themselves. It is never set back to false. */
  readonly edited: boolean;
  /** The examples a drafted part was written from. Empty for every other origin. */
  readonly citedDocumentIds: readonly string[];
}

const ORIGINS: ReadonlySet<string> = new Set<SectionOrigin>(['draft', 'answers', 'creator']);

/** Reads a saved slice defensively: a part that is not the shape this understands is left out. */
export function decodeGuideSections(slice: Readonly<Record<string, unknown>> | null): readonly GuideSection[] {
  const raw = slice?.['sections'];
  if (!Array.isArray(raw)) return [];
  const seen = new Set<string>();
  const sections: GuideSection[] = [];
  for (const entry of raw) {
    if (typeof entry !== 'object' || entry === null) continue;
    const { id, body, origin, edited, citedDocumentIds } = entry as Record<string, unknown>;
    if (typeof id !== 'string' || typeof body !== 'string' || typeof origin !== 'string' || !ORIGINS.has(origin)) continue;
    if (seen.has(id)) continue;
    seen.add(id);
    sections.push({
      id,
      body: body.slice(0, SECTION_BODY_MAX),
      origin: origin as SectionOrigin,
      edited: edited === true,
      citedDocumentIds: Array.isArray(citedDocumentIds) ? citedDocumentIds.filter((each): each is string => typeof each === 'string') : [],
    });
  }
  return sections;
}

export function sameSections(a: readonly GuideSection[], b: readonly GuideSection[]): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

export function isWritten(section: GuideSection): boolean {
  return section.body.trim() !== '';
}

/** "4 of 12 parts written" — the one sentence the step and its nav both count from. */
export function writtenSummary(written: number, total: number): string {
  return `${written} of ${total} ${total === 1 ? 'part' : 'parts'} written`;
}

// ---- Putting a part together from the creator's own answers ----

const VOICE: Readonly<Record<string, string>> = {
  'warm-friend': 'I write like a warm friend talking you through it.',
  'calm-teacher': 'I write like a calm teacher, explaining each step plainly.',
  'expert-pro': 'I write like an expert: direct and precise.',
  'playful-storyteller': 'I write like a storyteller, with a bit of a tale around the food.',
};

const TONE_FEELS: Readonly<Record<string, string>> = {
  cozy: 'cozy and comforting',
  upbeat: 'upbeat and energetic',
  calm: 'calm and reassuring',
  confident: 'confident and direct',
  curious: 'curious and adventurous',
};

const HUMOR: Readonly<Record<string, string>> = {
  none: 'I keep the humor out of it.',
  light: 'A light touch of humor now and then.',
  playful: 'Playful throughout.',
};

const SPEAKING: Readonly<Record<string, string>> = {
  friend: 'It reads like chatting with a friend in the kitchen.',
  student: 'It reads like guiding a student.',
  colleague: 'It reads like advising a colleague.',
  crowd: 'It reads like speaking to a crowd.',
};

const FORMALITY: Readonly<Record<string, string>> = {
  casual: 'My writing is very casual.',
  relaxed: 'My writing is relaxed.',
  polished: 'My writing is polished.',
};

const RHYTHM: Readonly<Record<string, string>> = {
  punchy: 'My sentences are short and punchy.',
  flowing: 'My sentences are easy and flowing.',
  mixed: 'My sentences run long or short, as the moment calls for.',
};

const WORDS: Readonly<Record<string, string>> = {
  everyday: 'I use everyday words rather than kitchen jargon.',
  kitchen: 'I use proper kitchen terms.',
  both: 'I mix everyday words with kitchen terms.',
};

const VIEWPOINT: Readonly<Record<string, string>> = {
  'i-you': 'I say "I" and "you".',
  we: 'I say "we".',
  'mostly-you': 'I mostly say "you", and rarely "I".',
};

const ASKING: Readonly<Record<string, string>> = {
  gentle: 'I ask gently, and only when it fits.',
  direct: 'I ask directly.',
  'low-key': 'I rarely ask readers to do anything.',
};

function picked(style: StyleAnswers, key: StyleQuestionKey): string | null {
  return style.choices[key][0] ?? null;
}

function lookup(table: Readonly<Record<string, string>>, key: string | null): string | null {
  return key === null ? null : (table[key] ?? null);
}

function sentences(...parts: (string | null)[]): string {
  return parts.filter((part): part is string => part !== null && part.trim() !== '').join(' ');
}

function audienceWords(goals: GoalsAnswers): string | null {
  if (goals.audience === 'other') {
    return goals.audienceNote.trim() === '' ? null : `I'm writing for ${goals.audienceNote.trim()}.`;
  }
  const option = AUDIENCE_OPTIONS.find((each) => each.key === goals.audience);
  return option ? `I'm writing for ${option.label.toLowerCase()}.` : null;
}

function toneWords(style: StyleAnswers): string | null {
  const feels = style.choices.tones.map((key) => TONE_FEELS[key]).filter((each): each is string => each !== undefined);
  if (feels.length === 0) return null;
  const list = feels.length === 1 ? feels[0] : `${feels.slice(0, -1).join(', ')} and ${feels[feels.length - 1]}`;
  return `It should feel ${list}.`;
}

/**
 * The creator's own answers, said back as a sentence or two.
 *
 * **No model is involved**: every word here is either theirs or a fixed phrase for the answer they picked, and
 * a question they did not answer contributes nothing rather than being invented. Their own free-text note for
 * a question travels verbatim, on its own line, because it is the one part they wrote themselves.
 */
export function composeSection(id: string, goals: GoalsAnswers, style: StyleAnswers): string {
  const note = (key: StyleQuestionKey): string | null => (style.notes[key].trim() === '' ? null : style.notes[key].trim());
  const body = (main: string, key: StyleQuestionKey): string => {
    const extra = note(key);
    return extra === null ? main : `${main}\n${extra}`;
  };

  switch (id) {
    case 'voice':
      return body(sentences(lookup(VOICE, picked(style, 'voice'))), 'voice');
    case 'tone':
      return body(sentences(toneWords(style), lookup(HUMOR, picked(style, 'humor'))), 'tones');
    case 'audience':
      return body(sentences(audienceWords(goals), lookup(SPEAKING, picked(style, 'audience'))), 'audience');
    case 'writing-style':
      return body(sentences(lookup(FORMALITY, picked(style, 'formality')), lookup(RHYTHM, picked(style, 'rhythm'))), 'formality');
    case 'words':
      return body(sentences(lookup(WORDS, picked(style, 'words'))), 'words');
    case 'viewpoint':
      return body(sentences(lookup(VIEWPOINT, picked(style, 'viewpoint'))), 'viewpoint');
    case 'calls-to-action':
      return body(sentences(lookup(ASKING, picked(style, 'cta'))), 'cta');
    case 'notes': {
      const different = goals.different.trim() === '' ? null : `What makes my food different: ${goals.different.trim()}`;
      const avoid = goals.avoid.trim() === '' ? null : `I never write about: ${goals.avoid.trim()}`;
      return [different, avoid].filter((each): each is string => each !== null).join('\n');
    }
    default:
      // Channels and photos: the earlier steps asked where the creator shares, never how that writing differs.
      // Composing a sentence about it would be inventing their answer.
      return '';
  }
}

/**
 * The parts to show, from the creator's saved draft and their earlier answers.
 *
 * A part they have touched is theirs and comes back exactly as they left it. A part still composed from their
 * answers is composed again, so changing an answer and coming back is not silently ignored — and that is the
 * only case in which anything is re-derived.
 */
export function seedSections(
  definitions: readonly GuideSectionDefinition[],
  saved: readonly GuideSection[],
  goals: GoalsAnswers,
  style: StyleAnswers,
): readonly GuideSection[] {
  return definitions.map((definition) => {
    const previous = saved.find((each) => each.id === definition.id);
    if (previous && (previous.origin !== 'answers' || previous.edited)) return previous;

    const composed = composeSection(definition.id, goals, style);
    if (previous) return { ...previous, body: composed };
    return {
      id: definition.id,
      body: composed,
      origin: composed === '' ? 'creator' : 'answers',
      edited: false,
      citedDocumentIds: [],
    };
  });
}

/** Reads the two earlier steps' slices, each defensively, so one malformed slice cannot empty the editor. */
export function answersOf(
  goalsSlice: Readonly<Record<string, unknown>> | null,
  styleSlice: Readonly<Record<string, unknown>> | null,
): { readonly goals: GoalsAnswers; readonly style: StyleAnswers } {
  return { goals: decodeGoals(goalsSlice), style: decodeStyle(styleSlice) };
}
