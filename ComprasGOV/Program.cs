using ComprasGOV;
using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

// ---------------------------------------------------------------------------
//  ComprasGOV - busca as licitações com proposta em aberto (1) por UASG da
//  planilha de clientes (API oficial do PNCP, padrão; ou o portal Compras.gov.br
//  pelo navegador) e (2) na busca geral do PNCP por UF e fontes/plataformas;
//  valida o objeto, baixa TODOS os arquivos do PNCP para a pasta de rede e envia
//  por e-mail (2 a 4 licitações por mensagem). Roda 24/7 em ciclos.
// ---------------------------------------------------------------------------

Configuracao cfg = Configuracao.Ler(args);

if (cfg.MostrarAjuda)
{
    cfg.ExibirAjuda();
    return cfg.Erros.Count == 0 ? 0 : 1;
}

// --validar-config: usado pelo deploy antes de parar o robô. Não exibe valores
// (e-mails/caminhos), porque a saída vai para o log público do GitHub Actions.
if (cfg.ValidarConfig)
{
    cfg.ValidarParaCiclo();
    Console.WriteLine("Arquivo de configuração: " + (cfg.ArquivoConfig ?? "(nenhum)"));
    foreach (string aviso in cfg.Avisos) Console.WriteLine("AVISO: " + aviso);
    foreach (string erro in cfg.Erros) Console.WriteLine("ERRO: " + erro);
    Console.WriteLine(cfg.Erros.Count == 0 ? "Configuração válida." : "Configuração INVÁLIDA.");
    return cfg.Erros.Count == 0 ? 0 : 1;
}

// --saude: só lê o arquivo de estado (heartbeat); não grava log nem estado.
if (cfg.VerificarSaude)
    return Saude.Verificar(cfg.ArquivoEstado);

// Log em arquivo com rotação; a senha SMTP nunca é gravada, mesmo se aparecer numa mensagem.
Log.Ocultar(cfg.SmtpSenha);
Log.Configurar(cfg.PastaLogs, cfg.LogTamanhoMaximoMb, cfg.LogRetencaoDias, cfg.LogTamanhoTotalMaximoMb, cfg.LogConsole);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Log.Erro($"Erro inesperado, o programa vai encerrar: {e.ExceptionObject}");

Log.Secao("ComprasGOV - automação Compras.gov.br -> PNCP");

// versao.txt é gravado pelo deploy (commit publicado); vai para o log e para o estado.json.
string arquivoVersao = Path.Combine(AppContext.BaseDirectory, "versao.txt");
string versao = File.Exists(arquivoVersao) ? File.ReadAllText(arquivoVersao).Trim() : "desenvolvimento";
Log.Info($"Versão: {versao}");
Log.Info("Configuração: " + (cfg.ArquivoConfig ?? "nenhum arquivo (padrões do código + variáveis + argumentos)"));
Log.Info($"Log em arquivo: {Log.ArquivoAtual ?? "(indisponível; só console)"}");
foreach (string aviso in cfg.Avisos) Log.Aviso(aviso);

if (!cfg.InstalarNavegadores) cfg.ValidarParaCiclo();
if (cfg.Erros.Count > 0)
{
    foreach (string erro in cfg.Erros) Log.Erro(erro);
    Log.Erro("Execução cancelada: corrija a configuração.");
    Log.Encerrar();
    return 1;
}

if (cfg.InstalarNavegadores)
{
    try
    {
        int rc = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        Log.Info(rc == 0 ? "Navegadores do Playwright instalados." : "Instalação dos navegadores retornou código " + rc);
        return rc;
    }
    catch (Exception ex)
    {
        Log.Erro($"Falha ao instalar os navegadores: {ex.Message}");
        return 1;
    }
}

