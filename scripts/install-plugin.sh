#!/usr/bin/env bash
# Install the DSH plugin on the server that runs DeepSeek Harness.
# Usage:
#   ./scripts/install-plugin.sh [profile] [plugin-dir-or-tgz]
#
# Preferred (no extraction layout issues):
#   ./scripts/install-plugin.sh web /path/to/dsh-board-link-0.1.0.tgz
#
# From an extracted plugin directory:
#   ./scripts/install-plugin.sh web /path/to/dsh-board-link
set -euo pipefail

PROFILE="${1:-web}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_INPUT="${2:-$SCRIPT_DIR/../plugin/dsh-board-link}"

if ! command -v dsh >/dev/null 2>&1; then
  echo "dsh is not on PATH. Run this on the DSH server, or add the dsh launcher to PATH." >&2
  exit 1
fi

if [ -f "$PLUGIN_INPUT" ]; then
  case "$PLUGIN_INPUT" in
    *.tgz|*.tar.gz) SPEC="file:$(cd "$(dirname "$PLUGIN_INPUT")" && pwd)/$(basename "$PLUGIN_INPUT")" ;;
    *.zip) echo "A .zip cannot be installed directly. Extract it, or use the .tgz package." >&2; exit 2 ;;
    *) echo "Unsupported plugin package: $PLUGIN_INPUT" >&2; exit 2 ;;
  esac
elif [ -d "$PLUGIN_INPUT" ]; then
  PLUGIN_INPUT="$(cd "$PLUGIN_INPUT" && pwd)"
  if [ ! -f "$PLUGIN_INPUT/package.json" ]; then
    echo "package.json not found in $PLUGIN_INPUT" >&2
    echo "This usually means the release ZIP was extracted with an old tool or an incomplete copy." >&2
    echo "Use the standalone plugin .tgz instead:" >&2
    echo "  dsh plugin --profile $PROFILE add file:/path/to/dsh-board-link-0.1.0.tgz" >&2
    exit 2
  fi
  SPEC="link:$PLUGIN_INPUT"
else
  SPEC="$PLUGIN_INPUT"
fi

echo "Installing $SPEC into DSH profile '$PROFILE'..."
dsh plugin --profile "$PROFILE" add "$SPEC"

cat <<'EOF'

Installed. The bundle is selected automatically. The plugin reads the agent token from
DSH_LINK_TOKEN in the DSH server process environment.

If your agent remote-forward port is not 18765, override the plugin config in
~/.dsh/profiles/<profile>/cordis.patch.yml:

- id: board-link
  config:
    baseUrl: 'http://127.0.0.1:18765'
    timeoutMs: 15000
    token: '...'

Then restart DSH (or let HMR reload the profile patch).
EOF
