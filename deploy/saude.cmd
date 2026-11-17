@echo off
rem Verificacao de saude do ComprasGOV em producao (le o heartbeat logs\estado.json).
rem Codigo de saida: 0 = robo rodando e em dia; 1 = parado, travado ou com o banco inacessivel.
rem O deploy copia este arquivo para a raiz da instalacao (padrao C:\Services\ComprasGOV).
chcp 65001 >nul
"%~dp0app\ComprasGOV.exe" --saude "%~dp0logs\estado.json"
exit /b %ERRORLEVEL%
