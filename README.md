<div>
<img src="docs/images/logo.png"/>
</div>

CreatorPantry is an AI-assisted productivity platform for food bloggers and content creators. It helps a creator move from a tested recipe and source material to a coordinated package of blog copy, recipe cards, social posts, newsletters, SEO metadata, imagery, and scheduled publications.

It is intentionally **not another recipe index**. The product is a private creator workspace: a place to develop, organize, refine, reuse, and publish food content without losing ownership of the canonical recipe or brand voice.

This starter contains the project constitution and Claude Code toolkit adapted from the architectural discipline of AegisScribe while removing its game-specific domain, providers, endpoints, mobile assumptions, and compliance rules.

## Product pillars

- **Recipe workspace:** structured recipes, creator-entered language, ordered steps, equipment, notes, yields, versions, and deterministic scaling.
- **Content studio:** canonical sources transformed into blog, email, social, recipe-card, and SEO drafts.
- **Media library:** originals, derivatives, AI generations, crops, alt text, provenance, and reusable visual briefs.
- **Brand intelligence:** workspace-specific voice, audience, style, examples, and constraints.
- **Editorial planning:** content projects, status, assignments, schedules, publication targets, and audit history.
- **AI assistance:** grounded drafting, refinement, repurposing, semantic search, and structured actions with creator approval.
- **Publishing adapters:** provider-neutral publication records and gateways for external platforms.

## Architecture

The backend uses the same seam for every API feature:

```text
Controller → Facade → Business → DataLayer → Repository | Gateway
```

Controllers live in `CreatorPantry.ApiService`; the remaining layers live in `CreatorPantry.Domain`. Controllers accept ViewModels and return ServiceModels. EF entities do not cross the HTTP boundary. Semantic Kernel plugins call facades, so AI requests inherit the same validation, authorization, workspace isolation, and caching as normal requests.

CreatorPantry is multi-workspace. A single user may own a personal blog workspace, contribute to a client workspace, and edit an agency workspace. ASP.NET Core Identity establishes identity; workspace membership establishes authorization. Roles begin with `Viewer`, `Contributor`, `Editor`, and `Owner`. `PlatformAdmin` is the only platform-wide role.

## Technology baseline

- .NET 10, Aspire AppHost, and ServiceDefaults
- ASP.NET Core Web API and EF Core 10
- SQL Server 2025 with native vector support
- Redis
- Angular 22 with standalone components and strict TypeScript
- YARP backend-for-frontend
- ASP.NET Core Identity
- Microsoft.Extensions.AI and Semantic Kernel
- Azure AI Foundry or a configured compatible model provider
- Blob/object storage for media
- OpenTelemetry for logs, metrics, and traces

Versions are a starting baseline. Confirm current compatible package versions before the first implementation pass.

## Intended repository layout

```text
.
├── CLAUDE.md                         # auto-loaded project constitution
├── README.md
├── .claude/
│   ├── README.md                     # toolkit map and precedence
│   ├── settings.json                 # conservative command and hook policy
│   ├── rules/                        # architecture and domain rules
│   ├── skills/                       # deep implementation playbooks
│   ├── agents/                       # read-only review specialists
│   └── hooks/                        # secret and formatting guards
├── docs/
│   ├── architecture.md
│   └── scrub-prompts.md
└── src/
    ├── CreatorPantry.AppHost/
    ├── CreatorPantry.ServiceDefaults/
    ├── CreatorPantry.Gateway/
    ├── CreatorPantry.Web/
    ├── CreatorPantry.ApiService/
    ├── CreatorPantry.Domain/
    ├── CreatorPantry.Worker/
    ├── CreatorPantry.MigrationService/
    ├── CreatorPantry.Tests/
    └── web/                           # installed Angular 22 design-system workspace
        ├── DESIGN-SYSTEM.md
        ├── src/                       # showcase/product shell
        └── projects/creator-pantry-ui/# @creator-pantry/ui library
```

## Data ownership

Platform reference data and creator-owned content are deliberately separated.

| Platform reference data                   | Workspace-owned content                    |
| ----------------------------------------- | ------------------------------------------ |
| Ingredient, alias, unit, conversion       | Recipe, version, ingredient line, step     |
| Cuisine, technique, equipment type        | Content project, article, derivative copy  |
| Dietary and allergen vocabulary           | Media asset, visual brief, generated asset |
| Food categories and vetted references     | Brand voice, audience, SEO brief           |
| Shared templates approved by the platform | Workspace template, publication, audit log |

Normalized reference data may enrich creator content, but it never destructively replaces the creator's wording.

## AI principles

- AI outputs are drafts or structured proposals, not silent mutations.
- Deterministic code performs scaling, conversions, arithmetic, and time calculations.
- Model-generated database queries are prohibited.
- Prompts are versioned files, not large string literals hidden in application code.
- Every generation records provenance and operational metadata.
- Private workspace material never crosses workspace boundaries.
- Food safety, allergens, health, and nutrition require explicit uncertainty and appropriate references.

## Getting started

1. Extract the repository drop-in archive into the root of the CreatorPantry clone and allow directories to merge.

2. Review every item marked `DECIDE` or `TBD`; they are intentional product decisions, not missing implementation.

3. Read `src/web/README.md` and `src/web/DESIGN-SYSTEM.md`. The supplied Angular 22 workspace and `@creator-pantry/ui` package are already installed under `src/web/`.

4. Install and verify the frontend:
   
   ```bash
   cd src/web
   npm ci
   npm run build
   npm test
   cd ../..
   ```

5. Make the hooks executable:
   
   ```bash
   chmod +x .claude/hooks/*.sh
   git update-index --chmod=+x .claude/hooks/*.sh
   ```

6. Scaffold the .NET solution and resources through the AppHost.

7. Build feature seams with the matching `.claude/skills/` playbook and atomic SCRUB prompts.

8. Run `aspire run` for the complete local application and `dotnet test` plus the frontend commands above for verification.

## Installed design system

The drop-in includes the actual CreatorPantry Angular 22 design system rather than a placeholder reference.

- Package: `@creator-pantry/ui`
- Source: `src/web/projects/creator-pantry-ui/src/lib/`
- Public exports: `src/web/projects/creator-pantry-ui/src/public-api.ts`
- Tokens: `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css`
- Themes: `src/web/projects/creator-pantry-ui/src/lib/styles/themes.css`
- Global foundation: `src/web/projects/creator-pantry-ui/src/lib/styles/global.css`
- Showcase: `src/web/src/app/`
- Theme contract: `data-cp-theme="light|dark"` on `<html>`, managed by `CpThemeService`

The current public components are button, card, badge, field, progress, dialog, and quick action. New reusable UI extends this library and its public API; product workflow components stay in feature folders until repetition proves they belong in the library.

## Toolkit philosophy

The root `CLAUDE.md` holds stable project memory. Rules define non-negotiable boundaries. Skills explain how to implement a task within those boundaries. Agents inspect completed work. Hooks catch a small set of mechanical failures. Prompts should therefore remain small and specific instead of repeating the architecture on every turn.

The files are deliberately editable. Tune naming, providers, roles, and product scope as decisions become concrete, but preserve the core separation between creator-owned sources, derived content, external publication state, and AI-generated proposals.
