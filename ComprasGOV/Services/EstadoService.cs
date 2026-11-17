using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComprasGOV.Services;

/// <summary>Conteúdo do arquivo de heartbeat (estado.json). Sem dados sensíveis.</summary>
public sealed class EstadoRobo
{
    public string Versao { get; set; } = "";
    public int Pid { get; set; }
    public string Maquina { get; set; } = "";
    public DateTime InicioProcesso { get; set; }

    /// <summary>Renovado a cada minuto enquanto o processo está vivo.</summary>
    public DateTime AtualizadoEm { get; set; }

    /// <summary>iniciando | em-ciclo | inativo | adiado | encerrado.</summary>
    public string Situacao { get; set; } = SituacaoRobo.Iniciando;
    public bool Continuo { get; set; }
    public int IntervaloMinutos { get; set; }
    public int CicloMaximoMinutos { get; set; }

    public bool HistoricoCarregado { get; set; }
    public int RegistrosHistorico { get; set; }

    public int Ciclo { get; set; }
    public DateTime? InicioUltimoCiclo { get; set; }
    public DateTime? FimUltimoCiclo { get; set; }

    /// <summary>ok | falhas | adiado (null = nenhum ciclo concluído).</summary>
    public string? ResultadoUltimoCiclo { get; set; }
    public DateTime? ProximoCiclo { get; set; }
    public string? ArquivoLog { get; set; }
}

public static class SituacaoRobo
{
    public const string Iniciando = "iniciando";
    public const string EmCiclo = "em-ciclo";
    public const string Inativo = "inativo";
    public const string Adiado = "adiado";
    public const string Encerrado = "encerrado";
}

