using Cvolo.Analysis;
using Cvolo.Emitter.LLVM;
using Cvolo.Syntax;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: cvolo <file.cv> [options]");
	Console.Error.WriteLine("  Options:");
	Console.Error.WriteLine("    --llvm         generate .ll LLVM IR only (no linking)");
	Console.Error.WriteLine("    --shared       build a shared library (.dll/.so)");
	Console.Error.WriteLine("    --emit-ir      print IR to stdout");
	Console.Error.WriteLine("    --emit-native  use native LLVM library (requires libLLVM)");
	return 1;
}

var filePath = args[0];
var emitIr = args.Contains("--emit-ir");
var useNative = args.Contains("--emit-native");
var emitLlvmOnly = args.Contains("--llvm");
var isShared = args.Contains("--shared");

if (!File.Exists(filePath))
{
	Console.Error.WriteLine($"Error: file '{filePath}' not found");
	return 1;
}

var sourceCode = File.ReadAllText(filePath);

var parser = new SyntaxParser();
var ast = parser.Parse(sourceCode);

if (parser.Diagnostics.HasErrors)
{
	PrintCleanDiagnostics("Parse errors:", parser.Diagnostics.Diagnostics, filePath, sourceCode);
	return 1;
}

var binder = new Binder();
binder.Bind(ast!);

if (binder.Diagnostics.HasErrors)
{
	PrintCleanDiagnostics("Analysis errors:", binder.Diagnostics.Diagnostics, filePath, sourceCode);
	return 1;
}

var llPath = Path.ChangeExtension(filePath, ".ll");

if (useNative)
{
	var codeGen = new CodeGenerator("cvolo_module");
	codeGen.Emit(ast!);

	if (emitIr)
		Console.WriteLine(codeGen.PrintIR());

	var outputPath = Path.ChangeExtension(filePath, ".o");
	codeGen.WriteObjectFile(outputPath);

	if (emitLlvmOnly)
	{
		if (emitIr)
			Console.WriteLine(codeGen.PrintIR());
		Console.WriteLine($"Generated: {outputPath}");
		return 0;
	}

	var binaryExt = isShared
		? (OperatingSystem.IsWindows() ? ".dll" : ".so")
		: (OperatingSystem.IsWindows() ? ".exe" : "");
	var binaryPath = Path.ChangeExtension(filePath, null) + binaryExt;

	var typeFlag = isShared ? " -shared" : "";
	var linkerArgs = $"-o {binaryPath} {outputPath}{typeFlag}";
	var linkResult = System.Diagnostics.Process.Start("clang", linkerArgs);
	linkResult?.WaitForExit();

	if (linkResult?.ExitCode != 0)
	{
		Console.Error.WriteLine($"Linking failed with exit code {linkResult?.ExitCode}");
		return 1;
	}

	Console.WriteLine($"Built: {binaryPath}");
}
else
{
	var emitter = new IrEmitter();
	var ir = emitter.Emit(ast!);

	File.WriteAllText(llPath, ir);

	if (emitIr)
		Console.WriteLine(ir);

	if (emitLlvmOnly)
	{
		Console.WriteLine($"Generated: {llPath}");
		return 0;
	}

	// Try to link with clang
	var clangPath = FindTool("clang");
	if (clangPath is not null)
	{
		var binaryExt = isShared
			? (OperatingSystem.IsWindows() ? ".dll" : ".so")
			: (OperatingSystem.IsWindows() ? ".exe" : "");
		var binaryPath = Path.ChangeExtension(filePath, null) + binaryExt;

		var typeFlag = isShared ? " -shared" : "";
		var subsystemFlag = OperatingSystem.IsWindows() && !isShared ? " -Xlinker /subsystem:console" : "";
		var linkResult = System.Diagnostics.Process.Start(clangPath, $"-o {binaryPath} {llPath}{typeFlag}{subsystemFlag}");
		linkResult?.WaitForExit();

		if (linkResult?.ExitCode == 0)
		{
			Console.WriteLine($"Built: {binaryPath}");
			return 0;
		}

		Console.Error.WriteLine($"Linking failed (exit code {linkResult?.ExitCode})");
		return 1;
	}

	Console.Error.WriteLine("Error: clang not found. Install LLVM tools to compile.");
	return 1;
}

return 0;

static string? FindTool(string name)
{
	var which = OperatingSystem.IsWindows() ? "where" : "which";
	try
	{
		var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
		{
			FileName = which,
			Arguments = name,
			RedirectStandardOutput = true,
		});
		if (proc is null) return null;
		var output = proc.StandardOutput.ReadToEnd().Trim();
		proc.WaitForExit();
		return proc.ExitCode == 0 && output.Length > 0 ? output : null;
	}
	catch
	{
		return null;
	}
}

