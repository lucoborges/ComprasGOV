// Os testes mexem em variáveis de ambiente e no log estático: rodam em sequência.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ComprasGOV.Tests;

/// <summary>Pasta temporária apagada ao fim do teste.</summary>
public sealed class PastaTemporaria : IDisposable
{
    public string Caminho { get; } =
        Path.Combine(Path.GetTempPath(), "ComprasGOV.Tests", Guid.NewGuid().ToString("N"));

    public PastaTemporaria() => Directory.CreateDirectory(Caminho);

    public string Arquivo(string nome) => Path.Combine(Caminho, nome);

    public void Dispose()
    {
        try { Directory.Delete(Caminho, recursive: true); } catch { /* arquivo ainda aberto */ }
    }
}

/// <summary>Define variáveis COMPRASGOV_* só durante o teste (e limpa as que existirem).</summary>
public sealed class Ambiente : IDisposable
{
    private readonly Dictionary<string, string?> _originais = new();

    public Ambiente(params (string Nome, string? Valor)[] variaveis)
    {
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string nome = (string)e.Key;
            if (nome.StartsWith("COMPRASGOV_", StringComparison.OrdinalIgnoreCase)) Definir(nome, null);
        }
        foreach ((string nome, string? valor) in variaveis) Definir(nome, valor);
    }

    private void Definir(string nome, string? valor)
    {
        if (!_originais.ContainsKey(nome)) _originais[nome] = Environment.GetEnvironmentVariable(nome);
        Environment.SetEnvironmentVariable(nome, valor);
    }

    public void Dispose()
    {
        foreach ((string nome, string? valor) in _originais) Environment.SetEnvironmentVariable(nome, valor);
    }
}
