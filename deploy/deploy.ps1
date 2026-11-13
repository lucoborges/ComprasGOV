# ============================================================
# Deploy do ComprasGOV (tarefa agendada "ComprasGOV" no servidor).
#
# Ordem: publica a versao nova em uma pasta separada -> so entao para o robo ->
# troca as pastas -> inicia -> confere no log que a versao nova subiu e carregou o
# historico. Se nao subir, volta para a versao anterior e termina com erro.
#
# Estrutura em $Raiz (padrao C:\Services\ComprasGOV):
#   app\           versao em execucao
#   app-anterior\  versao anterior (usada para desfazer)
#   app-novo\      area de publicacao
#   logs\          log do robo (nao e apagado no deploy)
#   iniciar.cmd    lancador chamado pela tarefa agendada
#
# Compativel com PowerShell 4.0 (Windows Server 2012 R2). Precisa rodar como
# administrador (o runner do GitHub Actions roda como SYSTEM).
# ============================================================
param(
    [string]$Origem = (Split-Path $PSScriptRoot -Parent),
    [string]$Raiz = 'C:\Services\ComprasGOV',
    [string]$NomeTarefa = 'ComprasGOV',
    [string]$Versao = 'manual',
    # Tempo para o robo encerrar sozinho (sinal de parada) antes de ser finalizado a forca
    [int]$EsperaParadaSeg = 90,
    # Tempo para a versao iniciada aparecer no log com o historico carregado
    [int]$EsperaInicioSeg = 60,
    # Somente teste: trata a versao nova como reprovada para exercitar o desfazer
    [switch]$SimularFalha
)

$ErrorActionPreference = 'Stop'

$app = Join-Path $Raiz 'app'
$anterior = Join-Path $Raiz 'app-anterior'
$novo = Join-Path $Raiz 'app-novo'
$logs = Join-Path $Raiz 'logs'
$log = Join-Path $logs 'comprasgov.log'
$sinalParada = Join-Path $Raiz 'parar.flag'
$lancador = Join-Path $Raiz 'iniciar.cmd'

# Somente os processos desta instalacao (nunca um teste rodando de outra pasta)
function Obter-Processos {
    @(Get-Process -Name 'ComprasGOV' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Raiz, [StringComparison]::OrdinalIgnoreCase) })
}

