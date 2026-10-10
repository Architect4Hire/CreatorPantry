# AI Fluency Decisions

*Status: Accepted — 2026-10-09. Recorded by AI-fluency prompt AF.1.1.*

This record fixes the product decisions behind the AI-fluency work — turning AI Recipe Studio, Image Studio
and the Content Pipeline into one connected subsystem — so the AF prompts build on a stated baseline instead
of an assumption. It does not implement code, and it records nothing the AF prompt document does not state.

Identifiers continue the `B-` sequence from [`baseline.md`](baseline.md), which ends at B-26. Do not renumber
them; later documents may reference them.

## Sources

- `docs/prompts/creatorpantry-ai-fluency-scrub-prompts.md` — the AF prompts, the feedback table, and the
  decisions recorded here.
- Creator feedback received 2026-10-09, tracked as FLU-001 through FLU-009 in that document.
- Product-owner answers given 2026-10-09 — the four decisions below.
- `docs/prompts/creatorpantry-scrub-microprompts.md` — the master library whose prompts are superseded or
  narrowed.
- `CLAUDE.md` and `.claude/rules/` — architectural source of truth. Nothing here redefines them.

## Decision table

| ID | Topic | Feedback | Status | Decision |
| --- | --- | --- | --- | --- |
| B-27 | Posts for creator-selected channels | FLU-001 | Decided | Posts are written for the channels the creator picks from `ContentChannelCatalog`, not a fixed set of seven. |
| B-28 | Image compression | FLU-007 | Decided | Server-side, with no external NuGet image package if at all possible. |
| B-29 | Themed-day memory | FLU-009 | Decided | Creator-written standing notes plus derived history. Nothing is learned or written by a model. |
| B-30 | Scope: connected flows | FLU-001 – FLU-009 | Decided | Every feedback item is fixed and work can be handed from any AI surface to any other. No chat assistant. |
| B-31 | `CreativeContext` | FLU-003, 004, 006, 008, 009 | Concept recorded; entity design is AF.1.2's | One server-side record of what a piece of creative work is about, holding references by id. |

## Decisions

### B-27 Posts for creator-selected channels

- **Posts are written for the channels the creator picks**, not a fixed set of seven outputs.
- **The vocabulary is the existing `ContentChannelCatalog`.** No second channel list is introduced.
- **For the owned channels the output is short-form:** `blog` yields a blog intro and `newsletter` a
  newsletter blurb.
- **The long-form article stays with `content.editorial-package`** (master 11.4). Long-form blog articles and
  SEO packages are out of bounds for the AF work.

