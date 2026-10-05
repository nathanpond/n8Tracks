using System.Text;

namespace n8Tracks.Api.Cli;

/// <summary>
/// What a command run in the container reads and writes: standard input, standard error (every
/// prompt and message goes there; standard output stays empty), and whether input is a terminal.
/// </summary>
/// <param name="input">Standard input.</param>
/// <param name="error">Standard error.</param>
/// <param name="isTerminal">Whether standard input is a terminal someone types at.</param>
internal class CommandConsole(TextReader input, TextWriter error, bool isTerminal)
{
    public TextReader Input { get; } = input;

    public TextWriter Error { get; } = error;

    public bool IsTerminal { get; } = isTerminal;

    /// <summary>
    /// One line typed without being shown, without its line ending; null at the end of input or on
    /// Ctrl-C. This one reads a line of <see cref="Input"/>; the process console hides the typing.
    /// </summary>
    public virtual string? ReadSecretLine() => Input.ReadLine();
}

/// <summary>The process's own console.</summary>
internal sealed class SystemCommandConsole : CommandConsole
{
    private SystemCommandConsole()
        : base(Console.In, Console.Error, !Console.IsInputRedirected)
    {
    }

    public static SystemCommandConsole Instance { get; } = new();

    /// <summary>
    /// Reads key by key with nothing echoed. Ctrl-C is read as a key here instead of ending the
    /// process, so it ends the prompt like the end of input does (Ctrl-D on an empty line).
    /// </summary>
    public override string? ReadSecretLine()
    {
        var treatControlCAsInput = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        var text = new StringBuilder();

        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;

                if (key.Key == ConsoleKey.Enter)
                {
                    Error.WriteLine();
                    return text.ToString();
                }

                if ((control && key.Key == ConsoleKey.C) || (control && key.Key == ConsoleKey.D && text.Length == 0))
                {
                    Error.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    RemoveLastCharacter(text);
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    text.Append(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = treatControlCAsInput;
        }
    }

    /// <summary>Removes the last character typed, both halves of it when it is a surrogate pair.</summary>
    private static void RemoveLastCharacter(StringBuilder text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var remove = text.Length >= 2 && char.IsSurrogatePair(text[^2], text[^1]) ? 2 : 1;
        text.Remove(text.Length - remove, remove);
    }
}
