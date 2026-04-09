#!/bin/bash
# Install my-mcps — thin wrapper, delegates to multiagent-setup CLI
# Usage: ./tools/install-mcps.sh [options]
# Options:
#   --docker                  Use local Docker containers
#   --manual                  Enter connection strings manually
#   --age-conn <str>          AGE connection string
#   --obrien-conn <str>       O'Brien connection string
#   --target <dir>            Target dir for age-mcp clone

set -euo pipefail
export PATH="$PATH:$HOME/.dotnet/tools"

if ! command -v multiagent-setup &>/dev/null; then
  echo "  ..  Installing multiagent-setup..."
  dotnet tool install -g multiagent-setup 2>/dev/null \
    || dotnet tool update -g multiagent-setup
fi

exec multiagent-setup install-mcps "$@"
