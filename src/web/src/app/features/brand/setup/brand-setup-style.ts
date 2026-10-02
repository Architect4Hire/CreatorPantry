/** What the "How you sound" step asks, and how its saved answers are read back. Plain data, no framework. */

export interface StyleChoice {
  readonly key: string;
  readonly label: string;
  /** A sample sentence in that style, shown so the creator picks by ear. */
  readonly example: string;
}

export type StyleQuestionKey = 'voice' | 'tones' | 'audience' | 'formality' | 'rhythm' | 'words' | 'viewpoint' | 'humor' | 'cta';

export interface StyleQuestion {
  readonly key: StyleQuestionKey;
  readonly heading: string;
  readonly intro: string;
  readonly multiple: boolean;
  readonly choices: readonly StyleChoice[];
}

export const TONES_MAX = 3;
export const NOTE_MAX = 200;

const c = (key: string, label: string, example = ''): StyleChoice => ({ key, label, example });

export const STYLE_QUESTIONS: readonly StyleQuestion[] = [
  {
    key: 'voice',
    heading: 'How do you come across?',
    intro: 'Pick the one that sounds most like you.',
    multiple: false,
    choices: [
      c('warm-friend', 'Warm friend', '“Okay, this lasagna is going to be your new favorite. Trust me.”'),
      c('calm-teacher', 'Calm teacher', '“Start by browning the sausage until no pink remains.”'),
      c('expert-pro', 'Expert pro', '“Brown the sausage thoroughly to build a deeper base.”'),
      c('playful-storyteller', 'Playful storyteller', '“My nonna would gasp at this shortcut. I’m sorry, Nonna.”'),
    ],
  },
  {
    key: 'tones',
    heading: 'How does it feel to read?',
    intro: `Pick up to ${TONES_MAX}.`,
    multiple: true,
    choices: [
      c('cozy', 'Cozy and comforting'),
      c('upbeat', 'Upbeat and energetic'),
      c('calm', 'Calm and reassuring'),
      c('confident', 'Confident and direct'),
      c('curious', 'Curious and adventurous'),
    ],
  },
  {
    key: 'audience',
    heading: "Who does it sound like you're talking to?",
    intro: 'Pick the closest.',
    multiple: false,
    choices: [
      c('friend', 'Like chatting with a friend in the kitchen'),
      c('student', 'Like guiding a student'),
      c('colleague', 'Like advising a colleague'),
      c('crowd', 'Like speaking to a crowd'),
    ],
  },
  {
    key: 'formality',
    heading: 'How formal are you?',
    intro: 'Pick the one you would write.',
    multiple: false,
    choices: [
      c('casual', 'Very casual', '“Toss it all in, give it a stir, done.”'),
      c('relaxed', 'Relaxed', '“Add everything and stir until combined.”'),
      c('polished', 'Polished', '“Combine all ingredients and stir until evenly mixed.”'),
    ],
  },
  {
    key: 'rhythm',
    heading: 'How do your sentences flow?',
    intro: 'Pick the one that reads like you.',
    multiple: false,
    choices: [
      c('punchy', 'Short and punchy', '“Brown it. Drain it. Layer it.”'),
      c('flowing', 'Easy and flowing', '“Brown the sausage, drain it, then start layering.”'),
      c('mixed', 'Mixed, as the moment calls for'),
    ],
  },
  {
    key: 'words',
    heading: 'What kind of words do you use?',
    intro: 'Pick one.',
    multiple: false,
    choices: [
      c('everyday', 'Everyday words', '“Cook until soft.”'),
      c('kitchen', 'Kitchen terms', '“Sweat the onions.”'),
      c('both', 'A bit of both'),
    ],
  },
  {
    key: 'viewpoint',
    heading: 'How do you refer to yourself and your reader?',
    intro: 'Pick the one you use most.',
    multiple: false,
    choices: [
      c('i-you', '“I” and “you”', '“I love this, and you will too.”'),
      c('we', '“We”', '“We’ll start with the sauce.”'),
      c('mostly-you', 'Mostly “you”, rarely “I”', '“You’ll want the sauce thick.”'),
    ],
  },
  {
    key: 'humor',
    heading: 'How much humor do you use?',
    intro: 'Pick one.',
    multiple: false,
    choices: [c('none', 'None, keep it straight'), c('light', 'A light touch now and then'), c('playful', 'Playful throughout')],
  },
  {
    key: 'cta',
    heading: 'How do you invite readers to act?',
    intro: 'Pick the one you would write. A favorite sign-off can go in your own words.',
    multiple: false,
    choices: [
      c('gentle', 'Gentle', '“If you try it, I’d love to hear how it goes.”'),
      c('direct', 'Direct', '“Make it tonight and tag me.”'),
      c('low-key', 'Low-key: I rarely ask readers to do anything'),
    ],
  },
];

export interface StyleAnswers {
  readonly choices: Readonly<Record<StyleQuestionKey, readonly string[]>>;
  readonly notes: Readonly<Record<StyleQuestionKey, string>>;
}

const KEYS = STYLE_QUESTIONS.map((q) => q.key);

function blank<T>(value: T): Record<StyleQuestionKey, T> {
  return Object.fromEntries(KEYS.map((k) => [k, value])) as Record<StyleQuestionKey, T>;
}

export const BLANK_ANSWERS: StyleAnswers = { choices: blank<readonly string[]>([]), notes: blank('') };

/** Reads a saved slice defensively: anything unknown or malformed is read back as blank. */
export function decodeStyle(slice: Readonly<Record<string, unknown>> | null): StyleAnswers {
  if (slice === null) return BLANK_ANSWERS;
  const rawChoices = isRecord(slice['choices']) ? slice['choices'] : {};
  const rawNotes = isRecord(slice['notes']) ? slice['notes'] : {};
  const choices = blank<readonly string[]>([]);
  const notes = blank('');
  for (const q of STYLE_QUESTIONS) {
    const saved = rawChoices[q.key];
    const known = q.choices.map((x) => x.key).filter((k) => Array.isArray(saved) && saved.includes(k));
    choices[q.key] = q.multiple ? known.slice(0, TONES_MAX) : known.slice(0, 1);
    const note = rawNotes[q.key];
    notes[q.key] = typeof note === 'string' ? note.slice(0, NOTE_MAX) : '';
  }
  return { choices, notes };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function withNote(answers: StyleAnswers, key: StyleQuestionKey, value: string): StyleAnswers {
  return { ...answers, notes: { ...answers.notes, [key]: value.slice(0, NOTE_MAX) } };
}

export function sameStyle(a: StyleAnswers, b: StyleAnswers): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
