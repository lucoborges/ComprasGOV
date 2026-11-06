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

/// <summary>Tarefa que encontrou a licitação.</summary>
public static class OrigemLicitacao
{
    /// <summary>Tarefa específica: UASGs da planilha de clientes.</summary>
    public const string Uasg = "uasg";

    /// <summary>Tarefa geral: busca por UF e fontes/plataformas (filtros do site do PNCP).</summary>
    public const string Geral = "geral";
}

/// <summary>Registro persistido em JSON para não reprocessar a mesma licitação.</summary>
public sealed class LicitacaoHistorico
{
    /// <summary>
    /// Chave única: número de controle da compra no PNCP (ex.: 00394452000103-1-023561/2026).
    /// Registros antigos usavam "&lt;UASG&gt;-&lt;número&gt;" (ex.: 90014-67/2026) e são migrados
    /// na carga do histórico; o fluxo do portal (--fonte portal) ainda grava nesse formato.
    /// </summary>
    public string Chave { get; set; } = "";

    /// <summary>Código da unidade compradora (no Compras.gov.br, a UASG).</summary>
    public string Uasg { get; set; } = "";
    public string UasgNome { get; set; } = "";

    /// <summary>Ver <see cref="OrigemLicitacao"/> ("" em registros antigos: tarefa por UASG).</summary>
    public string Origem { get; set; } = "";

    /// <summary>Fonte/plataforma que publicou a compra no PNCP (ex.: "Compras.gov.br").</summary>
    public string Plataforma { get; set; } = "";

    public string Titulo { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Objeto { get; set; } = "";
    public string PrazoProposta { get; set; } = "";

    /// <summary>Ver <see cref="StatusLicitacao"/>.</summary>
    public string Status { get; set; } = "";

    public string Motivo { get; set; } = "";
    public string UrlCompras { get; set; } = "";
    public string UrlPncp { get; set; } = "";

    /// <summary>Caminho(s) do(s) arquivo(s) baixado(s), um por linha.</summary>
    public string Arquivo { get; set; } = "";

    /// <summary>
    /// Estado do e-mail desta licitação:
    /// null = registro antigo (não gerenciado por e-mail, nunca será enviado);
    /// false = baixado e PENDENTE de envio (será enviado/reenviado numa próxima execução);
    /// true = e-mail enviado com sucesso.
    /// </summary>
    public bool? EmailEnviado { get; set; }

    public DateTime DataProcessamento { get; set; } = DateTime.Now;
}
