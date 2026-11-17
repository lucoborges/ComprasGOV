using System.Text.Json;

namespace ComprasGOV.Config;

/// <summary>
/// Formato do arquivo de configuração (appsettings.json). Todos os campos são opcionais:
/// o que faltar fica com o padrão do código (<see cref="Configuracao"/>). O modelo
/// versionado é deploy/appsettings.exemplo.json; o arquivo real (com e-mails e caminhos
/// internos) não vai para o repositório. A senha SMTP NUNCA fica neste arquivo.
/// </summary>
public sealed class ArquivoConfiguracao
{
    public SecaoEmail? Email { get; set; }
    public SecaoPastas? Pastas { get; set; }
    public SecaoExecucao? Execucao { get; set; }
    public SecaoBuscaGeral? BuscaGeral { get; set; }
    public SecaoApis? Apis { get; set; }
    public SecaoLog? Log { get; set; }
    public SecaoSaude? Saude { get; set; }

    public sealed class SecaoEmail
    {
        public string? Destino { get; set; }
        public string? Remetente { get; set; }
        public string? SmtpHost { get; set; }
        public int? SmtpPorta { get; set; }
        public int? LimiteAnexosMb { get; set; }
    }

    public sealed class SecaoPastas
    {
        public string? Banco { get; set; }
        public string? Planilha { get; set; }
        public string? Saida { get; set; }
    }

    public sealed class SecaoExecucao
    {
        public int? IntervaloMinutos { get; set; }
        public int? CicloMaximoMinutos { get; set; }
    }

    public sealed class SecaoBuscaGeral
    {
        public List<string>? Ufs { get; set; }

        /// <summary>Código do sistema que publica no PNCP -> nome exibido no site.</summary>
        public Dictionary<int, string>? Plataformas { get; set; }
    }

    public sealed class SecaoApis
    {
        public string? Uasg { get; set; }
        public string? Propostas { get; set; }
        public string? Arquivos { get; set; }
        public string? Busca { get; set; }
        public string? PortalPesquisa { get; set; }
    }

    public sealed class SecaoLog
    {
        public string? Pasta { get; set; }
        public double? TamanhoMaximoMb { get; set; }
        public int? RetencaoDias { get; set; }
        public double? TamanhoTotalMaximoMb { get; set; }
        public bool? Console { get; set; }
    }

    public sealed class SecaoSaude
    {
        public string? ArquivoEstado { get; set; }
    }

    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Lê o arquivo. Lança exceção se o JSON for inválido. <paramref name="avisos"/> recebe
    /// os campos ignorados por segurança (qualquer chave com "senha"/"password").
    /// </summary>
    public static ArquivoConfiguracao Ler(string caminho, List<string> avisos)
    {
        string json = File.ReadAllText(caminho);

        using (JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
               {
                   CommentHandling = JsonCommentHandling.Skip,
                   AllowTrailingCommas = true
               }))
        {
            ProcurarSegredos(doc.RootElement, "", avisos);
        }

        return JsonSerializer.Deserialize<ArquivoConfiguracao>(json, Opcoes) ?? new ArquivoConfiguracao();
    }

    private static void ProcurarSegredos(JsonElement e, string caminho, List<string> avisos)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty p in e.EnumerateObject())
        {
            string nome = caminho.Length == 0 ? p.Name : $"{caminho}.{p.Name}";
            if (p.Name.Contains("senha", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("password", StringComparison.OrdinalIgnoreCase))
                avisos.Add($"O campo \"{nome}\" do arquivo de configuração foi IGNORADO: a senha SMTP só é lida " +
                           "da variável de ambiente COMPRASGOV_SMTP_SENHA. Remova-o do arquivo.");
            ProcurarSegredos(p.Value, nome, avisos);
        }
    }
}
