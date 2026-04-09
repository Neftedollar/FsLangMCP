#!/bin/bash
# Sync agent roles — thin wrapper, delegates to multiagent-setup CLI
# Usage: ./tools/sync-roles.sh [--clone|--pull] [--agency-dir <path>]

set -euo pipefail
export PATH="$PATH:$HOME/.dotnet/tools"

if ! command -v multiagent-setup &>/dev/null; then
  echo "  ..  Installing multiagent-setup..."
  dotnet tool install -g multiagent-setup 2>/dev/null \
    || dotnet tool update -g multiagent-setup
fi

exec multiagent-setup sync-roles "$@"
