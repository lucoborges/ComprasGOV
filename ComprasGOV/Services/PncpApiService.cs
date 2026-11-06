using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using ComprasGOV.Automation;
using ComprasGOV.Models;

namespace ComprasGOV.Services;

/// <summary>
/// Consulta as licitações pelas APIs públicas oficiais, sem navegador e sem captcha:
/// <list type="bullet">
/// <item>Dados Abertos do Compras.gov.br: UASG → CNPJ do órgão (guardado em cache local);</item>
/// <item>PNCP: contratações com proposta em aberto por órgão/unidade e os arquivos de cada compra.</item>
/// </list>
/// Falhas de rede são repetidas com espera crescente; se ainda assim falhar, lança
/// exceção (o cliente fica sem registro no histórico e é tentado no próximo ciclo).
/// </summary>
public sealed class PncpApiService
{
    private const string UrlUasg = "https://dadosabertos.compras.gov.br/modulo-uasg/1_consultarUasg";
    private const string UrlPropostas = "https://pncp.gov.br/api/consulta/v1/contratacoes/proposta";
    private const string UrlArquivos = "https://pncp.gov.br/api/pncp/v1/orgaos/{0}/compras/{1}/{2}/arquivos";
    private const int TamanhoPagina = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan[] Esperas =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)
    };

    private readonly HttpClient _http;
    private readonly string _arquivoCacheCnpj;
    private readonly Dictionary<string, string> _cacheCnpj;

    public PncpApiService(string arquivoCacheCnpj)
    {
        _arquivoCacheCnpj = arquivoCacheCnpj;
        _cacheCnpj = CarregarCache();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ComprasGOV/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // ------------------------------------------------------------------
    //  UASG -> CNPJ
    // ------------------------------------------------------------------

    /// <summary>CNPJ do órgão dono da UASG (null quando a UASG não existe/está inativa).</summary>
    public async Task<string?> ObterCnpjAsync(string uasg, CancellationToken ct = default)
    {
        string chave = SemZeros(uasg);
        if (_cacheCnpj.TryGetValue(chave, out string? emCache)) return Cnpj14(emCache);

        // A API aceita o código sem zeros à esquerda; tenta também com 6 dígitos.
        foreach (string codigo in new[] { chave, chave.PadLeft(6, '0') }.Distinct())
        {
            string url = $"{UrlUasg}?pagina=1&codigoUasg={codigo}&statusUasg=true";
            RespostaUasg? resp = await GetJsonAsync<RespostaUasg>(url, ct);
            string? cnpj = resp?.Resultado?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.CnpjCpfOrgao))?.CnpjCpfOrgao;
            if (string.IsNullOrWhiteSpace(cnpj)) continue;

            cnpj = Cnpj14(cnpj);
            _cacheCnpj[chave] = cnpj;
            GravarCache();
            return cnpj;
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Licitações com proposta em aberto
    // ------------------------------------------------------------------

    /// <summary>Todas as contratações da UASG que estão recebendo propostas (qualquer situação).</summary>
    public async Task<List<LicitacaoPncp>> ListarPropostasAbertasAsync(string uasg, string cnpj, CancellationToken ct = default)
    {
        var lista = new List<LicitacaoPncp>();
        string dataFinal = DateTime.Today.AddYears(2).ToString("yyyyMMdd");
        string uasgNormalizada = SemZeros(uasg);

        // O PNCP compara o código da unidade como texto e guarda as UASGs do
        // Compras.gov.br com 6 dígitos ("090014"); "90014" não encontra nada.
        string codigoUnidade = uasgNormalizada.PadLeft(6, '0');

        for (int pagina = 1; ; pagina++)
        {
            string url = $"{UrlPropostas}?dataFinal={dataFinal}&cnpj={cnpj}" +
                         $"&codigoUnidadeAdministrativa={codigoUnidade}&pagina={pagina}&tamanhoPagina={TamanhoPagina}";

            RespostaPaginada? resp = await GetJsonAsync<RespostaPaginada>(url, ct);
            if (resp?.Data is null || resp.Data.Count == 0) break;

            foreach (ContratacaoDto c in resp.Data)
            {
                // Garantia extra: o filtro da API é por unidade, mas confere mesmo assim.
                if (SemZeros(c.UnidadeOrgao?.CodigoUnidade ?? "") != uasgNormalizada) continue;
                lista.Add(Converter(uasgNormalizada, c));
            }

            if (resp.PaginasRestantes <= 0 || pagina >= resp.TotalPaginas) break;
        }

        return lista;
    }

    /// <summary>Arquivos ativos publicados na compra (edital, anexos, retificações, avisos).</summary>
    public async Task<List<ArquivoPncp>> ListarArquivosAsync(LicitacaoPncp lic, CancellationToken ct = default)
    {
        string url = string.Format(UrlArquivos, lic.Cnpj, lic.Ano, lic.Sequencial);
        List<ArquivoDto>? docs = await GetJsonAsync<List<ArquivoDto>>(url, ct);

        return (docs ?? new())
            .Where(d => d.StatusAtivo != false && !string.IsNullOrWhiteSpace(d.Url))
            .OrderBy(d => d.SequencialDocumento)
            .Select(d => new ArquivoPncp(d.Url!, NomeArquivo(d)))
            .ToList();
    }

    private static string NomeArquivo(ArquivoDto d)
    {
        string titulo = TextoUtil.LimparEspacos(d.Titulo);
        if (string.IsNullOrEmpty(titulo)) titulo = TextoUtil.LimparEspacos(d.TipoDocumentoNome);
        if (string.IsNullOrEmpty(titulo)) titulo = $"documento {d.SequencialDocumento}";
        return titulo;
    }

    private static LicitacaoPncp Converter(string uasg, ContratacaoDto c)
    {
        string numero = $"{SemZeros(c.NumeroCompra ?? "")}/{c.AnoCompra}";

        // "Pregão - Eletrônico" -> "PREGÃO ELETRÔNICO N° 127/2026" (como no portal).
        string modalidade = TextoUtil.LimparEspacos((c.ModalidadeNome ?? "Compra").Replace(" - ", " "))
            .ToUpperInvariant();

        return new LicitacaoPncp(
            Uasg: uasg,
            Cnpj: c.OrgaoEntidade?.Cnpj ?? "",
            Ano: c.AnoCompra,
            Sequencial: c.SequencialCompra,
            Numero: numero,
            Titulo: $"{modalidade} N° {numero}",
            Objeto: TextoUtil.LimparEspacos(c.ObjetoCompra),
            EncerramentoProposta: c.DataEncerramentoProposta,
            SituacaoId: c.SituacaoCompraId,
            SituacaoNome: c.SituacaoCompraNome ?? "",
            UrlCompras: c.LinkSistemaOrigem ?? "");
    }

    // ------------------------------------------------------------------
    //  HTTP com repetição
    // ------------------------------------------------------------------

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        for (int tentativa = 0; ; tentativa++)
        {
            try
            {
                using HttpResponseMessage resp = await _http.GetAsync(url, ct);

                // O PNCP responde 204 quando a consulta não tem resultados.
                if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;

                bool transitorio = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
                if (transitorio && tentativa < Esperas.Length)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");

                if (!resp.IsSuccessStatusCode)
                {
                    string corpo = await resp.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException(
                        $"API respondeu HTTP {(int)resp.StatusCode}: {(corpo.Length > 300 ? corpo[..300] : corpo)}");
                }

                await using Stream s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonSerializer.DeserializeAsync<T>(s, Json, ct);
            }
            catch (Exception ex) when (tentativa < Esperas.Length && !ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException)
            {
                Log.Aviso($"API indisponível ({ex.Message}); nova tentativa em {Esperas[tentativa].TotalSeconds:F0}s...");
                await Task.Delay(Esperas[tentativa], ct);
            }
        }
    }

    /// <summary>Os Dados Abertos devolvem o CNPJ como número (sem zeros à esquerda,
    /// ex.: "508903000188"); o PNCP exige os 14 dígitos ("00508903000188").</summary>
    private static string Cnpj14(string cnpj)
    {
        string digitos = new(cnpj.Where(char.IsDigit).ToArray());
        return digitos.Length < 14 ? digitos.PadLeft(14, '0') : digitos;
    }

    private static string SemZeros(string codigo)
    {
        string s = codigo.Trim().TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }

    // ------------------------------------------------------------------
    //  Cache UASG -> CNPJ (o vínculo praticamente não muda)
    // ------------------------------------------------------------------

    private Dictionary<string, string> CarregarCache()
    {
        try
        {
            if (File.Exists(_arquivoCacheCnpj))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_arquivoCacheCnpj), Json)
                       ?? new();
        }
        catch (Exception ex)
        {
            Log.Aviso($"Cache UASG->CNPJ ilegível ({ex.Message}); será recriado.");
        }
        return new();
    }

    private void GravarCache()
    {
        try
        {
            string? pasta = Path.GetDirectoryName(_arquivoCacheCnpj);
            if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);
            File.WriteAllText(_arquivoCacheCnpj, JsonSerializer.Serialize(_cacheCnpj, Json));
        }
        catch (Exception ex)
        {
            Log.Aviso($"Não foi possível gravar o cache UASG->CNPJ ({ex.Message}).");
        }
    }

    // ------------------------------------------------------------------
    //  DTOs das APIs
    // ------------------------------------------------------------------

    private sealed class RespostaUasg
    {
        public List<UasgDto>? Resultado { get; set; }
    }

    private sealed class UasgDto
    {
        public string? CodigoUasg { get; set; }
        public string? CnpjCpfOrgao { get; set; }
    }

    private sealed class RespostaPaginada
    {
        public List<ContratacaoDto>? Data { get; set; }
        public int TotalPaginas { get; set; }
        public int PaginasRestantes { get; set; }
    }

    private sealed class ContratacaoDto
    {
        public string? NumeroCompra { get; set; }
        public int AnoCompra { get; set; }
        public int SequencialCompra { get; set; }
        public string? ObjetoCompra { get; set; }
        public string? ModalidadeNome { get; set; }
        public int SituacaoCompraId { get; set; }
        public string? SituacaoCompraNome { get; set; }
        public DateTime? DataEncerramentoProposta { get; set; }
        public string? LinkSistemaOrigem { get; set; }
        public OrgaoDto? OrgaoEntidade { get; set; }
        public UnidadeDto? UnidadeOrgao { get; set; }
    }

    private sealed class OrgaoDto
    {
        public string? Cnpj { get; set; }
    }

    private sealed class UnidadeDto
    {
        public string? CodigoUnidade { get; set; }
    }

    private sealed class ArquivoDto
    {
        public string? Url { get; set; }
        public string? Titulo { get; set; }
        public string? TipoDocumentoNome { get; set; }
        public int SequencialDocumento { get; set; }
        public bool? StatusAtivo { get; set; }
    }
}
