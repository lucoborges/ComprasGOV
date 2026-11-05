namespace ComprasGOV.Config;

/// <summary>
/// Configuração da execução: caminhos, modo de navegador e filtros de trabalho.
/// Todos os valores podem ser sobrescritos por argumentos de linha de comando.
/// </summary>
public sealed class Configuracao
{
    public const string UrlPesquisa = "https://cnetmobile.estaleiro.serpro.gov.br/comprasnet-web/public/compras";

    /// <summary>
    /// Planilha com os clientes (coluna "Código UASG"). Origem do valor, nesta ordem:
    /// 1) argumento --planilha; 2) variável de ambiente COMPRASGOV_PLANILHA;
    /// 3) "planilha_uasgs.xlsx" no diretório de trabalho.
    /// </summary>
    public static readonly string PastaPlanilhaPadrao =
        Environment.GetEnvironmentVariable("COMPRASGOV_PLANILHA")
        ?? "planilha_uasgs.xlsx";

    /// <summary>
    /// Pasta de destino dos editais baixados. Origem do valor, nesta ordem:
    /// 1) argumento --pasta-saida; 2) variável de ambiente COMPRASGOV_PASTA_SAIDA;
    /// 3) pasta "editais" no diretório de trabalho.
    /// </summary>
    public static readonly string PastaSaidaPadrao =
        Environment.GetEnvironmentVariable("COMPRASGOV_PASTA_SAIDA")
        ?? "editais";

    public bool Headless { get; private set; }
    public bool ProcessarTodosClientes { get; private set; }
    public string? UasgManual { get; private set; }
    public int? MaxLicitacoes { get; private set; }
    public bool MostrarAjuda { get; private set; }
    public bool InstalarNavegadores { get; private set; }

    /// <summary>Somente para testes: ignora a regra de validade do objeto e baixa tudo.</summary>
    public bool IgnorarObjeto { get; private set; }

    /// <summary>Somente para testes: abre esta URL do PNCP direto e baixa o edital (sem a busca).</summary>
    public string? TestarPncpUrl { get; private set; }

    /// <summary>Log detalhado de rede/console (REQ, HTTP, corpos, console do site).</summary>
    public bool Debug { get; private set; }

    public string PastaPlanilha { get; private set; } = PastaPlanilhaPadrao;
    public string PastaSaida { get; private set; } = PastaSaidaPadrao;
    public string ArquivoHistorico { get; private set; } = CaminhoHistoricoPadrao();

    private static string CaminhoHistoricoPadrao() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ComprasGOV",
            "licitacoes_processadas.json");

    public static Configuracao Ler(string[] args)
    {
        var cfg = new Configuracao();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    cfg.MostrarAjuda = true;
                    break;

                case "--headless":
                    cfg.Headless = true;
                    break;

                case "--todos":
                    cfg.ProcessarTodosClientes = true;
                    break;

                case "--instalar-navegadores":
                    cfg.InstalarNavegadores = true;
                    break;

                case "--ignorar-objeto":
                    cfg.IgnorarObjeto = true;
                    break;

                case "--testar-pncp":
                    if (i + 1 < args.Length) cfg.TestarPncpUrl = args[++i].Trim();
                    break;

                case "--debug":
                    cfg.Debug = true;
                    break;

                case "--uasg":
                    if (i + 1 < args.Length) cfg.UasgManual = args[++i].Trim();
                    break;

                case "--max":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int max)) cfg.MaxLicitacoes = max;
                    break;

                case "--planilha":
                    if (i + 1 < args.Length) cfg.PastaPlanilha = args[++i];
                    break;

                case "--pasta-saida":
                    if (i + 1 < args.Length) cfg.PastaSaida = args[++i];
                    break;

                case "--historico":
                    if (i + 1 < args.Length) cfg.ArquivoHistorico = args[++i];
                    break;

                default:
                    Console.WriteLine($"Argumento desconhecido: {arg} (use --help)");
                    break;
            }
        }

        return cfg;
    }

    public static void ExibirAjuda()
    {
        Console.WriteLine("""
            ComprasGOV - automação Compras.gov.br -> PNCP (download de editais)

            Uso: ComprasGOV [opções]

            Opções:
              --uasg <codigo>           Processa apenas o código UASG informado (ex.: 90014).
              --todos                   Processa todos os clientes da planilha (padrão: só o primeiro).
              --headless                Executa sem janela do navegador (padrão: janela visível).
              --max <n>                 Limita a quantidade de licitações processadas por execução.
              --planilha <caminho>      Caminho da planilha de órgãos (xlsx).
              --pasta-saida <caminho>   Pasta onde os editais serão salvos.
              --historico <arquivo>     Arquivo JSON de licitações já processadas.
              --instalar-navegadores    Instala os navegadores do Playwright e encerra.
              --ignorar-objeto          SOMENTE TESTE: ignora a regra de objeto e baixa os editais de todas.
              --testar-pncp <url>       SOMENTE TESTE: abre esta URL do PNCP e baixa o edital (sem a busca).
              --debug                   Log detalhado de rede/console do portal (REQ, HTTP, corpos).
              --help                    Exibe esta ajuda.

            Variáveis de ambiente (opcionais, usadas quando o argumento não é informado):
              COMPRASGOV_PLANILHA       Caminho da planilha de órgãos (xlsx).
              COMPRASGOV_PASTA_SAIDA    Pasta onde os editais serão salvos.
            """);
    }
}
