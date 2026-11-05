namespace ComprasGOV.Models;

/// <summary>Licitação localizada na lista de resultados do Compras.gov.br (ainda não persistida).</summary>
public sealed record LicitacaoEncontrada(string Titulo, string Numero, string Chave, string Prazo, string Objeto);
