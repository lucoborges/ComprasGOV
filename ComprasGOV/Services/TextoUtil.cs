using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ComprasGOV.Services;

/// <summary>Utilitários de texto (normalização para comparações sem acento/caixa).</summary>
public static class TextoUtil
{
    /// <summary>Remove acentos, converte para minúsculas e normaliza espaços.</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";

        string semAcento = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(semAcento.Length);
        foreach (char c in semAcento)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return Regex.Replace(sb.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
    }

    /// <summary>Colapsa espaços/quebras de linha duplicados.</summary>
    public static string LimparEspacos(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? "" : Regex.Replace(texto.Trim(), @"\s+", " ");
}
