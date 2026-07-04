using Cvolo;
using Cvolo.Analysis;
using Cvolo.Core;
using Cvolo.Emitter.LLVM;
using Cvolo.Syntax;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: cvolo <file.cv_or_directory> [options]");
	Console.Error.WriteLine("  Options:");
	Console.Error.WriteLine("    --llvm         generate .ll LLVM IR only (no linking)");
	Console.Error.WriteLine("    --shared       build a shared library (.dll/.so)");
	Console.Error.WriteLine("    --emit-ir      print IR to stdout");
	Console.Error.WriteLine("    --emit-native  use native LLVM library (requires libLLVM)");
	return 1;
}

var inputPath = args[0];
var emitIr = args.Contains("--emit-ir");
var useNative = args.Contains("--emit-native");
var emitLlvmOnly = args.Contains("--llvm");
var isShared = args.Contains("--shared");

// CLI Tooling: Handle "new" project templates generation
if (inputPath == "new")
{
	if (args.Length < 2)
	{
		Console.Error.WriteLine("Usage: cvolo new <project_name>");
		return 1;
	}

	try
	{
		CompilationProject.CreateNewProject(args[1]);
		return 0;
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}
}

// 1. Load the project configuration (handles Single-File, Folder, or .cvlproj)
CompilationProject project;
try
{
	project = CompilationProject.Load(inputPath, isShared);
}
catch (Exception ex)
{
	Console.Error.WriteLine($"Error: {ex.Message}");
	return 1;
}

// PRINT DIAGNOSTIC FILES LIST
Console.WriteLine("Files selected for compilation:");
foreach (var file in project.SourceFiles)
{
	Console.WriteLine($"  -> {file}");
}
Console.WriteLine();

// 2. Parse all files individually
var asts = new List<CompilationUnitSyntax>();
var parser = new SyntaxParser();
CompilationContext? firstContext = null;

foreach (var file in project.SourceFiles)
{
	var sourceCode = File.ReadAllText(file);
	var context = new CompilationContext(sourceCode, file);
	firstContext ??= context;

	var ast = parser.Parse(sourceCode);
	if (parser.Diagnostics.HasErrors)
	{
		foreach (var diag in parser.Diagnostics.Diagnostics)
		{
			var lines = context.FormatDiagnostic("Parse Error", diag.Message, diag.Span);
			foreach (var line in lines) Console.Error.WriteLine(line);
		}
		return 1;
	}
	asts.Add(ast!);
}

// 3. Bind all files inside a single global analysis session
var binder = new Binder();
binder.Bind(asts);

if (binder.Diagnostics.HasErrors)
{
	// Bind errors belong to specific files, so we use their contexts
	foreach (var diag in binder.Diagnostics.Diagnostics)
	{
		var lines = firstContext!.FormatDiagnostic("Analysis Error", diag.Message, diag.Span);
		foreach (var line in lines) Console.Error.WriteLine(line);
	}
	return 1;
}

// Calculate output directories (C# Style: /bin and /obj)
var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(project.SourceFiles[0]))!;
var objDirectory = Path.Combine(outputDirectory, "obj", "Debug");
var binDirectory = Path.Combine(outputDirectory, "bin", "Debug");

Directory.CreateDirectory(objDirectory);
Directory.CreateDirectory(binDirectory);

// Write .ll IR file to the obj directory
var llPath = Path.Combine(objDirectory, project.OutputName + ".ll");

// 4. Emit unified LLVM IR
var emitter = new IrEmitter();
var ir = emitter.Emit(asts, firstContext!);

File.WriteAllText(llPath, ir);

if (emitIr)
	Console.WriteLine(ir);

if (emitLlvmOnly)
{
	Console.WriteLine($"Generated: {llPath}");
	return 0;
}

// 5. Try to link with clang
string? linkerPath = null;
string? linkerName = null;
string[] linkerCandidates = ["clang", "gcc", "g++"];

foreach (var candidate in linkerCandidates)
{
	var path = FindTool(candidate);
	if (path is not null)
	{
		linkerPath = path;
		linkerName = candidate;
		break;
	}
}

if (linkerPath is not null)
{
	var binaryExt = project.IsShared
		? (OperatingSystem.IsWindows() ? ".dll" : ".so")
		: (OperatingSystem.IsWindows() ? ".exe" : "");
	var binaryPath = Path.Combine(outputDirectory, project.OutputName + binaryExt);

	var typeFlag = project.IsShared ? " -shared" : "";

	// Subsystem flag is only needed/supported when using Clang on Windows
	var subsystemFlag = linkerName == "clang" && OperatingSystem.IsWindows() && !project.IsShared
		? " -Xlinker /subsystem:console"
		: "";

	Console.WriteLine($"Linking using: {linkerName}...");
	var linkResult = System.Diagnostics.Process.Start(linkerPath, $"-o {binaryPath} {llPath}{typeFlag}{subsystemFlag}");
	linkResult?.WaitForExit();

	if (linkResult?.ExitCode == 0)
	{
		Console.WriteLine($"Built: {binaryPath}");
		return 0;
	}

	Console.Error.WriteLine($"Linking failed (exit code {linkResult?.ExitCode})");
	return 1;
}

Console.Error.WriteLine("Error: no compatible linker found (clang, gcc, or g++). Install LLVM or GCC tools to compile.");
return 1;

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
