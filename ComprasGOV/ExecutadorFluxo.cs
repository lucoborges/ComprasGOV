using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

namespace ComprasGOV;

/// <summary>
/// Orquestra o fluxo completo descrito no HTML.md:
/// pesquisa -> filtra proposta em aberto -> valida objeto -> acompanha a compra ->
/// abre o PNCP -> baixa o edital -> registra no histórico.
/// </summary>
public sealed class ExecutadorFluxo
{
    private readonly Configuracao _cfg;
    private readonly HistoricoService _historico;
    private readonly ComprasNetAutomacao _compras = new();
    private readonly PncpAutomacao _pncp = new();
    private readonly DownloadEditalService _download;

    public ExecutadorFluxo(Configuracao cfg, HistoricoService historico)
    {
        _cfg = cfg;
        _historico = historico;
        _download = new DownloadEditalService(cfg.PastaSaida);
    }

    public async Task<int> ExecutarAsync(IBrowserContext context, IReadOnlyList<ClienteUasg> clientes)
    {
        int falhas = 0;

        foreach (ClienteUasg cliente in clientes)
        {
            Log.Secao($"CLIENTE: {cliente.Codigo} - {cliente.Orgao}");
            try
            {
                await ExecutarClienteAsync(context, cliente);
            }
            catch (Exception ex)
            {
                falhas++;
                Log.Erro($"Falha ao processar {cliente}: {ex.Message}");
            }
        }

        return falhas == 0 ? 0 : 1;
    }

    private async Task ExecutarClienteAsync(IBrowserContext context, ClienteUasg cliente)
    {
        // O contexto (perfil) é compartilhado entre clientes para reaproveitar a sessão.
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(45_000);

        Instrumentar(page);

        try
        {
            await _compras.PesquisarAsync(page, cliente);

            int pagina = 1;
            int processadas = 0;

            while (true)
            {
                await _compras.GarantirPaginaAsync(page, cliente, pagina);
                var alvos = await _compras.ColetarAlvosAsync(page, cliente, historico: _historico);

                foreach (LicitacaoEncontrada alvo in alvos)
                {
                    if (_cfg.MaxLicitacoes.HasValue && processadas >= _cfg.MaxLicitacoes.Value)
                    {
                        Log.Aviso($"Limite de {_cfg.MaxLicitacoes.Value} licitação(ões) por execução atingido.");
                        return;
                    }

                    await ProcessarLicitacaoAsync(page, cliente, alvo, pagina);
                    processadas++;
                }

                if (!await _compras.IrParaProximaPaginaAsync(page)) break;
                pagina++;
            }

            Log.Info($"Cliente {cliente.Codigo} concluído ({processadas} licitação(ões) avaliadas nesta execução).");
        }
        finally
        {
            // Fecha só a página do cliente; o contexto (perfil) permanece para o próximo.
            await page.CloseAsync();
        }
    }

