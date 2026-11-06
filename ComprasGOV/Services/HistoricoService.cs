using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    private readonly string _caminho;
    private readonly object _lock = new();
    private readonly Dictionary<string, LicitacaoHistorico> _itens = new(StringComparer.OrdinalIgnoreCase);

    public HistoricoService(string caminho)
    {
        _caminho = caminho;
        Carregar();
    }

    /// <summary>
    /// Considera terminais apenas os status que não devem ser reprocessados.
    /// "erro" e "sem-edital" são tentados de novo a cada ciclo: o órgão costuma
    /// publicar os arquivos no PNCP depois de divulgar a compra.
    /// </summary>
    public bool JaProcessada(string chave)
    {
        lock (_lock)
        {
            if (!_itens.TryGetValue(chave, out var registro)) return false;
            return registro.Status is not (StatusLicitacao.Erro or StatusLicitacao.SemEdital);
        }
    }

    public void Salvar(LicitacaoHistorico registro)
    {
        lock (_lock)
        {
            _itens[registro.Chave] = registro;
            Gravar();
        }
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
        string? pasta = Path.GetDirectoryName(_caminho);
        if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

        string temp = _caminho + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_itens.Values.ToList(), Opcoes));
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

        try
        {
            var lista = JsonSerializer.Deserialize<List<LicitacaoHistorico>>(File.ReadAllText(_caminho), Opcoes) ?? new();
            foreach (var item in lista) _itens[item.Chave] = item;
            Log.Info($"Histórico carregado: {_itens.Count} registro(s) em {_caminho}");
        }
        catch (Exception ex)
        {
            Log.Aviso($"Falha ao ler histórico ({ex.Message}); seguindo sem histórico.");
            _itens.Clear();
        }
    }
}
