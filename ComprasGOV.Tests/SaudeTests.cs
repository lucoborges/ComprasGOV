using System.Text.Json;
using ComprasGOV.Services;

namespace ComprasGOV.Tests;

public class SaudeTests
{
    private static readonly DateTime Agora = new(2026, 11, 17, 12, 0, 0);

    /// <summary>Estado de outra máquina: a avaliação não consulta o PID local.</summary>
    private static EstadoRobo Estado(string situacao) => new()
    {
        Versao = "abc1234",
        Pid = 1,
        Maquina = "OUTRA-MAQUINA-TESTE",
        InicioProcesso = Agora.AddHours(-5),
        AtualizadoEm = Agora.AddSeconds(-30),
        Situacao = situacao,
        Continuo = true,
        IntervaloMinutos = 60,
        CicloMaximoMinutos = 180,
        HistoricoCarregado = true,
        RegistrosHistorico = 10,
        Ciclo = 3,
        InicioUltimoCiclo = Agora.AddMinutes(-20),
        FimUltimoCiclo = Agora.AddMinutes(-18),
        ResultadoUltimoCiclo = "ok",
        ProximoCiclo = Agora.AddMinutes(40),
    };

    [Fact]
    public void Inativo_e_em_dia_esta_saudavel()
    {
        Assert.True(Saude.Avaliar(Estado(SituacaoRobo.Inativo), Agora).EmDia);
    }

    [Fact]
    public void Ciclo_com_falhas_continua_saudavel_mas_avisa()
    {
        EstadoRobo e = Estado(SituacaoRobo.Inativo);
        e.ResultadoUltimoCiclo = "falhas";
        (bool emDia, List<string> msgs) = Saude.Avaliar(e, Agora);
        Assert.True(emDia);
        Assert.Contains(msgs, m => m.StartsWith("Aviso"));
    }

    [Fact]
    public void Sem_atualizacao_recente_nao_esta_saudavel()
    {
        EstadoRobo e = Estado(SituacaoRobo.Inativo);
        e.AtualizadoEm = Agora.AddMinutes(-6);
        Assert.False(Saude.Avaliar(e, Agora).EmDia);
    }

    [Fact]
    public void Proximo_ciclo_atrasado_nao_esta_saudavel()
    {
        EstadoRobo e = Estado(SituacaoRobo.Inativo);
        e.ProximoCiclo = Agora.AddMinutes(-16);
        Assert.False(Saude.Avaliar(e, Agora).EmDia);
    }

    [Fact]
    public void Ciclo_longo_demais_nao_esta_saudavel()
    {
        EstadoRobo e = Estado(SituacaoRobo.EmCiclo);
        e.InicioUltimoCiclo = Agora.AddMinutes(-181);
        Assert.False(Saude.Avaliar(e, Agora).EmDia);

        e.InicioUltimoCiclo = Agora.AddMinutes(-30);
        Assert.True(Saude.Avaliar(e, Agora).EmDia);
    }

    [Theory]
    [InlineData(SituacaoRobo.Adiado)]
    [InlineData(SituacaoRobo.Encerrado)]
    public void Adiado_ou_encerrado_nao_esta_saudavel(string situacao)
    {
        Assert.False(Saude.Avaliar(Estado(situacao), Agora).EmDia);
    }

    [Fact]
    public void Processo_inexistente_na_mesma_maquina_nao_esta_saudavel()
    {
        EstadoRobo e = Estado(SituacaoRobo.Inativo);
        e.Maquina = Environment.MachineName;
        e.Pid = int.MaxValue;
        Assert.False(Saude.Avaliar(e, Agora).EmDia);
    }

    [Fact]
    public void Verificar_devolve_0_ou_1_conforme_o_arquivo()
    {
        using var pasta = new PastaTemporaria();
        Assert.Equal(1, Saude.Verificar(pasta.Arquivo("nao-existe.json")));

        File.WriteAllText(pasta.Arquivo("quebrado.json"), "{ nao e json");
        Assert.Equal(1, Saude.Verificar(pasta.Arquivo("quebrado.json")));

        // Estado gravado pelo próprio serviço, com este processo vivo: em dia.
        string arquivo = pasta.Arquivo("estado.json");
        using (var servico = new EstadoService(arquivo, "teste", continuo: true, intervaloMinutos: 60, cicloMaximoMinutos: 180))
        {
            servico.Atualizar(e =>
            {
                e.Situacao = SituacaoRobo.Inativo;
                e.HistoricoCarregado = true;
                e.ProximoCiclo = DateTime.Now.AddMinutes(60);
            });
            Assert.Equal(0, Saude.Verificar(arquivo));

            servico.Atualizar(e => e.Situacao = SituacaoRobo.Encerrado);
            Assert.Equal(1, Saude.Verificar(arquivo));
        }
    }

    [Fact]
    public void Arquivo_de_estado_usa_os_nomes_que_o_deploy_le()
    {
        // O deploy.ps1 lê estes campos pelo nome: mudar a serialização quebraria o deploy.
        string json = JsonSerializer.Serialize(Estado(SituacaoRobo.Inativo), EstadoService.Json);
        foreach (string campo in new[] { "\"versao\"", "\"pid\"", "\"situacao\"", "\"historicoCarregado\"",
                     "\"registrosHistorico\"", "\"resultadoUltimoCiclo\"" })
            Assert.Contains(campo, json);
    }
}
