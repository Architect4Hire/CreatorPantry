# Installed CreatorPantry Tokens

The authoritative values live in:

- `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css`
- `src/web/projects/creator-pantry-ui/src/lib/styles/themes.css`

Primitive scales include `--cp-font-*`, `--cp-space-*`, `--cp-radius-*`, `--cp-duration-*`, `--cp-ease`, `--cp-sidebar-width`, and `--cp-topbar-height`.

Semantic theme tokens include `--cp-bg`, `--cp-surface*`, `--cp-text*`, `--cp-border*`, `--cp-primary*`, tool tones (`--cp-pink*`, `--cp-orange*`, `--cp-purple*`, `--cp-blue*`), `--cp-danger`, `--cp-focus`, and shadows.

Do not duplicate values here. Read the CSS before use. Add a missing semantic intent to both theme blocks, then consume the semantic name from components and features.

