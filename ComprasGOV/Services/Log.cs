namespace ComprasGOV.Services;

/// <summary>Log simples no console com carimbo de data/hora.</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Secao(string mensagem) => Escrever(ConsoleColor.Cyan, "==", new string('=', 70) + Environment.NewLine + mensagem + Environment.NewLine + new string('=', 70));
    public static void Info(string mensagem) => Escrever(ConsoleColor.Gray, "--", mensagem);
    public static void Ok(string mensagem) => Escrever(ConsoleColor.Green, "OK", mensagem);
    public static void Aviso(string mensagem) => Escrever(ConsoleColor.Yellow, "!!", mensagem);
    public static void Erro(string mensagem) => Escrever(ConsoleColor.Red, "XX", mensagem);

    private static void Escrever(ConsoleColor cor, string icone, string mensagem)
    {
        lock (Lock)
        {
            ConsoleColor anterior = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = cor;
                Console.WriteLine($"[{DateTime.Now:dd/MM HH:mm:ss}] {icone} {mensagem}");
            }
            finally
            {
                Console.ForegroundColor = anterior;
            }
        }
    }
}
