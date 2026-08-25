namespace Cvolo.Core.Diagnostics;

public sealed class CompilationContext(string source, string filePath)
{
	public string Source { get; } = source;
	public string FilePath { get; } = filePath;

	public (int Line, int Col) GetCoordinates(int position)
	{
		int line = 1, col = 1;
		for (var i = 0; i < position && i < Source.Length; i++)
		{
			if (Source[i] == '\n') { line++; col = 1; }
			else col++;
		}

		return (line, col);
	}

	public string GetSourceLine(int lineIndex)
	{
		var lines = Source.Split('\n');
		return lineIndex >= 1 && lineIndex <= lines.Length ? lines[lineIndex - 1].TrimEnd('\r') : "";
	}

	public List<string> FormatDiagnostic(string header, string message, TextSpan span, bool prependNewLine = false, bool appendNewLine = true, bool compact = false)
	{
		var (lineNum, colNum) = GetCoordinates(span.Start);

		// ANSI Color Codes mapping to standard C# ConsoleColors
		var red = "\x1b[31m";       // ConsoleColor.Red
		var gray = "\x1b[90m";      // ConsoleColor.DarkGray
		var reset = "\x1b[0m";      // Console.ResetColor()

		if (compact)
		{
			var yellow = "\x1b[33m";  // ConsoleColor.Yellow

			return
			[
				..(prependNewLine ? [""] : Array.Empty<string>()),
				$"{yellow}[{header}]:{reset}",
				$"{gray}  1. [Line {lineNum}, Col {colNum}]: {message}{reset}",
				..(appendNewLine ? [""] : Array.Empty<string>()),
			];
		}

		var originalLine = GetSourceLine(lineNum);
		var lineText = originalLine.TrimStart();
		var trimmedCount = originalLine.Length - lineText.Length;

		var darkCyan = "\x1b[36m";  // ConsoleColor.DarkCyan
		var cyan = "\x1b[96m";      // ConsoleColor.Cyan
		var blue = "\x1b[94m";      // ConsoleColor.Blue
		var white = "\x1b[37m";     // ConsoleColor.White

		var coordStr = $"({lineNum},{colNum})";
		var paddedCoordStr = coordStr.PadRight(16); // Aligns the file paths nicely

		return
		[
			..(prependNewLine ? [""] : Array.Empty<string>()),
			$"{red}[{header}]:{reset}",
			$"{gray}  1. [Line {lineNum}, Col {colNum}]: {white}{message}{reset}",
			"",
			$"{gray}     |-- error: {message}{reset}",
			$"{gray}     |   {white}{lineText}{reset}",
			$"{gray}     |   {red}{new string(' ', Math.Max(0, colNum - trimmedCount - 1))}{new string('^', span.Length)}{reset}",
			"",
			$"{cyan}  Source files for errors:{reset}",
			$"{gray}    [1] {darkCyan}{paddedCoordStr} {blue}{FilePath}{reset}",
			..(appendNewLine ? [""] : Array.Empty<string>()),
		];
	}
}
