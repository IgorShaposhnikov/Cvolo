using System.Text;
using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM;
using Cvolo.Syntax;
using Cvolo.Syntax.Antlr;
using Cvolo.Syntax.Rewriters;

namespace Cvolo.Packaging;

/// <summary>
/// The result of an in-process pack compile: the LLVM IR text, the project-only source
/// list (for the Sector 5 source buffer), and a scratch workspace for clang output.
/// </summary>
public sealed class PackCompileResult : IDisposable
{
	private readonly Dictionary<string, byte[]> _bitcodeByTarget = new(StringComparer.Ordinal);

	internal PackCompileResult(string llIr, string llFilePath, IReadOnlyList<string> sourceFiles, IReadOnlyList<CompilationUnitSyntax> projectUnits, string workingDirectory)
	{
		LlIr = llIr;
		LlFilePath = llFilePath;
		SourceFiles = sourceFiles;
		ProjectUnits = projectUnits;
		WorkingDirectory = workingDirectory;
	}

	/// <summary>The emitted LLVM IR as text.</summary>
	public string LlIr { get; }

	/// <summary>Absolute path of the .ll file in the scratch workspace.</summary>
	public string LlFilePath { get; }

	/// <summary>Project .cvl files (sorted by relative path) shipped in Sector 5 unless stripped.</summary>
	public IReadOnlyList<string> SourceFiles { get; }

	/// <summary>Lowered project compilation units used to derive language-level package API metadata.</summary>
	public IReadOnlyList<CompilationUnitSyntax> ProjectUnits { get; }

	/// <summary>The scratch workspace directory (cleaned up on Dispose).</summary>
	public string WorkingDirectory { get; }

	/// <summary>
	/// Compiles the emitted IR to a native object file for the given LLVM triple.
	/// </summary>
	public byte[] ProduceObjectFile(string targetTriple)
	{
		var clang = RequireClang();
		var output = Path.Combine(WorkingDirectory, $"module.{Sanitize(targetTriple)}.o");
		ProcessRunner.Run(clang, "clang", "-target", targetTriple, "-c", LlFilePath, "-o", output);
		return File.ReadAllBytes(output);
	}

	/// <summary>
	/// Compiles the emitted IR to target-specific bitcode and caches it per target triple.
	/// Requires clang (gcc cannot emit LLVM bitcode).
	/// </summary>
	public byte[] ProduceBitcode(string targetTriple)
	{
		if (_bitcodeByTarget.TryGetValue(targetTriple, out var cached))
			return cached;

		var clang = RequireClang();
		var output = Path.Combine(WorkingDirectory, $"module.{Sanitize(targetTriple)}.bc");
		ProcessRunner.Run(clang, "clang", "-target", targetTriple, "-emit-llvm", "-c", LlFilePath, "-o", output);
		var bitcode = File.ReadAllBytes(output);
		_bitcodeByTarget[targetTriple] = bitcode;
		return bitcode;
	}

	public void Dispose()
	{
		try
		{
			if (!string.IsNullOrEmpty(WorkingDirectory) && Directory.Exists(WorkingDirectory))
				Directory.Delete(WorkingDirectory, recursive: true);
		}
		catch
		{
			// Best-effort cleanup of the scratch workspace.
		}
	}

	private static string RequireClang()
	{
		return ClangTool.ResolvePath()
			?? throw new InvalidOperationException(
				"No clang compiler found. Install clang on PATH or place a bundled clang next to the compiler host.");
	}

	private static string Sanitize(string targetTriple) => targetTriple.Replace(':', '_').Replace('/', '_');
}

/// <summary>
/// Runs the Cvolo front-end pipeline in-process (parse, rewrite, bind, emit IR) by mirroring
/// the CompilerDriver, then hands the IR to <see cref="PackCompileResult"/> for clang-backed
/// object/bitcode production.
/// </summary>
internal static class PackCompilation
{
	public static PackCompileResult Compile(ProjectManifest manifest, bool verbose = false, string configuration = BuildOutputLayout.DefaultConfiguration)
	{
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);

		// 1. Disambiguate sources: everything still compiles with the standard library,
		//    but only project files (non-stdlib) are shipped in the package source buffer.
		var stdlibFiles = FindStdlibFiles();
		var projectFiles = Directory.GetFiles(manifest.ProjectDirectory, "*.cvl", SearchOption.AllDirectories)
			.OrderBy(f => Path.GetRelativePath(manifest.ProjectDirectory, f), StringComparer.Ordinal)
			.ToList();

		if (projectFiles.Count == 0)
			throw new InvalidOperationException($"No .cvl source files found in '{manifest.ProjectDirectory}'.");

