using ComprasGOV.Automation;

namespace ComprasGOV.Services;

/// <summary>Download dos arquivos do PNCP para a pasta de impressão.</summary>
public sealed class DownloadEditalService
{
    private readonly HttpClient _http;
    private readonly string _pastaSaida;

    public DownloadEditalService(string pastaSaida)
    {
        _pastaSaida = pastaSaida;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/pdf,*/*");
    }

    /// <summary>Baixa um único arquivo (ver <see cref="BaixarTodosAsync"/>).</summary>
    public async Task<string> BaixarAsync(string url, string numeroLicitacao, string nomeOriginal,
        CancellationToken ct = default) =>
        (await BaixarTodosAsync(new[] { new ArquivoPncp(url, nomeOriginal) }, numeroLicitacao, ct))[0];

    /// <summary>
    /// Baixa TODOS os arquivos de uma licitação, tudo ou nada: cada arquivo vai primeiro
    /// para uma pasta temporária local e só quando todos chegaram eles são movidos para a
    /// pasta de saída. Se um download falhar, nada fica na pasta de saída - a licitação é
    /// tentada de novo no próximo ciclo sem deixar cópias repetidas ("(1)", "(2)"...).
    /// O nome final segue o padrão "&lt;número da licitação&gt; - &lt;nome original no PNCP&gt;&lt;extensão&gt;".
    /// </summary>
    public async Task<List<string>> BaixarTodosAsync(IReadOnlyList<ArquivoPncp> arquivos, string numeroLicitacao,
        CancellationToken ct = default)
    {
        string pastaTemp = Path.Combine(Path.GetTempPath(), "ComprasGOV", "downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pastaTemp);

        var destinos = new List<string>();
        try
        {
            // 1) Baixa tudo para a pasta temporária (direto para o disco, sem carregar na memória).
            var baixados = new List<(string Temp, string NomeFinal, string Extensao, long Bytes)>();
            for (int i = 0; i < arquivos.Count; i++)
            {
                ArquivoPncp arquivo = arquivos[i];
                string temp = Path.Combine(pastaTemp, $"{i}.tmp");

                using HttpResponseMessage resp = await _http.GetAsync(arquivo.Url,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                long bytes;
                await using (var saida = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await resp.Content.CopyToAsync(saida, ct);
                    bytes = saida.Length;
                }
                if (bytes == 0)
                    throw new InvalidOperationException("O PNCP devolveu um arquivo vazio.");

                // Extensão: do nome no PNCP; senão do nome real do arquivo (Content-Disposition,
                // ex.: "Edital.zip"); senão .pdf. Evita salvar .zip/.docx com extensão errada.
                string extensao = ExtensaoValida(arquivo.Nome);
                if (string.IsNullOrEmpty(extensao))
                {
                    var cd = resp.Content.Headers.ContentDisposition;
                    extensao = ExtensaoValida((cd?.FileNameStar ?? cd?.FileName ?? "").Trim('"'));
                }
                if (string.IsNullOrEmpty(extensao)) extensao = ".pdf";

                string nomeBase = string.IsNullOrWhiteSpace(arquivo.Nome)
                    ? "edital"
                    : string.IsNullOrEmpty(ExtensaoValida(arquivo.Nome))
                        ? arquivo.Nome
                        : Path.GetFileNameWithoutExtension(arquivo.Nome);

                baixados.Add((temp, $"{Sanitizar(numeroLicitacao)} - {Sanitizar(nomeBase)}", extensao, bytes));
            }

            // 2) Todos chegaram: move para a pasta de saída.
            Directory.CreateDirectory(_pastaSaida);
            foreach (var (temp, nomeFinal, extensao, bytes) in baixados)
            {
                // Não sobrescreve um arquivo já salvo (ex.: dois anexos com o mesmo título).
                string destino = Path.Combine(_pastaSaida, nomeFinal + extensao);
                for (int sufixo = 1; File.Exists(destino); sufixo++)
                    destino = Path.Combine(_pastaSaida, $"{nomeFinal} ({sufixo}){extensao}");

                destinos.Add(destino);
                File.Move(temp, destino);
                Log.Info($"Download: {bytes:N0} bytes -> {destino}");
            }

            return destinos;
        }
        catch
        {
            // Falhou no meio: desfaz o que já tinha ido para a pasta de saída.
            foreach (string destino in destinos)
            {
                try { File.Delete(destino); } catch { /* fica para limpeza manual */ }
            }
            throw;
        }
        finally
        {
            try { Directory.Delete(pastaTemp, recursive: true); } catch { /* temporário */ }
        }
    }

    /// <summary>Extensão curta e alfanumérica (".pdf", ".zip"); "" para títulos como "Edital 1.2".</summary>
    private static string ExtensaoValida(string nome)
    {
        string ext = Path.GetExtension(nome ?? "");
        return ext.Length is >= 2 and <= 6 && ext.Skip(1).All(char.IsLetterOrDigit) && ext.Skip(1).Any(char.IsLetter)
            ? ext.ToLowerInvariant()
            : "";
    }

    private static string Sanitizar(string valor)
    {
        string s = valor.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        s = s.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return TextoUtil.LimparEspacos(s);
    }
}