static void PrintCleanDiagnostics(string header, IReadOnlyList<Cvolo.Core.Diagnostic> diagnostics, string filePath, string sourceCode)
{
	var originalColor = Console.ForegroundColor;

	// 1. Print section header in Red (Vibrant and clear)
	Console.ForegroundColor = ConsoleColor.Red;
	Console.Error.WriteLine($"[{header}]");
	Console.ResetColor();
	Console.Error.WriteLine();

	var lines = sourceCode.Split('\n');
	var coordinates = new List<(int Line, int Col)>();

	for (var i = 0; i < diagnostics.Count; i++)
	{
		var diag = diagnostics[i];
		
		// 1. Calculate line and column offsets
		var lineStart = 0;
		var lineNum = 1;
		for (var j = 0; j < sourceCode.Length && j < diag.Span.Start; j++)
		{
			if (sourceCode[j] == '\n')
			{
				lineStart = j + 1;
				lineNum++;
			}
		}

		var colNum = diag.Span.Start - lineStart + 1;
		coordinates.Add((lineNum, colNum)); // Save for the file list at the bottom

		// 2. Print C#-style header (Coordinate in Gray, Message in White, on a single line)
		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.Error.Write($"  {i + 1}. [Line {lineNum}, Col {colNum}]: ");
		Console.ForegroundColor = ConsoleColor.White;
		Console.Error.WriteLine(diag.Message);
		Console.ResetColor();

		// 3. Print the source code context with connected error lines
		if (lineNum - 1 >= 0 && lineNum - 1 < lines.Length)
		{
			Console.Error.WriteLine(); // Let it breathe

			var originalLineText = lines[lineNum - 1].TrimEnd('\r');
			var lineText = originalLineText.TrimStart(); // Apply your TrimStart() optimization
			
			// Calculate exactly how many leading characters were stripped
			var trimmedCount = originalLineText.Length - lineText.Length;

			// AUTOMATIC COMMENT STRIPPING: Cleanly strip any trailing comments (//) from the printed line
			var commentIndex = lineText.IndexOf("//");
			if (commentIndex >= 0)
			{
				lineText = lineText.Substring(0, commentIndex).TrimStart().TrimEnd();
			}
			
			// Fixed-width, mathematically matched prefixes (Arrow '->' completely removed)
			var errorPrefix = "     |-- error: "; // 17 characters (5 spaces, '|', '-- error: ')
			var codePrefix  = "     |   ";         // 9 characters  (5 spaces, '|', 3 spaces)
			var caretPrefix = "     |   ";         // 9 characters  (Matches codePrefix exactly)

			// Calculate the new visual column offset after stripping leading whitespace
			var visualColNum = colNum - trimmedCount;
			var indent = new string(' ', Math.Max(0, visualColNum - 1));

			// A. Print the vertical connector and the error message ABOVE the code line (Entirely in DarkGray)
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Error.Write(errorPrefix);
			Console.Error.WriteLine(diag.Message);
			Console.ResetColor();

			// B. Print the code line (Aligned perfectly on index 17, no arrow)
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Error.Write(codePrefix);
			Console.ResetColor();
			Console.Error.WriteLine(lineText);

			// C. Print caret pointer and bar
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Error.Write(caretPrefix);
			
			// Highlight the error caret in Red
			Console.ForegroundColor = ConsoleColor.Red;
			var underline = new string('^', Math.Max(1, diag.Span.Length));
			Console.Error.WriteLine($"{indent}{underline}");
			Console.ResetColor();
		}

		Console.Error.WriteLine();
	}

	// 5. Print the source file references at the bottom (with a 4-space margin matching the headers)
	Console.ForegroundColor = ConsoleColor.Cyan;
	Console.Error.WriteLine("  Source files for errors:");
	Console.ResetColor();

	for (var i = 0; i < diagnostics.Count; i++)
	{
		var (line, col) = coordinates[i];
		var coordStr = $"({line},{col})";

		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.Error.Write("    "); // 4 spaces of margin for items
		Console.Error.Write($"[{i + 1}] ");
		Console.ForegroundColor = ConsoleColor.DarkCyan;
		Console.Error.Write($"{coordStr,-16} "); // Padded to 16 characters to align all file paths perfectly!
		Console.ForegroundColor = ConsoleColor.Blue;
		Console.Error.WriteLine(filePath);
		Console.ResetColor();
	}

	Console.ForegroundColor = originalColor;
}
static void PrintWrappedMessage(string prefix, string message, ConsoleColor color)
{
	// Get the actual terminal width, defaulting to 100 if redirected
	var width = 100;
	try
	{
		if (Console.WindowWidth > 0)
			width = Console.WindowWidth;
	}
	catch { }

	var indent = new string(' ', prefix.Length);
	var words = message.Split(' ');

	// Print the coordinate prefix in Dark Gray
	Console.ForegroundColor = ConsoleColor.DarkGray;
	Console.Error.Write(prefix);

	// Print the message in White
	Console.ForegroundColor = color;

	var currentLength = prefix.Length;
	var isFirstWordOnLine = true;

	foreach (var word in words)
	{
		// If the word exceeds the terminal width, wrap to the next line and indent
		if (currentLength + word.Length + 1 >= width)
		{
			Console.Error.WriteLine();
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.Error.Write(indent); // Maintain vertical alignment
			Console.ForegroundColor = color;

			Console.Error.Write(word);
			currentLength = indent.Length + word.Length;
			isFirstWordOnLine = true;
		}
		else
		{
			if (!isFirstWordOnLine)
			{
				Console.Error.Write(" ");
				currentLength++;
			}
			Console.Error.Write(word);
			currentLength += word.Length;
			isFirstWordOnLine = false;
		}
	}
	Console.Error.WriteLine();
	Console.ResetColor();
}
