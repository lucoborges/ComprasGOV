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

    /// <summary>Considera terminais apenas os status que não devem ser reprocessados.</summary>
    public bool JaProcessada(string chave)
    {
        lock (_lock)
        {
            if (!_itens.TryGetValue(chave, out var registro)) return false;
            return registro.Status is not StatusLicitacao.Erro;
        }
    }

    public void Salvar(LicitacaoHistorico registro)
    {
        lock (_lock)
        {
            _itens[registro.Chave] = registro;

            string? pasta = Path.GetDirectoryName(_caminho);
            if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

            string temp = _caminho + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_itens.Values.ToList(), Opcoes));
            File.Move(temp, _caminho, overwrite: true);
        }
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
