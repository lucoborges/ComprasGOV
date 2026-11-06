namespace ComprasGOV.Services;

public enum Classificacao
{
    Valido,
    Invalido,
    Indefinido
}

/// <summary>
/// Regra de validade do objeto da licitação (conforme HTML.md):
/// INVÁLIDO: prestação de serviço, locação, produtos químicos ou fornecimento de medicamentos.
/// VÁLIDO: registro de preço, aquisição ou fornecimento de produtos.
/// Termos inválidos vencem: se houver qualquer termo inválido, a licitação é rejeitada.
/// </summary>
public static class ClassificadorObjeto
{
    private static readonly string[] TermosInvalidos =
    {
        "servico",      // prestação de serviço / contratação de serviços
        "locacao",      // locação
        "quimico",      // produtos químicos
        "medicament",   // medicamento(s)
        "medicac",      // medicação / medicações
        "farmaco",      // fármaco(s)
        "farmaceutic",  // produtos/insumos farmacêuticos
        "remedio"       // remédio(s)
    };

    private static readonly string[] TermosValidos =
    {
        "registro de preco",
        "aquisicao",
        "fornecimento",
        "compra de"
    };

    public static Classificacao Classificar(string objeto)
    {
        string texto = TextoUtil.Normalizar(objeto);
        if (string.IsNullOrEmpty(texto)) return Classificacao.Indefinido;

        if (TermosInvalidos.Any(texto.Contains)) return Classificacao.Invalido;
        if (TermosValidos.Any(texto.Contains)) return Classificacao.Valido;

        return Classificacao.Indefinido;
    }

    public static string Descrever(Classificacao classificacao) => classificacao switch
    {
        Classificacao.Valido => "VÁLIDO",
        Classificacao.Invalido => "INVÁLIDO",
        _ => "INDEFINIDO"
    };
}
