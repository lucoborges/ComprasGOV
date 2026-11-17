using ComprasGOV.Models;
using ComprasGOV.Services;

namespace ComprasGOV.Tests;

/// <summary>Regras do fluxo principal: classificação do objeto, histórico e lotes de e-mail.</summary>
public class FluxoTests
{
    [Theory]
    [InlineData("Registro de preço para aquisição de material de expediente", Classificacao.Valido)]
    [InlineData("AQUISIÇÃO de computadores", Classificacao.Valido)]
    [InlineData("Contratação de empresa para prestação de serviços de limpeza", Classificacao.Invalido)]
    [InlineData("Locação de veículos", Classificacao.Invalido)]
    [InlineData("Aquisição de medicamentos", Classificacao.Invalido)]
    [InlineData("Fornecimento de produtos farmacêuticos", Classificacao.Invalido)]
    [InlineData("Aquisição de produtos químicos", Classificacao.Invalido)]
    [InlineData("Obra de pavimentação", Classificacao.Indefinido)]
    [InlineData("", Classificacao.Indefinido)]
    public void Classifica_o_objeto(string objeto, Classificacao esperado)
    {
        Assert.Equal(esperado, ClassificadorObjeto.Classificar(objeto));
    }

    private static LicitacaoHistorico Registro(string chave, string status, bool? email = null) => new()
    {
        Chave = chave,
        Uasg = "90014",
        Numero = "67/2026",
        Status = status,
        Arquivo = status == StatusLicitacao.Baixado ? @"C:\editais\a.pdf" : "",
        EmailEnviado = email,
    };

    [Fact]
    public void Historico_persiste_e_so_reprocessa_erro_e_sem_edital()
    {
        using var pasta = new PastaTemporaria();
        string arquivo = pasta.Arquivo("hist.json");
        const string baixada = "00394460000141-1-000067/2026";
        const string comErro = "00394460000141-1-000068/2026";
        const string semEdital = "00394460000141-1-000069/2026";

        using (var h = new HistoricoService(arquivo))
        {
            h.Salvar(Registro(baixada, StatusLicitacao.Baixado, email: false));
            h.Salvar(Registro(comErro, StatusLicitacao.Erro));
            h.Salvar(Registro(semEdital, StatusLicitacao.SemEdital));
        }

        using var relido = new HistoricoService(arquivo);
        Assert.Equal(3, relido.Quantidade);
        Assert.True(relido.JaProcessada(baixada));
        Assert.False(relido.JaProcessada(comErro));
        Assert.False(relido.JaProcessada(semEdital));
        Assert.False(relido.JaProcessada("00394460000141-1-000070/2026"));
    }

    [Fact]
    public void Pendentes_de_email_saem_apos_marcar_como_enviado()
    {
        using var pasta = new PastaTemporaria();
        const string chave = "00394460000141-1-000067/2026";
        using var h = new HistoricoService(pasta.Arquivo("hist.json"));
        h.Salvar(Registro(chave, StatusLicitacao.Baixado, email: false));
        h.Salvar(Registro("00394460000141-1-000071/2026", StatusLicitacao.Baixado, email: null)); // registro antigo

        Assert.Single(h.PendentesDeEmail());
        h.MarcarEmailEnviado(chave);
        Assert.Empty(h.PendentesDeEmail());
    }

    [Fact]
    public void Segunda_instancia_no_mesmo_historico_e_recusada()
    {
        // A trava usa o compartilhamento de arquivos do Windows (onde o robô roda).
        if (!OperatingSystem.IsWindows()) return;
        using var pasta = new PastaTemporaria();
        string arquivo = pasta.Arquivo("hist.json");
        using var primeira = new HistoricoService(arquivo);
        Assert.Throws<InvalidOperationException>(() => new HistoricoService(arquivo));
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(4, new[] { 4 })]
    [InlineData(5, new[] { 3, 2 })]
    [InlineData(9, new[] { 3, 3, 3 })]
    public void Lotes_de_email_tem_de_2_a_4_licitacoes(int quantidade, int[] esperado)
    {
        List<List<int>> lotes = EmailService.SepararEmLotes(Enumerable.Range(1, quantidade).ToList());
        Assert.Equal(esperado, lotes.Select(l => l.Count));
        Assert.Equal(Enumerable.Range(1, quantidade), lotes.SelectMany(l => l));
    }

    [Fact]
    public void Lote_acima_do_limite_de_tamanho_e_dividido()
    {
        var tamanhos = new long[] { 8, 8, 8, 30 };
        List<List<long>> partes = EmailService.DividirPorTamanho(tamanhos, t => t, limiteBytes: 20);
        Assert.Equal(new[] { 2, 1, 1 }, partes.Select(p => p.Count));
        Assert.Equal(new long[] { 30 }, partes[^1]); // sozinho acima do limite: mensagem só dele
    }

    [Fact]
    public void Corpo_do_email_lista_os_arquivos_quando_vai_sem_anexo()
    {
        var itens = new[]
        {
            new ItemEmail("k1", "90014", "PREGÃO N° 67/2026", "Aquisição de papel", new[] { @"C:\e\a.pdf" }, "90014-67/2026"),
            new ItemEmail("k2", "90015", "PREGÃO N° 1/2026", "Aquisição de toner", new[] { @"C:\e\grande.zip" }, "90015-1/2026"),
        };
        string corpo = EmailService.MontarCorpo(itens, new HashSet<string> { "k2" });
        Assert.Contains("Objeto: Aquisição de papel", corpo);
        Assert.Contains(@"C:\e\grande.zip", corpo);
        Assert.DoesNotContain(@"C:\e\a.pdf", corpo);
    }
}
