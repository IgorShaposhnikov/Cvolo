using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Cvolo.Analysis;
using Cvolo.Analysis.Passes.Validation;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Core.Diagnostics.Reporters;
using Cvolo.Emitter.LLVM;
using Cvolo.Packaging;
using Cvolo.Projects;
using Cvolo.Strategies;
using Cvolo.Syntax;
using Cvolo.Syntax.Antlr;
using Cvolo.Syntax.Rewriters;

namespace Cvolo.Drivers;

/// <summary>
/// Manages the full systems compilation pipeline, translating front-end Cvolo ASTs into optimized native machine binaries.
/// </summary>
internal sealed class CompilerDriver(PackageCache packageCache) : ICompilerDriver
{
	private static readonly string[] _linkerCandidates = ["clang", "gcc", "g++"];

	public int Compile(string path, bool llvmOnly, bool isShared, bool emitIr, string optLevel, bool checkOnly = false, bool runAfterCompile = false, bool verbose = false, bool emitLowered = false, string? noWarn = null, bool suppressWarnings = false, bool legacyVisibility = false, bool strictOption = false, bool noTbaa = false, string? targetOs = null, bool checkedFfiBounds = false, string format = "text", string configuration = BuildOutputLayout.DefaultConfiguration, bool useProjectReferencePackages = false, bool freestanding = false, bool debug = false)
	{
		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);

		// The driver itself is format-agnostic: it hands diagnostics to a
		// reporter and, for verbose chatter, asks whether the reporter owns
		// stdout exclusively. Concrete formats live in IDiagnosticReporter.
		var reporter = CreateReporter(format, suppressWarnings);

		// Diagnostics whose ids appear here are dropped from the warning stream entirely.
		var noWarnIds = noWarn?
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(id => id.ToUpperInvariant())
			.ToHashSet();

