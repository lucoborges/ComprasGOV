# ============================================================
# Deploy do ComprasGOV (tarefa agendada "ComprasGOV" no servidor).
#
# Ordem: confere a configuracao de producao -> publica a versao nova em uma pasta
# separada e valida a configuracao com ela -> so entao para o robo -> troca as
# pastas -> inicia -> confere no heartbeat (logs\estado.json) que a versao nova
# subiu e carregou o historico. Se nao subir, volta para a versao anterior e
# termina com erro.
#
# Estrutura em $Raiz (padrao C:\Services\ComprasGOV):
#   app\           versao em execucao
#   app-anterior\  versao anterior (usada para desfazer)
#   app-novo\      area de publicacao
#   config\        appsettings.json de producao (NAO e tocado pelo deploy; criado a mao
#                  a partir de deploy\appsettings.exemplo.json)
#   logs\          logs com rotacao + estado.json (heartbeat); nao e apagado no deploy
#   iniciar.cmd    lancador chamado pela tarefa agendada
#   saude.cmd      verificacao de saude (codigo de saida 0 = em dia, 1 = problema)
#
# Compativel com PowerShell 4.0 (Windows Server 2012 R2). Precisa rodar como
# administrador (o runner do GitHub Actions roda como SYSTEM). Nada do que este
# script escreve pode conter e-mails ou caminhos internos: o log do Actions e publico.
# ============================================================
param(
    [string]$Origem = (Split-Path $PSScriptRoot -Parent),
    [string]$Raiz = 'C:\Services\ComprasGOV',
    [string]$NomeTarefa = 'ComprasGOV',
    [string]$Versao = 'manual',
    # Configuracao de producao (fora de app\, que e trocada a cada deploy)
    [string]$ArquivoConfig = '',
    # Tempo para o robo encerrar sozinho (sinal de parada) antes de ser finalizado a forca
    [int]$EsperaParadaSeg = 90,
    # Tempo para a versao iniciada aparecer no heartbeat com o historico carregado
    [int]$EsperaInicioSeg = 60,
    # Somente teste: trata a versao nova como reprovada para exercitar o desfazer
    [switch]$SimularFalha
)

$ErrorActionPreference = 'Stop'

if (-not $ArquivoConfig) { $ArquivoConfig = Join-Path $Raiz 'config\appsettings.json' }
$app = Join-Path $Raiz 'app'
$anterior = Join-Path $Raiz 'app-anterior'
$novo = Join-Path $Raiz 'app-novo'
$logs = Join-Path $Raiz 'logs'
$estado = Join-Path $logs 'estado.json'
# Saida de console do lancador: so a versao anterior a Fase 4 (sem heartbeat) escreve nele
$logConsole = Join-Path $logs 'console.log'
$sinalParada = Join-Path $Raiz 'parar.flag'
$lancador = Join-Path $Raiz 'iniciar.cmd'

# Somente os processos desta instalacao (nunca um teste rodando de outra pasta)
function Obter-Processos {
    @(Get-Process -Name 'ComprasGOV' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Raiz, [StringComparison]::OrdinalIgnoreCase) })
}

