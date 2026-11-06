using ComprasGOV;
using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

// ---------------------------------------------------------------------------
//  ComprasGOV - busca as licitações com proposta em aberto por UASG (API
//  oficial do PNCP, padrão; ou o portal Compras.gov.br pelo navegador), valida
//  o objeto, baixa TODOS os arquivos do PNCP para a pasta de rede e envia por
//  e-mail (2 a 4 licitações por mensagem). Roda 24/7 em ciclos.
// ---------------------------------------------------------------------------

Configuracao cfg = Configuracao.Ler(args);

if (cfg.MostrarAjuda)
{
    Configuracao.ExibirAjuda();
    return 0;
}

Log.Secao("ComprasGOV - automação Compras.gov.br -> PNCP");

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

// Histórico (não reprocessa o que já foi avaliado) - fica no servidor e é
// carregado no primeiro ciclo em que a pasta estiver acessível.
HistoricoService historico = null!;
bool historicoCarregado = false;

// API oficial do PNCP (sem navegador/captcha) - usada pela fonte "api" e pela comparação.
var api = new PncpApiService(cfg.ArquivoCacheCnpj);

bool usaNavegador = cfg.UsarPortal || cfg.Comparar || !string.IsNullOrWhiteSpace(cfg.TestarPncpUrl);
Log.Info(cfg.Comparar ? "Modo: COMPARAÇÃO portal x API (não baixa, não envia e-mail)."
       : usaNavegador ? "Fonte: portal Compras.gov.br (navegador; sujeito ao hCaptcha)."
       : "Fonte: API oficial do PNCP (sem navegador e sem captcha).");

// --testar-pncp e --comparar são sempre execução única.
bool continuo = cfg.Continuo && !cfg.Comparar && string.IsNullOrWhiteSpace(cfg.TestarPncpUrl);
if (continuo)
    Log.Info($"Modo contínuo (24/7): pausa de {cfg.IntervaloMinutos} min entre ciclos. " +
             "Use --uma-vez para um único ciclo; Ctrl+C para encerrar.");

int exitCode = 0;
int ciclo = 0;

while (true)
{
    ciclo++;
    DateTime inicioCiclo = DateTime.Now;
    if (continuo) Log.Secao($"CICLO {ciclo} - início {inicioCiclo:dd/MM/yyyy HH:mm}");

    exitCode = await ExecutarCicloAsync();

    // --- Resumo do ciclo ---------------------------------------------------
    Log.Secao(continuo ? $"RESUMO DO CICLO {ciclo}" : "RESUMO DA EXECUÇÃO");
    if (historicoCarregado) historico.ExibirEstatisticas();
    Log.Info(exitCode == 0 ? "Ciclo concluído sem falhas." : "Ciclo concluído com falhas (veja os erros acima).");

    if (!continuo || cancelamento.IsCancellationRequested) break;

    DateTime proximo = DateTime.Now.AddMinutes(cfg.IntervaloMinutos);
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

Log.Info("ComprasGOV encerrado.");
return exitCode;

// ---------------------------------------------------------------------------
//  Um ciclo completo: lê os clientes, processa (pela API ou pelo navegador),
//  envia os e-mails. No modo portal abre/fecha o navegador a cada ciclo (evita
//  acúmulo de memória e recupera quedas do Chromium). Nunca lança exceção.
// ---------------------------------------------------------------------------
async Task<int> ExecutarCicloAsync()
{
    if (!CarregarHistorico()) return 1;

    IReadOnlyList<ClienteUasg>? clientes = CarregarClientes();
    if (clientes is null) return 1;

    if (!usaNavegador)
    {
        try
        {
            return await new ExecutadorFluxo(cfg, historico).ExecutarViaApiAsync(api, clientes, cancelamento.Token);
        }
        catch (Exception ex)
        {
            Log.Erro($"Falha na execução: {ex.Message}");
            return 1;
        }
    }

    try
    {
        using IPlaywright pw = await Playwright.CreateAsync();
        IBrowserContext context = await CriarContextoAsync(pw);

        try
        {
            if (cfg.Comparar)
                return await new ComparadorFontes(cfg, historico, api).ExecutarAsync(context, clientes, cancelamento.Token);

            return !string.IsNullOrWhiteSpace(cfg.TestarPncpUrl)
                ? await TestarPncpAsync(context, cfg)
                : await new ExecutadorFluxo(cfg, historico).ExecutarAsync(context, clientes, cancelamento.Token);
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
                return false;
            }
            Directory.CreateDirectory(pasta);
        }

        historico = new HistoricoService(cfg.ArquivoHistorico);
        historicoCarregado = true;
        return true;
    }
    catch (Exception ex)
    {
        Log.Erro($"{ex.Message}. Ciclo adiado (nada foi processado).");
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
