using System.Diagnostics;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM;
using Cvolo.Projects;
using Cvolo.Strategies;
using Cvolo.Syntax;

namespace Cvolo.Drivers;

/// <summary>
/// Manages the full systems compilation pipeline, translating front-end Cvolo ASTs into optimized native machine binaries.
/// </summary>
internal sealed class CompilerDriver : ICompilerDriver
{
	private static readonly string[] _linkerCandidates = ["clang", "gcc", "g++"];

	public int Compile(string path, bool llvmOnly, bool isShared, bool emitIr, string optLevel, bool checkOnly = false, bool runAfterCompile = false, bool verbose = false)
	{
		// 1. Load the project configuration (automatically walks up directory tree to locate standard libraries)
		CompilationProject project;
		try
		{
			project = CompilationProject.Load(path, AppContext.BaseDirectory, isShared);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 1;
		}

		// 2. Instrument compiled files list only under verbose logging rules
		if (verbose && !checkOnly)
		{
			Console.WriteLine("Files selected for compilation:");
			foreach (var file in project.SourceFiles)
			{
				Console.WriteLine($"  -> {file}");
			}

			Console.WriteLine();
		}

		// 3. Syntactic parsing pass
		var binder = new Binder();
		var asts = new List<CompilationUnitSyntax>();
		var parser = new SyntaxParser();
		CompilationContext? firstContext = null;

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			firstContext ??= context;

			var ast = parser.Parse(context);
			if (parser.Diagnostics.HasErrors)
			{
				foreach (var diag in parser.Diagnostics.Diagnostics)
				{
					var lines = diag.Context.FormatDiagnostic("Parse Error", diag.Message, diag.Span);
					foreach (var line in lines)
					{
						Console.Error.WriteLine(line);
					}
				}

				return 1;
			}

			asts.Add(ast!);
			binder.Context.FileContexts[ast!] = context;
		}

		// 4. Semantic analysis passes (Name resolution, types, moves, borrows, and lifetimes validation)
		binder.Bind(asts);

		if (binder.Diagnostics.HasErrors)
		{
			foreach (var diag in binder.Diagnostics.Diagnostics)
			{
				var lines = diag.Context.FormatDiagnostic("Analysis Error", diag.Message, diag.Span);
				foreach (var line in lines)
				{
					Console.Error.WriteLine(line);
				}
			}

			return 1;
		}

		// 5. Short-circuit immediately if in rapid syntax/semantic check mode
		if (checkOnly)
		{
			Console.WriteLine("Check completed. Code is semantically correct.");
			return 0;
		}

		// Enforce executable entry-point rules
		if (!project.IsShared && binder.Context.Globals.Lookup("main") is null && binder.Context.Globals.Lookup("Main") is null)
		{
			Console.Error.WriteLine("Error CS5001: Program does not contain a static 'main' method suitable for an entry point");
			return 1;
		}

		// Setup modern C# /bin and /obj folder layouts
		var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(project.SourceFiles[0]))!;
		var objDirectory = Path.Combine(outputDirectory, "obj", "Debug");
		var binDirectory = Path.Combine(outputDirectory, "bin", "Debug");

		Directory.CreateDirectory(objDirectory);
		Directory.CreateDirectory(binDirectory);

		var llPath = Path.Combine(objDirectory, project.OutputName + ".ll");

		// Resolve optimization level flag
		if (!Enum.TryParse<OptimizationLevel>(optLevel, true, out var parsedLevel))
		{
			parsedLevel = OptimizationLevel.Os; // Fallback to size profile
		}

		// 6. Programmatic LLVM Code Generation pass (Triggers optimization passes internally)
		var optimizer = new IrOptimizer(parsedLevel);
		IEmitter emitter = new CodeGenerator("cvolo_module", optimizer);
		var ir = emitter.Emit(asts, firstContext!, binder.Context);

		File.WriteAllText(llPath, ir);

		if (emitIr)
		{
			Console.WriteLine(ir);
		}

		// 7. Resolve target linker path
		string? linkerPath = null;
		string? linkerName = null;
		var localClangName = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		var localClangPath = Path.Combine(AppContext.BaseDirectory, localClangName);

		if (File.Exists(localClangPath))
		{
			linkerPath = localClangPath;
			linkerName = "bundled-clang";
		}
		else
		{
			foreach (var candidate in _linkerCandidates)
			{
				var pathLinker = FindTool(candidate);
				if (pathLinker is not null)
				{
					linkerPath = pathLinker;
					linkerName = candidate;
					break;
				}
			}
		}

		if (verbose && linkerPath is not null)
		{
			Console.WriteLine($"Linking using: {linkerName}...");
		}

		// 8. Execute Strategy selection (IR only or full target linkage)
		ICompilationStrategy strategy = llvmOnly
			? new IrOnlyStrategy()
			: new LinkStrategy(binDirectory);

		var linkResult = strategy.Execute(llPath, project, linkerPath, linkerName, verbose);
		if (linkResult != 0)
		{
			return linkResult;
		}

		// 9. Execute immediate runtime execution if requested
		if (runAfterCompile)
		{
			var binaryExt = project.IsShared
				? (OperatingSystem.IsWindows() ? ".dll" : ".so")
				: (OperatingSystem.IsWindows() ? ".exe" : "");
			var binaryPath = Path.Combine(binDirectory, project.OutputName + binaryExt);

			if (verbose)
			{
				Console.WriteLine($"Running: {binaryPath}... \n\n");
			}

			var runResult = Process.Start(new ProcessStartInfo
			{
				FileName = binaryPath,
				UseShellExecute = false,
				CreateNoWindow = false
			});
			runResult?.WaitForExit();
			return runResult?.ExitCode ?? 0;
		}

		return 0;
	}

	private static string? FindTool(string name)
	{
		var which = OperatingSystem.IsWindows() ? "where" : "which";
		try
		{
			var proc = Process.Start(new ProcessStartInfo
			{
				FileName = which,
				Arguments = name,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			});
			if (proc is null)
			{
				return null;
			}

			var output = proc.StandardOutput.ReadToEnd().Trim();
			proc.WaitForExit();
			return proc.ExitCode == 0 && output.Length > 0 ? output : null;
		}
		catch
		{
			return null;
		}
	}
}
