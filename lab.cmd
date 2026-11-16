@echo off
rem Atalho para o CLI de conteudo (lab). No PowerShell: .\lab.cmd challenge list   No cmd: lab challenge list
pushd "%~dp0"
dotnet run --project src\Lab.Cli -c Release --verbosity quiet -- %*
set code=%errorlevel%
popd
exit /b %code%
