using ComprasGOV.Automation;
using ComprasGOV.Config;
using ComprasGOV.Models;
using ComprasGOV.Services;
using Microsoft.Playwright;

namespace ComprasGOV;

/// <summary>
/// Orquestra as tarefas de um ciclo (chamadas nesta ordem pelo Program):
/// 1) tarefa específica - UASGs da planilha de clientes (API do PNCP ou portal);
/// 2) tarefa geral - busca por UF e fontes/plataformas (filtros do site do PNCP).
/// Nas duas: proposta em aberto e situação "divulgada" -> valida objeto -> baixa
/// TODOS os arquivos do PNCP -> registra no histórico.
/// 3) No fim, agrupa as licitações (2 a 4 por mensagem) e envia os e-mails.
/// </summary>
public sealed class ExecutadorFluxo
{
    private readonly Configuracao _cfg;
    private readonly HistoricoService _historico;
    private readonly ComprasNetAutomacao _compras = new();
    private readonly PncpAutomacao _pncp = new();
    private readonly DownloadEditalService _download;
    private readonly EmailService _email;

    /// <summary>Licitações com arquivos baixados NESTA execução (aguardando e-mail).</summary>
    private readonly List<ItemEmail> _paraEmail = new();

    /// <summary>Licitações avaliadas neste ciclo: a busca geral não repete o que a tarefa por UASG já avaliou.</summary>
    private readonly HashSet<string> _avaliadasNoCiclo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chaves antigas ("UASG-número/ano") avaliadas neste ciclo pelo fluxo do portal.</summary>
    private readonly HashSet<string> _avaliadasPortalNoCiclo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Licitações avaliadas neste ciclo (o limite --max vale para as duas tarefas).</summary>
    private int _processadasNoCiclo;

    /// <summary>Sinal de encerramento (Ctrl+C): para entre licitações, sem cortar uma no meio.</summary>
    private CancellationToken _cancelamento;

    /// <summary>Quem encontrou a licitação: dados gravados no histórico e usados no e-mail.</summary>
    private sealed record Contexto(string Uasg, string UasgNome, string Origem, string Plataforma = "")
    {
        public static Contexto DeCliente(ClienteUasg cliente, string plataforma = "") =>
            new(cliente.Codigo, cliente.Orgao, OrigemLicitacao.Uasg, plataforma);

        public string Descricao => Origem == OrigemLicitacao.Geral ? $"{Uasg} - {UasgNome}" : $"UASG {Uasg}";
    }

    public ExecutadorFluxo(Configuracao cfg, HistoricoService historico)
    {
        _cfg = cfg;
        _historico = historico;
        _download = new DownloadEditalService(cfg.PastaSaida);
        _email = new EmailService(cfg);
    }

    // ------------------------------------------------------------------
    //  Tarefa específica pelo portal Compras.gov.br (navegador)
    // ------------------------------------------------------------------

    /// <summary>Processa as UASGs pelo portal. Devolve a quantidade de falhas.</summary>
    public async Task<int> ProcessarUasgsViaPortalAsync(IBrowserContext context, IReadOnlyList<ClienteUasg> clientes,
        CancellationToken cancelamento = default)
    {
        _cancelamento = cancelamento;
        int falhas = 0;

        foreach (ClienteUasg cliente in clientes)
        {
            if (_cancelamento.IsCancellationRequested) break;

            Log.Secao($"CLIENTE: {cliente.Codigo} - {cliente.Orgao}");
            try
            {
                await ExecutarClienteAsync(context, cliente);
            }
            catch (Exception ex)
            {
                falhas++;
                Log.Erro($"Falha ao processar {cliente}: {ex.Message}");
            }
        }

        return falhas;
    }

    private async Task ExecutarClienteAsync(IBrowserContext context, ClienteUasg cliente)
    {
        // O contexto (perfil) é compartilhado entre clientes para reaproveitar a sessão.
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(45_000);

        Instrumentar(page);

        try
        {
            await _compras.PesquisarAsync(page, cliente);

            int pagina = 1;

            while (true)
            {
                await _compras.GarantirPaginaAsync(page, cliente, pagina);
                var alvos = await _compras.ColetarAlvosAsync(page, cliente, historico: _historico);

                foreach (LicitacaoEncontrada alvo in alvos)
                {
                    if (_cancelamento.IsCancellationRequested) return;

                    if (LimiteAtingido())
                    {
                        Log.Aviso($"Limite de {_cfg.MaxLicitacoes} licitação(ões) por ciclo atingido.");
                        return;
                    }

                    await ProcessarLicitacaoAsync(page, cliente, alvo, pagina);
                }

                if (_cancelamento.IsCancellationRequested) return;
                if (!await _compras.IrParaProximaPaginaAsync(page)) break;
                pagina++;
            }

            Log.Info($"Cliente {cliente.Codigo} concluído.");
        }
        finally
        {
            // Fecha só a página do cliente; o contexto (perfil) permanece para o próximo.
            await page.CloseAsync();
        }
    }

