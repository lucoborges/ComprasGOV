using System.Text;
using ComprasGOV.Services;

namespace ComprasGOV.Tests;

public class LogTests
{
    [Fact]
    public void Grava_em_utf8_e_oculta_a_senha()
    {
        using var pasta = new PastaTemporaria();
        try
        {
            Log.Configurar(pasta.Caminho, 10, 30, 200, console: false);
            Log.Ocultar("SenhaSecreta123");
            Log.Info("Licitação com acentuação; falha de login com SenhaSecreta123 no texto");
            string arquivo = Log.ArquivoAtual!;
            Log.Encerrar();

            byte[] bytes = File.ReadAllBytes(arquivo);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF, "sem BOM");
            string texto = Encoding.UTF8.GetString(bytes);
            Assert.Contains("Licitação com acentuação", texto);
            Assert.DoesNotContain("SenhaSecreta123", texto);
            Assert.Contains("***", texto);
            Assert.Matches(@"comprasgov-\d{8}\.log$", arquivo);
        }
        finally { Log.Encerrar(); }
    }

    [Fact]
    public void Rotaciona_por_tamanho()
    {
        using var pasta = new PastaTemporaria();
        try
        {
            // 0,001 MB vira o mínimo de 1 KB por arquivo.
            Log.Configurar(pasta.Caminho, 0.001, 30, 200, console: false);
            for (int i = 0; i < 60; i++) Log.Info($"linha {i} " + new string('x', 60));
            Log.Encerrar();

            string[] arquivos = Directory.GetFiles(pasta.Caminho, "comprasgov-*.log");
            Assert.True(arquivos.Length >= 3, $"esperava 3+ arquivos, veio {arquivos.Length}");
            Assert.Contains(arquivos, a => a.EndsWith("-2.log"));
            Assert.All(arquivos, a => Assert.True(new FileInfo(a).Length < 1024 + 200));
        }
        finally { Log.Encerrar(); }
    }

    [Fact]
    public void Apaga_antigos_pela_retencao_e_preserva_outros_arquivos()
    {
        using var pasta = new PastaTemporaria();
        try
        {
            string velho = pasta.Arquivo("comprasgov-20200101.log");
            string recente = pasta.Arquivo($"comprasgov-{DateTime.Today.AddDays(-1):yyyyMMdd}.log");
            string outro = pasta.Arquivo("outro.log");
            foreach (string f in new[] { velho, recente, outro }) File.WriteAllText(f, "x");
            File.SetLastWriteTime(velho, new DateTime(2020, 1, 2));
            File.SetLastWriteTime(outro, new DateTime(2020, 1, 2));
            File.SetLastWriteTime(recente, DateTime.Now.AddDays(-1));

            Log.Configurar(pasta.Caminho, 10, 7, 200, console: false);
            Log.Encerrar();

            Assert.False(File.Exists(velho));
            Assert.True(File.Exists(recente));
            Assert.True(File.Exists(outro));
        }
        finally { Log.Encerrar(); }
    }

    [Fact]
    public void Respeita_o_tamanho_total_apagando_os_mais_antigos()
    {
        using var pasta = new PastaTemporaria();
        try
        {
            // 3 arquivos de 2 KB recentes; total permitido = 2 KB (mínimo = tamanho por arquivo).
            for (int d = 1; d <= 3; d++)
            {
                string f = pasta.Arquivo($"comprasgov-{DateTime.Today.AddDays(-d):yyyyMMdd}.log");
                File.WriteAllText(f, new string('x', 2048));
                File.SetLastWriteTime(f, DateTime.Now.AddDays(-d));
            }

            Log.Configurar(pasta.Caminho, 0.002, 30, 0.002, console: false);
            Log.Encerrar();

            string[] restantes = Directory.GetFiles(pasta.Caminho, "comprasgov-*.log")
                .Where(f => !f.EndsWith($"{DateTime.Today:yyyyMMdd}.log")).ToArray();
            Assert.Single(restantes);
            Assert.EndsWith($"{DateTime.Today.AddDays(-1):yyyyMMdd}.log", restantes[0]);
        }
        finally { Log.Encerrar(); }
    }
}
