@echo off
rem Inicia o ComprasGOV em producao. Chamado pela tarefa agendada "ComprasGOV"
rem (gatilho de boot, sem sessao aberta). O deploy copia este arquivo para a raiz
rem da instalacao (padrao C:\Services\ComprasGOV), ao lado das pastas app\ e logs\.
chcp 65001 >nul
set DOTNET_ENVIRONMENT=Production
set COMPRASGOV_ARQUIVO_PARADA=%~dp0parar.flag
cd /d "%~dp0app"
ComprasGOV.exe >> "%~dp0logs\comprasgov.log" 2>&1
exit /b %ERRORLEVEL%
