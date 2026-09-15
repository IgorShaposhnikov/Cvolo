using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
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
internal sealed class CompilerDriver : ICompilerDriver
{
    private static readonly string[] _linkerCandidates = ["clang", "gcc", "g++"];
    private readonly PackageCache _packageCache;

    public CompilerDriver(PackageCache packageCache)
    {
        _packageCache = packageCache;
    }

    public int Compile(string path, bool llvmOnly, bool isShared, bool emitIr, string optLevel, bool checkOnly = false, bool runAfterCompile = false, bool verbose = false, bool emitLowered = false, string? noWarn = null, bool suppressWarnings = false, bool legacyVisibility = false, bool strictOption = false, bool noTbaa = false, string? targetOs = null, bool checkedFfiBounds = false, string format = "text")
    {
        // The driver itself is format-agnostic: it hands diagnostics to a
        // reporter and, for verbose chatter, asks whether the reporter owns
        // stdout exclusively. Concrete formats live in IDiagnosticReporter.
        var reporter = CreateReporter(format, suppressWarnings);

        // Diagnostics whose ids appear here are dropped from the warning stream entirely.
        var noWarnIds = noWarn?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.ToUpperInvariant())
            .ToHashSet();

        // 1. Load the project configuration (automatically walks up directory tree to locate standard libraries)
        CompilationProject project;
        try
        {
            project = CompilationProject.Load(path, AppContext.BaseDirectory, isShared);
        }
        catch (Exception ex)
        {
            reporter.ReportSynthetic(null, "error", ex.Message, path);
            return 1;
        }

        var packageState = ValidatePackageLock(path, reporter, out var packageLockValid);
        if (!packageLockValid)
            return 1;

        IReadOnlyList<ResolvedPackageArtifacts> packageArtifacts = [];
        if (!checkOnly && !emitLowered && !llvmOnly && packageState is { } validatedPackages)
        {
            try
            {
                var installer = new PackageInstaller(_packageCache);
                packageArtifacts = new PackageDependencyLoader(_packageCache, installer)
                    .Load(validatedPackages.Manifest, validatedPackages.LockFile);

                if (verbose && !reporter.Exclusive)
                {
                    Console.WriteLine("Package objects selected for linking:");
                    foreach (var artifact in packageArtifacts)
                        Console.WriteLine($"  -> {artifact.PackageId}@{artifact.Version}: {artifact.NativeObjectPath}");
                    Console.WriteLine();
                }
            }
            catch (PackageException ex)
            {
                reporter.ReportSynthetic(ex.Code, "error", ex.Message, path);
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                reporter.ReportSynthetic(PackageDiagnosticIds.LockOutOfSync, "error", $"Failed to prepare package dependencies: {ex.Message}", path);
                return 1;
            }
        }

        // 2. Instrument compiled files list only under verbose logging rules.
        // Machine reporters claim stdout, so verbose chatter is gated on !Exclusive.
        if (verbose && !checkOnly && !reporter.Exclusive)
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
        ISyntaxParser parser = new AntlrSyntaxParser();
        CompilationContext? firstContext = null;