**Confirmed 2026-10-10:** the short-form reading of `blog` and `newsletter` was approved with the AF.6.2
profile table. See [Open items](#open-items).
**Consequences:** the seven-output social model and generation prompts in the master library are superseded —
see [Supersessions](#supersessions).
**Rules:** `content.md`, `ai.md`.

### B-28 Image compression

- **Compression is server-side.**
- **No external NuGet package, if at all possible.** .NET has no built-in cross-platform image codec, so this
  means a small managed codec owned by the Media module, plus asking the image provider for a smaller format
  where it supports one.
- **AF.5.1 proves that out before anything is built on it**, and adds its findings to this record as a section
  of its own: what the provider can return, the managed path, the rendition set, and the input formats in
  scope.

**Options considered:** `System.Drawing` was rejected — it is Windows-only and is not an option for a
containerised Worker.
**Consequences:** if the AF.5.1 spike shows the managed path is not viable, the work stops and reports with
the numbers rather than adding a dependency. See [Open items](#open-items).
**Rules:** `media.md`, `external.md`.

### B-29 Themed-day memory

- **Day memory is creator-written notes plus history.**
- **Each themed day carries standing instructions the creator writes.**
- **Generation is shown what was already made for that day** so it does not repeat itself.
- **Nothing is learned or written by a model.** Any model-written memory is out of bounds for the AF work.

**Rules:** `ai.md`, `tenancy.md`.

### B-30 Scope: connected flows

- **Every feedback item (FLU-001 through FLU-009) is fixed.**
- **A shared creative context lets a concept, recipe, image, prompt, or themed day be handed from any AI
  surface to any other** — see [B-31](#b-31-creativecontext).
- **No chat assistant.** A conversational assistant is out of bounds for the AF work.

**Consequences:** also out of bounds for the AF prompts — scheduling, the content board and publishing (master
Phase 13), and an allowance charge for image generation, which 12.10l left open and which still needs a prompt
of its own.
**Rules:** `ai.md`, `content.md`.

### B-31 CreativeContext

*The concept as AF.1.2 states it. The entity, its indexes, delete behaviours and migration are designed and
approved in AF.1.2; this entry fixes only what that prompt already commits to.*

- **`CreativeContext` is the server-side record of what one piece of creative work is about.** It is
  workspace-owned and lives in the Content module. It answers the two structural gaps under the feedback:
  Content Pipeline and Image Studio state lived only in the browser, and there was no shared record each
  surface could read instead of re-asking for the recipe, the channel, the day and the picture.
- **It holds the creator's own words** — a working title and the picture they have in mind — the chosen
  channel keys, an optional day of week and weekly-theme key, and an ordered set of `CreativeContextReference`
  rows.
- **Each reference names one source by kind and id:** recipe (plus pinned version), recipe concept (request id
  plus concept id), DAM asset (plus version), staged generated image, prompt record, social package.
- **References are ids, never copies.** A context does not snapshot recipe text, image bytes or prompt bodies,
  and never becomes a competing source of truth.
- **No step or progress state.** That belongs to 13A's `WorkflowRun`, which links to a context by id. Neither
  replaces the other.
- **A reference whose target is later deleted or archived must not break the context.**
- **No provider-specific field.** Foreign keys that cross modules name entity types only; everything else
  crosses facade to facade.
- **It carries a row version for concurrency, created-by, and archived-at.**

**Consequences:** generation tasks read a context through a deterministic, bounded grounding package (AF.1.5),
mirroring `BrandContextAssembler` and the `BrandContextPackage` ([B-24](baseline.md)).
**Rules:** `tenancy.md`, `backend.md`, `content.md`, `ai.md`.

## B-28 findings: compression without a package (AF.5.1)

*Status: Accepted — 2026-10-10. Recorded by AF.5.1 and approved by the product owner the same day.*

**The managed path is viable, and is the method.** A PNG decoder on `System.IO.Compression`, an
area-average downscaler and a baseline JPEG encoder, none of them using a package, turned 15 real generated
pictures averaging 1.82 MB into a 153 KB web rendition and a 37 KB thumbnail in about 35 ms a picture. The
provider keeps being asked for PNG.

### (a) What the Venice image API can return

Sources, both read 2026-10-10: the `POST /image/generate` reference at
<https://docs.venice.ai/api-reference/endpoint/image/generate>, and the live model list at
`GET https://api.venice.ai/api/v1/models?type=image` (45 image models).

| Field | Documented | What it means here |
| --- | --- | --- |
| `format` | `jpeg`, `png`, `webp`; **default `webp`** | `VeniceImageGenerator` sends `png` explicitly, so the default never applies. |
| `width`, `height` | default 1024, maximum 1280 | Pixel-sized models only. Aspect-ratio models reject them with a 400. |
| `aspect_ratio`, `resolution` | model-dependent; `resolution` is `1K`, `2K` or `4K` | The only way to size a resolution-tier model. |
| `quality` | `low`, `medium`, `high`, model-dependent | Generation fidelity and price. **Not** an output-compression setting. |
| `return_binary` | default `false` | Raw bytes instead of base64 JSON. Saves transfer, not storage. |
| `embed_exif_metadata` | default `false` | Would write the prompt into the file. Stays off: a prompt is private content. |

- **There is no output-compression control.** No documented field sets JPEG or WebP quality. The provider's
  only lever on file size is which format it encodes to.
- **Sizing is per model, and the configured model is not pixel-sized.** `gpt-image-2-5-flare` (the
  `Venice:ImageModel` default) declares resolutions `1K`/`2K`/`4K` (default `1K`), eight aspect ratios (default
  `1:1`) and qualities `low`/`medium`/`high` (default `high`). Of the 45 models, 38 declare aspect ratios —
  17 of them a resolution tier as well — and 7 declare neither.
- **Not documented:** the pixel dimensions behind each tier, and whether a `jpeg` response is baseline or
  progressive.
- **Observed and unexplained:** all 15 stored pictures are 1024×768, and 4:3 is not among the configured
  model's listed aspect ratios. The spike measured the files; it did not establish which model made them.

**Decision on the provider side: keep requesting `png`, and send no size.**

- The stored original is the creator's picture and the source of every rendition, so it should be lossless.
- PNG is the one format the managed codec can read. Asking for `webp` would make a smaller original that can
  never be given a thumbnail — the wrong trade.
- A smaller provider format would also lose quality twice: once at the provider, at a setting nobody can
  choose, and again in the rendition.
- Requesting fewer pixels is a product decision about what the creator gets, not compression, and no field
  expresses it across models. It is out of scope here.

### (b) The managed path, measured

A throwaway console project in the session scratch directory, .NET 10, Release, one thread, no package
reference. Nothing from it is in the repository.

- **PNG decode:** chunk walk with CRC check, `ZLibStream` inflate, the five row filters, to RGBA.
- **Downscale:** separable area average — each source pixel weighted by how much of it the target pixel covers.
- **JPEG encode:** baseline JFIF, standard quantisation tables scaled by quality, Annex K Huffman tables,
  4:2:0 or 4:4:4.

**Input.** 15 pictures read from the local `generated-images` container: every one 1024×768, 8-bit truecolour
(colour type 2), non-interlaced, no alpha; 1.39–2.19 MB, mean 1.82 MB.

**Output size and time, per picture.**

| Rendition | Pixels | Mean bytes | Range | Of source | Time |
| --- | --- | --- | --- | --- | --- |
| Web, quality 82, 4:2:0 | 1024×768 (not upscaled) | 152,863 | 106,229 – 208,006 | 8.4% | encode 14–18 ms |
| Thumbnail, quality 78, 4:2:0 | 480×360 | 36,887 | 26,587 – 50,166 | 2.0% | scale 3–4 ms, encode 3–4 ms |

PNG decode took 9–12 ms a picture, so both renditions cost about 35 ms end to end.

**Quality sweep, mean bytes over the 15 pictures.**

| Quality | Web 4:2:0 | Web 4:4:4 | Thumbnail 4:2:0 | Thumbnail 4:4:4 |
| --- | --- | --- | --- | --- |
| 70 | 114,754 | 145,925 | 30,631 | 38,756 |
| 78 | 137,165 | 175,169 | 36,887 | 46,919 |
| 82 | 152,863 | 195,984 | 41,272 | 52,780 |
| 85 | 168,893 | 217,218 | 45,749 | 58,773 |
| 90 | 212,584 | 272,836 | 57,727 | 74,692 |

**Fidelity.** Every output was decoded by a second, independent decoder (GDI+, used as a verifier only) and
compared with the pixels that went into the encoder. All 30 decoded at the right dimensions. PSNR: web at
quality 82 4:2:0 mean 35.0 dB, worst 32.6; thumbnail at quality 78 4:2:0 mean 32.0 dB, worst 29.2. 4:4:4 at
quality 82 buys 2.1 dB for 28% more bytes. One web rendition was also opened and looked at; it shows no
visible blocking. Real-browser decoding is not proven here — that is AF.5.3's test.

**Larger inputs.** No larger real picture exists locally, so three were manufactured by tiling a real one.
Times are meaningful; the byte counts of tiled content are not and are left out.

| Source | Decode | Web 1600×1200: scale + encode | Thumbnail: scale + encode |
| --- | --- | --- | --- |
| 2048×1536 (3.1 MP) | 62 ms | 19 + 36 ms | 6 + 3 ms |
| 4096×3072 (12.6 MP) | 188 ms | 39 + 38 ms | 20 + 4 ms |
| 7168×5376 (38.5 MP) | 505 ms | 77 + 40 ms | 53 + 4 ms |

**Memory is the one real constraint.** The spike holds the whole picture three times — inflated rows, RGBA and
RGB, about 10 bytes a pixel — which is 385 MB at 38.5 MP and would be 500 MB at the 50 MP `MediaPolicy`
limit. Time is not the problem; a whole-picture buffer in a containerised Worker is.

- **AF.5.2 needs a rendition pixel cap of its own, below `ImageMaxPixels`.** Proposed: 24 MP, which covers a
  4096×4096 picture (16.8 MP) and costs about 100 MB as an RGBA buffer. Above it the source is reported as not
  compressed. The alternative is a row-streamed decoder feeding the scaler, which bounds memory by the output
  and is a larger piece of work than AF.5.2 describes.
- **CRC-32 has to be written by hand.** `System.IO.Hashing` is a NuGet package, not part of the shared
  framework. `ZLibStream` is in the box and checks Adler-32 itself.

### (c) The rendition set

| Purpose | Box (long edge) | Encoding | Used for |
| --- | --- | --- | --- |
| `web` | 1600 px | baseline JPEG, quality 82, 4:2:0 | Anywhere the picture is shown at reading size. |
| `thumbnail` | 480 px | baseline JPEG, quality 78, 4:2:0 | Grids, pickers, cards — up to 240 CSS px at 2× density. |

- **Never upscaled.** A source inside the box keeps its dimensions and is only re-encoded; that is every
  picture generated today, and it is still an 11.9× reduction.
- **Aspect ratio is preserved.** A rendition is fitted to the box, never cropped.
- **The original is untouched** and remains what a download or a publish uses.
- **A picture with any non-opaque pixel is not flattened, and has no rendition.** It is recorded as not
  compressed and served as stored. *Amended 2026-10-10 by AF.5.5, approved by the product owner: this
  first read "its renditions stay PNG, downscaled", which nothing in Phase 5 can write — there is a PNG
  decoder and no PNG encoder. All 15 generated pictures measured were opaque.*
- **A rendition that is not smaller than its source is discarded.**
- **Renditions carry no metadata** — no EXIF, no prompt, no colour profile.

### (d) Input formats in scope

| Source | Renditions | Why |
| --- | --- | --- |
| PNG, 8-bit, non-interlaced: greyscale, truecolour, palette, each with or without alpha | Yes | The decoder's whole matrix. The provider emits only 8-bit truecolour. |
| PNG, 16-bit | Yes, reduced to 8 bits | One extra branch; the spike handles it. |
| PNG, interlaced (Adam7) | No — refused explicitly | Not emitted by the provider; real work for no observed input. |
| PNG, greyscale or palette below 8 bits | No — refused explicitly | Same reason. |
| JPEG, baseline or progressive | No | There is no JPEG decoder in this plan at all. |
| WebP | No | A VP8/VP8L decoder is far beyond a small managed codec. |
| GIF | No | Animation, and no rendition worth having. |

A source the codec cannot read is **served as stored and recorded as not compressed, with the reason**. It is
not retried.

*Added 2026-10-10 by AF.5.5, approved by the product owner:* a failure that might not be permanent — storage
that cannot be reached, source bytes that cannot be found — is retried by the outbox, five attempts over
about two and a half minutes. If the last attempt still fails and storage is reachable, the picture is
recorded as not compressed with the reason `RetriesExhausted` and is not attempted again; nothing re-queues
such a row yet. If storage itself is down for the last attempt, nothing is recorded, and the backfill asks
again once the picture is an hour old, for as long as that lasts.

### What the no-package route cannot do

- **It cannot shrink a JPEG, WebP or GIF.** FLU-007 is about generated pictures, which are PNG, so it is met.
  A creator's own JPEG upload in the DAM gets no web rendition and no thumbnail and is served at full size.
  A baseline-only JPEG decoder could be added later without a package; progressive JPEG and WebP could not,
  realistically.
- **It cannot write WebP or AVIF.** Renditions are baseline JPEG, which is larger than either at the same
  quality. How much larger was not measured.
- **No progressive output and no optimised Huffman tables.** Both are possible later; neither was measured.
- **No colour management.** A PNG carrying a non-sRGB profile or gamma is treated as sRGB.
- **The downscale averages in sRGB space, not linear light.** Slightly dark on fine high-contrast detail; not
  visible on the samples.

### Approved

The product owner approved all four on 2026-10-10. Later Phase 5 prompts build on them.

1. The managed path, and `format: "png"` with no size, as above.
2. The rendition set in (c): 1600 px at quality 82 and 480 px at quality 78, both 4:2:0.
3. The input matrix in (d), including that uploaded JPEGs are left as stored.
4. A 24 MP rendition cap in AF.5.2, in place of a streamed decoder. This narrows AF.5.2 as written, which
   bounds allocation only by the 50 MP `MediaPolicy` limit.

## Supersessions

The effect of the AF prompt document on master-library prompts that had not been run.

| Master prompt | Effect |
| --- | --- |
| 13.1 Social package model | **Superseded by AF.6.1** — creator-selected channels replace seven fixed outputs. |
| 13.2 Seven-output social generation | **Superseded by AF.6.3–AF.6.4.** |
| 13.3a Content Pipeline social step | **Superseded by AF.6.5.** |
| 13.7a Content Pipeline keeper commit | **Narrowed.** AF.4.3 delivers the DAM save and prompt lineage; the board card remains with 13.7a. |
| 9.4a / 9.4b first-draft review and create | Server side is built. AF.2.1–AF.2.2 deliver the missing web half. |
| 13.3 Social Studio UI | Unchanged, but builds on AF.6 rather than 13.1–13.2. |
| 13A.2 Workflow run entities | Unchanged. A `WorkflowRun` links to a `CreativeContext` by id; neither replaces the other. |
| 13A.19 "Create food images" workflow | Unchanged; it composes the pieces delivered by the AF prompts. |

13.1, 13.2, 13.3a and 13.7a each carry a one-line pointer in the master file. The superseded prompts are kept
there, unedited, for the record.

## Open items

### B-27 Short-form reading of `blog` and `newsletter` — DECIDED

- **Question:** does picking `blog` or `newsletter` produce a short-form piece (an intro, a blurb), as assumed?
- **Answer:** yes. Confirmed with the AF.6.2 profile table, approved 2026-10-10: a blog intro of at most 600
  characters and a newsletter blurb of at most 400, both editorial ceilings set in `ContentChannelProfiles.cs`.
- **Default assumed by later prompts:** short-form, with the long-form article left to master 11.4.

### B-28 How compression is done — DECIDED

- **Question:** can a small managed codec, with no external package, meet FLU-007?
- **Answer:** yes. See the AF.5.1 [findings](#b-28-findings-compression-without-a-package-af51), approved
  2026-10-10.
- **Default assumed by later prompts:** the managed path, the rendition set and the input matrix recorded
  there.
