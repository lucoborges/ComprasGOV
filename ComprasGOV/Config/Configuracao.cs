namespace ComprasGOV.Config;

/// <summary>
/// Configuração da execução. Cada valor vem da primeira origem que o informar:
/// 1) argumento de linha de comando; 2) variável de ambiente COMPRASGOV_*;
/// 3) arquivo de configuração (appsettings.json); 4) padrão do código.
/// O arquivo é o indicado por --config, senão por COMPRASGOV_CONFIG, senão o
/// appsettings.json ao lado do executável ou no diretório de trabalho (se existir).
/// A senha SMTP vem SOMENTE da variável COMPRASGOV_SMTP_SENHA.
/// </summary>
public sealed class Configuracao
{
    // --- Padrões do código (só valores públicos; dados internos ficam no arquivo) ---
    public const string UrlPesquisaPadrao = "https://cnetmobile.estaleiro.serpro.gov.br/comprasnet-web/public/compras";
    public const string UrlUasgPadrao = "https://dadosabertos.compras.gov.br/modulo-uasg/1_consultarUasg";
    public const string UrlPropostasPadrao = "https://pncp.gov.br/api/consulta/v1/contratacoes/proposta";
    public const string UrlArquivosPadrao = "https://pncp.gov.br/api/pncp/v1/orgaos/{0}/compras/{1}/{2}/arquivos";
    public const string UrlBuscaPadrao = "https://pncp.gov.br/api/search/";

    /// <summary>
    /// Servidor SMTP (Locaweb) - 587/STARTTLS.
    /// O "smtp." do domínio próprio da empresa aponta para o mesmo relay, mas o certificado
    /// apresentado é "*.email-ssl.com.br" e a validação de nome falharia
    /// (RemoteCertificateNameMismatch). Usamos o hostname que casa com o cert,
    /// mantendo a validação TLS integral.
    /// </summary>
    public const string SmtpHostPadrao = "smtp.email-ssl.com.br";
    public const int SmtpPortaPadrao = 587;

    /// <summary>
    /// Soma máxima dos anexos de uma mensagem. Em base64 o e-mail cresce ~37%, então
    /// 20 MB de arquivos viram ~27 MB na rede, abaixo do limite usual dos provedores.
    /// Acima disso o lote é dividido; uma licitação que sozinha passa do limite vai
    /// sem anexo, com o caminho dos arquivos no corpo.
    /// </summary>
    public const int LimiteAnexosEmailMbPadrao = 20;

    public const string NomeArquivoConfig = "appsettings.json";

    // --- Origem da configuração -------------------------------------------------
    /// <summary>Arquivo de configuração carregado (null = nenhum; só padrões/variáveis/argumentos).</summary>
    public string? ArquivoConfig { get; private set; }

    /// <summary>Problemas que impedem a execução (o programa encerra com código 1).</summary>
    public List<string> Erros { get; } = new();

    /// <summary>Avisos para o log (ex.: campo de senha ignorado no arquivo).</summary>
    public List<string> Avisos { get; } = new();

    // --- Modos ---------------------------------------------------------------------
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

    /// <summary>--saude: só lê o arquivo de estado e devolve 0 (em dia) ou 1.</summary>
    public bool VerificarSaude { get; private set; }

    /// <summary>--validar-config: carrega a configuração, informa se é válida (0/1) e encerra.</summary>
    public bool ValidarConfig { get; private set; }

    /// <summary>Pausa entre ciclos no modo contínuo (padrão: 60 minutos).</summary>
    public int IntervaloMinutos { get; private set; } = 60;

    /// <summary>Duração acima da qual um ciclo em andamento é considerado travado (verificação de saúde).</summary>
    public int CicloMaximoMinutos { get; private set; } = 180;

    // --- Tarefas do ciclo -------------------------------------------------------
    /// <summary>Tarefa específica: UASGs da planilha de clientes (--so-busca-geral desativa).</summary>
    public bool ExecutarTarefaUasg { get; private set; } = true;

