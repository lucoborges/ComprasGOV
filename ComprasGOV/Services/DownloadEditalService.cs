namespace ComprasGOV.Services;

/// <summary>Download do edital do PNCP para a pasta de impressão.</summary>
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

    /// <summary>
    /// Baixa o arquivo informado. O nome final segue o padrão
    /// "&lt;número da licitação&gt; - &lt;nome original no PNCP&gt;.pdf".
    /// </summary>
    public async Task<string> BaixarAsync(string url, string numeroLicitacao, string nomeOriginal)
    {
        Directory.CreateDirectory(_pastaSaida);

        using HttpResponseMessage resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        byte[] conteudo = await resp.Content.ReadAsByteArrayAsync();
        if (conteudo.Length == 0)
            throw new InvalidOperationException("O PNCP devolveu um arquivo vazio.");

        // Extensão: do nome no PNCP; senão do nome real do arquivo (Content-Disposition,
        // ex.: "Edital.zip"); senão .pdf. Evita salvar .zip/.docx com extensão errada.
        string extensao = ExtensaoValida(nomeOriginal);
        if (string.IsNullOrEmpty(extensao))
        {
            var cd = resp.Content.Headers.ContentDisposition;
            extensao = ExtensaoValida((cd?.FileNameStar ?? cd?.FileName ?? "").Trim('"'));
        }
        if (string.IsNullOrEmpty(extensao)) extensao = ".pdf";

        string nomeBase = string.IsNullOrWhiteSpace(nomeOriginal)
            ? "edital"
            : string.IsNullOrEmpty(ExtensaoValida(nomeOriginal))
                ? nomeOriginal
                : Path.GetFileNameWithoutExtension(nomeOriginal);

        string destino = Path.Combine(_pastaSaida, $"{Sanitizar(numeroLicitacao)} - {Sanitizar(nomeBase)}{extensao}");

        // Evita sobrescrever um arquivo já salvo (ex.: reexecução no mesmo dia).
        int sufixo = 1;
        while (File.Exists(destino))
        {
            destino = Path.Combine(_pastaSaida,
                $"{Sanitizar(numeroLicitacao)} - {Sanitizar(nomeBase)} ({sufixo++}){extensao}");
        }

        await File.WriteAllBytesAsync(destino, conteudo);
        Log.Info($"Download: {conteudo.Length:N0} bytes -> {destino}");
        return destino;
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
