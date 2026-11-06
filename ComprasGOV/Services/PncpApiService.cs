using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComprasGOV.Automation;
using ComprasGOV.Models;

namespace ComprasGOV.Services;

/// <summary>
/// Consulta as licitações pelas APIs públicas oficiais, sem navegador e sem captcha:
/// <list type="bullet">
/// <item>Dados Abertos do Compras.gov.br: UASG → CNPJ do órgão (guardado em cache local);</item>
/// <item>PNCP: contratações com proposta em aberto por órgão/unidade ou por UF e plataforma,
/// e os arquivos de cada compra;</item>
/// <item>busca do site do PNCP (tela "Editais"), complementar na busca geral: traz também as
/// licitações que ainda vão abrir o prazo de propostas ("a receber").</item>
/// </list>
/// Falhas de rede são repetidas com espera crescente; se ainda assim falhar, lança
/// exceção (o cliente fica sem registro no histórico e é tentado no próximo ciclo).
/// </summary>
public sealed class PncpApiService
{
    private const string UrlUasg = "https://dadosabertos.compras.gov.br/modulo-uasg/1_consultarUasg";
    private const string UrlPropostas = "https://pncp.gov.br/api/consulta/v1/contratacoes/proposta";
    private const string UrlArquivos = "https://pncp.gov.br/api/pncp/v1/orgaos/{0}/compras/{1}/{2}/arquivos";
    private const string UrlBusca = "https://pncp.gov.br/api/search/";
    private const int TamanhoPagina = 50;

    /// <summary>Página grande: a busca do site costuma caber em uma única requisição.</summary>
    private const int TamanhoPaginaBusca = 500;

    /// <summary>Acima disto os filtros de UF/plataforma claramente não foram aplicados pela busca.</summary>
    private const int LimiteResultadosBusca = 3000;

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
        string uasgNormalizada = SemZeros(uasg);

        // O PNCP compara o código da unidade como texto e guarda as UASGs do
        // Compras.gov.br com 6 dígitos ("090014"); "90014" não encontra nada.
        string codigoUnidade = uasgNormalizada.PadLeft(6, '0');

