#!/usr/bin/env bash
# Roda o app a partir do código, para testar local: gera a interface e sobe o servidor (o navegador abre sozinho).
# Requisitos: .NET SDK 10 e Node.js 22.22.3+, 24.15+ ou 26. Uso: ./app.sh   (opções do app no fim, ex.: ./app.sh --Ai:Mode=none)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root/web"
node scripts/prepare-app.mjs
echo "Gerando a interface (ng build)..."
npm run build:app
cd "$root/src/Lab.App"
exec dotnet run -c Release --no-launch-profile -- "$@"
