using ClosedXML.Excel;
using ComprasGOV.Models;

namespace ComprasGOV.Services;

/// <summary>
/// Leitura da planilha de órgãos: aba "Órgãos Estaduais ES", cabeçalho com a coluna
/// "Código UASG". O arquivo é copiado para uma pasta temporária antes da leitura para
/// funcionar mesmo com a planilha aberta no Excel.
/// </summary>
public sealed class PlanilhaService
{
    public IReadOnlyList<ClienteUasg> LerClientes(string caminhoPlanilha)
    {
        if (!File.Exists(caminhoPlanilha))
            throw new FileNotFoundException($"Planilha não encontrada: {caminhoPlanilha}");

        string copia = CopiarParaTemporario(caminhoPlanilha);

        using var wb = new XLWorkbook(copia);
        IXLWorksheet ws = wb.Worksheets.FirstOrDefault(w => TextoUtil.Normalizar(w.Name).Contains("orgaos"))
                          ?? wb.Worksheets.First();

        // Localiza a linha de cabeçalho com "Código UASG".
        int linhaCabecalho = 0, colCodigo = 0, colOrgao = 0;
        int limiteCabecalho = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 1, 15);
        foreach (var row in ws.Rows(1, limiteCabecalho))
        {
            foreach (IXLCell cell in row.CellsUsed())
            {
                if (TextoUtil.Normalizar(cell.GetString()).Equals("codigo uasg"))
                {
                    linhaCabecalho = row.RowNumber();
                    colCodigo = cell.Address.ColumnNumber;
                    colOrgao = Math.Max(1, colCodigo - 1);
                    break;
                }
            }
            if (linhaCabecalho > 0) break;
        }

        if (linhaCabecalho == 0)
            throw new InvalidOperationException($"Coluna \"Código UASG\" não encontrada na planilha ({ws.Name}).");

        // Confirma a coluna do órgão pelo cabeçalho (ex.: "Órgão Estadual - ES").
        foreach (IXLCell cell in ws.Row(linhaCabecalho).CellsUsed())
        {
            string texto = TextoUtil.Normalizar(cell.GetString());
            if (texto.Contains("orgao"))
            {
                colOrgao = cell.Address.ColumnNumber;
                break;
            }
        }

        var clientes = new List<ClienteUasg>();
        int ultimaLinha = ws.LastRowUsed()?.RowNumber() ?? linhaCabecalho;

        for (int l = linhaCabecalho + 1; l <= ultimaLinha; l++)
        {
            string codigo = ExtrairCodigo(ws.Cell(l, colCodigo));
            if (string.IsNullOrEmpty(codigo)) continue; // órgãos sem código informado na planilha

            string orgao = TextoUtil.LimparEspacos(ws.Cell(l, colOrgao).GetString());
            clientes.Add(new ClienteUasg(codigo, string.IsNullOrEmpty(orgao) ? $"UASG {codigo}" : orgao));
        }

        if (clientes.Count == 0)
            throw new InvalidOperationException("Nenhum código UASG encontrado na planilha.");

        return clientes;
    }

    private static string ExtrairCodigo(IXLCell cell)
    {
        string valor = cell.DataType == XLDataType.Number
            ? ((long)cell.GetDouble()).ToString()
            : cell.GetString();

        valor = valor.Trim();
        return valor.All(char.IsDigit) ? valor : "";
    }

    private static string CopiarParaTemporario(string origem)
    {
        string destino = Path.Combine(Path.GetTempPath(), "ComprasGOV", "planilha_uasg.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        for (int tentativa = 1; tentativa <= 3; tentativa++)
        {
            try
            {
                File.Copy(origem, destino, overwrite: true);
                return destino;
            }
            catch (IOException) when (tentativa < 3)
            {
                Thread.Sleep(500);
            }
        }

        return destino;
    }
}