		var sourceFiles = stdlibFiles.Concat(projectFiles).ToList();
		if (verbose)
		{
			Console.WriteLine("Files selected for compilation:");
			foreach (var file in sourceFiles)
				Console.WriteLine($"  -> {file}");
			Console.WriteLine();
		}

		// 2. Parse.
		var binder = new Binder();
		var asts = new List<CompilationUnitSyntax>();
		var projectUnits = new HashSet<CompilationUnitSyntax>();
		var projectFileSet = projectFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
		ISyntaxParser parser = new AntlrSyntaxParser();
		CompilationContext? firstContext = null;

		foreach (var file in sourceFiles)
		{
			var artifactPath = SourcePathRemapper.Map(file, manifest.ProjectDirectory);
			var context = new CompilationContext(File.ReadAllText(file), artifactPath);
			firstContext ??= context;

			var ast = parser.Parse(context);
			if (parser.Diagnostics.HasErrors)
			{
				throw new PackageException(
					"PARSE",
					"Parse error.",
					string.Join(Environment.NewLine, FormatDiagnostics(parser.Diagnostics.Diagnostics)));
			}

			asts.Add(ast!);
			if (projectFileSet.Contains(file))
				projectUnits.Add(ast!);
			binder.Context.FileContexts[ast!] = context;
		}

		// ProjectReference libraries are consumed through the same language-level package
		// surface as installed .cvlib dependencies. Their implementations stay in their
		// own Sector 3 artifacts; this package emits only external declarations for them.
		if (ProjectGraph.TryLoad(manifest.ProjectPath, out var projectGraph)
			&& projectGraph is not null
			&& projectGraph.Nodes.Count > 1)
		{
			var projectReferenceArtifacts = ProjectReferenceArtifactLoader.Load(manifest.ProjectPath, configuration);
			foreach (var artifact in projectReferenceArtifacts)
			{
				foreach (var packageUnit in artifact.ApiMetadata.CreateCompilationUnits(artifact.PackageId, artifact.Version))
				{
					asts.Add(packageUnit);
					binder.Context.FileContexts[packageUnit] = packageUnit.Context;
					binder.Context.ExternalPackageUnits.Add(packageUnit);
				}

				foreach (var templateUnit in artifact.TemplateUnits)
				{
					asts.Add(templateUnit);
					binder.Context.FileContexts[templateUnit] = templateUnit.Context;
					binder.Context.PackageTemplateUnits.Add(templateUnit);
				}
			}
		}

		// 3. Cross-file declaration index + source-level lowering rewrites.
		var declarationIndex = DeclarationIndex.Build(asts);
		var effectiveStrictOption = manifest.StrictOption;
		var loweredAsts = new List<CompilationUnitSyntax>();
		try
		{
			foreach (var ast in asts)
			{
				if (binder.Context.ExternalPackageUnits.Contains(ast))
				{
					loweredAsts.Add(ast);
					continue;
				}

				var currentAst = ast;
				if (binder.Context.FileContexts.TryGetValue(ast, out var rewriteContext))
				{
					currentAst = (CompilationUnitSyntax)new TryCatchRewriter(binder.Diagnostics, rewriteContext, declarationIndex).Rewrite(currentAst);
					currentAst = (CompilationUnitSyntax)new DeferRewriter(binder.Diagnostics, rewriteContext).Rewrite(currentAst);
				}

				currentAst = (CompilationUnitSyntax)new StringInterpolationRewriter(parser).Rewrite(currentAst);

				if (binder.Context.FileContexts.TryGetValue(ast, out var optionalContext))
					currentAst = (CompilationUnitSyntax)new OptionalSyntaxRewriter(effectiveStrictOption, binder.Diagnostics, optionalContext).Rewrite(currentAst);

				if (binder.Context.FileContexts.TryGetValue(ast, out var fileContext))
				{
					binder.Context.FileContexts[currentAst] = fileContext;
					binder.Context.FileContexts.Remove(ast);
				}

				if (projectUnits.Remove(ast))
					projectUnits.Add(currentAst);
				if (binder.Context.PackageTemplateUnits.Remove(ast))
					binder.Context.PackageTemplateUnits.Add(currentAst);

				loweredAsts.Add(currentAst);
			}
		}
		catch (InvalidOperationException ex)
		{
			throw new PackageException("REWRITE", "Source-level lowering failed.", ex.Message);
		}

		asts = loweredAsts;

		// 4. Semantic analysis.
		binder.Context.LegacyVisibility = false;
		binder.Context.StrictOption = effectiveStrictOption;
		binder.Bind(asts);

		// Capture ownership while symbol DeclaringUnit still points at the post-rewrite,
		// pre-foreach units in projectUnits. The foreach lowering below replaces AST
		// instances, so classifying package globals after that point would lose provenance.
		var packageGlobalDefinitions = binder.Context.GlobalVariables
			.Where(entry => entry.Symbol.DeclaringUnit is not null && projectUnits.Contains(entry.Symbol.DeclaringUnit))
			.Select(entry => entry.Symbol.QualifiedGlobalName)
			.ToHashSet(StringComparer.Ordinal);

