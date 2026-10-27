import { ContentChannel } from '../../models/brand-profile.models';
import {
  ContentCard,
  ContentCardFields,
  ContentCardLineageEntry,
  ContentCardPublishing,
} from '../../models/content-board.models';

/** The limits the card routes hold a card to (`ContentCardPolicy`), so a field stops where the server would. */
export const CONTENT_CARD_TITLE_MAX = 200;
export const CONTENT_CARD_DESCRIPTION_MAX = 4000;
export const CONTENT_CARD_MOVE_NOTE_MAX = 500;

/** What a card says about delivery: a word, and the status pill's tone, which also gives it a glyph. */
export interface PublishingStatus {
  readonly label: string;
  readonly tone: 'neutral' | 'progress' | 'success';
}

/**
 * Where a card stands with publishing, from the server's own numbers and nothing else.
 *
 * **Never inferred from the column.** "Approved" is the creator's judgement of the work; whether anything went
 * out is a publication record's to say, and until one exists the answer is "Not scheduled" — for an approved
 * card as much as for a new one.
 */
export function publishingStatusOf(publishing: ContentCardPublishing | null): PublishingStatus {
  if (publishing === null || publishing.publicationCount === 0) return { label: 'Not scheduled', tone: 'neutral' };
  if (publishing.publishedCount > 0) return { label: 'Published', tone: 'success' };

  return { label: 'Publishing in progress', tone: 'progress' };
}

/** A channel's own name, or its key when the catalogue no longer lists it. */
export function channelNameOf(channels: readonly ContentChannel[], key: string | null): string {
  if (key === null) return '';

  return channels.find((channel) => channel.key === key)?.displayName ?? key;
}

/** The editable fields of a card as it stands, for a form to start from and to compare against. */
export function fieldsOf(card: ContentCard, description: string | null): ContentCardFields {
  return { title: card.title, description, channelKey: card.channelKey, plannedDate: card.plannedDate };
}

export const EMPTY_CARD_FIELDS: ContentCardFields = {
  title: '',
  description: null,
  channelKey: null,
  plannedDate: null,
};

/** What a creator typed, as it is sent: trimmed, and an emptied optional field as null. */
export function normalizedFields(fields: ContentCardFields): ContentCardFields {
  const description = fields.description?.trim() ?? '';

  return {
    title: fields.title.trim(),
    description: description === '' ? null : description,
    channelKey: fields.channelKey || null,
    plannedDate: fields.plannedDate || null,
  };
}

const LINEAGE_NOUNS: Record<ContentCardLineageEntry['kind'], string> = {
  CreativeContext: 'Piece of work',
  Recipe: 'Recipe',
  PromptRecord: 'Saved prompt',
  MediaAsset: 'Picture',
  SocialPackage: 'Posts',
};

export function lineageNounOf(entry: ContentCardLineageEntry): string {
  return LINEAGE_NOUNS[entry.kind];
}