// Ctrl+C encerra com segurança: durante a pausa sai na hora; durante um ciclo,
// termina a licitação atual e não inicia o próximo ciclo.
using var cancelamento = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!cancelamento.IsCancellationRequested)
    {
        Log.Aviso("Ctrl+C recebido: encerrando após a etapa atual...");
        cancelamento.Cancel();
    }
};

// Parada limpa sem console (tarefa agendada/deploy): quando o arquivo indicado em
// COMPRASGOV_ARQUIVO_PARADA aparece, o efeito é o mesmo do Ctrl+C. O deploy cria o
// arquivo, espera o processo sair e só mata à força se ele não sair a tempo.
string? arquivoParada = Environment.GetEnvironmentVariable("COMPRASGOV_ARQUIVO_PARADA");
if (!string.IsNullOrWhiteSpace(arquivoParada))
{
    try { File.Delete(arquivoParada); } catch { /* sinal antigo; será tentado de novo abaixo */ }

    _ = Task.Run(async () =>
    {
        while (!cancelamento.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(arquivoParada))
                {
                    try { File.Delete(arquivoParada); } catch { /* quem criou apaga */ }
                    Log.Aviso("Sinal de parada recebido: encerrando após a etapa atual...");
                    cancelamento.Cancel();
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancelamento.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { /* falha momentânea de disco: tenta de novo */ }
        }
    });
}

// Histórico (não reprocessa o que já foi avaliado) - fica no servidor e é
// carregado no primeiro ciclo em que a pasta estiver acessível.
HistoricoService historico = null!;
bool historicoCarregado = false;
bool cicloAdiado = false;

// API oficial do PNCP (sem navegador/captcha) - usada pela fonte "api", pela busca
// geral e pela comparação.
var api = new PncpApiService(cfg);

// --testar-pncp e --comparar são modos de teste/diagnóstico de execução única.
bool modoEspecial = cfg.Comparar || !string.IsNullOrWhiteSpace(cfg.TestarPncpUrl);

if (cfg.Comparar)
{
    Log.Info("Modo: COMPARAÇÃO portal x API (não baixa, não envia e-mail).");
}
else if (!modoEspecial)
{
    if (!cfg.ExecutarTarefaUasg && !cfg.ExecutarBuscaGeral)
    {
        Log.Erro("Nenhuma tarefa habilitada: --sem-busca-geral e --so-busca-geral juntos.");
        return 1;
    }

    if (cfg.ExecutarTarefaUasg)
        Log.Info("Tarefa específica (UASGs da planilha): " + (cfg.UsarPortal
            ? "portal Compras.gov.br (navegador; sujeito ao hCaptcha)."
            : "API oficial do PNCP (sem navegador e sem captcha)."));

    if (cfg.ExecutarBuscaGeral)
        Log.Info($"Tarefa geral (depois da específica): UF {string.Join(", ", cfg.UfsBuscaGeral)}; " +
                 $"fontes/plataformas {string.Join(" e ", cfg.PlataformasBuscaGeral.Values)}.");
}

bool continuo = cfg.Continuo && !modoEspecial;
if (continuo)
    Log.Info($"Modo contínuo (24/7): pausa de {cfg.IntervaloMinutos} min entre ciclos. " +
             "Use --uma-vez para um único ciclo; Ctrl+C para encerrar.");

// Heartbeat (rota de saúde): estado.json gravado agora, a cada etapa e a cada minuto.
// O deploy e o "--saude" leem este arquivo.
using var estado = new EstadoService(cfg.ArquivoEstado, versao, continuo, cfg.IntervaloMinutos, cfg.CicloMaximoMinutos);
Log.Info($"Arquivo de estado (heartbeat): {estado.Arquivo}");

int exitCode = 0;
int ciclo = 0;

