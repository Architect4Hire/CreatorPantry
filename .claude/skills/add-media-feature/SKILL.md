---
name: add-media-feature
description: Implement uploads, metadata, crops, derivatives, processing, alt text, or AI-generated media.
---

# Add a Media Feature

1. Classify asset as Original, Imported, Derived, or AiGenerated.
2. Store bytes in object storage and metadata/ownership/status in SQL.
3. Authorize the workspace before granting short-lived access.
4. Validate signature, size, dimensions, and allowed MIME type; define scanning and metadata policy.
5. Keep originals immutable and link every derivative to its source.
6. Use durable idempotent jobs for image analysis, optimization, crops, thumbnails, and generation.
7. Record checksum, dimensions, provenance, rights/attribution, alt text, model/prompt reference, and processing errors.
8. Define deletion/retention behavior for derivatives and published references.
9. Test unauthorized access, forged extensions, duplicate processing, failed jobs, deletion dependencies, and cross-workspace retrieval.

Alt text may use actual image analysis or confirmed metadata; it must not infer visible facts from the recipe title alone.

