using ComprasGOV;
using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

// ---------------------------------------------------------------------------
//  ComprasGOV - pesquisa no Compras.gov.br por UASG, valida o objeto,
//  acompanha a compra, abre o PNCP e baixa o edital para a pasta de rede.
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

// --- 1) Clientes (UASG) -----------------------------------------------------
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

        // Por padrão processa apenas o primeiro cliente; --todos processa a lista inteira.
        clientes = (!cfg.ProcessarTodosClientes && todos.Count > 0)
            ? new List<ClienteUasg> { todos[0] }
            : todos;

        Log.Info($"Planilha: {cfg.PastaPlanilha} ({todos.Count} UASG(s) válidos).");
    }
    catch (Exception ex)
    {
        Log.Erro($"Falha ao ler a planilha de clientes: {ex.Message}");
        Log.Info("Dica: use --uasg <codigo> para informar a UASG manualmente.");
        return 1;
    }
}

if (clientes.Count == 0)
{
    Log.Erro("Nenhum cliente/UASG para processar.");
    return 1;
}

Log.Info($"Clientes a processar: {clientes.Count}" +
         (cfg.MaxLicitacoes.HasValue ? $" (até {cfg.MaxLicitacoes} licitação(ões) por execução)" : ""));
foreach (ClienteUasg c in clientes)
    Log.Info($"   {c}");

// --- 2) Histórico local (não reprocessa o que já foi avaliado) --------------
var historico = new HistoricoService(cfg.ArquivoHistorico);

// --- 3) Navegador + fluxo ---------------------------------------------------
int exitCode = 0;

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

try
{
    using IPlaywright pw = await Playwright.CreateAsync();
    IBrowserContext context = await CriarContextoAsync(pw);

    try
    {
        exitCode = !string.IsNullOrWhiteSpace(cfg.TestarPncpUrl)
            ? await TestarPncpAsync(context, cfg)
            : await new ExecutadorFluxo(cfg, historico).ExecutarAsync(context, clientes);
    }
    finally
    {
        await context.CloseAsync();
    }
}
catch (PlaywrightException ex)
{
    Log.Erro($"Falha do navegador/Playwright: {ex.Message}");
    exitCode = 1;
}
catch (Exception ex)
{
    Log.Erro($"Falha na execução: {ex.Message}");
    exitCode = 1;
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
        ArquivoEdital? edital = await pncp.LocalizarEditalAsync(page);

        if (edital is null)
        {
            Log.Erro("Teste PNCP: nenhum arquivo 'Edital' localizado.");
            return 1;
        }

        var download = new DownloadEditalService(cfgLocal.PastaSaida);
        string caminho = await download.BaixarAsync(edital.Url, "TESTE", edital.Nome);
        Log.Ok($"Teste PNCP concluído: {caminho}");
        return 0;
    }
    finally
    {
        await page.CloseAsync();
    }
}

// --- 4) Resumo --------------------------------------------------------------
Log.Secao("RESUMO DA EXECUÇÃO");
historico.ExibirEstatisticas();
Log.Info(exitCode == 0 ? "Execução concluída sem falhas." : "Execução concluída com falhas (veja os erros acima).");

return exitCode;
