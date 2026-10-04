import { BrandStyleGuideRuleKind } from '../../models/brand-style-guide.models';

/**
 * How the guide screens write the vocabularies the API sends as enum names.
 *
 * Every section key the server defines has words here, so a key never reaches a creator as the identifier it
 * is stored under. The lookup below still falls back rather than throwing: `BrandStyleGuideSectionKey` can
 * grow, and a comparison row that disappeared because this build had no label for its key would hide a change
 * — which is the one thing the history screen exists to prevent.
 */
export const GUIDE_SECTION_LABELS: Readonly<Record<string, string>> = {
  Voice: 'Voice',
  Tone: 'Tone',
  Tenor: 'Tenor',
  WritingStyle: 'Writing style',
  Audience: 'Audience',
  PointOfView: 'Point of view',
  Vocabulary: 'Words you use',
  SentenceRhythm: 'Sentence rhythm',
  Formatting: 'Formatting',
  Storytelling: 'Storytelling',
  CallsToAction: 'Calls to action',
  ChannelVariant: 'Channel',
  BlogGuidance: 'Blog guidance',
  SocialGuidance: 'Social guidance',
  VisualIdentity: 'Visual identity',
  PhotographyDirection: 'Photography direction',
  ImagePromptGuidance: 'Image prompt guidance',
  NegativeVisualGuidance: 'What your imagery avoids',
  UserNotes: 'Your notes',
};

export const GUIDE_RULE_KIND_LABELS: Record<BrandStyleGuideRuleKind, string> = {
  Do: 'Always',
  Dont: 'Never',
};

/**
 * What one section of a guide is called on screen: its own words, with the channel named where a key holds
 * one per channel.
 *
 * `ChannelVariant` is the only key a version may hold more than once, so its label alone would name every
 * variant a guide has. An unknown key is spaced out from its identifier — "SomeNewKey" reads as "Some new
 * key" — which is worse than a written label and far better than nothing.
 */
export function guideSectionLabel(sectionKey: string, channelKey: string | null): string {
  const base = GUIDE_SECTION_LABELS[sectionKey] ?? humanise(sectionKey);

  return channelKey ? `${base}: ${channelKey}` : base;
}

/** "SomeNewKey" -> "Some new key". The fallback, never the path a defined key takes. */
function humanise(sectionKey: string): string {
  const spaced = sectionKey.replace(/([a-z0-9])([A-Z])/g, '$1 $2');

  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}