		// 1. Build the semantic project universe (project sources + standard library + merged
		// ProjectReference sources + package/.cvlib API units + compiler configuration). The same
		// loader is used by the language-server tooling, so both observe one authoritative universe.
		ProjectUniverse universe;
		try
		{
			universe = ProjectUniverseLoader.Load(new ProjectUniverseRequest(
				path,
				CompilerBaseDir: AppContext.BaseDirectory,
				ForceShared: isShared,
				MergeProjectReferences: !useProjectReferencePackages,
				UseProjectReferencePackages: useProjectReferencePackages,
				LoadPackages: !emitLowered,
				IncludeSystem: !freestanding,
				Freestanding: freestanding,
				Configuration: configuration,
				PackageCache: packageCache));
		}
		catch (PackageException ex)
		{
			reporter.ReportSynthetic(ex.Code, "error", ex.Message, path);
			return 1;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
		{
			reporter.ReportSynthetic(PackageDiagnosticIds.LockOutOfSync, "error", $"Failed to prepare project dependencies: {ex.Message}", path);
			return 1;
		}
		catch (Exception ex)
		{
			reporter.ReportSynthetic(null, "error", ex.Message, path);
			return 1;
		}

		var project = universe.Project;
		var packageArtifacts = universe.PackageArtifacts;

		if (project.IsFreestanding && project.RequiredSystemNamespaces.Count > 0)
		{
			foreach (var requiredNamespace in project.RequiredSystemNamespaces)
			{
				reporter.ReportSynthetic(
					DiagnosticIds.SystemInFreestanding,
					"error",
					$"Namespace '{requiredNamespace}' is not available in a freestanding build; the System library is disabled.",
					path);
			}

			return 1;
		}

		if (verbose && !reporter.Exclusive && packageArtifacts.Count > 0)
		{
			Console.WriteLine(AnsiConsoleStyle.Header("Package dependencies selected for build:"));
			foreach (var artifact in packageArtifacts)
			{
				var input = artifact.UsesSourceFallback
					? $"Sector 5 source fallback ({artifact.SourceFallbackUnits.Count} file(s))"
					: artifact.BitcodePath ?? "<no bitcode input>";
				Console.WriteLine($"  -> {AnsiConsoleStyle.Value($"{artifact.PackageId}@{artifact.Version}")}:");
				Console.WriteLine($"     {AnsiConsoleStyle.Path(input)}");
			}

			Console.WriteLine();
		}

		// 2. Instrument compiled files list only under verbose logging rules.
		// Machine reporters claim stdout, so verbose chatter is gated on !Exclusive.
		if (verbose && !checkOnly && !reporter.Exclusive)
		{
			Console.WriteLine(AnsiConsoleStyle.Header("Files selected for compilation:"));
			foreach (var file in project.SourceFiles)
			{
				Console.WriteLine($"  -> {AnsiConsoleStyle.Path(file)}");
			}

			Console.WriteLine();
		}

		// 3. Syntactic parsing pass
		var binder = new Binder();
		binder.Context.NativePointerBytes = IntPtr.Size;
		// Layout is target-parameterized, so the layout results this run produces (sizeof, alignof,
		// offsetof, emitted data layout) carry the target they were computed for.
		binder.Context.NativeTargetDisplay = TargetTriple.HostTriple();
		var asts = new List<CompilationUnitSyntax>();
		var sourceNativeLibraryUsages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		ISyntaxParser parser = new AntlrSyntaxParser();
		CompilationContext? firstContext = null;
		var baseSourceSet = new HashSet<string>(
			project.BaseSourceFiles.Select(Path.GetFullPath),
			OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		var systemSourceSet = new HashSet<string>(
			project.SystemSourceFiles.Select(Path.GetFullPath),
			OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var artifactPath = SourcePathRemapper.Map(file, project.ProjectDirectory, project.ProjectReferences);
			var context = new CompilationContext(sourceCode, artifactPath);
			var fullPath = Path.GetFullPath(file);
			context.Origin = baseSourceSet.Contains(fullPath)
				? SourceOrigin.BaseSdk
				: systemSourceSet.Contains(fullPath) ? SourceOrigin.SystemSdk : SourceOrigin.Project;
			firstContext ??= context;

			var ast = parser.Parse(context);
			if (parser.Diagnostics.HasErrors)
			{
				reporter.ReportErrors(parser.Diagnostics.Diagnostics, "Parse Error");
				return 1;
			}

			asts.Add(ast!);
			binder.Context.FileContexts[ast!] = context;
			CollectNativeLibraryUsages(ast!, context.FilePath, sourceNativeLibraryUsages);
		}

		// External package/artifact units contribute declarations to semantic analysis. They were
		// classified by the shared universe loader exactly as binding expects.
		foreach (var external in universe.ExternalUnits)
		{
			asts.Add(external.Unit);
			binder.Context.FileContexts[external.Unit] = external.Unit.Context;
			if (external.Kind == ExternalSemanticUnitKind.ExternalPackageApi)
			{
				binder.Context.ExternalPackageUnits.Add(external.Unit);
				if (external.DefaultabilityCertificates is { Count: > 0 } certificates)
				{
					foreach (var (typeName, cert) in certificates)
						binder.Context.ImportedDefaultCertificates[typeName] = new ImportedDefaultCertificate(cert.State, cert.Reason, cert.Fingerprint);
				}
			}
			else
				binder.Context.PackageTemplateUnits.Add(external.Unit);

			if (external.PackageId is { } externalPackageId && external.Unit.Context is { } externalContext)
				binder.Context.UnitPackageIds[externalContext] = externalPackageId;
		}

		// Cross-file declaration index feeding the try/catch lowering (function
		// Result error types, [Error] marks, declared type kinds).
		var declarationIndex = DeclarationIndex.Build(asts);
		var rewriters = new List<AstRewriterBase> {
			new StringInterpolationRewriter(parser)
		};

		var loweredAsts = new List<CompilationUnitSyntax>();
		var effectiveStrictOption = strictOption || project.StrictOption;
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
				if (binder.Context.FileContexts.TryGetValue(ast, out var deferContext))
				{
					var tryCatchRewriter = new TryCatchRewriter(binder.Diagnostics, deferContext, declarationIndex);
					currentAst = (CompilationUnitSyntax)tryCatchRewriter.Rewrite(currentAst);

					var deferRewriter = new DeferRewriter(binder.Diagnostics, deferContext);
					currentAst = (CompilationUnitSyntax)deferRewriter.Rewrite(currentAst);
				}

				foreach (var rewriter in rewriters)
				{
					currentAst = (CompilationUnitSyntax)rewriter.Rewrite(currentAst);
				}

				if (binder.Context.FileContexts.TryGetValue(ast, out var rewriterContext))
				{
					var optionalRewriter = new OptionalSyntaxRewriter(effectiveStrictOption, binder.Diagnostics, rewriterContext);
					currentAst = (CompilationUnitSyntax)optionalRewriter.Rewrite(currentAst);
				}

				if (binder.Context.FileContexts.TryGetValue(ast, out var fileContext))
				{
					binder.Context.FileContexts[currentAst] = fileContext;
					binder.Context.FileContexts.Remove(ast); // Clean up the old reference
				}

				if (binder.Context.PackageTemplateUnits.Remove(ast))
				{
					binder.Context.PackageTemplateUnits.Add(currentAst);
				}

				loweredAsts.Add(currentAst);
			}
		}
		catch (InvalidOperationException ex)
		{
			reporter.ReportSynthetic(null, "error", ex.Message, path);
			return 1;
		}

		asts = loweredAsts;

		// 4. Semantic analysis passes (Name resolution, types, moves, borrows, and lifetimes validation)
		binder.Context.LegacyVisibility = legacyVisibility;
		binder.Context.StrictOption = effectiveStrictOption;
		binder.Context.BaseSourcePaths.UnionWith(baseSourceSet);

		binder.Bind(asts);

		if (binder.Diagnostics.HasErrors)
		{
			// Preserve the original behaviour: the whole bag (errors + warnings)
			// goes through the error channel when there is at least one error.
			reporter.ReportErrors(binder.Diagnostics.Diagnostics, "Analysis Error");
			return 1;
		}

		// Warnings never fail compilation; they are printed and the pipeline continues.
		var warnings = binder.Diagnostics.Diagnostics
			.Where(d => d.Severity == DiagnosticSeverity.Warning)
			.ToList();
		reporter.ReportWarnings(warnings, noWarnIds);

		// 5. Short-circuit immediately if in rapid syntax/semantic check mode
		if (checkOnly)
		{
			reporter.ReportCheckSuccess();
			return 0;
		}

