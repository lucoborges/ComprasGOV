namespace ComprasGOV.Models;

/// <summary>Licitação a receber/recebendo propostas, obtida pelas APIs públicas do PNCP.</summary>
public sealed record LicitacaoPncp(
    string Uasg,            // código da unidade compradora sem zeros à esquerda (no Compras.gov.br, a UASG)
    string Cnpj,
    int Ano,
    int Sequencial,
    string Numero,          // "107/2026" (mesmo formato do título do Compras.gov.br)
    string Titulo,          // "CONCORRÊNCIA ELETRÔNICA N° 107/2026"
    string Objeto,
    DateTime? EncerramentoProposta,
    int SituacaoId,         // 1 divulgada, 2 revogada, 3 anulada, 4 suspensa (0 = cancelada/desconhecida)
    string SituacaoNome,
    string UrlCompras,
    string UnidadeNome = "",
    string Plataforma = "") // fonte/plataforma que publicou no PNCP (ex.: "Compras.gov.br")
{
    /// <summary>
    /// Chave do histórico: número de controle da compra no PNCP ("CNPJ-1-SEQUENCIAL/ANO"),
    /// único por licitação. A chave antiga ("UASG-número/ano") colidia quando o mesmo
    /// órgão usa o mesmo número em modalidades diferentes (ex.: Pregão 90015/2026 e
    /// Concorrência 90015/2026): a segunda era descartada como "já processada".
    /// </summary>
    public string Chave => ChavePncp(Cnpj, Ano, Sequencial);

    /// <summary>Formato antigo "UASG-número/ano", ainda usado pelo portal (--fonte portal / --comparar).</summary>
    public string ChaveLegada => $"{Uasg}-{Numero}";

    public string Prazo => EncerramentoProposta?.ToString("dd/MM/yyyy HH:mm") ?? "";

    public string UrlPncp => $"https://pncp.gov.br/app/editais/{Cnpj}/{Ano}/{Sequencial:D6}";

    public LicitacaoEncontrada ParaEncontrada() => new(Titulo, Numero, Chave, Prazo, Objeto);

    /// <summary>Número de controle PNCP de uma compra (ex.: "00394452000103-1-023561/2026").</summary>
    public static string ChavePncp(string cnpj, int ano, int sequencial) => $"{cnpj}-1-{sequencial:D6}/{ano}";
}