        foreach (var file in project.SourceFiles)
        {
            var sourceCode = File.ReadAllText(file);
            var context = new CompilationContext(sourceCode, file);
            firstContext ??= context;

            var ast = parser.Parse(context);
            if (parser.Diagnostics.HasErrors)
            {
                reporter.ReportErrors(parser.Diagnostics.Diagnostics, "Parse Error");
                return 1;
            }

            asts.Add(ast!);
            binder.Context.FileContexts[ast!] = context;
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
        var foreachLoweringRewriter = new ForeachLoweringRewriter();
        {
            var expandedAsts = new List<CompilationUnitSyntax>();
            foreach (var ast in asts)
            {
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
        if (!project.IsShared && binder.Context.Globals.Lookup("main") is null && binder.Context.Globals.Lookup("Main") is null)
        {
            reporter.ReportSynthetic(
                DiagnosticIds.MissingEntryPoint,
                "error",
                "Program does not contain a static 'main' method suitable for an entry point",
                path);
            return 1;
        }

        // Setup modern C# /bin and /obj folder layouts
        var outputDirectory = project.ProjectDirectory;
        var objDirectory = Path.Combine(outputDirectory, "obj", "Debug");
        var binDirectory = Path.Combine(outputDirectory, "bin", "Debug");
        var compilationFailuresDirectory = Path.Combine(outputDirectory, "CompilationFailures", "Debug");

        Directory.CreateDirectory(objDirectory);
        Directory.CreateDirectory(binDirectory);

        var llPath = Path.Combine(objDirectory, project.OutputName + ".ll");

        // Resolve optimization level flag
        if (!Enum.TryParse<OptimizationLevel>(optLevel, true, out var parsedLevel))
        {
            parsedLevel = OptimizationLevel.Os; // Fallback to size profile
        }

        // 6. Programmatic LLVM Code Generation pass (Triggers optimization passes internally)
        var targetLayout = new TargetLayout();
        var optimizer = new IrOptimizer(targetLayout, parsedLevel);
        var irVerifier = new IRVerifier(compilationFailuresDirectory);
        IEmitter emitter = new CodeGenerator("cvolo_module", targetLayout, optimizer, irVerifier, enableTbaa: !noTbaa, checkedFfiBounds: checkedFfiBounds);
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

        if (verbose && linkerPath is not null && !reporter.Exclusive)
        {
            Console.WriteLine($"Linking using: {linkerName}...");
        }

        // 8. Execute Strategy selection (IR only or full target linkage)
        ICompilationStrategy strategy = llvmOnly
            ? new IrOnlyStrategy()
            : new LinkStrategy(binDirectory);

        var linkResult = strategy.Execute(
            llPath,
            project,
            linkerPath,
            linkerName,
            optLevel,
            verbose,
            binder.Context.NativeLibraries.Values,
            targetOs,
            packageArtifacts.Select(a => a.NativeObjectPath));
        if (linkResult != 0)
        {
            return linkResult;
        }

        // 8b. Copy referenced native libraries to the output directory so the binary can find them at runtime
        CopyNativeLibrariesToOutput(binder.Context.NativeLibraries.Values, project, binDirectory, targetOs, verbose);

        // 9. Execute immediate runtime execution if requested
        if (runAfterCompile)
        {
            var binaryExt = project.IsShared
                ? (OperatingSystem.IsWindows() ? ".dll" : ".so")
                : (OperatingSystem.IsWindows() ? ".exe" : "");
            var binaryPath = Path.Combine(binDirectory, project.OutputName + binaryExt);

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
            ReportLockOutOfSync(reporter, path, "cvolo.lock.json is missing; run 'cvolo pkg update' or 'cvolo pkg install'.");
            valid = false;
            return null;
        }

        try
        {
            var lockFile = LockFile.Read(lockPath);
            if (!PackageLockValidator.Validate(manifest, lockFile, out var message))
            {
                ReportLockOutOfSync(reporter, path, message);
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

    private static void CopyNativeLibrariesToOutput(IEnumerable<NativeLibraryInfo> nativeLibraries, CompilationProject project, string binDirectory, string? targetOs, bool verbose)
    {
        var effectiveOs = ResolveTargetOs(targetOs);

        foreach (var lib in nativeLibraries)
        {
            var targetPath = effectiveOs switch
            {
                "windows" => lib.WinPath,
                "linux" => lib.LinuxPath,
                "macos" => lib.MacPath,
                _ => null
            };

            if (string.IsNullOrEmpty(targetPath) || (!targetPath.Contains('/') && !targetPath.Contains('\\')))
                continue;

            var fullPath = Path.GetFullPath(targetPath, project.ProjectDirectory);
            if (!File.Exists(fullPath))
                continue;

            var destPath = Path.Combine(binDirectory, Path.GetFileName(fullPath));
            try
            {
                File.Copy(fullPath, destPath, overwrite: true);
                if (verbose)
                    Console.WriteLine($"Copied native library: {Path.GetFileName(fullPath)} -> {binDirectory}");
            }
            catch (Exception ex)
            {
                if (verbose)
                    Console.Error.WriteLine($"Warning: Failed to copy {fullPath}: {ex.Message}");
            }

            // On Windows, import libraries (.lib) need their matching DLL at runtime.
            // The DLL may have a different base name (e.g. glfw3dll.lib -> glfw3.dll),
            // so scan the same directory for DLL files to copy alongside the import library.
            if (effectiveOs == "windows" && fullPath.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
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
                                Console.WriteLine($"Copied native DLL: {Path.GetFileName(dllFile)} -> {binDirectory}");
                        }
                        catch (Exception ex)
                        {
                            if (verbose)
                                Console.Error.WriteLine($"Warning: Failed to copy {dllFile}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (verbose)
                        Console.Error.WriteLine($"Warning: Failed to scan for DLLs in {dir}: {ex.Message}");
                }
            }
        }
    }

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