function Parar-Robo {
    if ((Obter-Processos).Count -gt 0) {
        # Parada limpa: o robo termina a etapa atual e sai sozinho
        Set-Content -Path $sinalParada -Value 'deploy' -Encoding ascii
        for ($i = 0; $i -lt $EsperaParadaSeg -and (Obter-Processos).Count -gt 0; $i += 2) { Start-Sleep -Seconds 2 }
    }

    Stop-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue
    $restantes = Obter-Processos
    if ($restantes.Count -gt 0) {
        Write-Host '   O robo nao encerrou sozinho a tempo; finalizando o processo.'
        $restantes | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $sinalParada -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Obter-TamanhoLog {
    if (Test-Path $log) { return (Get-Item $log).Length }
    return 0
}

# Texto gravado no log depois da posicao informada (o robo mantem o arquivo aberto)
function Ler-LogDesde([long]$posicao) {
    if (-not (Test-Path $log)) { return '' }
    $fs = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($fs.Length -le $posicao) { return '' }
        $fs.Seek($posicao, [IO.SeekOrigin]::Begin) | Out-Null
        $buffer = New-Object byte[] ($fs.Length - $posicao)
        $lidos = $fs.Read($buffer, 0, $buffer.Length)
        return [Text.Encoding]::UTF8.GetString($buffer, 0, $lidos)
    }
    finally { $fs.Close() }
}

# Inicia a tarefa e confere: processo vivo, versao esperada no log e historico carregado.
# (Os padroes evitam letras acentuadas para nao depender da codificacao do log.)
function Iniciar-EConferir([string]$versaoEsperada) {
    $posicao = Obter-TamanhoLog
    Start-ScheduledTask -TaskName $NomeTarefa

    for ($i = 0; $i -lt $EsperaInicioSeg; $i += 2) {
        Start-Sleep -Seconds 2
        $texto = Ler-LogDesde $posicao

        if ($texto -match 'Ciclo adiado') {
            Write-Host '   O robo iniciou, mas adiou o ciclo (historico inacessivel ou em uso).'
            return $false
        }

        $versaoOk = (-not $versaoEsperada) -or ($texto -match ('Vers.{1,3}o: ' + [regex]::Escape($versaoEsperada)))
        if ($versaoOk -and $texto -match 'rico carregado: \d+ registro') {
            # Continua vivo depois de carregar?
            Start-Sleep -Seconds 3
            return ((Obter-Processos).Count -gt 0)
        }
    }

    Write-Host "   Em $EsperaInicioSeg s o log nao mostrou a versao iniciada com o historico carregado."
    return $false
}

function Renomear-ComRepeticao([string]$de, [string]$para) {
    for ($i = 1; ; $i++) {
        try { Rename-Item $de $para; return }
        catch {
            if ($i -ge 5) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

Write-Host "1. Publicando a versao $Versao (o robo continua rodando)..."
if (Test-Path $novo) { Remove-Item $novo -Recurse -Force }
New-Item -ItemType Directory -Force $logs | Out-Null
dotnet publish (Join-Path $Origem 'ComprasGOV\ComprasGOV.csproj') -c Release -o $novo --nologo -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $novo 'ComprasGOV.exe'))) {
    throw 'A publicacao falhou. O robo em execucao nao foi alterado.'
}
Set-Content -Path (Join-Path $novo 'versao.txt') -Value $Versao -Encoding ascii

Write-Host '2. Parando o robo...'
Parar-Robo

Write-Host '3. Trocando as pastas...'
$haviaVersaoAnterior = Test-Path $app
try {
    if (Test-Path $anterior) { Remove-Item $anterior -Recurse -Force }
    if ($haviaVersaoAnterior) { Renomear-ComRepeticao $app $anterior }
    Renomear-ComRepeticao $novo $app
}
catch {
    Write-Host "ERRO na troca das pastas: $($_.Exception.Message)" -ForegroundColor Red
    if (-not (Test-Path $app) -and (Test-Path $anterior)) { Rename-Item $anterior $app }
    if (Test-Path $app) { Start-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue }
    exit 1
}
Copy-Item (Join-Path $Origem 'deploy\iniciar.cmd') $lancador -Force

if (-not (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue)) {
    Write-Host "   Criando a tarefa agendada '$NomeTarefa'..."
    # S4U: sobe no boot sem sessao aberta e sem senha gravada; enxerga as variaveis
    # COMPRASGOV_* do usuario e as pastas de rede do proprio servidor.
    $acao = New-ScheduledTaskAction -Execute $lancador -WorkingDirectory $app
    $gatilho = New-ScheduledTaskTrigger -AtStartup
    $conta = New-ScheduledTaskPrincipal -UserId 'Administrator' -LogonType S4U -RunLevel Highest
    $opcoes = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $NomeTarefa -Action $acao -Trigger $gatilho -Principal $conta `
        -Settings $opcoes -Description 'Robo ComprasGOV (PNCP) - sobe no boot, sem sessao aberta' | Out-Null
}

Write-Host '4. Iniciando e conferindo...'
$subiu = Iniciar-EConferir $Versao
if ($subiu -and $SimularFalha) {
    Write-Host '   TESTE (-SimularFalha): tratando a versao nova como reprovada.'
    $subiu = $false
}
if ($subiu) {
    # Mantem so as duas ultimas pastas de versoes reprovadas
    Get-ChildItem $Raiz -Directory -Filter 'app-falhou-*' | Sort-Object Name -Descending |
        Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "SUCESSO: versao $Versao rodando." -ForegroundColor Green
    exit 0
}

Write-Host 'ERRO: a versao nova nao subiu corretamente. Desfazendo...' -ForegroundColor Red
Parar-Robo
if (Test-Path $app) { Renomear-ComRepeticao $app (Join-Path $Raiz ('app-falhou-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))) }

if (-not $haviaVersaoAnterior) {
    Write-Host 'ATENCAO: nao havia versao anterior para restaurar. O robo esta parado.' -ForegroundColor Red
    exit 2
}

Renomear-ComRepeticao $anterior $app
if (Iniciar-EConferir $null) {
    Write-Host 'Versao anterior restaurada e rodando.'
    exit 1
}

Write-Host 'ATENCAO: a versao anterior tambem nao subiu. O robo esta parado.' -ForegroundColor Red
exit 2
