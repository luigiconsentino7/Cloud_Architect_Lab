@echo off
rem Roda o app a partir do codigo, para testar local: gera a interface e sobe o servidor (o navegador abre sozinho).
rem Requisitos: .NET SDK 10 e Node.js 22.22.3+, 24.15+ ou 26. No PowerShell: .\app.cmd   No cmd: app
rem Opcoes do app vao no fim, por exemplo: .\app.cmd --Ai:Mode=none
pushd "%~dp0web"
node scripts\prepare-app.mjs || goto :erro
echo Gerando a interface (ng build)...
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
echo.
echo Nao consegui gerar a interface: a causa esta na mensagem acima.
exit /b %code%
