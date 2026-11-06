using System.Text;
using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

namespace ComprasGOV;

/// <summary>
/// Compara, UASG por UASG, as licitações com proposta em aberto encontradas pelo
/// portal Compras.gov.br (navegador) e pela API oficial do PNCP. Não baixa arquivos,
/// não envia e-mails e não altera o histórico. Gera um relatório em Markdown.
/// </summary>
public sealed class ComparadorFontes
{
    private readonly Configuracao _cfg;
    private readonly HistoricoService _historico;
    private readonly PncpApiService _api;
    private readonly ComprasNetAutomacao _compras = new();

    public ComparadorFontes(Configuracao cfg, HistoricoService historico, PncpApiService api)
    {
        _cfg = cfg;
        _historico = historico;
        _api = api;
    }

    private sealed record ResultadoUasg(
        ClienteUasg Cliente,
        Dictionary<string, LicitacaoEncontrada>? Portal, string ErroPortal,
        Dictionary<string, LicitacaoPncp>? Api, string ErroApi);

    public async Task<int> ExecutarAsync(IBrowserContext? context, IReadOnlyList<ClienteUasg> clientes, CancellationToken ct)
    {
        var resultados = new List<ResultadoUasg>();

        // Histórico vazio e descartável: no portal, ColetarAlvosAsync pula o que
        // já foi processado - aqui queremos TODAS as licitações em aberto.
        var semHistorico = new HistoricoService(Path.Combine(Path.GetTempPath(), "ComprasGOV",
            $"comparacao_{Guid.NewGuid():N}.json"));

        foreach (ClienteUasg cliente in clientes)
        {
            if (ct.IsCancellationRequested) break;
            Log.Secao($"COMPARAÇÃO: {cliente.Codigo} - {cliente.Orgao}");

            // --- API ---
            Dictionary<string, LicitacaoPncp>? api = null;
            string erroApi = "";
            var relogio = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string? cnpj = await _api.ObterCnpjAsync(cliente.Codigo, ct);
                if (cnpj is null) erroApi = "UASG não encontrada nos Dados Abertos";
                else
                {
                    api = (await _api.ListarPropostasAbertasAsync(cliente.Codigo, cnpj, ct))
                        .GroupBy(l => l.Chave).ToDictionary(g => g.Key, g => g.First());
                    Log.Info($"API: {api.Count} licitação(ões) em {relogio.Elapsed.TotalSeconds:F1}s.");
                }
            }
            catch (Exception ex) { erroApi = ex.Message; Log.Erro($"API: {ex.Message}"); }

            // --- Portal ---
            Dictionary<string, LicitacaoEncontrada>? portal = null;
            string erroPortal = "";
            if (context is null) erroPortal = "portal não consultado";
            else
            {
                relogio.Restart();
                IPage page = await context.NewPageAsync();
                page.SetDefaultTimeout(45_000);
                try
                {
                    await _compras.PesquisarAsync(page, cliente);
                    if (!await _compras.PesquisaConcluidaAsync(page))
                    {
                        erroPortal = "pesquisa não concluída (hCaptcha / tempo esgotado)";
                        Log.Erro($"Portal: {erroPortal}");
                    }
                    else
                    {
                        portal = new();
                        while (true)
                        {
                            foreach (var alvo in await _compras.ColetarAlvosAsync(page, cliente, semHistorico))
                                portal.TryAdd(alvo.Chave, alvo);
                            if (!await _compras.IrParaProximaPaginaAsync(page)) break;
                        }
                        Log.Info($"Portal: {portal.Count} licitação(ões) em {relogio.Elapsed.TotalSeconds:F1}s.");
                    }
                }
                catch (Exception ex) { erroPortal = ex.Message; Log.Erro($"Portal: {ex.Message}"); }
                finally { try { await page.CloseAsync(); } catch { } }
            }

            resultados.Add(new ResultadoUasg(cliente, portal, erroPortal, api, erroApi));
        }

        string relatorio = MontarRelatorio(resultados);
        string pasta = Path.GetDirectoryName(Path.GetFullPath(_cfg.ArquivoHistorico)) ?? ".";
        Directory.CreateDirectory(pasta);
        string arquivo = Path.Combine(pasta, $"comparacao_{DateTime.Now:yyyyMMdd_HHmm}.md");
        await File.WriteAllTextAsync(arquivo, relatorio, Encoding.UTF8, ct);

