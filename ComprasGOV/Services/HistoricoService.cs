using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ComprasGOV.Models;

namespace ComprasGOV.Services;

/// <summary>
/// Histórico de licitações já avaliadas, persistido em JSON local.
/// Evita reextrair licitações que já foram baixadas (ou reavaliadas).
/// </summary>
public sealed class HistoricoService
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Chave no formato atual: número de controle PNCP ("CNPJ-1-SEQUENCIAL/ANO").</summary>
    private static readonly Regex FormatoChavePncp = new(@"^\d{14}-1-\d{6,}/\d{4,}$", RegexOptions.Compiled);

    /// <summary>Link do PNCP gravado nos registros: .../editais/{cnpj}/{ano}/{sequencial}.</summary>
    private static readonly Regex LinkEdital = new(@"/editais/(\d{14})/(\d{4,})/(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Versão do formato do arquivo. Desde a chave nova o arquivo é um objeto
    /// {"Versao":2,"Itens":[...]} e não mais uma lista: uma versão antiga do programa
    /// (que só conhece a chave "UASG-número/ano") falha ao ler e adia o ciclo, em vez
    /// de reprocessar e reenviar por e-mail tudo o que já foi enviado.
    /// </summary>
    private const int VersaoArquivo = 2;

    private readonly string _caminho;
    private readonly bool _somenteLeitura;
    private readonly object _lock = new();
    private readonly Dictionary<string, LicitacaoHistorico> _itens = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="somenteLeitura">Modos de diagnóstico (--comparar/--testar-pncp): nada é
    /// gravado no arquivo (nem a migração das chaves antigas, feita só em memória).</param>
    public HistoricoService(string caminho, bool somenteLeitura = false)
    {
        _caminho = caminho;
        _somenteLeitura = somenteLeitura;
        Carregar();
    }

    private sealed class ArquivoHistorico
    {
        public int Versao { get; set; }
        public List<LicitacaoHistorico>? Itens { get; set; }
    }

    /// <summary>
    /// Considera terminais apenas os status que não devem ser reprocessados.
    /// "erro" e "sem-edital" são tentados de novo a cada ciclo: o órgão costuma
    /// publicar os arquivos no PNCP depois de divulgar a compra.
    /// A chave antiga "UASG-número/ano" (montada pelo fluxo do portal) também é
    /// procurada nos registros gravados com a chave nova (mesma UASG e número).
    /// </summary>
    public bool JaProcessada(string chave)
    {
        lock (_lock)
        {
            if (_itens.TryGetValue(chave, out var registro) && Terminal(registro)) return true;
            if (FormatoChavePncp.IsMatch(chave)) return false;

            string legada = NormalizarChaveLegada(chave);
            return _itens.Values.Any(r => Terminal(r) &&
                string.Equals(NormalizarChaveLegada($"{r.Uasg}-{r.Numero}"), legada, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool Terminal(LicitacaoHistorico registro) =>
        registro.Status is not (StatusLicitacao.Erro or StatusLicitacao.SemEdital);

    /// <summary>"090014-67/2026" -> "90014-67/2026" (a UASG pode vir com zeros à esquerda).</summary>
    private static string NormalizarChaveLegada(string chave)
    {
        int traco = chave.IndexOf('-');
        if (traco <= 0) return chave.Trim();

        string uasg = chave[..traco].Trim().TrimStart('0');
        return $"{(uasg.Length == 0 ? "0" : uasg)}-{chave[(traco + 1)..].Trim()}";
    }

    /// <summary>
    /// Grava o registro. O fluxo do portal ainda monta a chave antiga; quando o registro
    /// já tem o link do PNCP, ele é gravado com a chave nova (o registro é atualizado),
    /// para a busca geral do mesmo ciclo reconhecer a licitação.
    /// </summary>
    public void Salvar(LicitacaoHistorico registro)
    {
        lock (_lock)
        {
            registro.Chave = ChaveCanonica(registro);
            RemoverAntigoSemLink(registro);
            _itens[registro.Chave] = registro;
            Gravar();
        }
    }

    /// <summary>
    /// Registros antigos do portal sem link do PNCP (só rejeitado/indefinido/erro: quem
    /// baixou sempre tem o link) não puderam ser migrados e a licitação é reavaliada
    /// com a chave nova; ao gravá-la, o registro antigo da mesma UASG e número sai.
    /// </summary>
    private void RemoverAntigoSemLink(LicitacaoHistorico registro)
    {
        if (!FormatoChavePncp.IsMatch(registro.Chave)) return;

        string legada = NormalizarChaveLegada($"{registro.Uasg}-{registro.Numero}");
        foreach (LicitacaoHistorico antigo in _itens.Values
                     .Where(r => string.IsNullOrWhiteSpace(r.UrlPncp) &&
                                 r.Status != StatusLicitacao.Baixado &&
                                 !FormatoChavePncp.IsMatch(r.Chave) &&
                                 string.Equals(NormalizarChaveLegada(r.Chave), legada, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _itens.Remove(antigo.Chave);
        }
    }

    /// <summary>Chave nova (número de controle PNCP) quando o registro tem a antiga e o link do PNCP.</summary>
    private static string ChaveCanonica(LicitacaoHistorico registro)
    {
        if (FormatoChavePncp.IsMatch(registro.Chave)) return registro.Chave;

        Match link = LinkEdital.Match(registro.UrlPncp ?? "");
        return link.Success &&
               int.TryParse(link.Groups[2].Value, out int ano) &&
               int.TryParse(link.Groups[3].Value, out int sequencial)
            ? LicitacaoPncp.ChavePncp(link.Groups[1].Value, ano, sequencial)
            : registro.Chave;
    }

    /// <summary>
    /// Licitações baixadas cujo e-mail ainda NÃO foi enviado (EmailEnviado == false).
    /// Registros antigos (EmailEnviado == null) nunca são considerados pendentes.
    /// </summary>
    public List<LicitacaoHistorico> PendentesDeEmail()
    {
        lock (_lock)
        {
            return _itens.Values
                .Where(r => r.Status == StatusLicitacao.Baixado
                            && r.EmailEnviado == false
                            && !string.IsNullOrWhiteSpace(r.Arquivo))
                .ToList();
        }
    }

    /// <summary>Marca a licitação como e-mailada e persiste o histórico.</summary>
    public void MarcarEmailEnviado(string chave)
    {
        lock (_lock)
        {
            if (!_itens.TryGetValue(chave, out var registro)) return;
            registro.EmailEnviado = true;
            Gravar();
        }
    }

    /// <summary>Grava o histórico em disco (lock já segurado pelo chamador).</summary>
    private void Gravar()
    {
        if (_somenteLeitura) return;

        string? pasta = Path.GetDirectoryName(_caminho);
        if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

        string temp = _caminho + ".tmp";
        var arquivo = new ArquivoHistorico { Versao = VersaoArquivo, Itens = _itens.Values.ToList() };
        File.WriteAllText(temp, JsonSerializer.Serialize(arquivo, Opcoes));
        File.Move(temp, _caminho, overwrite: true);
    }

    public void ExibirEstatisticas()
    {
        lock (_lock)
        {
            Log.Info($"Histórico: {_itens.Count} licitação(ões) registradas em {_caminho}");
            foreach (var grupo in _itens.Values.GroupBy(r => r.Status).OrderBy(g => g.Key))
                Log.Info($"   {grupo.Key}: {grupo.Count()}");
        }
    }

    private void Carregar()
    {
        if (!File.Exists(_caminho)) return;

        bool formatoAntigo;
        try
        {
            string json = File.ReadAllText(_caminho);
            formatoAntigo = json.TrimStart().StartsWith('[');

            List<LicitacaoHistorico> lista;
            if (formatoAntigo)
            {
                lista = JsonSerializer.Deserialize<List<LicitacaoHistorico>>(json, Opcoes) ?? new();
            }
            else
            {
                var arquivo = JsonSerializer.Deserialize<ArquivoHistorico>(json, Opcoes)
                              ?? throw new InvalidOperationException("arquivo vazio");
                if (arquivo.Versao > VersaoArquivo)
                    throw new InvalidOperationException(
                        $"gravado por uma versão mais nova do programa (formato {arquivo.Versao}); atualize esta máquina");
                lista = arquivo.Itens ?? throw new InvalidOperationException("lista de itens ausente");
            }

            foreach (var item in lista) _itens[item.Chave] = item;
            Log.Info($"Histórico carregado: {_itens.Count} registro(s) em {_caminho}");
        }
        catch (Exception ex)
        {
            // Nunca seguir com histórico vazio: tudo seria reprocessado e os
            // e-mails já enviados seriam repetidos. Quem chama adia o ciclo.
            throw new InvalidOperationException($"Falha ao ler o histórico {_caminho}: {ex.Message}", ex);
        }

        lock (_lock)
        {
            int migrados = MigrarChavesAntigas();
            if ((migrados == 0 && !formatoAntigo) || _somenteLeitura) return;

            // Cópia do arquivo original antes de regravar com as chaves novas / formato novo.
            string copia = $"{_caminho}.antes-migracao-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_caminho, copia, overwrite: true);
            Gravar();
            Log.Info($"Histórico: {migrados} registro(s) migrados para a chave nova " +
                     $"(número de controle do PNCP); cópia do original em {copia}");
        }
    }

    /// <summary>
    /// Registros gravados com a chave antiga "UASG-número/ano" passam a usar o número
    /// de controle do PNCP, extraído do link do PNCP gravado no próprio registro.
    /// Sem link (rejeitados/indefinidos do fluxo do portal) o registro fica como está:
    /// no pior caso a licitação é reavaliada, sem novo download nem e-mail.
    /// </summary>
    private int MigrarChavesAntigas()
    {
        int migrados = 0;

        foreach (LicitacaoHistorico registro in _itens.Values.ToList())
        {
            string nova = ChaveCanonica(registro);
            if (string.Equals(nova, registro.Chave, StringComparison.OrdinalIgnoreCase)) continue;

            _itens.Remove(registro.Chave);
            migrados++;

            // Já existe registro com a chave nova (ex.: gravado por outra execução): fica o
            // mais "forte" (baixado > rejeitado/indefinido > sem-edital/erro; empate: o mais
            // recente) e o e-mail continua marcado como enviado se um dos dois já foi.
            if (_itens.TryGetValue(nova, out var existente))
            {
                bool emailEnviado = existente.EmailEnviado == true || registro.EmailEnviado == true;
                bool ficaExistente = Forca(existente) != Forca(registro)
                    ? Forca(existente) > Forca(registro)
                    : existente.DataProcessamento >= registro.DataProcessamento;
                LicitacaoHistorico vencedor = ficaExistente ? existente : registro;
                if (emailEnviado) vencedor.EmailEnviado = true;
                if (ficaExistente) continue;
            }

            registro.Chave = nova;
            _itens[nova] = registro;
        }

        return migrados;
    }

    private static int Forca(LicitacaoHistorico registro) => registro.Status switch
    {
        StatusLicitacao.Baixado => 2,
        StatusLicitacao.Erro or StatusLicitacao.SemEdital => 0,
        _ => 1
    };
}