		if (binder.Diagnostics.HasErrors)
		{
			throw new PackageException(
				"ANALYSIS",
				"Analysis error.",
				string.Join(Environment.NewLine, FormatDiagnostics(binder.Diagnostics.Diagnostics)));
		}

		// 5. Foreach lowering (source-level) after binding.
		{
			var expandedAsts = new List<CompilationUnitSyntax>();
			var foreachLoweringRewriter = new ForeachLoweringRewriter();
			foreach (var ast in asts)
			{
				if (binder.Context.ExternalPackageUnits.Contains(ast))
				{
					expandedAsts.Add(ast);
					continue;
				}

				var expanded = (CompilationUnitSyntax)foreachLoweringRewriter.Rewrite(ast);
				if (binder.Context.FileContexts.TryGetValue(ast, out var expandedContext))
				{
					binder.Context.FileContexts[expanded] = expandedContext;
					binder.Context.FileContexts.Remove(ast);
				}

				if (projectUnits.Remove(ast))
					projectUnits.Add(expanded);
				if (binder.Context.PackageTemplateUnits.Remove(ast))
					binder.Context.PackageTemplateUnits.Add(expanded);

				expandedAsts.Add(expanded);
			}

			asts = expandedAsts;
		}

		// 6. Entry-point enforcement for executable packages.
		if (!manifest.IsLibrary
			&& binder.Context.Globals.Lookup("main") is null
			&& binder.Context.Globals.Lookup("Main") is null)
		{
			throw new PackageException(
				"ENTRYPOINT",
				"Program does not contain a static 'main' method suitable for an entry point.");
		}

		// 7. Emit LLVM IR for the package's own units only. The standard library is
		// present above for parsing/binding, but its definitions belong to the final
		// consumer compilation. Emitting stdlib bodies into every .cvlib makes the
		// linker see duplicate System.* symbols when package bitcode is combined with
		// an application that also compiles the standard library.
		var packagedProjectUnits = asts.Where(projectUnits.Contains).ToArray();
		if (packagedProjectUnits.Length == 0)
			throw new InvalidOperationException("No project compilation units remained after lowering.");

		var targetLayout = new TargetLayout();
		var optimizer = new IrOptimizer(targetLayout, OptimizationLevel.Os);
		var irVerifier = new IRVerifier(BuildOutputLayout.GetCompilationFailuresDirectory(manifest.ProjectDirectory, configuration));
		using var emitter = new CodeGenerator(
			"cvolo_module",
			targetLayout,
			optimizer,
			irVerifier,
			enableTbaa: true,
			checkedFfiBounds: false,
			definedGlobalNames: packageGlobalDefinitions);

		var emitContext = binder.Context.FileContexts.TryGetValue(packagedProjectUnits[0], out var projectContext)
			? projectContext
			: firstContext!;
		var ir = emitter.Emit(packagedProjectUnits, emitContext, binder.Context);

		// 8. Materialize the scratch workspace.
		var workspace = Directory.CreateTempSubdirectory("cvolopack-").FullName;
		var llPath = Path.Combine(workspace, "module.ll");
		File.WriteAllText(llPath, ir, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		return new PackCompileResult(ir, llPath, projectFiles, packagedProjectUnits, workspace);
	}

	private static IReadOnlyList<string> FindStdlibFiles()
	{
		var stdlib = CompilationProjectLibraries.FindStandardLibraryPath(AppContext.BaseDirectory)
			?? CompilationProjectLibraries.FindStandardLibraryPath(Directory.GetCurrentDirectory());
		if (stdlib is null)
			return [];

		return Directory.GetFiles(stdlib, "*.cv", SearchOption.AllDirectories)
			.Concat(Directory.GetFiles(stdlib, "*.cvl", SearchOption.AllDirectories))
			.ToList();
	}

	private static IEnumerable<string> FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
		{
			var id = string.IsNullOrEmpty(diagnostic.Id) ? string.Empty : $"[{diagnostic.Id}] ";
			yield return $"{id}{diagnostic.Message}";
		}
	}
}

/// <summary>
/// Replicates CompilationProject's standard-library discovery (walking up the tree) without
/// referencing the Cvolo executable project.
/// </summary>
internal static class CompilationProjectLibraries
{
	public static string? FindStandardLibraryPath(string startDir)
	{
		var dir = new DirectoryInfo(startDir);
		while (dir is not null)
		{
			var libPath = Path.Combine(dir.FullName, "libraries");
			if (Directory.Exists(libPath))
				return libPath;

			dir = dir.Parent;
		}

		return null;
	}
}