        Log.Secao("RESULTADO DA COMPARAÇÃO");
        foreach (string linha in relatorio.Split('\n').TakeWhile(l => !l.StartsWith("## Detalhes")))
            if (!string.IsNullOrWhiteSpace(linha)) Console.WriteLine(linha.TrimEnd());
        Log.Ok($"Relatório completo: {arquivo}");
        return 0;
    }

    private string MontarRelatorio(List<ResultadoUasg> resultados)
    {
        var sb = new StringBuilder();
        int ambos = 0, soPortal = 0, soApi = 0, divergPrazo = 0, divergClass = 0, bloqueadas = 0;

        var detalhes = new StringBuilder();
        foreach (ResultadoUasg r in resultados)
        {
            detalhes.AppendLine($"### {r.Cliente.Codigo} - {r.Cliente.Orgao}");
            if (r.Portal is null) { bloqueadas++; detalhes.AppendLine($"- Portal: **falhou** ({r.ErroPortal})"); }
            if (r.Api is null) detalhes.AppendLine($"- API: **falhou** ({r.ErroApi})");

            var portal = r.Portal ?? new();
            var api = r.Api ?? new();
            var chaves = portal.Keys.Union(api.Keys).OrderBy(k => k).ToList();
            if (chaves.Count == 0) { detalhes.AppendLine("- Nenhuma licitação em aberto.\n"); continue; }

            detalhes.AppendLine("| Licitação | Portal | API | Situação (API) | Prazo portal | Prazo API | Objeto portal / API | Histórico |");
            detalhes.AppendLine("|---|---|---|---|---|---|---|---|");

            foreach (string chave in chaves)
            {
                portal.TryGetValue(chave, out var p);
                api.TryGetValue(chave, out var a);

                if (p is not null && a is not null) ambos++;
                else if (p is not null && r.Api is not null) soPortal++;
                else if (a is not null && r.Portal is not null) soApi++;

                string obs = "";
                if (p is not null && a is not null)
                {
                    if (!string.IsNullOrEmpty(p.Prazo) && p.Prazo != a.Prazo) { divergPrazo++; obs += " ⚠ prazo"; }
                    var cp = ClassificadorObjeto.Classificar(p.Objeto);
                    var ca = ClassificadorObjeto.Classificar(a.Objeto);
                    if (cp != ca) { divergClass++; obs += " ⚠ classificação"; }
                }

                string classif = $"{Descr(p?.Objeto)} / {Descr(a?.Objeto)}{obs}";
                string hist = _historico.JaProcessada(chave) ? "sim" : "não";
                detalhes.AppendLine($"| {p?.Titulo ?? a?.Titulo} | {(p is null ? "—" : "✔")} | {(a is null ? "—" : "✔")} | " +
                                    $"{a?.SituacaoNome ?? ""} | {p?.Prazo ?? ""} | {a?.Prazo ?? ""} | {classif} | {hist} |");
            }
            detalhes.AppendLine();
        }

        sb.AppendLine($"# Comparação portal Compras.gov.br x API PNCP - {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- UASGs comparadas: {resultados.Count} (portal falhou em {bloqueadas})");
        sb.AppendLine($"- Encontradas pelas duas fontes: {ambos}");
        sb.AppendLine($"- Só no portal: {soPortal}");
        sb.AppendLine($"- Só na API: {soApi}");
        sb.AppendLine($"- Divergências de prazo: {divergPrazo}");
        sb.AppendLine($"- Divergências na classificação do objeto: {divergClass}");
        sb.AppendLine();
        sb.AppendLine("Observação: o portal já esconde licitações suspensas/revogadas pelo texto do card;");
        sb.AppendLine("a API informa a situação oficial (o fluxo via API só processa as \"Divulgadas\").");
        sb.AppendLine();
        sb.AppendLine("## Detalhes por UASG");
        sb.AppendLine();
        sb.Append(detalhes);
        return sb.ToString();
    }

    private static string Descr(string? objeto) =>
        objeto is null ? "—" : ClassificadorObjeto.Descrever(ClassificadorObjeto.Classificar(objeto));
}
