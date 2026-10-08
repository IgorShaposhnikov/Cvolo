namespace Cvolo.Core.Diagnostics;

public static class AnsiConsoleStyle
{
	public const string Reset = "\x1b[0m";
	public const string Gray = "\x1b[90m";
	public const string Cyan = "\x1b[96m";
	public const string Blue = "\x1b[94m";
	public const string Green = "\x1b[32m";
	public const string White = "\x1b[37m";
	public const string Yellow = "\x1b[33m";

	public static string Header(string text) => Cyan + text + Reset;
	public static string Value(string text) => White + text + Reset;
	public static string Path(string text) => Gray + text + Reset;
	public static string Success(string text) => Green + text + Reset;
	public static string Muted(string text) => Gray + text + Reset;
}
