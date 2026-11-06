namespace ComprasGOV.Models;

/// <summary>Licitação com proposta em aberto, obtida pela API pública do PNCP.</summary>
public sealed record LicitacaoPncp(
    string Uasg,
    string Cnpj,
    int Ano,
    int Sequencial,
    string Numero,          // "107/2026" (mesmo formato do título do Compras.gov.br)
    string Titulo,          // "CONCORRÊNCIA ELETRÔNICA N° 107/2026"
    string Objeto,
    DateTime? EncerramentoProposta,
    int SituacaoId,         // 1 divulgada, 2 revogada, 3 anulada, 4 suspensa
    string SituacaoNome,
    string UrlCompras)
{
    /// <summary>Mesma chave do histórico usada pelo fluxo do portal ("UASG-número/ano").</summary>
    public string Chave => $"{Uasg}-{Numero}";

    public string Prazo => EncerramentoProposta?.ToString("dd/MM/yyyy HH:mm") ?? "";

    public string UrlPncp => $"https://pncp.gov.br/app/editais/{Cnpj}/{Ano}/{Sequencial:D6}";

    public LicitacaoEncontrada ParaEncontrada() => new(Titulo, Numero, Chave, Prazo, Objeto);
}