		// 6. Source-level foreach expansion (arrays/slices → cached-length index loops).
		// Runs after binding so the binder-stamped fields on ForEachStatementSyntax are
		// populated; enumerator-based foreach is left for the emitter to expand.
		var foreachLoweringRewriter = new ForeachLoweringRewriter(binary =>
			binder.Context.RegisterCompilerGeneratedBinaryExpression(binary, TypeSymbol.Int, TypeSymbol.Int));
		{
			var expandedAsts = new List<CompilationUnitSyntax>();
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

				expandedAsts.Add(expanded);
			}

			asts = expandedAsts;
		}

		if (emitLowered)
		{
			foreach (var ast in asts)
			{
				Console.WriteLine(CvoloSourcePrinter.Print(ast));
			}

			return 0; // Terminate successfully before codegen or linking (post-binding dump)
		}

		// Enforce executable entry-point rules
		if (!project.IsShared && !llvmOnly && !emitIr && !EntryPointDiscovery.HasEntryPoint(binder.Context, asts))
		{
			reporter.ReportSynthetic(
				DiagnosticIds.MissingEntryPoint,
				"error",
				"Program does not contain a static 'main' method suitable for an entry point",
				path);
			return 1;
		}

		// Keep final artifacts and compiler intermediates in the shared build layout.
		// Verification dumps are intermediates too, so they live below obj/<configuration> and
		// are removed by the same clean operation as generated LLVM IR.
		var objDirectory = BuildOutputLayout.GetObjDirectory(project.ProjectDirectory, configuration);
		var binDirectory = BuildOutputLayout.GetBinDirectory(project.ProjectDirectory, configuration);
		var compilationFailuresDirectory = BuildOutputLayout.GetCompilationFailuresDirectory(project.ProjectDirectory, configuration);

		Directory.CreateDirectory(objDirectory);
		Directory.CreateDirectory(binDirectory);

		var llPath = BuildOutputLayout.GetIntermediateIrPath(project.ProjectDirectory, project.OutputName, configuration);

		// Resolve optimization level flag
		if (!Enum.TryParse<OptimizationLevel>(optLevel, true, out var parsedLevel))
		{
			parsedLevel = OptimizationLevel.Os; // Fallback to size profile
		}

		// The compile unit is anchored on the first project source file rather than on whatever
		// source happened to be parsed first, which may be a Base or System SDK file. SDK code still
		// gets its own DIFile nodes, it just does not name the unit.
		string? primarySourcePath = null;
		foreach (var context in binder.Context.FileContexts.Values)
		{
			if (context.Origin is SourceOrigin.Project or SourceOrigin.Package)
			{
				primarySourcePath = context.FilePath;
				break;
			}
		}

		primarySourcePath ??= firstContext?.FilePath;

		var debugInfo = debug
			? new DebugInfoOptions
			{
				Enabled = true,
				Producer = BuildProducerString(),
				IsOptimized = parsedLevel != OptimizationLevel.O0,
				PrimarySourcePath = primarySourcePath
			}
			: null;

		// 6. Programmatic LLVM Code Generation pass (Triggers optimization passes internally)
		var targetLayout = new TargetLayout();
		var optimizer = new IrOptimizer(targetLayout, parsedLevel);
		var irVerifier = new IRVerifier(compilationFailuresDirectory);
		using var emitter = new CodeGenerator("cvolo_module", targetLayout, optimizer, irVerifier, enableTbaa: !noTbaa, checkedFfiBounds: checkedFfiBounds, debugInfo: debugInfo);
		var ir = emitter.Emit(asts, firstContext!, binder.Context);

		File.WriteAllText(llPath, ir);

		if (emitIr)
		{
			Console.WriteLine(ir);
			return 0;
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

		if (verbose && linkerPath is not null && !reporter.Exclusive)
		{
			Console.WriteLine($"{AnsiConsoleStyle.Header("Linking using:")} {AnsiConsoleStyle.Value(linkerName ?? "<unknown>")}...");
		}

		// 8. Execute Strategy selection (IR only or full target linkage)
		ICompilationStrategy strategy = llvmOnly
			? new IrOnlyStrategy()
			: new LinkStrategy(binDirectory);

		// A [LibraryImport("Foo")] matching an installed PackageId is satisfied by that
		// package's Sector 3 bitcode, so do not also forward -lFoo to the native linker.
		var packageIds = packageArtifacts
			.Select(a => a.PackageId)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		// Collect every logical native-library requirement with its origin: the application's own
		// plain-source [LibraryImport] declarations plus logical requirements published by packages.
		// Package artifacts are collected FIRST: the consumer binder re-registers rehydrated package
		// [LibraryImport] declarations in its own context, and the ordinal dedup below keeps the
		// first entry — so a package-origin entry must precede the identical binder-context entry
		// for diagnostics to report the originating package rather than "<application>".
		var requirements = new List<NativeRequirement>();

		foreach (var artifact in packageArtifacts)
		{
			foreach (var library in artifact.NativeLibraries)
			{
				if (!packageIds.Contains(library.LibraryName))
				{
					requirements.Add(new NativeRequirement(library, artifact.PackageId));
				}
			}
		}

		foreach (var library in binder.Context.NativeLibraries.Values)
		{
			if (!packageIds.Contains(library.LibraryName))
			{
				requirements.Add(new NativeRequirement(library, NativeLinkInput.ApplicationOrigin));
			}
		}

		// Logical names are opaque and ordinal: "Foo" and "foo" are distinct libraries, dash/dot
		// names are never coalesced. The originating package of the first requirement is kept for
		// diagnostics (a single app mapping may satisfy several packages).
		var distinctRequirements = requirements
			.GroupBy(r => r.Library.LibraryName, StringComparer.Ordinal)
			.Select(g => g.First())
			.ToList();

		// Application-level native mapping model: only project inputs load a .cvlproj manifest, and
		// the loader tolerates non-package applications (no <PackageId>). Bare .cvl files keep the
		// legacy source-level OS paths and bare system names unchanged.
		var nativeGroups = new List<NativeLibrariesGroup>();
		if (!path.EndsWith(".cvl", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var (groups, manifestWarnings) = ProjectManifest.LoadNativeLibraries(project.ProjectDirectory);
				nativeGroups.AddRange(groups);
				foreach (var (code, message) in manifestWarnings)
					reporter.ReportSynthetic(code, "warning", message, path);
			}
			catch (FileNotFoundException)
			{
				// No .cvlproj reachable from the input; treat it as a source bundle without mappings.
			}
			catch (PackageException ex)
			{
				// <NativeLibraries> validation errors (CVLP31xx) are dedicated project-configuration
				// diagnostics: report and fail before any linker interaction.
				reporter.ReportSynthetic(ex.Code, "error", ex.Message, path);
				return 1;
			}
		}

		var (nativeTargetLabel, requestedArch, effectiveTargetOs) = ResolveRequestedTarget(targetOs, targetLayout);

		var linkInputs = new List<NativeLinkInput>();
		var legacyCopyPaths = new List<string>();
		var runtimePlan = new List<RuntimeStageEntry>();
		var nativeConfigurationFailed = false;
		foreach (var requirement in distinctRequirements)
		{
			if (!ResolveNativeRequirement(
				requirement,
				nativeGroups,
				effectiveTargetOs,
				requestedArch,
				configuration,
				nativeTargetLabel,
				project,
				linkInputs,
				legacyCopyPaths,
				runtimePlan,
				reporter,
				path))
			{
				nativeConfigurationFailed = true;
			}
		}

		if (nativeConfigurationFailed)
		{
			return 1;
		}

		if (verbose && !reporter.Exclusive && linkInputs.Count > 0)
		{
			var nativeLibraryUsages = MergeNativeLibraryUsages(binder.Context.NativeLibraryUsages, sourceNativeLibraryUsages);
			PrintNativeLibrariesSelectedForBuild(linkInputs, nativeLibraryUsages, nativeTargetLabel);
		}

		var linkResult = strategy.Execute(
			llPath,
			project,
			linkerPath,
			linkerName,
			optLevel,
			verbose,
			linkInputs,
			targetOs,
			nativeTargetLabel,
			packageArtifacts.Where(a => a.BitcodePath is not null).Select(a => a.BitcodePath!),
			new DebugLinkOptions(debug, targetLayout.ResolveTriple(targetOs), targetOs));

		if (linkResult != 0)
		{
			return linkResult;
		}

		// 8b. Copy native libraries to the output directory so the binary can find them at runtime:
		// legacy source-level OS paths keep their historical copy behavior (including the Windows
		// .lib → *.dll directory scan); project-level mappings stage only their explicit RuntimeFiles.
		CopyLegacyNativeLibrariesToOutput(legacyCopyPaths, binDirectory, effectiveTargetOs, verbose);
		if (StageProjectRuntimeFiles(runtimePlan, project, binDirectory, reporter, path) != 0)
		{
			return 1;
		}

		// 9. Execute immediate runtime execution if requested
		if (runAfterCompile)
		{
			var binaryPath = BuildOutputLayout.GetNativeOutputPath(
				project.ProjectDirectory, project.OutputName, project.IsShared, configuration);

			if (verbose && !reporter.Exclusive)
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

	/// <summary>
	/// Builds the <c>DICompileUnit</c> producer string recorded in debug information, so a debugger
	/// can report which compiler produced a module.
	/// </summary>
	private static string BuildProducerString()
	{
		var version = System.Reflection.Assembly.GetEntryAssembly()?
			.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
			.OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
			.FirstOrDefault()?
			.InformationalVersion;

		return string.IsNullOrWhiteSpace(version)
			? "Cvolo"
			: $"Cvolo {version.Split('+')[0]}";
	}

	// The single point where a format string becomes a reporting strategy.
	// Adding a new format (e.g. a future JSON envelope for cvolo lsp) means
	// adding one implementation and one case here — the pipeline stays put.
	private static IDiagnosticReporter CreateReporter(string format, bool suppressWarnings)
	{
		return format.ToLowerInvariant() switch
		{
			"machine" => new MachineDiagnosticReporter(),
			"json" => new JsonDiagnosticReporter(),
			_ => new TextDiagnosticReporter(suppressWarnings)
		};
	}

	private static (ProjectManifest Manifest, LockFile LockFile)? ValidatePackageLock(string path, IDiagnosticReporter reporter, out bool valid)
	{
		valid = true;
		if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".cvl", StringComparison.OrdinalIgnoreCase))
			return null;

		var hasRefs = HasPackageReferences(path);
		ProjectManifest manifest;
		try
		{
			manifest = ProjectManifest.Load(path);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (PackageException ex) when ((ex.Code is PackageDiagnosticIds.MissingPackageId or PackageDiagnosticIds.MissingVersion) && !hasRefs)
		{
			return null;
		}
		catch (PackageException ex)
		{
			reporter.ReportSynthetic(PackageDiagnosticIds.LockOutOfSync, "error", ex.Message, path);
			valid = false;
			return null;
		}
		catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException)
		{
			reporter.ReportSynthetic(PackageDiagnosticIds.LockOutOfSync, "error", $"Cannot read project manifest: {ex.Message}", path);
			valid = false;
			return null;
		}

		if (manifest.Dependencies.Count == 0)
			return null;

		var lockPath = LockFile.GetPath(manifest);
		if (!File.Exists(lockPath))
		{
			ReportLockOutOfSync(reporter, path, "cvolo.lock.json is missing; run 'cvolo restore' or build/run without '--no-restore'.");
			valid = false;
			return null;
		}

		try
		{
			var lockFile = LockFile.Read(lockPath);
			if (!PackageLockValidator.Validate(manifest, lockFile, out var message))
			{
				ReportLockOutOfSync(reporter, path, $"{message} Run 'cvolo restore' or build/run without '--no-restore'.");
				valid = false;
				return null;
			}

			return (manifest, lockFile);
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or PackageException)
		{
			ReportLockOutOfSync(reporter, path, $"cvolo.lock.json is invalid: {ex.Message}");
			valid = false;
			return null;
		}
	}

	private static bool HasPackageReferences(string path)
	{
		var projectPath = Directory.Exists(path)
			? Directory.GetFiles(Path.GetFullPath(path), "*.cvlproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
			: string.Equals(Path.GetExtension(path), ".cvlproj", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null;
		if (projectPath is null || !File.Exists(projectPath))
			return false;

		try
		{
			return XDocument.Load(projectPath).Descendants("PackageReference").Any();
		}
		catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException)
		{
			return false;
		}
	}

	private static void ReportLockOutOfSync(IDiagnosticReporter reporter, string path, string message)
	{
		reporter.ReportSynthetic(PackageDiagnosticIds.LockOutOfSync, "error", message, path);
	}

	/// <summary>
	/// Resolves one logical native-library requirement into a lowered link input (explicit path or
	/// bare system name) plus its runtime staging entries, following the application-native-library
	/// resolution order: 1) unique matching application mapping; 2) legacy source-level OS path
	/// (application plain sources only); 3) bare system lookup. Returns false when a defensive
	/// ambiguity aborts the build.
	/// </summary>
	private bool ResolveNativeRequirement(
		NativeRequirement requirement,
		IReadOnlyList<NativeLibrariesGroup> groups,
		string effectiveTargetOs,
		string? requestedArch,
		string requestedConfiguration,
		string nativeTargetLabel,
		CompilationProject project,
		List<NativeLinkInput> inputs,
		List<string> legacyCopyPaths,
		List<RuntimeStageEntry> runtimePlan,
		IDiagnosticReporter reporter,
		string inputPath)
	{
		var library = requirement.Library;
		var forName = library.LibraryName;
		var target = effectiveTargetOs switch
		{
			"windows" => NativeLibraryTarget.Windows,
			"linux" => NativeLibraryTarget.Linux,
			_ => NativeLibraryTarget.MacOS
		};

		// 1. Application mapping — selected mapping is authoritative and never silently falls back.
		var selection = NativeLibrarySelector.Select(groups, forName, target, requestedArch, requestedConfiguration);
		if (selection.Kind == NativeLibraryMatchKind.Selected && selection.Declaration is not null)
		{
			var linkPath = Path.GetFullPath(selection.Declaration.Path, project.ProjectDirectory);
			inputs.Add(new NativeLinkInput(forName, linkPath, null, requirement.Origin));
			foreach (var runtimeFile in selection.Declaration.RuntimeFiles)
			{
				// CVL1707 must identify the failing runtime payload, not its link library.
				runtimePlan.Add(new RuntimeStageEntry(
					Path.GetFullPath(runtimeFile, project.ProjectDirectory),
					Path.GetFileName(runtimeFile),
					forName,
					runtimeFile));
			}

			return true;
		}

		if (selection.Kind == NativeLibraryMatchKind.Ambiguous)
		{
			// Load-time validation rejects ambiguity over declared selector combos; this defensive
			// branch covers a request outside the declared selector space and still fails loudly.
			reporter.ReportSynthetic(
				PackageDiagnosticIds.NativeLibrariesAmbiguous,
				"error",
				$"Native-library mappings for '{forName}' are ambiguous for {nativeTargetLabel}/{requestedConfiguration}: " +
				$"add an explicit mapping that dominates the conflicting alternatives.",
				inputPath);
			return false;
		}

		// 2. Legacy source-level OS path (application plain sources only). Package-published paths
		// are never forced on consumers; packages carry logical requirements only.
		string? legacyPath = null;
		string? legacySystemName = null;
		if (requirement.Origin == NativeLinkInput.ApplicationOrigin)
		{
			var targetPath = effectiveTargetOs switch
			{
				"windows" => library.WinPath,
				"linux" => library.LinuxPath,
				"macos" => library.MacPath,
				_ => null
			};

			if (!string.IsNullOrEmpty(targetPath))
			{
				if (targetPath.Contains('/') || targetPath.Contains('\\'))
				{
					legacyPath = Path.GetFullPath(targetPath, project.ProjectDirectory);
				}
				else
				{
					// System marker: a separator-less legacy value like "opengl32.lib" is a bare
					// system-library name; strip the extension for the -l flag.
					legacySystemName = Path.GetFileNameWithoutExtension(targetPath);
				}
			}
		}

		if (legacyPath is not null)
		{
			inputs.Add(new NativeLinkInput(forName, legacyPath, null, requirement.Origin));
			legacyCopyPaths.Add(legacyPath);
			return true;
		}

		// 3. Bare logical name via the system linker search path.
		var systemName = legacySystemName ?? forName;
		inputs.Add(new NativeLinkInput(forName, null, systemName, requirement.Origin));
		if (groups.Any(g => g.Libraries.Any(l => l.For == forName)))
		{
			reporter.ReportSynthetic(
				DiagnosticIds.NativeLibraryFallbackWarning,
				"warning",
				$"Native library '{forName}' is mapped in the application .cvlproj for some targets but not for '{nativeTargetLabel}'; " +
				$"falling back to system lookup ('-l{systemName}').",
				inputPath);
		}

		return true;
	}

	/// <summary>
	/// Derives the requested target label ("windows/x64"), the INC architecture selector, and the
	/// canonical target OS from the compilation target. Cross compilation honors the requested
	/// triple's architecture; the host architecture is never substituted for a requested arch.
	/// </summary>
	private static (string Label, string? Arch, string EffectiveOs) ResolveRequestedTarget(string? targetOs, TargetLayout targetLayout)
	{
		var effectiveOs = ResolveTargetOs(targetOs);
		var arch = RequestedArchitecture(targetLayout.ResolveTriple(targetOs))
			?? RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.X64 => "x64",
				Architecture.Arm64 => "arm64",
				Architecture.X86 => "x86",
				_ => null
			};
		return ($"{effectiveOs}/{arch ?? "any"}", arch, effectiveOs);
	}

	private static void PrintNativeLibrariesSelectedForBuild(
		IReadOnlyList<NativeLinkInput> linkInputs,
		IReadOnlyDictionary<string, HashSet<string>> usages,
		string targetLabel)
	{
		Console.WriteLine(AnsiConsoleStyle.Header($"Native libraries selected for build ({targetLabel}):"));
		foreach (var input in linkInputs.OrderBy(i => i.LibraryName, StringComparer.Ordinal))
		{
			var selectedInput = input.FullPath is not null
				? input.FullPath
				: $"system lookup (-l{input.SystemName ?? input.LibraryName})";
			Console.WriteLine($"  -> {input.LibraryName}: {AnsiConsoleStyle.Path(selectedInput)}");

			if (input.Origin != NativeLinkInput.ApplicationOrigin)
				Console.WriteLine($"     origin: {AnsiConsoleStyle.Value(input.Origin)}");

			Console.WriteLine($"     {AnsiConsoleStyle.Header("used by:")}");
			if (usages.TryGetValue(input.LibraryName, out var files) && files.Count > 0)
			{
				foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
					Console.WriteLine($"       - {AnsiConsoleStyle.Path(file)}");
			}
			else if (input.Origin != NativeLinkInput.ApplicationOrigin)
			{
				Console.WriteLine($"       - {AnsiConsoleStyle.Muted($"package metadata ({input.Origin})")}");
			}
			else
			{
				Console.WriteLine($"       - {AnsiConsoleStyle.Muted("<source file unavailable>")}");
			}
		}

		Console.WriteLine();
	}

	private static Dictionary<string, HashSet<string>> MergeNativeLibraryUsages(
		IReadOnlyDictionary<string, HashSet<string>> binderUsages,
		IReadOnlyDictionary<string, HashSet<string>> sourceUsages)
	{
		var merged = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		foreach (var (libraryName, files) in binderUsages)
		{
			if (!merged.TryGetValue(libraryName, out var targetFiles))
			{
				targetFiles = new HashSet<string>(StringComparer.Ordinal);
				merged[libraryName] = targetFiles;
			}

			foreach (var file in files)
				targetFiles.Add(file);
		}

		foreach (var (libraryName, files) in sourceUsages)
		{
			if (!merged.TryGetValue(libraryName, out var targetFiles))
			{
				targetFiles = new HashSet<string>(StringComparer.Ordinal);
				merged[libraryName] = targetFiles;
			}

			foreach (var file in files)
				targetFiles.Add(file);
		}

		return merged;
	}

	private static void CollectNativeLibraryUsages(
		CompilationUnitSyntax unit,
		string filePath,
		Dictionary<string, HashSet<string>> usages)
	{
		var members = unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;
		foreach (var member in members)
		{
			switch (member)
			{
				case ExternBlockSyntax block:
					RecordLibraryImportUsage(block.Attributes, filePath, usages);
					break;
				case GlobalVariableDeclarationSyntax global when global.IsForeign:
					RecordLibraryImportUsage(global.Attributes, filePath, usages);
					break;
			}
		}
	}

	private static void RecordLibraryImportUsage(
		IReadOnlyList<AttributeSyntax> attributes,
		string filePath,
		Dictionary<string, HashSet<string>> usages)
	{
		foreach (var attribute in attributes)
		{
			var name = attribute.Name.EndsWith("Attribute", StringComparison.Ordinal)
				? attribute.Name[..^"Attribute".Length]
				: attribute.Name;
			if (!string.Equals(name, "LibraryImport", StringComparison.Ordinal))
				continue;

			if (attribute.Arguments.Count == 0 || attribute.ArgumentNames[0] is not null)
				continue;

			if (attribute.Arguments[0] is not StringLiteralExpressionSyntax literal)
				continue;

			if (!usages.TryGetValue(literal.Value, out var files))
			{
				files = new HashSet<string>(StringComparer.Ordinal);
				usages[literal.Value] = files;
			}

			files.Add(filePath);
		}
	}

	private static string? RequestedArchitecture(string triple)
	{
		var normalized = triple.ToLowerInvariant();
		if (normalized.StartsWith("x86_64", StringComparison.Ordinal) || normalized.StartsWith("amd64", StringComparison.Ordinal))
			return "x64";
		if (normalized.StartsWith("aarch64", StringComparison.Ordinal) || normalized.StartsWith("arm64", StringComparison.Ordinal))
			return "arm64";
		if (normalized.StartsWith("i386", StringComparison.Ordinal)
			|| normalized.StartsWith("i486", StringComparison.Ordinal)
			|| normalized.StartsWith("i586", StringComparison.Ordinal)
			|| normalized.StartsWith("i686", StringComparison.Ordinal)
			|| normalized.StartsWith("x86-", StringComparison.Ordinal))
			return "x86";
		return null;
	}

	/// <summary>
	/// Legacy runtime copy: preserves the historical behavior for source-level OS paths, including
	/// the Windows import-library (.lib) → sibling *.dll directory scan. Project-level mappings
	/// never use this scan; they stage only explicit RuntimeFiles.
	/// </summary>
	private static void CopyLegacyNativeLibrariesToOutput(IReadOnlyList<string> legacyCopyPaths, string binDirectory, string effectiveTargetOs, bool verbose)
	{
		foreach (var fullPath in legacyCopyPaths)
		{
			if (!File.Exists(fullPath))
				continue;

			var destPath = Path.Combine(binDirectory, Path.GetFileName(fullPath));
			try
			{
				File.Copy(fullPath, destPath, overwrite: true);
				if (verbose)
					Console.WriteLine($"{AnsiConsoleStyle.Success("Copied native library:")} {AnsiConsoleStyle.Value(Path.GetFileName(fullPath))} -> {AnsiConsoleStyle.Path(binDirectory)}");
			}
			catch (Exception ex)
			{
				if (verbose)
					Console.Error.WriteLine($"{AnsiConsoleStyle.Yellow}Warning:{AnsiConsoleStyle.Reset} Failed to copy {AnsiConsoleStyle.Path(fullPath)}: {ex.Message}");
			}

			// On Windows, import libraries (.lib) need their matching DLL at runtime. The DLL may
			// have a different base name (e.g. glfw3dll.lib -> glfw3.dll), so scan the same
			// directory for DLL files to copy alongside the import library.
			if (effectiveTargetOs == "windows" && fullPath.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
			{
				var dir = Path.GetDirectoryName(fullPath)!;
				try
				{
					foreach (var dllFile in Directory.GetFiles(dir, "*.dll"))
					{
						var dllDest = Path.Combine(binDirectory, Path.GetFileName(dllFile));
						try
						{
							File.Copy(dllFile, dllDest, overwrite: true);
							if (verbose)
								Console.WriteLine($"{AnsiConsoleStyle.Success("Copied native DLL:")} {AnsiConsoleStyle.Value(Path.GetFileName(dllFile))} -> {AnsiConsoleStyle.Path(binDirectory)}");
						}
						catch (Exception ex)
						{
							if (verbose)
								Console.Error.WriteLine($"{AnsiConsoleStyle.Yellow}Warning:{AnsiConsoleStyle.Reset} Failed to copy {AnsiConsoleStyle.Path(dllFile)}: {ex.Message}");
						}
					}
				}
				catch (Exception ex)
				{
					if (verbose)
						Console.Error.WriteLine($"{AnsiConsoleStyle.Yellow}Warning:{AnsiConsoleStyle.Reset} Failed to scan for DLLs in {AnsiConsoleStyle.Path(dir)}: {ex.Message}");
				}
			}
		}
	}

	/// <summary>
	/// Stages the explicit RuntimeFiles of selected project-level mappings next to the output.
	/// The entire plan is computed and validated BEFORE any copy: destination collisions (CVL1706)
	/// and missing/broken/unreadable sources (CVL1707) fail the build even when the link succeeded.
	/// </summary>
	private static int StageProjectRuntimeFiles(IReadOnlyList<RuntimeStageEntry> plan, CompilationProject project, string binDirectory, IDiagnosticReporter reporter, string inputPath)
	{
		if (plan.Count == 0)
			return 0;

		// Lexically normalize every source (collapse ./.., keep case, no symlink resolution).
		var entries = new List<(RuntimeStageEntry Entry, string NormalizedSource)>(plan.Count);
		foreach (var entry in plan)
		{
			try
			{
				entries.Add((entry, Path.GetFullPath(entry.SourcePath)));
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
			{
				reporter.ReportSynthetic(
					DiagnosticIds.NativeRuntimeUnstageable,
					"error",
					$"Native library '{entry.LogicalName}': runtime file '{entry.DeclaredPath}' is not a valid path ({ex.Message}).",
					inputPath);
				return 1;
			}
		}

		// Destination owners: dedup identical (destination, normalized source) pairs; different
		// sources claiming the same destination filename is an error regardless of byte identity.
		var owners = new Dictionary<string, string>(StringComparer.Ordinal);
		var collided = new HashSet<string>(StringComparer.Ordinal);
		var copyDestinations = new List<string>();
		foreach (var (entry, normalizedSource) in entries)
		{
			var destination = Path.Combine(binDirectory, entry.DestinationName);
			if (owners.TryGetValue(destination, out var owner))
			{
				if (!string.Equals(owner, normalizedSource, StringComparison.Ordinal))
				{
					collided.Add(destination);
				}

				continue;
			}

			owners.Add(destination, normalizedSource);
			copyDestinations.Add(destination);
		}

		var errors = 0;
		foreach (var destination in collided)
		{
			var sources = entries
				.Where(e => string.Equals(Path.Combine(binDirectory, e.Entry.DestinationName), destination, StringComparison.Ordinal))
				.Select(e => e.NormalizedSource)
				.ToList();
			reporter.ReportSynthetic(
				DiagnosticIds.NativeRuntimeDestinationCollision,
				"error",
				$"Runtime file collision: '{sources[0]}' and '{sources[^1]}' both stage to destination '{Path.GetFileName(destination)}'. " +
				"Update the RuntimeFile paths so every destination filename is unique.",
				inputPath);
			errors++;
		}

		// Collisions with compiler-generated output artifacts are rejected before staging.
		var outputExt = project.IsShared
			? (OperatingSystem.IsWindows() ? ".dll" : ".so")
			: (OperatingSystem.IsWindows() ? ".exe" : "");
		var outputPath = Path.GetFullPath(Path.Combine(binDirectory, project.OutputName + outputExt));
		var pdbPath = Path.GetFullPath(Path.Combine(binDirectory, project.OutputName + ".pdb"));
		foreach (var destination in copyDestinations)
		{
			var full = Path.GetFullPath(destination);
			if (full.Equals(outputPath, StringComparison.Ordinal) || full.Equals(pdbPath, StringComparison.Ordinal))
			{
				reporter.ReportSynthetic(
					DiagnosticIds.NativeRuntimeDestinationCollision,
					"error",
					$"Runtime file '{Path.GetFileName(destination)}' collides with a compiler-generated output artifact; choose a different RuntimeFile name.",
					inputPath);
				errors++;
			}
		}

		if (errors > 0)
			return 1;

		// Validate sources (existence + symlink resolution) before copying anything.
		foreach (var (entry, normalizedSource) in entries)
		{
			var destination = Path.Combine(binDirectory, entry.DestinationName);
			if (collided.Contains(destination))
				continue;

			try
			{
				var info = new FileInfo(normalizedSource);
				var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
				if (resolved is not null && !resolved.Exists)
				{
					reporter.ReportSynthetic(
						DiagnosticIds.NativeRuntimeUnstageable,
						"error",
						$"Native library '{entry.LogicalName}': runtime file '{entry.DeclaredPath}' resolves to missing target '{resolved.FullName}'.",
						inputPath);
					return 1;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
			{
				reporter.ReportSynthetic(
					DiagnosticIds.NativeRuntimeUnstageable,
					"error",
					$"Native library '{entry.LogicalName}': runtime file '{entry.DeclaredPath}' cannot be stage-validated: {ex.Message}.",
					inputPath);
				return 1;
			}
		}

		// Copy phase: follow symlinks to the final regular-file payload and copy its bytes; the
		// destination keeps the DECLARED basename, never the resolved target's name.
		foreach (var (entry, normalizedSource) in entries)
		{
			var destination = Path.Combine(binDirectory, entry.DestinationName);
			if (collided.Contains(destination))
				continue;

			try
			{
				var payload = new FileInfo(normalizedSource).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? normalizedSource;
				using (var source = File.OpenRead(payload))
				using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					source.CopyTo(target);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
			{
				reporter.ReportSynthetic(
					DiagnosticIds.NativeRuntimeUnstageable,
					"error",
					$"Native library '{entry.LogicalName}': failed to stage runtime file '{normalizedSource}' to '{destination}': {ex.Message}.",
					inputPath);
				return 1;
			}
		}

		return 0;
	}

	private sealed record NativeRequirement(NativeLibraryInfo Library, string Origin);

	private sealed record RuntimeStageEntry(string SourcePath, string DestinationName, string LogicalName, string DeclaredPath);

	private static string ResolveTargetOs(string? targetOs)
	{
		if (string.IsNullOrWhiteSpace(targetOs) || targetOs.Equals("host", StringComparison.OrdinalIgnoreCase))
		{
			if (OperatingSystem.IsWindows()) return "windows";
			if (OperatingSystem.IsLinux()) return "linux";
			if (OperatingSystem.IsMacOS()) return "macos";
			return "windows";
		}

		return targetOs.ToLowerInvariant() switch
		{
			"windows" or "win" or "win32" => "windows",
			"linux" => "linux",
			"macos" or "mac" or "osx" or "darwin" => "macos",
			_ => "windows"
		};
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