    /// <summary>
    /// Tarefa geral, executada depois da de UASGs: licitações das <see cref="UfsBuscaGeral"/>
    /// publicadas pelas <see cref="PlataformasBuscaGeral"/> (--sem-busca-geral desativa;
    /// --uasg também, pois processa apenas a UASG informada).
    /// </summary>
    public bool ExecutarBuscaGeral { get; private set; } = true;

    /// <summary>UFs da busca geral (filtro "UFs" do site do PNCP).</summary>
    public IReadOnlyList<string> UfsBuscaGeral { get; private set; } = new[] { "ES" };

    /// <summary>
    /// Fontes/Plataformas da busca geral (filtro "Fontes/Plataformas" do site do PNCP):
    /// código do sistema que publica no PNCP -> nome exibido no site.
    /// </summary>
    public IReadOnlyDictionary<int, string> PlataformasBuscaGeral { get; private set; } = new Dictionary<int, string>
    {
        [3] = "Compras.gov.br",
        [350] = "PORTAL GOV SERVICOS DIGITAIS LTDA / PORTAL GOV",
    };

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

    // --- APIs ---------------------------------------------------------------------
    public string UrlPesquisa { get; private set; } = UrlPesquisaPadrao;
    public string UrlUasg { get; private set; } = UrlUasgPadrao;
    public string UrlPropostas { get; private set; } = UrlPropostasPadrao;

    /// <summary>Modelo com {0} = CNPJ, {1} = ano e {2} = sequencial da compra.</summary>
    public string UrlArquivos { get; private set; } = UrlArquivosPadrao;
    public string UrlBusca { get; private set; } = UrlBuscaPadrao;

    // --- E-mail ---------------------------------------------------------------
    /// <summary>Destinatário final das licitações com arquivos baixados (obrigatório para enviar).</summary>
    public string? EmailDestino { get; private set; }

    /// <summary>Remetente (conta autenticada no SMTP; obrigatório para enviar).</summary>
    public string? EmailRemetente { get; private set; }

    public string SmtpHost { get; private set; } = SmtpHostPadrao;
    public int SmtpPorta { get; private set; } = SmtpPortaPadrao;
    public int LimiteAnexosEmailMb { get; private set; } = LimiteAnexosEmailMbPadrao;

    /// <summary>Não envia e-mail no fim da execução (downloads continuam normalmente).</summary>
    public bool SemEmail { get; private set; }

    /// <summary>
    /// Senha do remetente. Vem SOMENTE da variável de ambiente COMPRASGOV_SMTP_SENHA
    /// (nunca escrita em código, em arquivo de configuração nem em argumentos).
    /// </summary>
    public string? SmtpSenha { get; } = Env("COMPRASGOV_SMTP_SENHA");

    // --- Pastas -----------------------------------------------------------------
    /// <summary>Planilha com os clientes (coluna "Código UASG").</summary>
    public string PastaPlanilha { get; private set; } = "planilha_uasgs.xlsx";

    /// <summary>Pasta de destino dos editais baixados.</summary>
    public string PastaSaida { get; private set; } = "editais";

    /// <summary>
    /// Pasta do "banco de dados" (histórico JSON, cache UASG->CNPJ e relatórios de
    /// comparação) no servidor, para não depender da máquina que executa. Sem padrão
    /// no código: vem do arquivo, de COMPRASGOV_PASTA_BANCO ou (só o histórico) de --historico.
    /// </summary>
    public string? PastaBanco { get; private set; }

    private string? _historicoArgumento;

    /// <summary>Histórico JSON ("" quando nem --historico nem a pasta do banco foram configurados).</summary>
    public string ArquivoHistorico =>
        _historicoArgumento ?? (PastaBanco is null ? "" : Path.Combine(PastaBanco, "licitacoes_processadas.json"));

