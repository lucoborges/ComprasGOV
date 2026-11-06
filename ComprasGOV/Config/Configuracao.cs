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

    /// <summary>
    /// Modo contínuo (24/7): após cada ciclo completo o sistema fica inativo por
    /// <see cref="IntervaloMinutos"/> e volta a processar. --uma-vez desativa.
    /// </summary>
    public bool Continuo { get; private set; } = true;

    /// <summary>
    /// Origem das licitações: API oficial do PNCP (padrão: sem navegador e sem
    /// captcha) ou o portal Compras.gov.br pelo navegador (fluxo antigo, --fonte portal).
    /// </summary>
    public bool UsarPortal { get; private set; }

    /// <summary>Compara as duas fontes (portal x API) por UASG, sem baixar nem gravar histórico.</summary>
    public bool Comparar { get; private set; }

    /// <summary>Cache UASG -> CNPJ do órgão (consultado nos Dados Abertos do Compras.gov.br).</summary>
    public string ArquivoCacheCnpj =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ArquivoHistorico)) ?? ".", "uasg_cnpj.json");

    /// <summary>Pausa entre ciclos no modo contínuo (padrão: 60 minutos).</summary>
    public int IntervaloMinutos { get; private set; } = 60;

    /// <summary>Padrão: processa TODOS os clientes da planilha (item 1).</summary>
    public bool ProcessarTodosClientes { get; private set; } = true;
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

    // --- E-mail ---------------------------------------------------------------
    /// <summary>Destinatário final das licitações com arquivos baixados.</summary>
    public const string EmailDestinoPadrao = "vendas9@venturainformatica.com.br";

    /// <summary>Remetente (conta autenticada no SMTP).</summary>
    public const string EmailRemetentePadrao = "suporte@venturainformatica.com.br";

    /// <summary>
    /// Servidor SMTP (Locaweb) - 587/STARTTLS.
    /// "smtp.venturainformatica.com.br" aponta para o mesmo relay, mas o certificado
    /// apresentado é "*.email-ssl.com.br" e a validação de nome falharia
    /// (RemoteCertificateNameMismatch). Usamos o hostname que casa com o cert,
    /// mantendo a validação TLS integral.
    /// </summary>
    public const string SmtpHostPadrao = "smtp.email-ssl.com.br";
    public const int SmtpPortaPadrao = 587;

    /// <summary>Destinatário; pode ser sobrescrito com --email-destino.</summary>
    public string EmailDestino { get; private set; } = EmailDestinoPadrao;

    /// <summary>Não envia e-mail no fim da execução (downloads continuam normalmente).</summary>
    public bool SemEmail { get; private set; }

    /// <summary>
    /// Senha do remetente. Vem SOMENTE da variável de ambiente COMPRASGOV_SMTP_SENHA
    /// (nunca escrita em código nem em argumentos de linha de comando).
    /// </summary>
    public string? SmtpSenha { get; private set; } =
        Environment.GetEnvironmentVariable("COMPRASGOV_SMTP_SENHA");

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

                case "--fonte":
                    if (i + 1 < args.Length)
                    {
                        string fonte = args[++i].Trim().ToLowerInvariant();
                        if (fonte is "portal" or "api") cfg.UsarPortal = fonte == "portal";
                        else Console.WriteLine($"--fonte inválida: {fonte} (use api ou portal)");
                    }
                    break;

                case "--comparar":
                    cfg.Comparar = true;
                    break;

                case "--uma-vez":
                    cfg.Continuo = false;
                    break;

                case "--intervalo":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int intervalo) && intervalo > 0)
                        cfg.IntervaloMinutos = intervalo;
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

                case "--sem-email":
                    cfg.SemEmail = true;
                    break;

                case "--email-destino":
                    if (i + 1 < args.Length) cfg.EmailDestino = args[++i].Trim();
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
              --todos                   Sem efeito: o padrão já processa TODOS os clientes.
              --headless                Executa sem janela do navegador (padrão: janela visível).
              --fonte <api|portal>      Origem das licitações. api (padrão): API oficial do PNCP,
                                        sem navegador e sem captcha. portal: fluxo antigo pelo
                                        navegador no Compras.gov.br (sujeito ao hCaptcha).
              --comparar                Compara portal x API por UASG (não baixa, não grava
                                        histórico) e salva um relatório; executa uma vez.
              --uma-vez                 Executa um único ciclo e encerra (padrão: contínuo 24/7).
              --intervalo <minutos>     Pausa entre ciclos no modo contínuo (padrão: 60).
              --max <n>                 Limita a quantidade de licitações processadas por execução.
              --planilha <caminho>      Caminho da planilha de órgãos (xlsx).
              --pasta-saida <caminho>   Pasta onde os arquivos serão salvos.
              --historico <arquivo>     Arquivo JSON de licitações já processadas.
              --sem-email               Não envia os e-mails no fim da execução (só baixa).
              --email-destino <email>   Altera o destinatário dos e-mails.
              --instalar-navegadores    Instala os navegadores do Playwright e encerra.
              --ignorar-objeto          SOMENTE TESTE: ignora a regra de objeto e baixa tudo.
              --testar-pncp <url>       SOMENTE TESTE: abre esta URL do PNCP e baixa os arquivos (sem a busca).
              --debug                   Log detalhado de rede/console do portal (REQ, HTTP, corpos).
              --help                    Exibe esta ajuda.

            E-mail (no fim da execução, agrupa de 2 a 4 licitações por mensagem):
              Remetente: suporte@venturainformatica.com.br (SMTP smtp.email-ssl.com.br:587)
              Destinatário padrão: vendas9@venturainformatica.com.br

            Variáveis de ambiente (opcionais, usadas quando o argumento não é informado):
              COMPRASGOV_PLANILHA       Caminho da planilha de órgãos (xlsx).
              COMPRASGOV_PASTA_SAIDA    Pasta onde os arquivos serão salvos.
              COMPRASGOV_SMTP_SENHA     Senha do remetente (obrigatória para enviar e-mail).
            """);
    }
}