        // Garantia extra: o filtro da API é por unidade, mas confere mesmo assim.
        return await ConsultarPropostasAsync($"cnpj={cnpj}&codigoUnidadeAdministrativa={codigoUnidade}",
            c => SemZeros(c.UnidadeOrgao?.CodigoUnidade ?? "") == uasgNormalizada, ct);
    }

    /// <summary>
    /// Busca geral nas UFs e fontes/plataformas informadas (qualquer situação), com os
    /// mesmos filtros da tela "Editais" do site do PNCP (Status "A Receber/Recebendo
    /// Proposta", UFs e Fontes/Plataformas):
    /// <list type="number">
    /// <item>API oficial de consulta: as que estão recebendo propostas (fonte principal);</item>
    /// <item>busca do site, uma tentativa por ciclo: acrescenta as que ainda vão abrir o
    /// prazo ("a receber"). Se ela falhar, essas entram no ciclo em que o prazo abrir,
    /// pela API oficial.</item>
    /// </list>
    /// Só lança exceção se nenhuma consulta da API oficial concluir e a busca do site também falhar.
    /// </summary>
    public async Task<List<LicitacaoPncp>> ListarBuscaGeralAsync(IReadOnlyCollection<string> ufs,
        IReadOnlyCollection<int> plataformas, CancellationToken ct = default)
    {
        var lista = new List<LicitacaoPncp>();
        Exception? erroOficial = null;
        int consultasOk = 0;

        // Cada UF x plataforma é independente: uma consulta que falhar não descarta as outras.
        foreach (string uf in ufs)
        {
            foreach (int plataforma in plataformas)
            {
                try
                {
                    lista.AddRange(await ConsultarPropostasAsync($"uf={uf}&idUsuario={plataforma}",
                        c => string.IsNullOrEmpty(c.UnidadeOrgao?.UfSigla) ||
                             string.Equals(c.UnidadeOrgao.UfSigla, uf, StringComparison.OrdinalIgnoreCase), ct));
                    consultasOk++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    erroOficial = ex;
                    Log.Aviso($"API oficial do PNCP falhou para UF {uf}, plataforma {plataforma} ({Causa(ex)}); " +
                              "a busca do site completa, se responder.");
                }
            }
        }
        if (consultasOk > 0)
            Log.Info($"API oficial do PNCP: {lista.Count} licitação(ões) recebendo propostas.");

        try
        {
            List<LicitacaoPncp> site = await BuscarNoSiteAsync(ufs, plataformas, ct);
            var chaves = new HashSet<string>(lista.Select(l => l.Chave), StringComparer.OrdinalIgnoreCase);
            int extras = 0;
            foreach (LicitacaoPncp lic in site)
            {
                if (!chaves.Add(lic.Chave)) continue;
                lista.Add(lic);
                extras++;
            }
            Log.Info($"Busca do site do PNCP: {site.Count} licitação(ões); {extras} além da API oficial " +
                     "(a receber ou recém-publicadas).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (consultasOk == 0 && erroOficial is not null)
                throw new InvalidOperationException($"API oficial: {Causa(erroOficial)}; busca do site: {Causa(ex)}", ex);

            Log.Info($"Busca do site do PNCP indisponível agora ({Causa(ex)}); as \"a receber\" entram " +
                     "quando o prazo de propostas abrir.");
        }

        // A mesma compra pode aparecer em mais de uma página (a lista muda durante a paginação).
        return lista.GroupBy(l => l.Chave, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>Mensagem da causa raiz (ex.: conexão encerrada pelo servidor), não só a genérica.</summary>
    private static string Causa(Exception ex)
    {
        string raiz = ex.GetBaseException().Message;
        return raiz == ex.Message ? ex.Message : $"{ex.Message} {raiz}";
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

    /// <summary>API oficial de consulta: contratações recebendo propostas (todas as páginas).</summary>
    private async Task<List<LicitacaoPncp>> ConsultarPropostasAsync(string filtros,
        Func<ContratacaoDto, bool> aceitar, CancellationToken ct)
    {
        var lista = new List<LicitacaoPncp>();

        // Credenciamentos ficam abertos por anos: o horizonte longo não deixa nenhum de fora.
        string dataFinal = DateTime.Today.AddYears(10).ToString("yyyyMMdd");

        for (int pagina = 1; ; pagina++)
        {
            string url = $"{UrlPropostas}?dataFinal={dataFinal}&{filtros}&pagina={pagina}&tamanhoPagina={TamanhoPagina}";

            RespostaPaginada? resp = await GetJsonAsync<RespostaPaginada>(url, ct);
            if (resp?.Data is null || resp.Data.Count == 0) break;

            lista.AddRange(resp.Data.Where(aceitar).Select(Converter));

            if (resp.PaginasRestantes <= 0 || pagina >= resp.TotalPaginas) break;
        }

        return lista;
    }

    /// <summary>
    /// Busca do site do PNCP (status "A Receber/Recebendo Proposta"), todas as páginas.
    /// Sem novas tentativas: é complementar à API oficial e é repetida no próximo ciclo.
    /// </summary>
    private async Task<List<LicitacaoPncp>> BuscarNoSiteAsync(IReadOnlyCollection<string> ufs,
        IReadOnlyCollection<int> plataformas, CancellationToken ct)
    {
        var lista = new List<LicitacaoPncp>();
        string filtroUfs = Uri.EscapeDataString(string.Join("|", ufs));
        string filtroFontes = Uri.EscapeDataString(string.Join("|", plataformas));
        int recebidos = 0, descartados = 0;

        for (int pagina = 1; ; pagina++)
        {
            string url = $"{UrlBusca}?tipos_documento=edital&ordenacao=-data&status=recebendo_proposta" +
                         $"&ufs={filtroUfs}&fontes={filtroFontes}&pagina={pagina}&tam_pagina={TamanhoPaginaBusca}";

            RespostaBusca resp = await GetJsonAsync<RespostaBusca>(url, ct, repetir: false)
                ?? throw new InvalidOperationException("a busca não retornou conteúdo");
            if (resp.Items is null)
                throw new InvalidOperationException("resposta da busca em formato inesperado");
            if (resp.Total > LimiteResultadosBusca)
                throw new InvalidOperationException($"{resp.Total} resultados: os filtros de UF/plataforma não foram aplicados");

            foreach (ItemBusca item in resp.Items)
            {
                recebidos++;

                // Garantia extra: confere UF e plataforma mesmo com o filtro aplicado pela busca.
                bool noFiltro = ufs.Contains(item.Uf ?? "", StringComparer.OrdinalIgnoreCase) &&
                                item.UsuarioId is int plataforma && plataformas.Contains(plataforma);

                LicitacaoPncp? lic = noFiltro ? ConverterBusca(item) : null;
                if (lic is null) descartados++;
                else lista.Add(lic);
            }

            if (resp.Items.Count == 0 || recebidos >= resp.Total) break;
        }

        if (descartados > 0)
            Log.Aviso($"Busca do PNCP: {descartados} resultado(s) fora do filtro (UF/plataforma) ou incompleto(s) descartado(s).");

        return lista;
    }

    private static string NomeArquivo(ArquivoDto d)
    {
        string titulo = TextoUtil.LimparEspacos(d.Titulo);
        if (string.IsNullOrEmpty(titulo)) titulo = TextoUtil.LimparEspacos(d.TipoDocumentoNome);
        if (string.IsNullOrEmpty(titulo)) titulo = $"documento {d.SequencialDocumento}";
        return titulo;
    }

    private static LicitacaoPncp Converter(ContratacaoDto c)
    {
        string numero = $"{SemZeros(c.NumeroCompra ?? "")}/{c.AnoCompra}";

        return new LicitacaoPncp(
            Uasg: SemZeros(c.UnidadeOrgao?.CodigoUnidade ?? ""),
            Cnpj: c.OrgaoEntidade?.Cnpj ?? "",
            Ano: c.AnoCompra,
            Sequencial: c.SequencialCompra,
            Numero: numero,
            Titulo: Titulo(c.ModalidadeNome, numero),
            Objeto: TextoUtil.LimparEspacos(c.ObjetoCompra),
            EncerramentoProposta: c.DataEncerramentoProposta,
            SituacaoId: c.SituacaoCompraId,
            SituacaoNome: c.SituacaoCompraNome ?? "",
            UrlCompras: c.LinkSistemaOrigem ?? "",
            UnidadeNome: TextoUtil.LimparEspacos(c.UnidadeOrgao?.NomeUnidade),
            Plataforma: c.UsuarioNome ?? "");
    }

    /// <summary>Item da busca do site; null quando faltam os dados que identificam a compra.</summary>
    private static LicitacaoPncp? ConverterBusca(ItemBusca i)
    {
        if (string.IsNullOrWhiteSpace(i.OrgaoCnpj) || i.Ano is not int ano || i.NumeroSequencial is not int sequencial)
            return null;

        string numero = $"{SemZeros(i.Numero ?? "")}/{ano}";
        bool cancelada = i.Cancelado == true;

        return new LicitacaoPncp(
            Uasg: SemZeros(i.UnidadeCodigo ?? ""),
            Cnpj: Cnpj14(i.OrgaoCnpj),
            Ano: ano,
            Sequencial: sequencial,
            Numero: numero,
            Titulo: Titulo(i.ModalidadeNome, numero),
            Objeto: TextoUtil.LimparEspacos(i.Descricao),
            EncerramentoProposta: LerData(i.DataFimVigencia),
            SituacaoId: cancelada ? 0 : i.SituacaoId ?? 0,
            SituacaoNome: cancelada ? "Cancelada" : i.SituacaoNome ?? "",
            UrlCompras: i.LinkSistemaOrigem ?? "",
            UnidadeNome: TextoUtil.LimparEspacos(i.UnidadeNome),
            Plataforma: i.UsuarioNome ?? "");
    }

    /// <summary>"Pregão - Eletrônico" + "127/2026" -> "PREGÃO ELETRÔNICO N° 127/2026" (como no portal).</summary>
    private static string Titulo(string? modalidade, string numero) =>
        $"{TextoUtil.LimparEspacos((modalidade ?? "Compra").Replace(" - ", " ")).ToUpperInvariant()} N° {numero}";

    /// <summary>Datas da busca chegam sem fuso (horário de Brasília), ex.: "2026-11-12T08:00".</summary>
    private static DateTime? LerData(string? valor) =>
        DateTime.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime data) ? data : null;

    // ------------------------------------------------------------------
    //  HTTP com repetição
    // ------------------------------------------------------------------

    /// <param name="repetir">false = uma única tentativa (consultas complementares).</param>
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct, bool repetir = true) where T : class
    {
        int maxTentativas = repetir ? Esperas.Length : 0;

        for (int tentativa = 0; ; tentativa++)
        {
            try
            {
                using HttpResponseMessage resp = await _http.GetAsync(url, ct);

                // O PNCP responde 204 quando a consulta não tem resultados.
                if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;

                bool transitorio = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
                if (transitorio && tentativa < maxTentativas)
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
            catch (Exception ex) when (tentativa < maxTentativas && !ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TaskCanceledException)
            {
                Log.Aviso($"API indisponível ({Causa(ex)}); nova tentativa em {Esperas[tentativa].TotalSeconds:F0}s...");
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
        public string? UsuarioNome { get; set; }
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
        public string? NomeUnidade { get; set; }
        public string? UfSigla { get; set; }
    }

    private sealed class ArquivoDto
    {
        public string? Url { get; set; }
        public string? Titulo { get; set; }
        public string? TipoDocumentoNome { get; set; }
        public int SequencialDocumento { get; set; }
        public bool? StatusAtivo { get; set; }
    }

    private sealed class RespostaBusca
    {
        public List<ItemBusca>? Items { get; set; }
        public int Total { get; set; }
    }

    /// <summary>Item da busca do site (campos em snake_case; números chegam como texto).</summary>
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    private sealed class ItemBusca
    {
        [JsonPropertyName("orgao_cnpj")] public string? OrgaoCnpj { get; set; }
        [JsonPropertyName("ano")] public int? Ano { get; set; }
        [JsonPropertyName("numero_sequencial")] public int? NumeroSequencial { get; set; }
        [JsonPropertyName("numero")] public string? Numero { get; set; }
        [JsonPropertyName("modalidade_licitacao_nome")] public string? ModalidadeNome { get; set; }
        [JsonPropertyName("description")] public string? Descricao { get; set; }
        [JsonPropertyName("data_fim_vigencia")] public string? DataFimVigencia { get; set; }
        [JsonPropertyName("situacao_id")] public int? SituacaoId { get; set; }
        [JsonPropertyName("situacao_nome")] public string? SituacaoNome { get; set; }
        [JsonPropertyName("cancelado")] public bool? Cancelado { get; set; }
        [JsonPropertyName("link_sistema_origem")] public string? LinkSistemaOrigem { get; set; }
        [JsonPropertyName("unidade_codigo")] public string? UnidadeCodigo { get; set; }
        [JsonPropertyName("unidade_nome")] public string? UnidadeNome { get; set; }
        [JsonPropertyName("uf")] public string? Uf { get; set; }
        [JsonPropertyName("usuario_id")] public int? UsuarioId { get; set; }
        [JsonPropertyName("usuario_nome")] public string? UsuarioNome { get; set; }
    }
}