    private async Task ProcessarLicitacaoAsync(IPage page, ClienteUasg cliente, LicitacaoEncontrada alvo, int pagina)
    {
        var ctx = Contexto.DeCliente(cliente);
        _processadasNoCiclo++;
        _avaliadasPortalNoCiclo.Add($"{SemZeros(cliente.Codigo)}-{alvo.Numero}");

        Log.Secao($"LICITAÇÃO {alvo.Titulo} (UASG {cliente.Codigo})");
        if (!string.IsNullOrEmpty(alvo.Objeto))
            Log.Info($"Objeto: {alvo.Objeto}");
        else
            Log.Aviso("Objeto não encontrado no card.");

        if (!ObjetoAprovado(ctx, alvo)) return;

        IPage? pncp = null;
        try
        {
            await _compras.GarantirPaginaAsync(page, cliente, pagina);

            ILocator? card = await _compras.LocalizarCardAsync(page, alvo.Titulo);
            if (card is null)
            {
                Log.Aviso($"Card da licitação não encontrado na lista: {alvo.Titulo}");
                _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Erro,
                    motivo: "Card não encontrado na lista de resultados"));
                return;
            }

            await _compras.IrParaAcompanharAsync(page, card);
            Log.Info($"Acompanhamento da compra: {page.Url}");

