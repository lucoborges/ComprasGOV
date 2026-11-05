namespace ComprasGOV.Models;

/// <summary>Cliente (órgão) lido da planilha de "Código UASG".</summary>
public sealed record ClienteUasg(string Codigo, string Orgao)
{
    /// <summary>
    /// A máscara do campo "Unidade compradora" aceita 5 ou 6 dígitos. Códigos de 5 dígitos
    /// (ex.: 90014) são enviados com o "slot" vazio ("90014_") e rejeitados pela API,
    /// por isso o código é sempre preenchido com zero à esquerda até 6 posições.
    /// </summary>
    public string CodigoMascara => Codigo.Length >= 6 ? Codigo : Codigo.PadLeft(6, '0');

    public override string ToString() => $"{Codigo} - {Orgao}";
}
