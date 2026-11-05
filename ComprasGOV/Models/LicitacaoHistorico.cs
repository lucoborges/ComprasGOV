namespace ComprasGOV.Models;

/// <summary>Status possíveis de uma licitação no histórico (JSON).</summary>
public static class StatusLicitacao
{
    /// <summary>Edital baixado com sucesso na pasta de saída.</summary>
    public const string Baixado = "baixado";

    /// <summary>Licitação válida, mas o PNCP não possui arquivo com tipo "Edital".</summary>
    public const string SemEdital = "sem-edital";

    /// <summary>Objeto inválido (serviço, locação, produtos químicos) - não baixado.</summary>
    public const string Rejeitado = "rejeitado";

    /// <summary>Objeto sem termos que permitam classificar como válido ou inválido.</summary>
    public const string Indefinido = "indefinido";

    /// <summary>Falha de execução - será tentado novamente na próxima execução.</summary>
    public const string Erro = "erro";
}

/// <summary>Registro persistido em JSON para não reprocessar a mesma licitação.</summary>
public sealed class LicitacaoHistorico
{
    /// <summary>Chave única: "&lt;UASG&gt;-&lt;número&gt;" (ex.: 90014-67/2026).</summary>
    public string Chave { get; set; } = "";

    public string Uasg { get; set; } = "";
    public string UasgNome { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Objeto { get; set; } = "";
    public string PrazoProposta { get; set; } = "";

    /// <summary>Ver <see cref="StatusLicitacao"/>.</summary>
    public string Status { get; set; } = "";

    public string Motivo { get; set; } = "";
    public string UrlCompras { get; set; } = "";
    public string UrlPncp { get; set; } = "";
    public string Arquivo { get; set; } = "";
    public DateTime DataProcessamento { get; set; } = DateTime.Now;
}