            pncp = await _pncp.AbrirPncpAsync(page);
            if (pncp is null)
            {
                _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Erro,
                    motivo: "Não foi possível abrir o PNCP", urlCompras: page.Url));
                return;
            }

            var arquivos = await _pncp.ListarArquivosAsync(pncp);
            if (arquivos.Count == 0)
            {
                _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.SemEdital,
                    motivo: "Nenhum arquivo publicado na aba 'Arquivos' do PNCP",
                    urlCompras: page.Url, urlPncp: pncp.Url));
                Log.Aviso("PNCP sem arquivos publicados; seguindo para a próxima licitação.");
                return;
            }

            // Baixa TODOS os arquivos do PNCP (não apenas o edital).
            var caminhos = new List<string>();
            foreach (ArquivoPncp arquivo in arquivos)
                caminhos.Add(await _download.BaixarAsync(arquivo.Url, alvo.Numero, arquivo.Nome));

            LicitacaoHistorico registro = NovoRegistro(ctx, alvo, StatusLicitacao.Baixado,
                urlCompras: page.Url, urlPncp: pncp.Url,
                arquivo: string.Join("\n", caminhos),
                emailEnviado: false);
            _historico.Salvar(registro);

            Log.Ok($"{caminhos.Count} arquivo(s) salvo(s) na pasta de saída.");

            // Entra na fila de e-mail desta execução.
            _paraEmail.Add(ItemEmail.DeRegistro(registro, caminhos));
        }
        catch (Exception ex)
        {
            Log.Erro($"Erro ao processar {alvo.Titulo}: {ex.Message}");
            _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Erro,
                motivo: ex.Message, urlCompras: page.Url, urlPncp: pncp?.Url ?? ""));
        }
        finally
        {
            if (pncp is not null && !pncp.IsClosed)
            {
                try { await pncp.CloseAsync(); } catch { /* aba já fechada */ }
            }

            await _compras.VoltarParaPesquisaAsync(page);
        }
    }

    /// <summary>
    /// Regra de validade do HTML.md (termos inválidos vencem). Registra no histórico
    /// as licitações rejeitadas/indefinidas e devolve true quando deve baixar.
    /// </summary>
    private bool ObjetoAprovado(Contexto ctx, LicitacaoEncontrada alvo,
        string urlCompras = "", string urlPncp = "")
    {
        Classificacao classificacao = ClassificadorObjeto.Classificar(alvo.Objeto);
        Log.Info($"Classificação do objeto: {ClassificadorObjeto.Descrever(classificacao)}");

        if (_cfg.IgnorarObjeto)
        {
            Log.Aviso("MODO TESTE (--ignorar-objeto): a regra de validade do objeto está sendo ignorada.");
            return true;
        }

        if (classificacao == Classificacao.Invalido)
        {
            _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Rejeitado,
                motivo: "Objeto inválido (serviço, locação, produtos químicos ou medicamentos)",
                urlCompras: urlCompras, urlPncp: urlPncp));
            Log.Aviso("Licitação rejeitada pela regra do objeto; nada será baixado.");
            return false;
        }

        if (classificacao == Classificacao.Indefinido)
        {
            _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Indefinido,
                motivo: "Objeto sem termos que classifiquem como válido ou inválido",
                urlCompras: urlCompras, urlPncp: urlPncp));
            Log.Aviso("Objeto indefinido; nada será baixado (revisar a regra de palavras-chave).");
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    //  Tarefa específica via API oficial do PNCP (sem navegador, sem captcha)
    // ------------------------------------------------------------------

    /// <summary>
    /// Mesmo resultado do fluxo do portal, pela API: para cada UASG lista as compras
    /// recebendo propostas, aplica a regra do objeto e baixa todos os arquivos ativos.
    /// Uma UASG que falhar na API não grava nada no histórico, então é tentada de novo
    /// no próximo ciclo (nenhuma licitação se perde). Devolve a quantidade de falhas.
    /// </summary>
    public async Task<int> ProcessarUasgsViaApiAsync(PncpApiService api, IReadOnlyList<ClienteUasg> clientes,
        CancellationToken cancelamento = default)
    {
        _cancelamento = cancelamento;
        int falhas = 0;

        foreach (ClienteUasg cliente in clientes)
        {
            if (_cancelamento.IsCancellationRequested || LimiteAtingido()) break;

            Log.Secao($"CLIENTE: {cliente.Codigo} - {cliente.Orgao}");
            try
            {
                string? cnpj = await api.ObterCnpjAsync(cliente.Codigo, _cancelamento);
                if (cnpj is null)
                {
                    falhas++;
                    Log.Erro($"UASG {cliente.Codigo} não encontrada (ou inativa) nos Dados Abertos do Compras.gov.br.");
                    continue;
                }

                List<LicitacaoPncp> abertas = await api.ListarPropostasAbertasAsync(cliente.Codigo, cnpj, _cancelamento);
                Log.Info($"API PNCP: {abertas.Count} compra(s) recebendo propostas (CNPJ {cnpj}).");

                foreach (LicitacaoPncp lic in abertas.OrderBy(l => l.EncerramentoProposta))
                {
                    if (_cancelamento.IsCancellationRequested) break;

                    if (lic.SituacaoId != 1)
                    {
                        Log.Info($"  Ignorada ({lic.SituacaoNome}): {lic.Titulo}");
                        continue;
                    }

                    if (PrazoEncerrado(lic))
                    {
                        Log.Info($"  Ignorada (prazo encerrado em {lic.Prazo}): {lic.Titulo}");
                        continue;
                    }

                    if (_historico.JaProcessada(lic.Chave))
                    {
                        Log.Info($"  Já processada anteriormente: {lic.Titulo}");
                        continue;
                    }

                    if (LimiteAtingido())
                    {
                        Log.Aviso($"Limite de {_cfg.MaxLicitacoes} licitação(ões) por ciclo atingido.");
                        break;
                    }

                    await ProcessarLicitacaoApiAsync(api, lic, Contexto.DeCliente(cliente, lic.Plataforma));
                }
            }
            catch (OperationCanceledException) when (_cancelamento.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                falhas++;
                Log.Erro($"Falha ao consultar a API para {cliente}: {ex.Message} (será tentado no próximo ciclo)");
            }
        }

        return falhas;
    }

    // ------------------------------------------------------------------
    //  Tarefa geral: UF + fontes/plataformas (filtros do site do PNCP)
    // ------------------------------------------------------------------

    /// <summary>
    /// Busca geral: licitações a receber/recebendo propostas nas UFs e fontes/plataformas
    /// de <see cref="Configuracao"/>, com as MESMAS regras da tarefa por UASG: só as
    /// "Divulgada no PNCP" com prazo em aberto, regra do objeto, download e e-mail.
    /// O que a tarefa por UASG já avaliou (neste ciclo ou antes) não é repetido.
    /// Se a busca falhar, nada é gravado no histórico e ela é repetida no próximo ciclo.
    /// Devolve a quantidade de falhas.
    /// </summary>
    public async Task<int> ProcessarBuscaGeralAsync(PncpApiService api, CancellationToken cancelamento = default)
    {
        _cancelamento = cancelamento;
        Log.Secao($"TAREFA GERAL - UF: {string.Join(", ", Configuracao.UfsBuscaGeral)} | " +
                  $"Fontes/Plataformas: {string.Join(" e ", Configuracao.PlataformasBuscaGeral.Values)}");

        List<LicitacaoPncp> encontradas;
        try
        {
            encontradas = await api.ListarBuscaGeralAsync(Configuracao.UfsBuscaGeral,
                Configuracao.PlataformasBuscaGeral.Keys.ToList(), _cancelamento);
        }
        catch (OperationCanceledException) when (_cancelamento.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Erro($"Falha na busca geral: {ex.Message} (será tentada no próximo ciclo)");
            return 1;
        }

        Log.Info($"Busca geral: {encontradas.Count} licitação(ões) a receber/recebendo propostas.");

        int ignoradas = 0, jaProcessadas = 0, avaliadas = 0;
        try
        {
            foreach (LicitacaoPncp lic in encontradas.OrderBy(l => l.EncerramentoProposta))
            {
                if (_cancelamento.IsCancellationRequested) break;

                if (lic.SituacaoId != 1)
                {
                    ignoradas++;
                    Log.Info($"  Ignorada ({lic.SituacaoNome}): {lic.Titulo} - {lic.UnidadeNome}");
                    continue;
                }

                if (PrazoEncerrado(lic))
                {
                    ignoradas++;
                    Log.Info($"  Ignorada (prazo encerrado em {lic.Prazo}): {lic.Titulo} - {lic.UnidadeNome}");
                    continue;
                }

                // Avaliada há pouco pela tarefa por UASG ou já processada em ciclos anteriores.
                if (_avaliadasNoCiclo.Contains(lic.Chave) || _avaliadasPortalNoCiclo.Contains(lic.ChaveLegada) ||
                    _historico.JaProcessada(lic.Chave))
                {
                    jaProcessadas++;
                    continue;
                }

                if (LimiteAtingido())
                {
                    Log.Aviso($"Limite de {_cfg.MaxLicitacoes} licitação(ões) por ciclo atingido.");
                    break;
                }

                avaliadas++;
                await ProcessarLicitacaoApiAsync(api, lic,
                    new Contexto(lic.Uasg, lic.UnidadeNome, OrigemLicitacao.Geral, lic.Plataforma));
            }
        }
        catch (OperationCanceledException) when (_cancelamento.IsCancellationRequested)
        {
            // Ctrl+C: a licitação em andamento não foi gravada e volta no próximo ciclo.
        }

        Log.Info($"Busca geral concluída: {encontradas.Count} encontrada(s), {jaProcessadas} já processada(s), " +
                 $"{ignoradas} ignorada(s), {avaliadas} avaliada(s) agora.");
        return 0;
    }

    private static bool PrazoEncerrado(LicitacaoPncp lic) =>
        lic.EncerramentoProposta is DateTime fim && fim <= DateTime.Now;

    private static string SemZeros(string codigo)
    {
        string s = codigo.Trim().TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }

    private bool LimiteAtingido() =>
        _cfg.MaxLicitacoes.HasValue && _processadasNoCiclo >= _cfg.MaxLicitacoes.Value;

    private async Task ProcessarLicitacaoApiAsync(PncpApiService api, LicitacaoPncp lic, Contexto ctx)
    {
        _processadasNoCiclo++;
        _avaliadasNoCiclo.Add(lic.Chave);

        LicitacaoEncontrada alvo = lic.ParaEncontrada();
        Log.Secao($"LICITAÇÃO {alvo.Titulo} ({ctx.Descricao}) - prazo {alvo.Prazo}");
        Log.Info($"Objeto: {alvo.Objeto}");

        if (!ObjetoAprovado(ctx, alvo, lic.UrlCompras, lic.UrlPncp)) return;

        try
        {
            List<ArquivoPncp> arquivos = await api.ListarArquivosAsync(lic, _cancelamento);
            if (arquivos.Count == 0)
            {
                _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.SemEdital,
                    motivo: "Nenhum arquivo publicado no PNCP",
                    urlCompras: lic.UrlCompras, urlPncp: lic.UrlPncp));
                Log.Aviso("PNCP sem arquivos publicados; seguindo para a próxima licitação.");
                return;
            }

            var caminhos = new List<string>();
            foreach (ArquivoPncp arquivo in arquivos)
                caminhos.Add(await _download.BaixarAsync(arquivo.Url, alvo.Numero, arquivo.Nome));

            LicitacaoHistorico registro = NovoRegistro(ctx, alvo, StatusLicitacao.Baixado,
                urlCompras: lic.UrlCompras, urlPncp: lic.UrlPncp,
                arquivo: string.Join("\n", caminhos),
                emailEnviado: false);
            _historico.Salvar(registro);

            Log.Ok($"{caminhos.Count} arquivo(s) salvo(s) na pasta de saída.");
            _paraEmail.Add(ItemEmail.DeRegistro(registro, caminhos));
        }
        catch (OperationCanceledException) when (_cancelamento.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Erro($"Erro ao processar {alvo.Titulo}: {ex.Message}");
            _historico.Salvar(NovoRegistro(ctx, alvo, StatusLicitacao.Erro,
                motivo: ex.Message, urlCompras: lic.UrlCompras, urlPncp: lic.UrlPncp));
        }
    }

    // ------------------------------------------------------------------
    //  E-mails (fim do ciclo, depois das duas tarefas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Fase final de e-mail: junta os downloads desta execução (das duas tarefas) com
    /// as pendências de execuções anteriores, divide em lotes de 2 a 4 licitações
    /// (1 → lote único) e envia cada lote com corpo (UASG/número/objeto) e anexos
    /// (1 arquivo direto; 2 ou mais embalados em .zip).
    /// Falha de envio mantém as licitações PENDENTES para a próxima execução.
    /// Devolve a quantidade de falhas.
    /// </summary>
    public async Task<int> EnviarEmailsAsync()
    {
        var itens = new List<ItemEmail>(_paraEmail);

        // Pendências de execuções anteriores (download ok, e-mail ainda não saiu).
        foreach (LicitacaoHistorico reg in _historico.PendentesDeEmail())
        {
            if (itens.Any(i => string.Equals(i.Chave, reg.Chave, StringComparison.OrdinalIgnoreCase))) continue;

            string[] arquivos = reg.Arquivo
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists)
                .ToArray();

            if (arquivos.Length == 0)
            {
                Log.Aviso($"Arquivos de {reg.Chave} não existem mais; e-mail descartado.");
                continue;
            }

            itens.Add(ItemEmail.DeRegistro(reg, arquivos));
        }

        if (itens.Count == 0)
        {
            Log.Info("Nenhuma licitação com arquivos para enviar por e-mail.");
            return 0;
        }

        if (_cfg.SemEmail)
        {
            Log.Aviso($"--sem-email: {itens.Count} licitação(ões) prontas, " +
                      "mas o envio foi suprimido (ficam pendentes).");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(_cfg.SmtpSenha))
        {
            Log.Erro("Variável de ambiente COMPRASGOV_SMTP_SENHA não definida: " +
                     "e-mails NÃO enviados (ficam pendentes).");
            return 1;
        }

        var lotes = EmailService.SepararEmLotes(itens);
        Log.Secao($"ENVIANDO E-MAILS: {itens.Count} licitação(ões) em " +
                  $"{lotes.Count} mensagem(ns) para {_cfg.EmailDestino}");

        int enviados = 0, falhas = 0;

        foreach (var lote in lotes)
        {
            var anexos = new List<string>();
            var zips = new List<string>();

            try
            {
                foreach (ItemEmail item in lote)
                {
                    // 1 arquivo → anexo direto; 2 ou mais → embala em .zip.
                    if (item.Arquivos.Count == 1)
                    {
                        anexos.Add(item.Arquivos[0]);
                    }
                    else
                    {
                        string zip = EmailService.CriarZip(item.Arquivos, item.NomeAnexo);
                        anexos.Add(zip);
                        zips.Add(zip);
                    }
                }

                string assunto = $"Licitações Compras.gov.br - {DateTime.Now:dd/MM/yyyy}";
                await _email.EnviarAsync(assunto, EmailService.MontarCorpo(lote), anexos);

                foreach (ItemEmail item in lote)
                    _historico.MarcarEmailEnviado(item.Chave);

                enviados++;
                long bytes = anexos.Sum(a => new FileInfo(a).Length);
                Log.Ok($"E-mail enviado para {_cfg.EmailDestino}: {lote.Count} licitação(ões), " +
                       $"{anexos.Count} anexo(s), {bytes / 1024.0 / 1024.0:F1} MB.");
            }
            catch (Exception ex)
            {
                falhas++;
                // Exibe a causa raiz (ex.: AuthenticationException de TLS), não só a genérica.
                Exception raiz = ex.GetBaseException();
                Log.Erro($"Falha ao enviar e-mail com {lote.Count} licitação(ões): {raiz.Message}");
                if (raiz != ex) Log.Aviso($"Detalhe: {ex.Message}");
                Log.Aviso("Elas ficam PENDENTES e serão reenviadas na próxima execução.");
            }
            finally
            {
                foreach (string zip in zips)
                {
                    try { File.Delete(zip); } catch { /* temporário */ }
                }
            }
        }

        Log.Info($"E-mails: {enviados} enviado(s), {falhas} falha(s).");
        return falhas;
    }

    /// <summary>Log de rede/console para diagnosticar falhas da automação.
    /// O detalhamento (REQ/HTTP/corpo/console) só acontece com --debug.</summary>
    private void Instrumentar(IPage page)
    {
        page.Request += (_, requisicao) =>
        {
            try
            {
                if (!_cfg.Debug || Ignorar(requisicao.Url)) return;
                Log.Info($"REQ {requisicao.Method} {requisicao.Url}");
            }
            catch { /* requisição já descartada */ }
        };

        page.RequestFailed += (_, requisicao) =>
        {
            try
            {
                if (Ignorar(requisicao.Url)) return;
                Log.Aviso($"REQ-FAIL {requisicao.Url} ({requisicao.Failure})");
            }
            catch { /* requisição já descartada */ }
        };

        page.Response += (_, resposta) =>
        {
            try
            {
                string url = resposta.Url;
                if (Ignorar(url)) return;

                int status = resposta.Status;

                // Erro de HTTP é sempre útil; o restante só com --debug.
                if (status >= 400)
                {
                    Log.Erro($"HTTP {status} {url}");
                }
                else if (_cfg.Debug && url.Contains("fase-externa", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"HTTP {status} {url}");
                    _ = LogCorpoAsync(resposta);
                }
            }
            catch { /* resposta já descartada */ }
        };

        page.Console += (_, msg) =>
        {
            // console.error é relevante; o restante (ex.: EvalError rotineiro do
            // hCaptcha) é ruído e só aparece com --debug.
            if (msg.Type == "error") Log.Aviso($"console.error: {Truncar(msg.Text, 300)}");
            else if (_cfg.Debug) Log.Info($"console.{msg.Type}: {Truncar(msg.Text, 300)}");
        };

        page.PageError += (_, erro) => Log.Erro($"pageerror: {Truncar(erro, 300)}");
    }

    private static bool Ignorar(string url) =>
        url.Contains(".js", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".css", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".gif", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".svg", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".woff", StringComparison.OrdinalIgnoreCase) ||
        url.Contains(".ttf", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("imgs.hcaptcha.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("google-analytics", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("googletagmanager", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("doubleclick", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lê o corpo de forma assíncrona (nunca bloquear o handler de eventos).</summary>
    private static async Task LogCorpoAsync(IResponse resposta)
    {
        try
        {
            byte[] corpo = await resposta.BodyAsync();
            string texto = System.Text.Encoding.UTF8.GetString(corpo);
            Log.Info($"    corpo: {Truncar(texto, 400)}");
        }
        catch { /* downloads/streams/resposta já descartada */ }
    }

    private static string Truncar(string texto, int max) =>
        string.IsNullOrEmpty(texto) ? "" : texto.Length <= max ? texto : texto[..max] + "...";

    private static LicitacaoHistorico NovoRegistro(
        Contexto ctx,
        LicitacaoEncontrada alvo,
        string status,
        string motivo = "",
        string urlCompras = "",
        string urlPncp = "",
        string arquivo = "",
        bool? emailEnviado = null) => new()
    {
        Chave = alvo.Chave,
        Uasg = ctx.Uasg,
        UasgNome = ctx.UasgNome,
        Origem = ctx.Origem,
        Plataforma = ctx.Plataforma,
        Titulo = alvo.Titulo,
        Numero = alvo.Numero,
        Objeto = alvo.Objeto,
        PrazoProposta = alvo.Prazo,
        Status = status,
        Motivo = motivo,
        UrlCompras = urlCompras,
        UrlPncp = urlPncp,
        Arquivo = arquivo,
        EmailEnviado = emailEnviado,
        DataProcessamento = DateTime.Now
    };
}
