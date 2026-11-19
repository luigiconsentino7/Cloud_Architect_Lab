#!/usr/bin/env bash
# Atalho para o CLI de conteúdo (lab): ./lab.sh challenge validate --quick
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"
exec dotnet run --project src/Lab.Cli -c Release --verbosity quiet -- "$@"
