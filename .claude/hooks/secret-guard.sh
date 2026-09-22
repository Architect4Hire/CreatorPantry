#!/usr/bin/env bash
set -euo pipefail

# Claude Code passes tool input as JSON on stdin. Reject obvious credential material.
payload="$(cat)"

if printf '%s' "$payload" | grep -Eiq '(BEGIN (RSA|EC|OPENSSH|PRIVATE) KEY|AccountKey=|SharedAccessSignature=|client[_-]?secret[[:space:]]*[:=][[:space:]]*["'"'][^"'"']+|api[_-]?key[[:space:]]*[:=][[:space:]]*["'"'][^"'"']+|password[[:space:]]*[:=][[:space:]]*["'"'][^"'"']+)'; then
  echo "Blocked: possible secret or private key in file content. Use user-secrets, Aspire secret parameters, or the deployment secret store." >&2
  exit 2
fi

exit 0

