@echo off
rem Inicia o ComprasGOV em producao. Chamado pela tarefa agendada "ComprasGOV"
rem (gatilho de boot, sem sessao aberta). O deploy copia este arquivo para a raiz
rem da instalacao (padrao C:\Services\ComprasGOV), ao lado das pastas app\, config\ e logs\.
rem
rem   config\appsettings.json  configuracao de producao (fora de app\, que e trocada a cada deploy)
rem   logs\comprasgov-*.log    log do proprio programa, com rotacao diaria/por tamanho e retencao
rem   logs\estado.json         heartbeat conferido pelo deploy e pelo saude.cmd
rem   logs\console.log         so a saida inesperada de console (ex.: falha grave do .NET);
rem                            recriado a cada inicio, entao nao cresce sem limite
chcp 65001 >nul
set DOTNET_ENVIRONMENT=Production
set COMPRASGOV_CONFIG=%~dp0config\appsettings.json
set COMPRASGOV_PASTA_LOGS=%~dp0logs
set COMPRASGOV_LOG_CONSOLE=false
set COMPRASGOV_ARQUIVO_PARADA=%~dp0parar.flag
if not exist "%~dp0logs" mkdir "%~dp0logs"
cd /d "%~dp0app"
ComprasGOV.exe > "%~dp0logs\console.log" 2>&1
exit /b %ERRORLEVEL%
