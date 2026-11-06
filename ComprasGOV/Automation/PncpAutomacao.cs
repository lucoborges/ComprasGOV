using System.Text;
using ComprasGOV.Services;
using Microsoft.Playwright;

namespace ComprasGOV.Automation;

/// <summary>Arquivo publicado na aba "Arquivos" do PNCP.</summary>
public sealed record ArquivoPncp(string Url, string Nome);

/// <summary>
/// Automação do Portal Nacional de Contratações Públicas: abre a compra
/// correspondente e lê TODOS os arquivos publicados na aba "Arquivos".
/// </summary>
public sealed class PncpAutomacao
{
    /// <summary>Clica no botão do PNCP e devolve a página aberta em nova aba.</summary>
    public async Task<IPage?> AbrirPncpAsync(IPage pageCompras)
    {
        var botao = pageCompras.Locator(Seletores.BotaoPncp);
        if (await botao.CountAsync() == 0)
        {
            Log.Aviso("Botão do PNCP não encontrado na página de acompanhamento da compra.");
            return null;
        }

        Task<IPage> aguardandoPopup = pageCompras.Context.WaitForPageAsync(new BrowserContextWaitForPageOptions
        {
            Timeout = 30_000
        });

        try
        {
            await botao.First.ClickAsync();
        }
        catch (Exception ex)
        {
            Log.Aviso($"Falha ao clicar no botão do PNCP: {ex.Message}");
        }

        IPage? pncp = null;
        try
        {
            pncp = await aguardandoPopup;
        }
        catch (TimeoutException)
        {
            // Algumas configurações abrem o PNCP na própria aba.
            if (pageCompras.Url.Contains("pncp.gov.br", StringComparison.OrdinalIgnoreCase))
                pncp = pageCompras;
        }

        if (pncp is null)
        {
            Log.Aviso("O portal PNCP não foi aberto.");
            return null;
        }

        try
        {
            await pncp.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = 45_000
            });
        }
        catch (Exception)
        {
            // A página pode já estar carregada/inalcançável; segue para a leitura.
        }

        await Task.Delay(1500);
        Log.Info($"PNCP aberto: {pncp.Url}");
        return pncp;
    }

    /// <summary>Abre a aba "Arquivos" e lista TODOS os arquivos publicados (com download).</summary>
    public async Task<List<ArquivoPncp>> ListarArquivosAsync(IPage pncp)
    {
        var arquivos = new List<ArquivoPncp>();

        // O PNCP é uma SPA: a barra de abas só aparece depois dos XHRs iniciais.
        try
        {
            await pncp.Locator(Seletores.BarraAbas).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 45_000
            });
        }
        catch (Exception)
        {
            await DiagnosticarAsync(pncp, "barra_abas_ausente");
            Log.Aviso("Barra de abas não encontrada no PNCP (página sem os arquivos da compra ou ainda carregando).");
            return arquivos;
        }

        // Escolhe o primeiro seletor que casa a aba "Arquivos" (ver Seletores).
        ILocator? aba = null;
        string seletorUsado = "";
        foreach (string candidato in Seletores.AbaArquivosCandidatos)
        {
            if (await pncp.Locator(candidato).CountAsync() == 0) continue;
            aba = pncp.Locator(candidato).First;
            seletorUsado = candidato;
            break;
        }

        if (aba is null)
        {
            await DiagnosticarAsync(pncp, "aba_arquivos_ausente");
            Log.Aviso("Aba 'Arquivos' não encontrada no PNCP (página sem os arquivos da compra).");
            return arquivos;
        }

        Log.Info($"Aba 'Arquivos' localizada (seletor: {seletorUsado}).");

        try
        {
            await aba.ScrollIntoViewIfNeededAsync();
            await aba.ClickAsync();
        }
        catch (Exception ex)
        {
            Log.Aviso($"Não foi possível abrir a aba 'Arquivos' ({ex.Message}).");
        }

        // Sinal real de que a tabela de arquivos renderizou: o link de download.
        // (As linhas da tabela de Itens já existem antes do clique, por isso não
        // dá para esperar por "datatable-body-row".)
        var downloads = pncp.Locator(Seletores.LinkDownload);
        DateTime limite = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limite && await downloads.CountAsync() == 0)
            await Task.Delay(500);

        var linhas = pncp.Locator(Seletores.LinhaArquivoComDownload);
        int total = await linhas.CountAsync();

        if (total == 0)
        {
            await DiagnosticarAsync(pncp, "tabela_arquivos_vazia");
            Log.Aviso("Aba 'Arquivos' sem linhas de download (nenhum documento publicado nesta compra).");
            return arquivos;
        }

        for (int i = 0; i < total; i++)
        {
            var linha = linhas.Nth(i);
            var download = linha.Locator(Seletores.LinkDownload);
            if (await download.CountAsync() == 0) continue;

            var celulas = linha.Locator(Seletores.CelulaArquivo);
            int qtdCelulas = await celulas.CountAsync();

            string nome = qtdCelulas > 0 ? await celulas.Nth(0).InnerTextAsync() : "";

            string href = await download.First.GetAttributeAsync("href") ?? "";
            if (string.IsNullOrEmpty(href)) continue;

            string url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : new Uri(new Uri("https://pncp.gov.br"), href).ToString();

            string nomeArquivo = TextoUtil.LimparEspacos(nome);
            if (string.IsNullOrWhiteSpace(nomeArquivo)) continue;

            arquivos.Add(new ArquivoPncp(url, nomeArquivo));
        }

        if (arquivos.Count == 0)
            Log.Aviso("Aba 'Arquivos' sem arquivos utilizáveis (nenhum download disponível).");
        else
            Log.Ok($"Aba 'Arquivos': {arquivos.Count} arquivo(s) localizado(s) no PNCP.");

        return arquivos;
    }

    /// <summary>Grava screenshot e HTML da página para inspeção quando algo não é encontrado.</summary>
    private static async Task DiagnosticarAsync(IPage pncp, string rotulo)
    {
        try
        {
            string pasta = Path.Combine(Path.GetTempPath(), "ComprasGOV");
            Directory.CreateDirectory(pasta);
            string baseNome = Path.Combine(pasta,
                $"pncp_{rotulo}_{DateTime.Now:yyyyMMdd_HHmmss}");

            await pncp.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = baseNome + ".png",
                FullPage = true
            });

            string html = await pncp.Locator("body").InnerHTMLAsync();
            await File.WriteAllTextAsync(baseNome + ".html", html, Encoding.UTF8);

            Log.Info($"Diagnóstico PNCP salvo: {baseNome}.png / .html ({pncp.Url})");
        }
        catch (Exception ex)
        {
            Log.Erro($"Falha ao gravar diagnóstico do PNCP: {ex.Message}");
        }
    }
}