    /// <summary>Cache UASG -> CNPJ do órgão (consultado nos Dados Abertos do Compras.gov.br).</summary>
    public string ArquivoCacheCnpj =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ArquivoHistorico)) ?? ".", "uasg_cnpj.json");

    // --- Log e saúde --------------------------------------------------------------
    /// <summary>Pasta dos arquivos de log (rotação diária e por tamanho).</summary>
    public string PastaLogs { get; private set; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public double LogTamanhoMaximoMb { get; private set; } = 10;
    public int LogRetencaoDias { get; private set; } = 30;
    public double LogTamanhoTotalMaximoMb { get; private set; } = 200;

    /// <summary>Repete o log no console (em produção a saída de console é descartada).</summary>
    public bool LogConsole { get; private set; } = true;

    private string? _arquivoEstado;

    /// <summary>Arquivo de heartbeat/estado (padrão: estado.json na pasta de logs).</summary>
    public string ArquivoEstado => _arquivoEstado ?? Path.Combine(PastaLogs, "estado.json");

    private static string? Env(string nome)
    {
        string? valor = Environment.GetEnvironmentVariable(nome);
        return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
    }

    public static Configuracao Ler(string[] args)
    {
        var cfg = new Configuracao();

        // 1) Arquivo (a menor prioridade depois dos padrões).
        string? configExplicito = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase)) configExplicito = args[i + 1];
        configExplicito ??= Env("COMPRASGOV_CONFIG");
        cfg.CarregarArquivo(configExplicito);

        // 2) Variáveis de ambiente.
        cfg.AplicarVariaveis();

        // 3) Argumentos.
        cfg.AplicarArgumentos(args);

        cfg.Validar();
        return cfg;
    }

    private void CarregarArquivo(string? explicito)
    {
        string? caminho = explicito;
        if (caminho is null)
        {
            foreach (string candidato in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, NomeArquivoConfig),
                         Path.Combine(Environment.CurrentDirectory, NomeArquivoConfig)
                     })
            {
                if (File.Exists(candidato)) { caminho = candidato; break; }
            }
            if (caminho is null) return;
        }

        caminho = Path.GetFullPath(caminho);
        if (!File.Exists(caminho))
        {
            Erros.Add($"Arquivo de configuração não encontrado: {caminho}");
            return;
        }

        ArquivoConfiguracao a;
        try
        {
            a = ArquivoConfiguracao.Ler(caminho, Avisos);
        }
        catch (Exception ex)
        {
            Erros.Add($"Arquivo de configuração inválido ({caminho}): {ex.Message}");
            return;
        }

        ArquivoConfig = caminho;

        if (a.Email is { } e)
        {
            EmailDestino = Texto(e.Destino) ?? EmailDestino;
            EmailRemetente = Texto(e.Remetente) ?? EmailRemetente;
            SmtpHost = Texto(e.SmtpHost) ?? SmtpHost;
            SmtpPorta = e.SmtpPorta ?? SmtpPorta;
            LimiteAnexosEmailMb = e.LimiteAnexosMb ?? LimiteAnexosEmailMb;
        }
        if (a.Pastas is { } p)
        {
            PastaBanco = Texto(p.Banco) ?? PastaBanco;
            PastaPlanilha = Texto(p.Planilha) ?? PastaPlanilha;
            PastaSaida = Texto(p.Saida) ?? PastaSaida;
        }
        if (a.Execucao is { } x)
        {
            IntervaloMinutos = x.IntervaloMinutos ?? IntervaloMinutos;
            CicloMaximoMinutos = x.CicloMaximoMinutos ?? CicloMaximoMinutos;
        }
        if (a.BuscaGeral is { } b)
        {
            if (b.Ufs is { Count: > 0 })
                UfsBuscaGeral = b.Ufs.Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).Distinct().ToList();
            if (b.Plataformas is { Count: > 0 })
                PlataformasBuscaGeral = new Dictionary<int, string>(b.Plataformas);
        }
        if (a.Apis is { } api)
        {
            UrlUasg = Texto(api.Uasg) ?? UrlUasg;
            UrlPropostas = Texto(api.Propostas) ?? UrlPropostas;
            UrlArquivos = Texto(api.Arquivos) ?? UrlArquivos;
            UrlBusca = Texto(api.Busca) ?? UrlBusca;
            UrlPesquisa = Texto(api.PortalPesquisa) ?? UrlPesquisa;
        }
        if (a.Log is { } l)
        {
            PastaLogs = Texto(l.Pasta) ?? PastaLogs;
            LogTamanhoMaximoMb = l.TamanhoMaximoMb ?? LogTamanhoMaximoMb;
            LogRetencaoDias = l.RetencaoDias ?? LogRetencaoDias;
            LogTamanhoTotalMaximoMb = l.TamanhoTotalMaximoMb ?? LogTamanhoTotalMaximoMb;
            LogConsole = l.Console ?? LogConsole;
        }
        if (a.Saude is { } s)
            _arquivoEstado = Texto(s.ArquivoEstado) ?? _arquivoEstado;
    }

    private static string? Texto(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private void AplicarVariaveis()
    {
        PastaPlanilha = Env("COMPRASGOV_PLANILHA") ?? PastaPlanilha;
        PastaSaida = Env("COMPRASGOV_PASTA_SAIDA") ?? PastaSaida;
        PastaBanco = Env("COMPRASGOV_PASTA_BANCO") ?? PastaBanco;
        EmailDestino = Env("COMPRASGOV_EMAIL_DESTINO") ?? EmailDestino;
        EmailRemetente = Env("COMPRASGOV_EMAIL_REMETENTE") ?? EmailRemetente;
        SmtpHost = Env("COMPRASGOV_SMTP_HOST") ?? SmtpHost;
        SmtpPorta = Inteiro("COMPRASGOV_SMTP_PORTA") ?? SmtpPorta;
        IntervaloMinutos = Inteiro("COMPRASGOV_INTERVALO_MINUTOS") ?? IntervaloMinutos;
        PastaLogs = Env("COMPRASGOV_PASTA_LOGS") ?? PastaLogs;
        _arquivoEstado = Env("COMPRASGOV_ARQUIVO_ESTADO") ?? _arquivoEstado;

        if (Env("COMPRASGOV_UFS") is { } ufs)
            UfsBuscaGeral = ufs.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.ToUpperInvariant()).Distinct().ToList();

        if (Env("COMPRASGOV_LOG_CONSOLE") is { } console)
        {
            if (bool.TryParse(console, out bool b)) LogConsole = b;
            else if (console is "0" or "1") LogConsole = console == "1";
            else Erros.Add($"COMPRASGOV_LOG_CONSOLE inválida: {console} (use true/false)");
        }
    }

    private int? Inteiro(string nome)
    {
        string? valor = Env(nome);
        if (valor is null) return null;
        if (int.TryParse(valor, out int n)) return n;
        Erros.Add($"Variável {nome} inválida: {valor} (esperado número inteiro)");
        return null;
    }

    private void AplicarArgumentos(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    MostrarAjuda = true;
                    break;

                case "--config":
                    i++; // já tratado antes (arquivo de configuração)
                    break;

                case "--saude":
                    VerificarSaude = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        _arquivoEstado = args[++i];
                    break;

                case "--validar-config":
                    ValidarConfig = true;
                    break;

                case "--pasta-logs":
                    if (i + 1 < args.Length) PastaLogs = args[++i];
                    break;

                case "--headless":
                    Headless = true;
                    break;

                case "--fonte":
                    if (i + 1 < args.Length)
                    {
                        string fonte = args[++i].Trim().ToLowerInvariant();
                        if (fonte is "portal" or "api") UsarPortal = fonte == "portal";
                        else Console.WriteLine($"--fonte inválida: {fonte} (use api ou portal)");
                    }
                    break;

                case "--comparar":
                    Comparar = true;
                    break;

                case "--uma-vez":
                    Continuo = false;
                    break;

                case "--intervalo":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int intervalo) && intervalo > 0)
                        IntervaloMinutos = intervalo;
                    break;

                case "--todos":
                    ProcessarTodosClientes = true;
                    break;

                case "--instalar-navegadores":
                    InstalarNavegadores = true;
                    break;

                case "--ignorar-objeto":
                    IgnorarObjeto = true;
                    break;

                case "--testar-pncp":
                    if (i + 1 < args.Length) TestarPncpUrl = args[++i].Trim();
                    break;

                case "--debug":
                    Debug = true;
                    break;

                case "--sem-email":
                    SemEmail = true;
                    break;

                case "--email-destino":
                    if (i + 1 < args.Length) EmailDestino = args[++i].Trim();
                    break;

                case "--uasg":
                    if (i + 1 < args.Length) UasgManual = args[++i].Trim();
                    break;

                case "--sem-busca-geral":
                    ExecutarBuscaGeral = false;
                    break;

                case "--so-busca-geral":
                    ExecutarTarefaUasg = false;
                    break;

                case "--max":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int max)) MaxLicitacoes = max;
                    break;

                case "--planilha":
                    if (i + 1 < args.Length) PastaPlanilha = args[++i];
                    break;

                case "--pasta-saida":
                    if (i + 1 < args.Length) PastaSaida = args[++i];
                    break;

                case "--historico":
                    if (i + 1 < args.Length) _historicoArgumento = args[++i];
                    break;

                default:
                    Console.WriteLine($"Argumento desconhecido: {arg} (use --help)");
                    break;
            }
        }

        // --uasg processa apenas a UASG informada (sem a busca geral).
        if (!string.IsNullOrWhiteSpace(UasgManual))
        {
            if (!ExecutarTarefaUasg)
                Avisos.Add("--uasg informado: --so-busca-geral ignorado (processa só a UASG, sem a busca geral).");
            ExecutarTarefaUasg = true;
            ExecutarBuscaGeral = false;
        }
    }

    private void Validar()
    {
        if (SmtpPorta is < 1 or > 65535) Erros.Add($"Porta SMTP inválida: {SmtpPorta}");
        if (IntervaloMinutos < 1) Erros.Add($"Intervalo entre ciclos inválido: {IntervaloMinutos} min");
        if (CicloMaximoMinutos < 1) Erros.Add($"Execucao.CicloMaximoMinutos inválido: {CicloMaximoMinutos}");
        if (LimiteAnexosEmailMb < 1) Erros.Add($"Email.LimiteAnexosMb inválido: {LimiteAnexosEmailMb}");
        if (LogTamanhoMaximoMb <= 0 || LogRetencaoDias < 1 || LogTamanhoTotalMaximoMb <= 0)
            Erros.Add("Seção Log inválida: TamanhoMaximoMb, RetencaoDias e TamanhoTotalMaximoMb devem ser positivos.");
        if (UfsBuscaGeral.Count == 0) Erros.Add("BuscaGeral.Ufs vazio.");
        if (PlataformasBuscaGeral.Count == 0) Erros.Add("BuscaGeral.Plataformas vazio.");
        if (!(UrlArquivos.Contains("{0}") && UrlArquivos.Contains("{1}") && UrlArquivos.Contains("{2}")))
            Erros.Add("Apis.Arquivos precisa conter {0} (CNPJ), {1} (ano) e {2} (sequencial).");
    }

    /// <summary>
    /// Problemas que impedem um ciclo normal (histórico sem lugar definido). Verificado
    /// só nos modos que processam licitações (não em --help, --saude etc.).
    /// </summary>
    public void ValidarParaCiclo()
    {
        if (string.IsNullOrWhiteSpace(ArquivoHistorico))
            Erros.Add("Pasta do banco de dados não configurada: informe Pastas.Banco no arquivo de configuração, " +
                      "a variável COMPRASGOV_PASTA_BANCO ou o argumento --historico.");
    }

    public void ExibirAjuda()
    {
        Console.WriteLine($"""
            ComprasGOV - automação Compras.gov.br -> PNCP (download de editais)

            Uso: ComprasGOV [opções]

            Cada ciclo executa duas tarefas, nesta ordem:
              1) específica: UASGs da planilha de clientes;
              2) geral: licitações a receber/recebendo propostas no PNCP com os filtros
                 UF = {string.Join(", ", UfsBuscaGeral)} e Fontes/Plataformas = {string.Join(" e ", PlataformasBuscaGeral.Values)}
                 (mesmos filtros do site do PNCP).
              Nas duas valem as mesmas regras (só divulgadas, prazo em aberto, regra do
              objeto), o mesmo download e o mesmo e-mail; nada é processado duas vezes.

            Opções:
              --config <arquivo>        Arquivo de configuração (padrão: COMPRASGOV_CONFIG ou
                                        appsettings.json ao lado do exe / no diretório atual).
              --saude [arquivo]         Lê o arquivo de estado (heartbeat) e encerra com código
                                        0 se o robô está em dia ou 1 se não está.
              --validar-config          Confere a configuração (arquivo + variáveis) e encerra
                                        com código 0 (válida) ou 1, sem exibir os valores.
              --uasg <codigo>           Processa apenas o código UASG informado (ex.: 90014), sem a busca geral.
              --sem-busca-geral         Executa só a tarefa de UASGs da planilha.
              --so-busca-geral          Executa só a busca geral (UF + fontes/plataformas).
              --todos                   Sem efeito: o padrão já processa TODOS os clientes.
              --headless                Executa sem janela do navegador (padrão: janela visível).
              --fonte <api|portal>      Origem das licitações. api (padrão): API oficial do PNCP,
                                        sem navegador e sem captcha. portal: fluxo antigo pelo
                                        navegador no Compras.gov.br (sujeito ao hCaptcha).
              --comparar                Compara portal x API por UASG (não baixa, não grava
                                        histórico) e salva um relatório; executa uma vez.
              --uma-vez                 Executa um único ciclo e encerra (padrão: contínuo 24/7).
              --intervalo <minutos>     Pausa entre ciclos no modo contínuo (padrão: 60).
              --max <n>                 Limita a quantidade de licitações processadas por ciclo (somando as tarefas).
              --planilha <caminho>      Caminho da planilha de órgãos (xlsx).
              --pasta-saida <caminho>   Pasta onde os arquivos serão salvos.
              --historico <arquivo>     Arquivo JSON de licitações já processadas.
              --pasta-logs <caminho>    Pasta dos arquivos de log e do estado.json.
              --sem-email               Não envia os e-mails no fim da execução (só baixa).
              --email-destino <email>   Altera o destinatário dos e-mails.
              --instalar-navegadores    Instala os navegadores do Playwright e encerra.
              --ignorar-objeto          SOMENTE TESTE: ignora a regra de objeto e baixa tudo.
              --testar-pncp <url>       SOMENTE TESTE: abre esta URL do PNCP e baixa os arquivos (sem a busca).
              --debug                   Log detalhado de rede/console do portal (REQ, HTTP, corpos).
              --help                    Exibe esta ajuda.

            Configuração atual{(ArquivoConfig is null ? " (nenhum arquivo de configuração encontrado)" : $" ({ArquivoConfig})")}:
              E-mail: {EmailRemetente ?? "(remetente não configurado)"} -> {EmailDestino ?? "(destinatário não configurado)"}
              SMTP: {SmtpHost}:{SmtpPorta}; anexos até {LimiteAnexosEmailMb} MB por mensagem
              Histórico: {(ArquivoHistorico.Length > 0 ? ArquivoHistorico : "(não configurado)")}
              Logs: {PastaLogs}

            Variáveis de ambiente (sobrepõem o arquivo; os argumentos sobrepõem as variáveis):
              COMPRASGOV_CONFIG          Arquivo de configuração.
              COMPRASGOV_PLANILHA        Caminho da planilha de órgãos (xlsx).
              COMPRASGOV_PASTA_SAIDA     Pasta onde os arquivos serão salvos.
              COMPRASGOV_PASTA_BANCO     Pasta do histórico/cache.
              COMPRASGOV_EMAIL_DESTINO / COMPRASGOV_EMAIL_REMETENTE
              COMPRASGOV_SMTP_HOST / COMPRASGOV_SMTP_PORTA
              COMPRASGOV_INTERVALO_MINUTOS, COMPRASGOV_UFS (ex.: ES,MG)
              COMPRASGOV_PASTA_LOGS, COMPRASGOV_LOG_CONSOLE (true/false), COMPRASGOV_ARQUIVO_ESTADO
              COMPRASGOV_SMTP_SENHA      Senha do remetente (obrigatória para enviar e-mail;
                                         só por variável, nunca no arquivo).
            """);
    }
}
