using ComprasGOV.Config;

namespace ComprasGOV.Tests;

public class ConfiguracaoTests
{
    private const string ConfigBasica = """
        {
          // comentários são aceitos
          "Email": { "Destino": "destino@exemplo.invalid", "Remetente": "remetente@exemplo.invalid",
                     "SmtpHost": "smtp.exemplo.invalid", "SmtpPorta": 2525, "LimiteAnexosMb": 7 },
          "Pastas": { "Banco": "C:\\banco" },
          "Execucao": { "IntervaloMinutos": 45, "CicloMaximoMinutos": 90 },
          "BuscaGeral": { "Ufs": [ "es", "mg" ], "Plataformas": { "3": "Compras.gov.br" } },
          "Apis": { "Busca": "https://exemplo.invalid/busca" }
        }
        """;

    private static string Gravar(PastaTemporaria pasta, string json)
    {
        string arquivo = pasta.Arquivo("appsettings.json");
        File.WriteAllText(arquivo, json);
        return arquivo;
    }

    [Fact]
    public void Arquivo_preenche_os_valores_e_mantem_os_padroes_do_que_falta()
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Configuracao cfg = Configuracao.Ler(new[] { "--config", Gravar(pasta, ConfigBasica) });

        Assert.Empty(cfg.Erros);
        Assert.Equal("destino@exemplo.invalid", cfg.EmailDestino);
        Assert.Equal("remetente@exemplo.invalid", cfg.EmailRemetente);
        Assert.Equal("smtp.exemplo.invalid", cfg.SmtpHost);
        Assert.Equal(2525, cfg.SmtpPorta);
        Assert.Equal(7, cfg.LimiteAnexosEmailMb);
        Assert.Equal(45, cfg.IntervaloMinutos);
        Assert.Equal(90, cfg.CicloMaximoMinutos);
        Assert.Equal(new[] { "ES", "MG" }, cfg.UfsBuscaGeral);
        Assert.Equal(new[] { 3 }, cfg.PlataformasBuscaGeral.Keys);
        Assert.Equal(Path.Combine(@"C:\banco", "licitacoes_processadas.json"), cfg.ArquivoHistorico);
        Assert.Equal("https://exemplo.invalid/busca", cfg.UrlBusca);
        Assert.Equal(Configuracao.UrlPropostasPadrao, cfg.UrlPropostas);
    }

    [Fact]
    public void Precedencia_argumento_variavel_arquivo()
    {
        using var pasta = new PastaTemporaria();
        string arquivo = Gravar(pasta, ConfigBasica);

        using (new Ambiente())
            Assert.Equal(45, Configuracao.Ler(new[] { "--config", arquivo }).IntervaloMinutos);

        using (new Ambiente(("COMPRASGOV_INTERVALO_MINUTOS", "30"), ("COMPRASGOV_EMAIL_DESTINO", "env@exemplo.invalid")))
        {
            Configuracao porVariavel = Configuracao.Ler(new[] { "--config", arquivo });
            Assert.Equal(30, porVariavel.IntervaloMinutos);
            Assert.Equal("env@exemplo.invalid", porVariavel.EmailDestino);

            Configuracao porArgumento = Configuracao.Ler(new[]
                { "--config", arquivo, "--intervalo", "15", "--email-destino", "arg@exemplo.invalid" });
            Assert.Equal(15, porArgumento.IntervaloMinutos);
            Assert.Equal("arg@exemplo.invalid", porArgumento.EmailDestino);
        }
    }

    [Fact]
    public void Arquivo_indicado_pela_variavel_COMPRASGOV_CONFIG()
    {
        using var pasta = new PastaTemporaria();
        using var amb = new Ambiente(("COMPRASGOV_CONFIG", Gravar(pasta, ConfigBasica)));
        Assert.Equal(45, Configuracao.Ler(Array.Empty<string>()).IntervaloMinutos);
    }

    [Fact]
    public void Senha_so_vem_da_variavel_e_campo_de_senha_no_arquivo_e_ignorado()
    {
        using var pasta = new PastaTemporaria();
        string arquivo = Gravar(pasta, """{ "Email": { "SmtpSenha": "nao-usar" }, "Pastas": { "Banco": "C:\\b" } }""");

        using (new Ambiente())
        {
            Configuracao cfg = Configuracao.Ler(new[] { "--config", arquivo });
            Assert.Null(cfg.SmtpSenha);
            Assert.Contains(cfg.Avisos, a => a.Contains("Email.SmtpSenha"));
            Assert.Empty(cfg.Erros);
        }

        using (new Ambiente(("COMPRASGOV_SMTP_SENHA", "da-variavel")))
            Assert.Equal("da-variavel", Configuracao.Ler(new[] { "--config", arquivo }).SmtpSenha);
    }

    [Fact]
    public void Sem_pasta_do_banco_o_ciclo_nao_roda()
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Configuracao cfg = Configuracao.Ler(new[] { "--config", Gravar(pasta, "{}") });
        Assert.Empty(cfg.Erros);

        cfg.ValidarParaCiclo();
        Assert.Contains(cfg.Erros, e => e.Contains("Pasta do banco"));
    }

    [Fact]
    public void Historico_por_argumento_dispensa_a_pasta_do_banco()
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Configuracao cfg = Configuracao.Ler(new[] { "--config", Gravar(pasta, "{}"), "--historico", @"C:\x\h.json" });
        cfg.ValidarParaCiclo();
        Assert.Empty(cfg.Erros);
        Assert.Equal(@"C:\x\h.json", cfg.ArquivoHistorico);
    }

    [Theory]
    [InlineData("{ \"Email\": ")]                                         // JSON quebrado
    [InlineData("{ \"Email\": { \"SmtpPorta\": 70000 } }")]                // porta fora da faixa
    [InlineData("{ \"Apis\": { \"Arquivos\": \"https://x/{0}\" } }")]       // modelo sem {1} e {2}
    [InlineData("{ \"Execucao\": { \"IntervaloMinutos\": 0 } }")]
    public void Configuracao_invalida_gera_erro(string json)
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Assert.NotEmpty(Configuracao.Ler(new[] { "--config", Gravar(pasta, json) }).Erros);
    }

    [Fact]
    public void Arquivo_inexistente_gera_erro()
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Configuracao cfg = Configuracao.Ler(new[] { "--config", pasta.Arquivo("nao-existe.json") });
        Assert.Contains(cfg.Erros, e => e.Contains("não encontrado"));
    }

    [Fact]
    public void Variavel_numerica_invalida_gera_erro()
    {
        using var amb = new Ambiente(("COMPRASGOV_SMTP_PORTA", "abc"));
        using var pasta = new PastaTemporaria();
        Assert.Contains(Configuracao.Ler(new[] { "--config", Gravar(pasta, "{}") }).Erros,
            e => e.Contains("COMPRASGOV_SMTP_PORTA"));
    }

    [Fact]
    public void Uasg_manual_desliga_a_busca_geral()
    {
        using var amb = new Ambiente();
        using var pasta = new PastaTemporaria();
        Configuracao cfg = Configuracao.Ler(new[] { "--config", Gravar(pasta, "{}"), "--uasg", "90014", "--so-busca-geral" });
        Assert.True(cfg.ExecutarTarefaUasg);
        Assert.False(cfg.ExecutarBuscaGeral);
    }

    [Fact]
    public void Exemplo_versionado_e_valido()
    {
        using var amb = new Ambiente();
        string exemplo = Path.Combine(RaizDoRepositorio(), "deploy", "appsettings.exemplo.json");
        Configuracao cfg = Configuracao.Ler(new[] { "--config", exemplo });
        cfg.ValidarParaCiclo();
        Assert.Empty(cfg.Erros);
        Assert.Empty(cfg.Avisos);
    }

    internal static string RaizDoRepositorio()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy"))) return d.FullName;
        throw new DirectoryNotFoundException("Raiz do repositório não encontrada.");
    }
}
