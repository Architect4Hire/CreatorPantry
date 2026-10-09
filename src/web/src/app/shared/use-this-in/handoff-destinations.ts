// Where a piece of work can be handed to, and the route each destination opens with a creative context.
//
// The context id is a path segment, after a literal `context` segment, in every destination. The literal is
// what keeps the routes unambiguous: the Content Pipeline's own step is a single segment too, and without it
// `content-pipeline/<id>` and `content-pipeline/<step>` would be the same shape.

/** The path segment that introduces a creative context id in a destination's route. */
export const CONTEXT_ROUTE_SEGMENT = 'context';

/** The route parameter a destination reads the id from. */
export const CONTEXT_ROUTE_PARAM = 'contextId';

/** One place the "Use this in…" control can send a creator. */
export interface HandoffDestination {
  /** Stable, unique within one control. What the control's outputs name the destination by. */
  readonly key: string;
  /** The button's words. Say where it goes or what it does: "Make a picture", not "Go". */
  readonly label: string;
  /** One short line under the button on what happens next. Optional. */
  readonly detail?: string;
  /** The one obvious next step, if there is one. At most one destination in a control should set it. */
  readonly primary?: boolean;
  /** The route segments under the workspace that open this destination with that context. */
  readonly route: (contextId: string) => readonly string[];
}

/**
 * The routes that accept a creative context, built in one place so a control and the route table cannot
 * disagree about their shape.
 */
export const HANDOFF_ROUTES = {
  /** `image-studio/context/:contextId` */
  imageStudio: (contextId: string): readonly string[] => ['image-studio', CONTEXT_ROUTE_SEGMENT, contextId],

  /** `workflows/content-pipeline/context/:contextId`, which opens at the pipeline's first step. */
  contentPipeline: (contextId: string): readonly string[] => [
    'workflows',
    'content-pipeline',
    CONTEXT_ROUTE_SEGMENT,
    contextId,
  ],

  /** `ai-recipe-studio/draft/context/:contextId` */
  recipeDraft: (contextId: string): readonly string[] => ['ai-recipe-studio', 'draft', CONTEXT_ROUTE_SEGMENT, contextId],
} as const;