    private async Task ProcessarLicitacaoAsync(IPage page, ClienteUasg cliente, LicitacaoEncontrada alvo, int pagina)
    {
        Log.Secao($"LICITAÇÃO {alvo.Titulo} (UASG {cliente.Codigo})");
        if (!string.IsNullOrEmpty(alvo.Objeto))
            Log.Info($"Objeto: {alvo.Objeto}");
        else
            Log.Aviso("Objeto não encontrado no card.");

        // Regra de validade do HTML.md: termos inválidos vencem.
        Classificacao classificacao = ClassificadorObjeto.Classificar(alvo.Objeto);
        Log.Info($"Classificação do objeto: {ClassificadorObjeto.Descrever(classificacao)}");

        if (_cfg.IgnorarObjeto)
        {
            Log.Aviso("MODO TESTE (--ignorar-objeto): a regra de validade do objeto está sendo ignorada.");
        }
        else if (classificacao == Classificacao.Invalido)
        {
            _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Rejeitado,
                motivo: "Objeto inválido (serviço, locação ou produtos químicos)"));
            Log.Aviso("Licitação rejeitada pela regra do objeto; nada será baixado.");
            return;
        }
        else if (classificacao == Classificacao.Indefinido)
        {
            _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Indefinido,
                motivo: "Objeto sem termos que classifiquem como válido ou inválido"));
            Log.Aviso("Objeto indefinido; nada será baixado (revisar a regra de palavras-chave).");
            return;
        }

        IPage? pncp = null;
        try
        {
            await _compras.GarantirPaginaAsync(page, cliente, pagina);

            ILocator? card = await _compras.LocalizarCardAsync(page, alvo.Titulo);
            if (card is null)
            {
                Log.Aviso($"Card da licitação não encontrado na lista: {alvo.Titulo}");
                _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Erro,
                    motivo: "Card não encontrado na lista de resultados"));
                return;
            }

            await _compras.IrParaAcompanharAsync(page, card);
            Log.Info($"Acompanhamento da compra: {page.Url}");

            pncp = await _pncp.AbrirPncpAsync(page);
            if (pncp is null)
            {
                _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Erro,
                    motivo: "Não foi possível abrir o PNCP", urlCompras: page.Url));
                return;
            }

            ArquivoEdital? edital = await _pncp.LocalizarEditalAsync(pncp);
            if (edital is null)
            {
                _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.SemEdital,
                    motivo: "Nenhum arquivo com tipo 'Edital' no PNCP",
                    urlCompras: page.Url, urlPncp: pncp.Url));
                Log.Aviso("PNCP sem arquivo 'Edital'; seguindo para a próxima licitação.");
                return;
            }

            string caminho = await _download.BaixarAsync(edital.Url, alvo.Numero, edital.Nome);

            _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Baixado,
                urlCompras: page.Url, urlPncp: pncp.Url, arquivo: caminho));

            Log.Ok($"Edital salvo em: {caminho}");
        }
        catch (Exception ex)
        {
            Log.Erro($"Erro ao processar {alvo.Titulo}: {ex.Message}");
            _historico.Salvar(NovoRegistro(cliente, alvo, StatusLicitacao.Erro,
                motivo: ex.Message, urlCompras: page.Url, urlPncp: pncp?.Url ?? ""));
        }
        finally
        {
            if (pncp is not null && !pncp.IsClosed)
            {
                try { await pncp.CloseAsync(); } catch { /* aba já fechada */ }
            }

            await _compras.VoltarParaPesquisaAsync(page);
        }
    }

    /// <summary>Log de rede/console para diagnosticar falhas da automação.
    /// O detalhamento (REQ/HTTP/corpo/console) só acontece com --debug.</summary>
    private void Instrumentar(IPage page)
    {
        page.Request += (_, requisicao) =>
        {
            try
            {
                if (!_cfg.Debug || Ignorar(requisicao.Url)) return;
                Log.Info($"REQ {requisicao.Method} {requisicao.Url}");
            }
            catch { /* requisição já descartada */ }
        };

        page.RequestFailed += (_, requisicao) =>
        {
            try
            {
                if (Ignorar(requisicao.Url)) return;
                Log.Aviso($"REQ-FAIL {requisicao.Url} ({requisicao.Failure})");
            }
            catch { /* requisição já descartada */ }
        };

        page.Response += (_, resposta) =>
        {
            try
            {
                string url = resposta.Url;
                if (Ignorar(url)) return;

                int status = resposta.Status;

                // Erro de HTTP é sempre útil; o restante só com --debug.
                if (status >= 400)
                {
                    Log.Erro($"HTTP {status} {url}");
                }
                else if (_cfg.Debug && url.Contains("fase-externa", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"HTTP {status} {url}");
                    _ = LogCorpoAsync(resposta);
                }
            }
            catch { /* resposta já descartada */ }
        };

        page.Console += (_, msg) =>
        {
            // console.error é relevante; o restante (ex.: EvalError rotineiro do
            // hCaptcha) é ruído e só aparece com --debug.
            if (msg.Type == "error") Log.Aviso($"console.error: {Truncar(msg.Text, 300)}");
            else if (_cfg.Debug) Log.Info($"console.{msg.Type}: {Truncar(msg.Text, 300)}");
        };

        page.PageError += (_, erro) => Log.Erro($"pageerror: {Truncar(erro, 300)}");
    }

    private static bool Ignorar(string url) =>
        url.Contains(".js", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".css", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".svg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".woff", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".ttf", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("imgs.hcaptcha.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("google-analytics", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("doubleclick", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lê o corpo de forma assíncrona (nunca bloquear o handler de eventos).</summary>
    private static async Task LogCorpoAsync(IResponse resposta)
    {
        try
        {
            byte[] corpo = await resposta.BodyAsync();
            string texto = System.Text.Encoding.UTF8.GetString(corpo);
            Log.Info($"    corpo: {Truncar(texto, 400)}");
        }
        catch { /* downloads/streams/resposta já descartada */ }
    }

    private static string Truncar(string texto, int max) =>
        string.IsNullOrEmpty(texto) ? "" : texto.Length <= max ? texto : texto[..max] + "...";

    private static LicitacaoHistorico NovoRegistro(
        ClienteUasg cliente,
        LicitacaoEncontrada alvo,
        string status,
        string motivo = "",
        string urlCompras = "",
        string urlPncp = "",
        string arquivo = "") => new()
    {
        Chave = alvo.Chave,
        Uasg = cliente.Codigo,
        UasgNome = cliente.Orgao,
        Titulo = alvo.Titulo,
        Numero = alvo.Numero,
        Objeto = alvo.Objeto,
        PrazoProposta = alvo.Prazo,
        Status = status,
        Motivo = motivo,
        UrlCompras = urlCompras,
        UrlPncp = urlPncp,
        Arquivo = arquivo,
        DataProcessamento = DateTime.Now
    };
}
