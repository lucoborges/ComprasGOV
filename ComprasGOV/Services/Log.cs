using System.Text;

namespace ComprasGOV.Services;

/// <summary>
/// Log com carimbo de data/hora no console (opcional) e em arquivo UTF-8 com rotação:
/// um arquivo por dia (comprasgov-AAAAMMDD.log); ao passar do tamanho máximo abre
/// comprasgov-AAAAMMDD-2.log, -3 etc. Arquivos mais velhos que a retenção, ou que
/// passem do tamanho total, são apagados (os mais antigos primeiro).
/// Valores registrados com <see cref="Ocultar"/> (ex.: senha SMTP) são trocados por
/// "***" antes de qualquer gravação. Falha ao gravar o arquivo nunca derruba o programa.
/// </summary>
public static class Log
{
    private const string Prefixo = "comprasgov-";
    private static readonly object Lock = new();
    private static readonly List<string> Segredos = new();

    private static string? _pasta;
    private static long _tamanhoMaximo;
    private static long _tamanhoTotalMaximo;
    private static int _retencaoDias;
    private static bool _console = true;

    private static StreamWriter? _arquivo;
    private static DateTime _diaArquivo;
    private static DateTime _proximaTentativa;

    /// <summary>Arquivo de log em uso (null = só console).</summary>
    public static string? ArquivoAtual { get; private set; }

    public static void Secao(string mensagem) => Escrever(ConsoleColor.Cyan, "==", new string('=', 70) + Environment.NewLine + mensagem + Environment.NewLine + new string('=', 70));
    public static void Info(string mensagem) => Escrever(ConsoleColor.Gray, "--", mensagem);
    public static void Ok(string mensagem) => Escrever(ConsoleColor.Green, "OK", mensagem);
    public static void Aviso(string mensagem) => Escrever(ConsoleColor.Yellow, "!!", mensagem);
    public static void Erro(string mensagem) => Escrever(ConsoleColor.Red, "XX", mensagem);

    /// <summary>Liga a gravação em arquivo. Até ser chamado, o log vai só para o console.</summary>
    public static void Configurar(string pasta, double tamanhoMaximoMb, int retencaoDias,
        double tamanhoTotalMaximoMb, bool console)
    {
        lock (Lock)
        {
            FecharArquivo();
            _pasta = Path.GetFullPath(pasta);
            _tamanhoMaximo = Math.Max(1024, (long)(tamanhoMaximoMb * 1024 * 1024));
            _tamanhoTotalMaximo = Math.Max(_tamanhoMaximo, (long)(tamanhoTotalMaximoMb * 1024 * 1024));
            _retencaoDias = Math.Max(1, retencaoDias);
            _console = console;
            _proximaTentativa = DateTime.MinValue;
            AbrirArquivo(DateTime.Today);
        }
    }

    /// <summary>Nunca grava este valor (console ou arquivo): é trocado por "***".</summary>
    public static void Ocultar(string? segredo)
    {
        if (string.IsNullOrEmpty(segredo) || segredo.Length < 4) return;
        lock (Lock)
        {
            if (!Segredos.Contains(segredo)) Segredos.Add(segredo);
        }
    }

    public static void Encerrar()
    {
        lock (Lock) FecharArquivo();
    }

    private static void Escrever(ConsoleColor cor, string icone, string mensagem)
    {
        lock (Lock)
        {
            foreach (string segredo in Segredos)
                mensagem = mensagem.Replace(segredo, "***", StringComparison.Ordinal);

            DateTime agora = DateTime.Now;

            if (_console || _pasta is null)
            {
                ConsoleColor anterior = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = cor;
                    Console.WriteLine($"[{agora:dd/MM HH:mm:ss}] {icone} {mensagem}");
                }
                catch { /* console indisponível (sessão 0 sem saída): segue só no arquivo */ }
                finally
                {
                    try { Console.ForegroundColor = anterior; } catch { /* idem */ }
                }
            }

            if (_pasta is not null)
                GravarArquivo($"[{agora:yyyy-MM-dd HH:mm:ss}] {icone} {mensagem}");
        }
    }

    private static void GravarArquivo(string linha)
    {
        try
        {
            if (_arquivo is null || _diaArquivo != DateTime.Today || _arquivo.BaseStream.Length >= _tamanhoMaximo)
            {
                if (_arquivo is null && DateTime.Now < _proximaTentativa) return;
                AbrirArquivo(DateTime.Today);
                if (_arquivo is null) return;
            }
            _arquivo.WriteLine(linha);
        }
        catch (Exception ex)
        {
            FalhaArquivo(ex);
        }
    }

    /// <summary>Abre o primeiro arquivo do dia com espaço livre e faz a limpeza da retenção.</summary>
    private static void AbrirArquivo(DateTime dia)
    {
        FecharArquivo();
        try
        {
            Directory.CreateDirectory(_pasta!);
            string caminho = "";
            for (int parte = 1; ; parte++)
            {
                caminho = Path.Combine(_pasta!, $"{Prefixo}{dia:yyyyMMdd}{(parte == 1 ? "" : $"-{parte}")}.log");
                if (!File.Exists(caminho) || new FileInfo(caminho).Length < _tamanhoMaximo) break;
            }

            var fs = new FileStream(caminho, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _arquivo = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            _diaArquivo = dia;
            ArquivoAtual = caminho;
            Limpar(caminho);
        }
        catch (Exception ex)
        {
            FalhaArquivo(ex);
        }
    }

    private static void Limpar(string emUso)
    {
        try
        {
            var arquivos = new DirectoryInfo(_pasta!).GetFiles(Prefixo + "*.log")
                .Where(f => !f.FullName.Equals(emUso, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            DateTime limite = DateTime.Today.AddDays(-_retencaoDias);
            long total = arquivos.Sum(f => f.Length);

            foreach (FileInfo f in arquivos)
            {
                if (f.LastWriteTime >= limite && total <= _tamanhoTotalMaximo) continue;
                try
                {
                    long tamanho = f.Length;
                    f.Delete();
                    total -= tamanho;
                }
                catch { /* em uso por outro leitor: tenta na próxima rotação */ }
            }
        }
        catch { /* limpeza é melhor esforço */ }
    }

    private static void FalhaArquivo(Exception ex)
    {
        FecharArquivo();
        _proximaTentativa = DateTime.Now.AddMinutes(1);
        try { Console.Error.WriteLine($"[{DateTime.Now:dd/MM HH:mm:ss}] XX Falha ao gravar o arquivo de log: {ex.Message}"); }
        catch { /* sem console */ }
    }

    private static void FecharArquivo()
    {
        try { _arquivo?.Dispose(); } catch { /* já fechado */ }
        _arquivo = null;
    }
}
