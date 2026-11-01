// The record on the page the creator is looking at, which a quick capture may link its task to (13A.31).

/** The request field a link is sent in. */
export type QuickCaptureLinkField = 'recipeId' | 'contentCardId' | 'mediaAssetId' | 'workflowRunId';

export interface QuickCaptureLink {
  readonly field: QuickCaptureLinkField;
  readonly id: string;
  /** How the offer reads: "Link it to the recipe you're viewing". */
  readonly label: string;
}

/**
 * The route parameter each linkable page names its record by, and how the offer to link it reads. Only these:
 * a link is a pointer to a record the page already shows, never a search for one.
 */
const PAGE_RECORDS: readonly {
  readonly param: string;
  readonly field: QuickCaptureLinkField;
  readonly label: string;
}[] = [
  { param: 'recipeId', field: 'recipeId', label: 'Link it to the recipe you’re viewing' },
  { param: 'cardId', field: 'contentCardId', label: 'Link it to the card you’re viewing' },
  { param: 'assetId', field: 'mediaAssetId', label: 'Link it to the picture you’re viewing' },
  { param: 'runId', field: 'workflowRunId', label: 'Link it to the workflow you’re in' },
];

/**
 * The page's record from its route parameters, in the order listed above, or null on a page about no one record.
 * No route names two of these today. A parameter that is not an id is ignored rather than sent.
 */
export function quickCaptureLinkFrom(params: ReadonlyMap<string, string>): QuickCaptureLink | null {
  for (const record of PAGE_RECORDS) {
    const id = params.get(record.param);
    if (id && UUID.test(id)) return { field: record.field, id, label: record.label };
  }

  return null;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
