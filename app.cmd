@echo off
rem Roda o app a partir do codigo, para testar local: gera a interface e sobe o servidor (o navegador abre sozinho).
rem Requisitos: .NET SDK 10 e Node.js 22. No PowerShell: .\app.cmd   No cmd: app
rem Opcoes do app vao no fim, por exemplo: .\app.cmd --Ai:Mode=none
pushd "%~dp0web"
echo Gerando a interface (npm ci e build)...
call npm ci --no-audit --no-fund || goto :erro
call npm run build:app || goto :erro
popd
pushd "%~dp0src\Lab.App"
dotnet run -c Release --no-launch-profile -- %*
set code=%errorlevel%
popd
exit /b %code%

:erro
set code=%errorlevel%
popd
echo Nao consegui gerar a interface. Confira o Node.js 22 com: node --version
exit /b %code%
