using System.Text.RegularExpressions;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

namespace ComprasGOV.Automation;

/// <summary>
/// Automação do Compras.gov.br: pesquisa por unidade compradora, filtro das licitações
/// com proposta em aberto, leitura do objeto e navegação para o acompanhamento da compra.
/// </summary>
public sealed class ComprasNetAutomacao
{
    /// <summary>Abre a pesquisa e consulta as compras da unidade compradora informada.</summary>
    public async Task PesquisarAsync(IPage page, ClienteUasg cliente)
    {
        Log.Info($"Pesquisando compras da UASG {cliente.Codigo} ({cliente.Orgao})...");

        await page.GotoAsync(Configuracao.UrlPesquisa, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });

        var input = page.Locator(Seletores.UnidadeCompradora);
        await input.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 45_000
        });

        string codigo = cliente.CodigoMascara;
        await input.FillAsync(codigo);
        await GarantirValorAsync(page, codigo);

        await page.ClickAsync(Seletores.BotaoPesquisar);
        int encontradas = await AguardarResultadosAsync(page);
        Log.Info($"Resultados da pesquisa: {encontradas} licitação(ões).");
    }

    /// <summary>
    /// A máscara do campo (9?99999) preenche as posições vazias com "_". Se o valor do
    /// modelo ficar com o slot vazio a API do Compras rejeita a consulta, por isso o
    /// valor é conferido e corrigido antes de pesquisar.
    /// </summary>
    private static async Task GarantirValorAsync(IPage page, string codigo)
    {
        string atual = await page.InputValueAsync(Seletores.UnidadeCompradora);
        if (string.Equals(atual, codigo, StringComparison.Ordinal)) return;

        Log.Aviso($"Campo da unidade compradora com '{atual}' (esperado '{codigo}'); corrigindo a máscara.");
        await page.Locator(Seletores.UnidadeCompradora).First.EvaluateAsync<string>(
            "(el, valor) => { const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            " set.call(el, valor); el.dispatchEvent(new Event('input', { bubbles: true })); return el.value; }",
            codigo);
    }

    /// <summary>Aguarda a lista de resultados (ou a mensagem de nenhum resultado).</summary>
    public async Task<int> AguardarResultadosAsync(IPage page, int timeoutSegundos = 60)
    {
        DateTime limite = DateTime.UtcNow.AddSeconds(timeoutSegundos);
        DateTime inicio = DateTime.UtcNow;
        DateTime proximaChecagem = inicio + TimeSpan.FromSeconds(15);
        bool avisouDesafio = false;
        bool registrouIframes = false;

        while (DateTime.UtcNow < limite)
        {
            int cards = await page.Locator(Seletores.CardLicitacao).CountAsync();
            if (cards > 0)
            {
                await Task.Delay(500);
                return await page.Locator(Seletores.CardLicitacao).CountAsync();
            }

            if (await page.Locator(Seletores.MensagemSemResultado).CountAsync() > 0)
                return 0;

            // O Compras.gov.br protege a busca com hCaptcha. Quando o portal exige o
            // desafio de imagens, só um humano na janela visível pode resolvê-lo;
            // assim que ele terminar, a pesquisa segue sozinha.
            if (DateTime.UtcNow >= proximaChecagem)
            {
                proximaChecagem = DateTime.UtcNow.AddSeconds(10);
                string situacao = await LerDesafioCaptchaAsync(page);

                if (situacao.StartsWith("SIM", StringComparison.Ordinal))
                {
                    if (!avisouDesafio)
                    {
                        avisouDesafio = true;
                        Log.Aviso("Desafio hCaptcha detectado! Resolva-o na janela do navegador - " +
                                  "a pesquisa continuará sozinha assim que o desafio for concluído.");
                        limite = DateTime.UtcNow.AddSeconds(Math.Max(180, timeoutSegundos));
                    }
                }
                else if (!registrouIframes)
                {
                    registrouIframes = true;
                    Log.Info($"Iframes do captcha na página: {situacao}");
                }
            }

            await Task.Delay(700);
        }

        Log.Aviso("Tempo esgotado aguardando os resultados da pesquisa.");
        await DiagnosticarAsync(page);
        return await page.Locator(Seletores.CardLicitacao).CountAsync();
    }

    /// <summary>
    /// "SIM|..." quando o desafio de imagens do hCaptcha está aberto sobre a página;
    /// "NAO|..." caso contrário. Os detalhes dos iframes acompanham a resposta para
    /// diagnóstico (tamanho/visibilidade de cada iframe do captcha).
    /// </summary>
    private static async Task<string> LerDesafioCaptchaAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>("""
                () => {
                    const itens = [...document.querySelectorAll('iframe')].filter(
                        f => /hcaptcha/i.test(f.src || '') || /challenge/i.test(f.title || ''));
                    const info = itens.map(f => {
                        const r = f.getBoundingClientRect(), cs = getComputedStyle(f);
                        return `'${f.title || 'sem-titulo'}' ${Math.round(r.width)}x${Math.round(r.height)} ${cs.visibility}/${cs.display}`;
                    }).join('; ') || 'nenhum iframe';
                    const visivel = itens.some(f => {
                        const r = f.getBoundingClientRect(), cs = getComputedStyle(f);
                        return r.width > 0 && r.height >= 150 && cs.visibility !== 'hidden' && cs.display !== 'none';
                    });
                    return (visivel ? 'SIM' : 'NAO') + '|' + info;
                }
                """);
        }
        catch (Exception ex)
        {
            return "NAO|erro na leitura: " + ex.Message;
        }
    }

    /// <summary>Captura o estado da página quando a pesquisa não devolve resultados.</summary>
    private static async Task DiagnosticarAsync(IPage page)
    {
        try
        {
            Log.Info($"URL atual: {page.Url}");
            Log.Info($"cards={await page.Locator(Seletores.CardLicitacao).CountAsync()} " +
                     $"vazio={await page.Locator(Seletores.MensagemSemResultado).CountAsync()} " +
                     $"campo={await page.InputValueAsync(Seletores.UnidadeCompradora)}");

            string texto = TextoUtil.LimparEspacos(await page.Locator("body").InnerTextAsync());
            Log.Info($"texto: {(texto.Length > 500 ? texto[..500] + "..." : texto)}");

            string ambiente = await page.EvaluateAsync<string>(
                "() => JSON.stringify({ webdriver: navigator.webdriver, hcaptcha: typeof window.hcaptcha," +
                " iframes: document.querySelectorAll('iframe').length, overlay: !!document.querySelector('.loading-overlay, .spinner') })");
            Log.Info($"ambiente JS: {ambiente}");

            string pasta = Path.Combine(Path.GetTempPath(), "ComprasGOV");
            Directory.CreateDirectory(pasta);
            string arq = Path.Combine(pasta, "diagnostico_pesquisa.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = arq, FullPage = true });
            Log.Info($"screenshot: {arq}");
        }
        catch (Exception ex)
        {
            Log.Aviso($"Diagnóstico indisponível: {ex.Message}");
        }
    }

    /// <summary>
    /// Percorre os cards da página atual e monta a lista de licitações com proposta em
    /// aberto que ainda não foram processadas. Licitações suspensas/revogadas (que não
    /// apresentam a div "Etapa/Proposta") são ignoradas.
    /// </summary>
    public async Task<List<LicitacaoEncontrada>> ColetarAlvosAsync(IPage page, ClienteUasg cliente, HistoricoService historico)
    {
        var alvos = new List<LicitacaoEncontrada>();
        var cards = page.Locator(Seletores.CardLicitacao);
        int total = await cards.CountAsync();

        for (int i = 0; i < total; i++)
        {
            var card = cards.Nth(i);
            string titulo = await ObterTituloAsync(card);
            if (string.IsNullOrEmpty(titulo)) continue;

            string texto = await card.InnerTextAsync();
            if (!TemPropostaAberta(texto))
            {
                Log.Info($"  Ignorada (sem proposta em aberto): {titulo}");
                continue;
            }

            string numero = ExtrairNumero(titulo);
            string chave = $"{cliente.Codigo}-{numero}";

            if (historico.JaProcessada(chave))
            {
                Log.Info($"  Já processada anteriormente: {titulo}");
                continue;
            }

            string objeto = await ObterObjetoAsync(card);
            alvos.Add(new LicitacaoEncontrada(
                titulo,
                numero,
                chave,
                ExtrairPrazo(texto),
                objeto));

            Log.Info($"  Encontrada: {titulo} | prazo: {alvos[^1].Prazo} | objeto: {Resumir(objeto)}");
        }

        return alvos;
    }

    /// <summary>Localiza o card pelo título exato da licitação.</summary>
    public async Task<ILocator?> LocalizarCardAsync(IPage page, string titulo)
    {
        var cards = page.Locator(Seletores.CardLicitacao);
        int total = await cards.CountAsync();

        for (int i = 0; i < total; i++)
        {
            var card = cards.Nth(i);
            string atual = await ObterTituloAsync(card);
            if (string.Equals(atual, titulo, StringComparison.OrdinalIgnoreCase))
                return card;
        }

        return null;
    }

    /// <summary>Clica em "Acompanhar compra" e aguarda a página de detalhes.</summary>
    public async Task IrParaAcompanharAsync(IPage page, ILocator card)
    {
        await card.Locator(Seletores.BotaoAcompanhar).First.ClickAsync();
        await page.WaitForURLAsync(Seletores.UrlAcompanhamento, new PageWaitForURLOptions
        {
            Timeout = 45_000,
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
    }

    /// <summary>Volta da página de detalhes para a lista de resultados.</summary>
    public async Task VoltarParaPesquisaAsync(IPage page)
    {
        try
        {
            await page.GoBackAsync(new PageGoBackOptions
            {
                Timeout = 30_000,
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
        }
        catch (Exception ex)
        {
            Log.Aviso($"Não foi possível voltar automaticamente ({ex.Message}).");
        }
    }

    public async Task<bool> TemResultadosAsync(IPage page) =>
        await page.Locator(Seletores.CardLicitacao).CountAsync() > 0;

    /// <summary>Número da página ativa no paginador (1 quando não há paginação visível).</summary>
    public async Task<int> PaginaAtualAsync(IPage page)
    {
        var ativa = page.Locator(Seletores.PaginaAtiva).First;
        if (await ativa.CountAsync() == 0) return 1;

        string rotulo = await ativa.GetAttributeAsync("aria-label") ?? "";
        var m = Regex.Match(rotulo, @"\d+");
        return m.Success ? int.Parse(m.Value) : 1;
    }

    /// <summary>Clica em "Próxima Página"; retorna false quando chegou na última.</summary>
    public async Task<bool> IrParaProximaPaginaAsync(IPage page)
    {
        var proxima = page.Locator(Seletores.ProximaPagina).First;
        if (await proxima.CountAsync() == 0) return false;

        string classe = await proxima.GetAttributeAsync("class") ?? "";
        bool desabilitada = classe.Contains("p-disabled") ||
                            await proxima.GetAttributeAsync("disabled") is not null;
        if (desabilitada) return false;

        int paginaAnterior = await PaginaAtualAsync(page);
        await proxima.ClickAsync();

        DateTime limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(500);
            if (await PaginaAtualAsync(page) != paginaAnterior &&
                await page.Locator(Seletores.CardLicitacao).CountAsync() > 0)
            {
                Log.Info($"Avançou para a página {paginaAnterior + 1} dos resultados.");
                return true;
            }
        }

        Log.Aviso("Não foi possível avançar para a próxima página de resultados.");
        return false;
    }

    /// <summary>Garante que a página de resultados está na página esperada (repetindo a pesquisa se preciso).</summary>
    public async Task GarantirPaginaAsync(IPage page, ClienteUasg cliente, int paginaEsperada)
    {
        if (await TemResultadosAsync(page) && await PaginaAtualAsync(page) == paginaEsperada)
            return;

        Log.Aviso($"Resultados não restaurados; refazendo a pesquisa (página {paginaEsperada}).");
        await PesquisarAsync(page, cliente);

        for (int i = 1; i < paginaEsperada; i++)
        {
            if (!await IrParaProximaPaginaAsync(page)) break;
        }
    }

    // ------------------------------------------------------------------
    //  Leituras do card
    // ------------------------------------------------------------------

    private static async Task<string> ObterTituloAsync(ILocator card)
    {
        var numero = card.Locator(Seletores.NumeroLicitacao).First;
        if (await numero.CountAsync() == 0) return "";
        return TextoUtil.LimparEspacos(await numero.InnerTextAsync());
    }

    /// <summary>Expande o card (quando preciso) e devolve o conteúdo da linha "Objeto:".</summary>
    private static async Task<string> ObterObjetoAsync(ILocator card)
    {
        var linha = card.Locator(Seletores.LinhaObjeto)
            .Filter(new LocatorFilterOptions { HasText = "Objeto:" });

        try
        {
            if (await linha.CountAsync() == 0)
            {
                var expandir = card.Locator(Seletores.BotaoExpandir).First;
                if (await expandir.CountAsync() > 0)
                    await expandir.ClickAsync();

                await linha.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 10_000
                });
            }

            var valor = linha.First.Locator(Seletores.ValorObjeto).First;
            return TextoUtil.LimparEspacos(await valor.InnerTextAsync());
        }
        catch (Exception ex)
        {
            Log.Aviso($"Não foi possível ler o objeto da licitação ({ex.Message}).");
            return "";
        }
    }

    /// <summary>Card com "Etapa: / Proposta / data", sem suspensão ou revogação.</summary>
    private static bool TemPropostaAberta(string texto)
    {
        string t = texto.ToUpperInvariant();
        return t.Contains("ETAPA:") &&
               t.Contains("PROPOSTA") &&
               Regex.IsMatch(texto, @"\d{2}/\d{2}/\d{4}") &&
               !t.Contains("SUSPENSA") &&
               !t.Contains("REVOGADA");
    }

    /// <summary>Extrai o número/ano do título (ex.: "Pregão Eletrônico N° 67/2026" -> "67/2026").</summary>
    public static string ExtrairNumero(string titulo)
    {
        var m = Regex.Match(titulo, @"N\s*[°º]\s*(\d+\s*/\s*\d{4})");
        if (!m.Success) m = Regex.Match(titulo, @"(\d+\s*/\s*\d{4})");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", "") : TextoUtil.LimparEspacos(titulo);
    }

    /// <summary>Extrai o prazo da proposta (ex.: "06/11/2026 13:00").</summary>
    public static string ExtrairPrazo(string texto)
    {
        var m = Regex.Match(texto, @"\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}");
        return m.Success ? m.Value : "";
    }

    private static string Resumir(string texto) =>
        string.IsNullOrEmpty(texto) ? "(sem objeto)"
            : texto.Length <= 90 ? texto
            : texto[..90] + "...";
}
