using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Text;
using ComprasGOV.Models;

namespace ComprasGOV.Services;

/// <summary>Licitação com arquivos baixados a ser incluída em um e-mail.</summary>
/// <param name="Chave">Chave única no histórico (número de controle do PNCP).</param>
/// <param name="Uasg">UASG (unidade compradora) como aparece no corpo do e-mail.</param>
/// <param name="Numero">Número/identificação da licitação (modalidade + número).</param>
/// <param name="Objeto">Texto do objeto da licitação.</param>
/// <param name="Arquivos">Caminhos dos arquivos baixados do PNCP.</param>
/// <param name="NomeAnexo">Nome base do .zip quando há 2 ou mais arquivos ("UASG-número/ano").</param>
public sealed record ItemEmail(
    string Chave,
    string Uasg,
    string Numero,
    string Objeto,
    IReadOnlyList<string> Arquivos,
    string NomeAnexo)
{
    /// <summary>
    /// Item a partir do registro do histórico (downloads do ciclo e pendências anteriores).
    /// Na busca geral o órgão não vem da planilha de clientes, então a linha "UASG" traz
    /// também o nome da unidade (e a plataforma, quando não é o Compras.gov.br).
    /// </summary>
    public static ItemEmail DeRegistro(LicitacaoHistorico registro, IReadOnlyList<string> arquivos)
    {
        string uasg = registro.Uasg;
        if (registro.Origem == OrigemLicitacao.Geral)
        {
            if (!string.IsNullOrWhiteSpace(registro.UasgNome))
                uasg += $" - {registro.UasgNome}";
            if (!string.IsNullOrWhiteSpace(registro.Plataforma) &&
                !registro.Plataforma.Equals("Compras.gov.br", StringComparison.OrdinalIgnoreCase))
                uasg += $" ({registro.Plataforma})";
        }

        return new ItemEmail(registro.Chave, uasg, registro.Titulo, registro.Objeto, arquivos,
            $"{registro.Uasg}-{registro.Numero}");
    }
}

/// <summary>
/// E-mail das licitações com arquivos: agrupa de 2 a 4 licitações por mensagem,
/// monta o corpo (apenas UASG, número e objeto), anexa os arquivos baixados
/// (1 arquivo direto; 2 ou mais embalados em .zip) e envia via SMTP 587/STARTTLS.
/// </summary>
public sealed class EmailService
{
    private readonly Config.Configuracao _cfg;

    public EmailService(Config.Configuracao cfg) => _cfg = cfg;

    /// <summary>
    /// Divide N itens em lotes de no máximo 4 (regra: "mais de uma e menos de
    /// 5" por e-mail). N=1 resulta em um lote único (caso especial aceito);
    /// para N>=2 todo lote fica em [2..4].
    /// </summary>
    public static List<List<T>> SepararEmLotes<T>(IReadOnlyList<T> itens, int maximo = 4)
    {
        var lotes = new List<List<T>>();
        int n = itens.Count;
        if (n == 0) return lotes;

        int quantidadeLotes = Math.Max(1, (int)Math.Ceiling(n / (double)maximo));
        int baseLote = n / quantidadeLotes;
        int resto = n % quantidadeLotes;

        int inicio = 0;
        for (int l = 0; l < quantidadeLotes; l++)
        {
            int tamanho = baseLote + (l < resto ? 1 : 0);
            var lote = new List<T>(tamanho);
            for (int i = 0; i < tamanho; i++) lote.Add(itens[inicio++]);
            lotes.Add(lote);
        }

        return lotes;
    }

    /// <summary>Corpo do e-mail: apenas UASG, número e objeto de cada licitação.</summary>
    public static string MontarCorpo(IReadOnlyList<ItemEmail> itens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < itens.Count; i++)
        {
            if (i > 0)
                sb.AppendLine().AppendLine(new string('-', 60)).AppendLine();

            sb.AppendLine($"UASG: {itens[i].Uasg}");
            sb.AppendLine($"Número: {itens[i].Numero}");
            sb.AppendLine($"Objeto: {itens[i].Objeto}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Embala os arquivos de uma licitação em um .zip (pasta temporária).
    /// Usado quando a licitação tem 2 ou mais arquivos.
    /// </summary>
    public static string CriarZip(IReadOnlyList<string> arquivos, string nomeBase)
    {
        string pasta = Path.Combine(Path.GetTempPath(), "ComprasGOV", "emails");
        Directory.CreateDirectory(pasta);

        // Duas licitações do mesmo lote podem ter o mesmo "UASG-número" (modalidades diferentes).
        string nome = $"{Sanitizar(nomeBase)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
        string zip = Path.Combine(pasta, nome + ".zip");
        for (int i = 2; File.Exists(zip); i++)
            zip = Path.Combine(pasta, $"{nome}_{i}.zip");

        using (var fs = new FileStream(zip, FileMode.CreateNew))
        using (var arquivo = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (string caminho in arquivos)
                arquivo.CreateEntryFromFile(caminho, Path.GetFileName(caminho), CompressionLevel.Optimal);
        }

        return zip;
    }

    /// <summary>Envia um e-mail (texto puro + anexos) para o destinatário configurado.</summary>
    public async Task EnviarAsync(string assunto, string corpo, IReadOnlyList<string> anexos)
    {
        string senha = _cfg.SmtpSenha ?? "";
        if (string.IsNullOrWhiteSpace(senha))
            throw new InvalidOperationException(
                "Senha SMTP ausente: defina a variável de ambiente COMPRASGOV_SMTP_SENHA.");

        using var mensagem = new MailMessage
        {
            From = new MailAddress(Config.Configuracao.EmailRemetentePadrao),
            Subject = assunto,
            SubjectEncoding = Encoding.UTF8,
            Body = corpo,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        mensagem.To.Add(_cfg.EmailDestino);

        foreach (string anexo in anexos)
        {
            if (!File.Exists(anexo))
                throw new FileNotFoundException("Anexo não encontrado para o envio.", anexo);
            mensagem.Attachments.Add(new Attachment(anexo));
        }

        using var smtp = new SmtpClient(Config.Configuracao.SmtpHostPadrao, Config.Configuracao.SmtpPortaPadrao)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(Config.Configuracao.EmailRemetentePadrao, senha),
            Timeout = 180_000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        await smtp.SendMailAsync(mensagem);
    }

    private static string Sanitizar(string valor)
    {
        string s = valor.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        s = s.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return TextoUtil.LimparEspacos(s);
    }
}
