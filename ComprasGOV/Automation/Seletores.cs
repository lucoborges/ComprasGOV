namespace ComprasGOV.Automation;

/// <summary>
/// Seletores usados na automação. Cada um corresponde a um passo descrito no HTML.md.
/// </summary>
public static class Seletores
{
    // 1) Campo da unidade compradora (código do cliente).
    public const string UnidadeCompradora = "#unidadeCompradora";

    // 2) Botão de pesquisa.
    public const string BotaoPesquisar = "button.br-button.is-primary:has-text('Pesquisar')";

    // 3) Card de cada licitação nos resultados.
    public const string CardLicitacao = ".cp-itens-card";

    // Div com "Etapa: / Até: / Proposta / data" dentro do card (só aparece em compra em aberto).
    public const string DivEtapaProposta = "div.row.ng-star-inserted";

    // 4) Número da licitação (ex.: "Pregão Eletrônico N° 67/2026").
    public const string NumeroLicitacao = "span.text-uppercase.cp-item-bold";

    // 5) Linha "Objeto:" (só após expandir o card).
    public const string LinhaObjeto = "div.br-divider";
    public const string ValorObjeto = ".cp-valor-item";

    // Botão que expande/colapsa os detalhes do card.
    public const string BotaoExpandir = "button[data-test='btn-expandir']";

    // 6) Botão "Acompanhar compra" (vai para a página de detalhes).
    public const string BotaoAcompanhar = "button[aria-label='Acompanhar compra']";

    // 7) Botão que abre o PNCP (o elemento duplicado é o visível em telas grandes).
    public const string BotaoPncp = "button[aria-label='Documentos e demais informações da contratação no PNCP']:visible";

    // 8) Aba "Arquivos" na página do PNCP e linhas da tabela de arquivos.
    //    IMPORTANTE: a página mantém TODAS as abas no DOM (Itens, Arquivos, Atas,
    //    Contratos, Histórico), então "datatable-body-row" sozinho casa também com
    //    as linhas da tabela de itens. Sempre escopar pelas linhas que têm download.
    //
    //    Atenção com ":text-is(...)": ele casa apenas o elemento mais interno com
    //    o texto exato (o <span class="name">), NUNCA o <button> que o envolve.
    //    Por isso a aba é procurada por "li.tab-item" + ":has-text()".
    public const string BarraAbas = "li.tab-item";
    public const string AbaArquivos = "li.tab-item:has-text('Arquivos') button";
    public static readonly string[] AbaArquivosCandidatos =
    {
        "li.tab-item:has-text('Arquivos') button",
        "button:has-text('Arquivos')",
        "button >> text='Arquivos'",
    };
    public const string LinhaArquivo = "datatable-body-row";
    public const string LinhaArquivoComDownload =
        "datatable-body-row:has(a[aria-label='Fazer download'])";
    public const string CelulaArquivo = "datatable-body-cell";
    public const string LinkDownload = "a[aria-label='Fazer download']";

    // Paginação dos resultados.
    public const string ProximaPagina = "button[aria-label='Próxima Página']";
    public const string PaginaAtiva = "button[aria-current='page']";

    // Estado vazio dos resultados.
    public const string MensagemSemResultado = ".p-dataview-empty-message";

    public const string UrlAcompanhamento = "**/*acompanhamento-compra*";
}