/// <summary>
/// Mantém o arquivo de heartbeat: gravado ao iniciar, a cada mudança de etapa e a cada
/// minuto (prova de vida). A gravação é atômica (arquivo temporário + troca), então quem
/// lê nunca pega um JSON pela metade. Falhas de disco só geram aviso.
/// </summary>
public sealed class EstadoService : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _arquivo;
    private readonly object _lock = new();
    private readonly Timer _pulso;
    private bool _avisouFalha;

    public EstadoRobo Estado { get; }

    public EstadoService(string arquivo, string versao, bool continuo, int intervaloMinutos, int cicloMaximoMinutos)
    {
        _arquivo = Path.GetFullPath(arquivo);
        using Process atual = Process.GetCurrentProcess();
        Estado = new EstadoRobo
        {
            Versao = versao,
            Pid = Environment.ProcessId,
            Maquina = Environment.MachineName,
            InicioProcesso = atual.StartTime,
            Continuo = continuo,
            IntervaloMinutos = intervaloMinutos,
            CicloMaximoMinutos = cicloMaximoMinutos,
            ArquivoLog = Log.ArquivoAtual,
        };
        Gravar();
        _pulso = new Timer(_ => Gravar(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string Arquivo => _arquivo;

    /// <summary>Aplica a alteração e grava o arquivo.</summary>
    public void Atualizar(Action<EstadoRobo> alterar)
    {
        lock (_lock)
        {
            alterar(Estado);
            Gravar();
        }
    }

    private void Gravar()
    {
        lock (_lock)
        {
            try
            {
                Estado.AtualizadoEm = DateTime.Now;
                Estado.ArquivoLog = Log.ArquivoAtual ?? Estado.ArquivoLog;
                string? pasta = Path.GetDirectoryName(_arquivo);
                if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

                string temporario = _arquivo + ".tmp";
                File.WriteAllText(temporario, JsonSerializer.Serialize(Estado, Json));
                File.Move(temporario, _arquivo, overwrite: true);
                _avisouFalha = false;
            }
            catch (Exception ex)
            {
                if (_avisouFalha) return;
                _avisouFalha = true;
                Log.Aviso($"Não foi possível gravar o arquivo de estado {_arquivo}: {ex.Message}");
            }
        }
    }

    public void Dispose() => _pulso.Dispose();
}

/// <summary>Avalia o arquivo de estado: o robô está vivo e em dia?</summary>
public static class Saude
{
    /// <summary>Sem renovação por mais que isto, o processo é considerado morto/travado.</summary>
    public static readonly TimeSpan LimitePulso = TimeSpan.FromMinutes(5);

    /// <summary>Tolerância depois do horário previsto do próximo ciclo.</summary>
    public static readonly TimeSpan ToleranciaProximoCiclo = TimeSpan.FromMinutes(15);

    /// <summary>Devolve (em dia?, mensagens). Puro, exceto a consulta ao PID na mesma máquina.</summary>
    public static (bool EmDia, List<string> Mensagens) Avaliar(EstadoRobo e, DateTime agora)
    {
        var msgs = new List<string>
        {
            $"Versão {e.Versao}, PID {e.Pid} em {e.Maquina}, iniciado em {e.InicioProcesso:dd/MM/yyyy HH:mm:ss}.",
            $"Situação: {e.Situacao}; ciclo {e.Ciclo}; último resultado: {e.ResultadoUltimoCiclo ?? "(nenhum ciclo concluído)"}; " +
            $"histórico {(e.HistoricoCarregado ? $"carregado ({e.RegistrosHistorico} registros)" : "NÃO carregado")}.",
        };
        if (e.ProximoCiclo is { } prox) msgs.Add($"Próximo ciclo previsto: {prox:dd/MM/yyyy HH:mm}.");
        bool ok = true;

        void Falha(string m) { ok = false; msgs.Add("PROBLEMA: " + m); }

        if (e.Situacao == SituacaoRobo.Encerrado)
            Falha("o robô foi encerrado.");

        if (agora - e.AtualizadoEm > LimitePulso)
            Falha($"estado sem atualização desde {e.AtualizadoEm:dd/MM/yyyy HH:mm:ss} (processo parado ou travado).");

        if (string.Equals(e.Maquina, Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !ProcessoVivo(e.Pid))
            Falha($"o processo {e.Pid} não está em execução.");

        if (e.Situacao == SituacaoRobo.EmCiclo && e.InicioUltimoCiclo is { } inicio &&
            agora - inicio > TimeSpan.FromMinutes(Math.Max(1, e.CicloMaximoMinutos)))
            Falha($"ciclo em andamento desde {inicio:dd/MM/yyyy HH:mm} (acima de {e.CicloMaximoMinutos} min).");

        if (e.Situacao == SituacaoRobo.Inativo && e.ProximoCiclo is { } proximo && agora > proximo + ToleranciaProximoCiclo)
            Falha($"o ciclo previsto para {proximo:dd/MM/yyyy HH:mm} não começou.");

        if (e.Situacao == SituacaoRobo.Adiado || e.ResultadoUltimoCiclo == "adiado")
            Falha("o último ciclo foi adiado (banco de dados inacessível ou em uso por outra instância).");

        if (ok && e.ResultadoUltimoCiclo == "falhas")
            msgs.Add("Aviso: o último ciclo terminou com falhas (veja o log), mas o robô está rodando.");

        return (ok, msgs);
    }

    private static bool ProcessoVivo(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>--saude: imprime a avaliação e devolve o código de saída (0 em dia, 1 não).</summary>
    public static int Verificar(string arquivo)
    {
        string caminho = Path.GetFullPath(arquivo);
        if (!File.Exists(caminho))
        {
            Console.WriteLine($"NÃO SAUDÁVEL: arquivo de estado não encontrado: {caminho}");
            return 1;
        }

        EstadoRobo? estado;
        try
        {
            string json = "";
            for (int tentativa = 0; ; tentativa++)
            {
                try
                {
                    using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var leitor = new StreamReader(fs);
                    json = leitor.ReadToEnd();
                    break;
                }
                catch (IOException) when (tentativa < 3) { Thread.Sleep(300); }
            }
            estado = JsonSerializer.Deserialize<EstadoRobo>(json, EstadoService.Json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"NÃO SAUDÁVEL: arquivo de estado ilegível ({caminho}): {ex.Message}");
            return 1;
        }

        if (estado is null)
        {
            Console.WriteLine($"NÃO SAUDÁVEL: arquivo de estado vazio: {caminho}");
            return 1;
        }

        (bool emDia, List<string> msgs) = Avaliar(estado, DateTime.Now);
        Console.WriteLine((emDia ? "SAUDÁVEL" : "NÃO SAUDÁVEL") + $" ({caminho})");
        foreach (string m in msgs) Console.WriteLine("  " + m);
        return emDia ? 0 : 1;
    }
}