function Processo-Vivo([int]$processoId) {
    @(Obter-Processos | Where-Object { $_.Id -eq $processoId }).Count -gt 0
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

# Le um arquivo que o robo pode estar regravando (compartilhamento total)
function Ler-Texto([string]$caminho) {
    if (-not (Test-Path $caminho)) { return '' }
    try {
        $fs = [IO.File]::Open($caminho, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try {
            $leitor = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
            return $leitor.ReadToEnd()
        }
        finally { $fs.Close() }
    }
    catch { return '' }
}

# Heartbeat gravado pelo robo (null enquanto nao existe ou esta sendo trocado)
function Ler-Estado {
    $texto = Ler-Texto $estado
    if (-not $texto) { return $null }
    try { return ($texto | ConvertFrom-Json) } catch { return $null }
}

# Inicia a tarefa e confere pelo heartbeat: versao esperada, processo vivo e historico
# carregado. Com $versaoEsperada vazio (desfazer) aceita tambem a versao anterior a
# Fase 4, que nao grava heartbeat: nesse caso confere o texto do console.log.
function Iniciar-EConferir([string]$versaoEsperada) {
    # Heartbeat antigo nao pode ser confundido com o desta inicializacao
    Remove-Item $estado -Force -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $NomeTarefa
    $semProcesso = 0

    for ($i = 0; $i -lt $EsperaInicioSeg; $i += 2) {
        Start-Sleep -Seconds 2
        $e = Ler-Estado

        if ($e) {
            if ($e.situacao -eq 'adiado' -or $e.resultadoUltimoCiclo -eq 'adiado') {
                Write-Host '   O robo iniciou, mas adiou o ciclo (historico inacessivel ou em uso).'
                return $false
            }
            if ($e.situacao -eq 'encerrado') {
                Write-Host '   O robo iniciou e encerrou em seguida (veja o log em logs\).'
                return $false
            }
            if ($versaoEsperada -and $e.versao -ne $versaoEsperada) {
                Write-Host "   O heartbeat mostra a versao '$($e.versao)'; esperada '$versaoEsperada'."
                return $false
            }
            if ($e.historicoCarregado) {
                # Continua vivo depois de carregar?
                Start-Sleep -Seconds 3
                if (-not (Processo-Vivo $e.pid)) {
                    Write-Host "   O processo $($e.pid) encerrou logo depois de carregar o historico."
                    return $false
                }
                Write-Host "   Heartbeat: versao $($e.versao), PID $($e.pid), historico com $($e.registrosHistorico) registro(s)."
                return $true
            }
        }
        elseif (-not $versaoEsperada) {
            # Versao anterior a Fase 4 (sem heartbeat): confere o texto do console.
            # (Os padroes evitam letras acentuadas para nao depender da codificacao.)
            $texto = Ler-Texto $logConsole
            if ($texto -match 'Ciclo adiado') {
                Write-Host '   O robo iniciou, mas adiou o ciclo (historico inacessivel ou em uso).'
                return $false
            }
            if ($texto -match 'rico carregado: \d+ registro') {
                Start-Sleep -Seconds 3
                return ((Obter-Processos).Count -gt 0)
            }
        }

        # Processo saiu (ex.: configuracao invalida): nao adianta esperar o prazo todo
        if ((Obter-Processos).Count -eq 0) { $semProcesso += 2 } else { $semProcesso = 0 }
        if ($semProcesso -ge 10) {
            Write-Host '   O processo do robo nao esta em execucao (veja o log em logs\).'
            return $false
        }
    }

    Write-Host "   Em $EsperaInicioSeg s o heartbeat nao mostrou a versao iniciada com o historico carregado."
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
if (-not (Test-Path $ArquivoConfig)) {
    throw ('Configuracao de producao ausente (config\appsettings.json na pasta da instalacao). ' +
        'Crie-a a partir de deploy\appsettings.exemplo.json. O robo em execucao nao foi alterado.')
}
if (Test-Path $novo) { Remove-Item $novo -Recurse -Force }
New-Item -ItemType Directory -Force $logs | Out-Null
dotnet publish (Join-Path $Origem 'ComprasGOV\ComprasGOV.csproj') -c Release -o $novo --nologo -v q
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $novo 'ComprasGOV.exe'))) {
    throw 'A publicacao falhou. O robo em execucao nao foi alterado.'
}
Set-Content -Path (Join-Path $novo 'versao.txt') -Value $Versao -Encoding ascii

# A versao nova aceita a configuracao de producao? (so informa valida/invalida, sem os valores)
& (Join-Path $novo 'ComprasGOV.exe') --validar-config --config $ArquivoConfig
if ($LASTEXITCODE -ne 0) {
    throw 'A configuracao de producao e invalida para a versao nova. O robo em execucao nao foi alterado.'
}

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
Copy-Item (Join-Path $Origem 'deploy\saude.cmd') (Join-Path $Raiz 'saude.cmd') -Force
Copy-Item (Join-Path $Origem 'deploy\appsettings.exemplo.json') `
    (Join-Path (Split-Path $ArquivoConfig -Parent) 'appsettings.exemplo.json') -Force

if (-not (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue)) {
    Write-Host "   Criando a tarefa agendada '$NomeTarefa'..."
    # S4U: sobe no boot sem sessao aberta e sem senha gravada; enxerga as variaveis
    # COMPRASGOV_* do usuario e as pastas de rede do proprio servidor.
    $acao = New-ScheduledTaskAction -Execute $lancador -WorkingDirectory $app
    $gatilho = New-ScheduledTaskTrigger -AtStartup
    # 2 min apos o boot: da tempo da rede e do compartilhamento do banco subirem
    $gatilho.Delay = 'PT2M'
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