while (true)
{
    ciclo++;
    DateTime inicioCiclo = DateTime.Now;
    if (continuo) Log.Secao($"CICLO {ciclo} - início {inicioCiclo:dd/MM/yyyy HH:mm}");
    cicloAdiado = false;
    estado.Atualizar(e =>
    {
        e.Situacao = SituacaoRobo.EmCiclo;
        e.Ciclo = ciclo;
        e.InicioUltimoCiclo = inicioCiclo;
        e.ProximoCiclo = null;
    });

    exitCode = await ExecutarCicloAsync();

    // --- Resumo do ciclo ---------------------------------------------------
    Log.Secao(continuo ? $"RESUMO DO CICLO {ciclo}" : "RESUMO DA EXECUÇÃO");
    if (historicoCarregado) historico.ExibirEstatisticas();
    Log.Info(exitCode == 0 ? "Ciclo concluído sem falhas." : "Ciclo concluído com falhas (veja os erros acima).");

    bool vaiContinuar = continuo && !cancelamento.IsCancellationRequested;
    DateTime proximo = DateTime.Now.AddMinutes(cfg.IntervaloMinutos);
    estado.Atualizar(e =>
    {
        e.FimUltimoCiclo = DateTime.Now;
        e.ResultadoUltimoCiclo = cicloAdiado ? "adiado" : exitCode == 0 ? "ok" : "falhas";
        if (historicoCarregado) e.RegistrosHistorico = historico.Quantidade;
        e.Situacao = !vaiContinuar ? SituacaoRobo.Encerrado : cicloAdiado ? SituacaoRobo.Adiado : SituacaoRobo.Inativo;
        e.ProximoCiclo = vaiContinuar ? proximo : null;
    });

    if (!vaiContinuar) break;

    Log.Info($"Inativo por {cfg.IntervaloMinutos} min; próximo ciclo às {proximo:dd/MM/yyyy HH:mm}.");
    try
    {
        await Task.Delay(TimeSpan.FromMinutes(cfg.IntervaloMinutos), cancelamento.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

estado.Atualizar(e => { e.Situacao = SituacaoRobo.Encerrado; e.ProximoCiclo = null; });
Log.Info("ComprasGOV encerrado.");
Log.Encerrar();
return exitCode;

// ---------------------------------------------------------------------------
//  Um ciclo completo: (1) tarefa específica - UASGs da planilha (pela API ou pelo
//  navegador); (2) tarefa geral - UF + fontes/plataformas no PNCP; (3) e-mails
//  de tudo o que foi baixado. Uma tarefa que falhar não impede a outra nem o
//  envio dos e-mails. No modo portal abre/fecha o navegador a cada ciclo (evita
//  acúmulo de memória e recupera quedas do Chromium). Nunca lança exceção.
// ---------------------------------------------------------------------------
async Task<int> ExecutarCicloAsync()
{
    if (!CarregarHistorico()) return 1;
    if (modoEspecial) return await ExecutarModoEspecialAsync();

    var fluxo = new ExecutadorFluxo(cfg, historico);
    int falhas = 0;

    // 1) Tarefa específica: UASGs da planilha de clientes.
    if (cfg.ExecutarTarefaUasg)
        falhas += await ExecutarTarefaUasgAsync(fluxo);

    // 2) Tarefa geral: UF + fontes/plataformas (mesmos filtros do site do PNCP).
    if (cfg.ExecutarBuscaGeral && !cancelamento.IsCancellationRequested)
    {
        try
        {
            falhas += await fluxo.ProcessarBuscaGeralAsync(api, cancelamento.Token);
        }
        catch (Exception ex)
        {
            falhas++;
            Log.Erro($"Falha na busca geral: {ex.Message}");
        }
    }

    // 3) E-mails: downloads deste ciclo (das duas tarefas) + pendências anteriores.
    try
    {
        falhas += await fluxo.EnviarEmailsAsync();
    }
    catch (Exception ex)
    {
        falhas++;
        Log.Erro($"Falha no envio dos e-mails: {ex.Message}");
    }

    return falhas == 0 ? 0 : 1;
}

// Tarefa específica: lê os clientes (UASG) e processa pela API ou pelo navegador.
async Task<int> ExecutarTarefaUasgAsync(ExecutadorFluxo fluxo)
{
    Log.Secao("TAREFA ESPECÍFICA - UASGs DA PLANILHA DE CLIENTES");

    IReadOnlyList<ClienteUasg>? clientes = CarregarClientes();
    if (clientes is null) return 1;

    try
    {
        if (!cfg.UsarPortal)
            return await fluxo.ProcessarUasgsViaApiAsync(api, clientes, cancelamento.Token);

        using IPlaywright pw = await Playwright.CreateAsync();
        IBrowserContext context = await CriarContextoAsync(pw);

        try
        {
            return await fluxo.ProcessarUasgsViaPortalAsync(context, clientes, cancelamento.Token);
        }
        finally
        {
            try { await context.CloseAsync(); } catch { /* navegador já caiu */ }
        }
    }
    catch (PlaywrightException ex)
    {
        Log.Erro($"Falha do navegador/Playwright: {ex.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        Log.Erro($"Falha na tarefa de UASGs: {ex.Message}");
        return 1;
    }
}

// --comparar (portal x API por UASG) e --testar-pncp: sempre pelo navegador.
async Task<int> ExecutarModoEspecialAsync()
{
    IReadOnlyList<ClienteUasg>? clientes = null;
    if (cfg.Comparar)
    {
        clientes = CarregarClientes();
        if (clientes is null) return 1;
    }

    try
    {
        using IPlaywright pw = await Playwright.CreateAsync();
        IBrowserContext context = await CriarContextoAsync(pw);

        try
        {
            return clientes is not null
                ? await new ComparadorFontes(cfg, historico, api).ExecutarAsync(context, clientes, cancelamento.Token)
                : await TestarPncpAsync(context, cfg);
        }
        finally
        {
            try { await context.CloseAsync(); } catch { /* navegador já caiu */ }
        }
    }
    catch (PlaywrightException ex)
    {
        Log.Erro($"Falha do navegador/Playwright: {ex.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        Log.Erro($"Falha na execução: {ex.Message}");
        return 1;
    }
}

// Sem acesso ao banco no servidor o ciclo NÃO roda: com o histórico vazio tudo
// seria reprocessado e os e-mails já enviados seriam repetidos.
bool CarregarHistorico()
{
    if (historicoCarregado) return true;

    string pasta = Path.GetDirectoryName(Path.GetFullPath(cfg.ArquivoHistorico)) ?? ".";
    try
    {
        if (!Directory.Exists(pasta))
        {
            // Pasta local (ex.: --historico de teste) pode ser criada; a do servidor não.
            if (pasta.StartsWith(@"\\", StringComparison.Ordinal))
            {
                Log.Erro($"Banco de dados inacessível: {pasta}. Ciclo adiado (nada foi processado).");
                cicloAdiado = true;
                return false;
            }
            Directory.CreateDirectory(pasta);
        }

        // Nos modos de diagnóstico o histórico é só lido (nada é regravado no servidor).
        historico = new HistoricoService(cfg.ArquivoHistorico, somenteLeitura: modoEspecial);
        historicoCarregado = true;
        estado.Atualizar(e =>
        {
            e.HistoricoCarregado = true;
            e.RegistrosHistorico = historico.Quantidade;
        });
        return true;
    }
    catch (Exception ex)
    {
        Log.Erro($"{ex.Message}. Ciclo adiado (nada foi processado).");
        cicloAdiado = true;
        return false;
    }
}

// Clientes (UASG): --uasg ou a planilha (relida a cada ciclo para pegar alterações).
IReadOnlyList<ClienteUasg>? CarregarClientes()
{
    IReadOnlyList<ClienteUasg> clientes;

    if (!string.IsNullOrWhiteSpace(cfg.UasgManual))
    {
        clientes = new List<ClienteUasg>
        {
            new(cfg.UasgManual, "UASG informada na linha de comando")
        };
    }
    else
    {
        try
        {
            IReadOnlyList<ClienteUasg> todos = new PlanilhaService().LerClientes(cfg.PastaPlanilha);

            // Por padrão processa TODOS os clientes da planilha (--uasg limita a um só).
            clientes = (!cfg.ProcessarTodosClientes && todos.Count > 0)
                ? new List<ClienteUasg> { todos[0] }
                : todos;

            Log.Info($"Planilha: {cfg.PastaPlanilha} ({todos.Count} UASG(s) válidos).");
        }
        catch (Exception ex)
        {
            Log.Erro($"Falha ao ler a planilha de clientes: {ex.Message}");
            Log.Info("Dica: use --uasg <codigo> para informar a UASG manualmente.");
            return null;
        }
    }

    if (clientes.Count == 0)
    {
        Log.Erro("Nenhum cliente/UASG para processar.");
        return null;
    }

    Log.Info($"Clientes a processar: {clientes.Count}" +
             (cfg.MaxLicitacoes.HasValue ? $" (até {cfg.MaxLicitacoes} licitação(ões) por ciclo)" : ""));
    foreach (ClienteUasg c in clientes)
        Log.Info($"   {c}");

    return clientes;
}

// Cria o contexto com o perfil persistente e as medidas anti-detecção do hCaptcha.
async Task<IBrowserContext> CriarContextoAsync(IPlaywright pw)
{
    // Perfil persistente: cookies/localStorage do portal (e a "passada" do hCaptcha)
    // são reaproveitados entre execuções, reduzindo a frequência de desafios.
    string perfil = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComprasGOV", "perfil");
    Directory.CreateDirectory(perfil);

    Log.Info($"Iniciando Chromium ({(cfg.Headless ? "headless" : "janela visível")}) - perfil: {perfil}");
    IBrowserContext context = await pw.Chromium.LaunchPersistentContextAsync(perfil,
        new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = cfg.Headless,
            // "--enable-automation" (padrão do Playwright) é um dos sinais que fazem o
            // hCaptcha do Compras.gov.br exigir desafio de imagens.
            IgnoreDefaultArgs = new[] { "--enable-automation" },
            Args = new[] { "--disable-blink-features=AutomationControlled" },
            Locale = "pt-BR",
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1600, Height = 950 },
        });

    // Navegador normal expõe navigator.webdriver === false; sem isto o hCaptcha do
    // Compras.gov.br considera o perfil anômalo e exige o desafio de imagens.
    await context.AddInitScriptAsync(script: """
        Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => false, configurable: true });
        """);

    return context;
}

// Abre uma URL do PNCP direto, localiza o edital e baixa (teste isolado do passo 3).
async Task<int> TestarPncpAsync(IBrowserContext context, Configuracao cfgLocal)
{
    Log.Secao($"TESTE ISOLADO DO PNCP: {cfgLocal.TestarPncpUrl}");
    var page = await context.NewPageAsync();
    page.SetDefaultTimeout(45_000);

    try
    {
        await page.GotoAsync(cfgLocal.TestarPncpUrl!, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
        await Task.Delay(2000);

        var pncp = new PncpAutomacao();
        List<ArquivoPncp> arquivos = await pncp.ListarArquivosAsync(page);

        if (arquivos.Count == 0)
        {
            Log.Erro("Teste PNCP: nenhum arquivo na aba 'Arquivos'.");
            return 1;
        }

        var download = new DownloadEditalService(cfgLocal.PastaSaida);
        foreach (ArquivoPncp arquivo in arquivos)
        {
            string caminho = await download.BaixarAsync(arquivo.Url, "TESTE", arquivo.Nome);
            Log.Ok($"Teste PNCP: {caminho}");
        }
        return 0;
    }
    finally
    {
        await page.CloseAsync();
    }
}
