#!/usr/bin/env bash
# PostToolUse hook: format the file Claude just edited.
# Claude Code passes the hook context as JSON on stdin; we pull out the edited
# file path and run the right formatter. Formatting failures never block the edit.
# Deliberately no `set -e`: a formatting hook must never fail the edit it follows, and a malformed
# payload (jq erroring on non-JSON) would otherwise exit nonzero and show up as hook noise.
set -uo pipefail

input="$(cat)"

# Extract the edited file path (jq preferred, grep fallback if jq isn't installed).
if command -v jq >/dev/null 2>&1; then
  file="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty' 2>/dev/null || true)"
else
  file="$(printf '%s' "$input" \
    | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -1 | sed 's/.*"file_path"[[:space:]]*:[[:space:]]*"//; s/"$//')"
fi

[ -z "${file:-}" ] && exit 0
[ -f "$file" ] || exit 0

case "$file" in
  *.cs)
    # Format just this file against the solution.
    dotnet format --include "$file" >/dev/null 2>&1 || true
    ;;
  *.ts|*.html|*.scss|*.css|*.json)
    # prettier is a devDependency of the Angular app, not of the repo root. A bare `npx prettier`
    # here finds no node_modules at the root and silently downloads a floating version from the
    # registry — so the hook would format with a different prettier than the app pins, and would need
    # the network to run at all. Invoke the app's own pinned binary instead.
    prettier="$(git rev-parse --show-toplevel)/src/web/node_modules/.bin/prettier"
    if [ -x "$prettier" ]; then
      "$prettier" --write "$file" >/dev/null 2>&1 || true
    else
      # Not silently: a missing formatter should be fixable, not invisible. Still exit 0 — a format
      # hook never blocks an edit.
      echo "format.sh: prettier not installed; skipped $file (run: npm ci --prefix src/web)" >&2
    fi
    ;;
esac

exit 0
