# Media

Media bytes live in object storage; metadata, ownership, relationships, and processing status live in SQL.

## Asset types

- `Original`: creator upload, immutable.
- `Imported`: obtained through an integration with recorded source and rights metadata.
- `Derived`: crop, resize, thumbnail, optimization, or format conversion tied to a source asset.
- `AiGenerated`: generated output with prompt-template and model provenance.

## Required metadata

Workspace, MIME type, byte size, dimensions/duration, checksum, source type, created time, processing status, alt text, rights/attribution, parent asset, crop/aspect variant, and AI provenance when applicable.

## Access and processing

- Never expose storage credentials or durable public container access.
- Authorize metadata first, then issue short-lived controlled access or proxy the content as designed.
- Validate type using file signatures, not extension alone; enforce size/dimension limits and malware scanning policy.
- Strip unsafe metadata where appropriate while preserving creator-required copyright fields.
- Processing is idempotent and runs as a durable background job.
- Deleting an original follows retention rules and accounts for derivatives and published references.

## The shared upload controls (15.2)

- Every picture, wherever it enters or is embedded, is read by `Managers/FileInspection/ImageFileInspector`:
  PNG, JPEG, WebP and GIF walked end to end. Bytes after a format's own end are refused, and so is a ZIP
  directory reachable from the end of any file. The one allowance is complete JPEGs after a JPEG — a gain map,
  a depth map or a multi-picture file. A motion photo's video tail is refused. Do not write a module-private
  signature sniffer.
- Read untrusted bytes with `BoundedCopy`, never an unbounded `CopyToAsync`, when the whole file has to be in hand.
- Originals keep their EXIF. A GPS position is reported (`MediaAssetVersion.ContainsLocationMetadata`, null when
  nobody checked), and renditions — which carry no metadata — are what pages show.
- Object keys are built from server identifiers only and stay inside `Managers/Storage/ObjectKeyGrammar`. The store
  refuses anything outside it, and only a module's `*ObjectGateway` may take `IPrivateObjectStore`
  (`ObjectStoreBoundaryTests`).
- Every path that stores bytes from outside scans them through the DataLayer before writing, typed text included.
- A raised request-size limit is set both on the API action and on the gateway route.
  `UploadRouteLimitDriftTests` fails when the two disagree.

Alt text must describe known visible content. When the system has not analyzed the pixels, it must not claim image details from the filename or recipe alone.

