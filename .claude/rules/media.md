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

Alt text must describe known visible content. When the system has not analyzed the pixels, it must not claim image details from the filename or recipe alone.

