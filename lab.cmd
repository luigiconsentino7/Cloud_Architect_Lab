@echo off
rem Atalho para o CLI de conteudo (lab) no Windows: lab challenge validate --quick
pushd "%~dp0"
dotnet run --project src\Lab.Cli -c Release --verbosity quiet -- %*
set code=%errorlevel%
popd
exit /b %code%
