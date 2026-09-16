using System.CommandLine;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Commands;

internal sealed class BuildCommand : Command
{
	private readonly ICompilerDriver _compilerDriver;
	private readonly PackageBuildRestoreService _buildRestore;
	public BuildCommand(ICompilerDriver compilerDriver, PackageBuildRestoreService buildRestore) : base("build", "Compiles Cvolo project files into target binaries.")
	{
		_compilerDriver = compilerDriver;
		_buildRestore = buildRestore;

		var pathArg = new Argument<string>("path") { Description = "The path to the Cvolo source file, directory, or .cvlproj file." };
		var llvmOption = new Option<bool>("--llvm") { Description = "Generate .ll LLVM IR only (no linking)" };
		var sharedOption = new Option<bool>("--shared") { Description = "Build a shared library (.dll/.so)" };
		var emitIrOption = new Option<bool>("--emit-ir") { Description = "Print generated IR directly to stdout" };
		var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show verbose compiler debug and linkage information." };
		var optOption = new Option<string>("--optimize", "-O", "Os") { Description = "Select optimization level (O0, O1, O2, O3, Os, Oz)" };
		var emitLoweredOption = new Option<bool>("--emit-lowered", "-l") { Description = "Print lowered Cvolo source code directly to stdout" };
		var nowarnOption = new Option<string>("--nowarn") { Description = "Comma-separated diagnostic ids whose warnings are suppressed (e.g. CVL1001)" };
		var legacyVisibilityOption = new Option<bool>("--legacy-visibility") { Description = "Disable the visibility system and treat all declarations as public (v0.2.0-alpha behavior)" };
		var strictOption = new Option<bool>("--strict-option") { Description = "Disable the '?' optional type syntax; require explicit Option<T> types" };
		var noTbaaOption = new Option<bool>("--no-tbaa") { Description = "Disable generation of !tbaa alias-analysis metadata nodes" };
		var targetOption = new Option<string>("--target") { Description = "Target OS for native library resolution (host, windows, linux, macos). Controls which win:/linux:/mac: [LibraryImport] path is forwarded to the linker." };
		var checkedFfiBoundsOption = new Option<bool>("--checked-ffi-bounds") { Description = "Generate explicit null-check prologues in expose extern functions for debug builds" };
		var configurationOption = new Option<string>("--configuration", "-c", BuildOutputLayout.DefaultConfiguration) { Description = "Build configuration (Debug or Release)." };
		var noRestoreOption = new Option<bool>("--no-restore") { Description = "Do not restore package dependencies before building; require an existing in-sync lock and cache." };

		Add(pathArg);
		Add(llvmOption);
		Add(sharedOption);
		Add(emitIrOption);
		Add(optOption);
		Add(verboseOption);
		Add(emitLoweredOption);
		Add(nowarnOption);
		Add(legacyVisibilityOption);
		Add(strictOption);
		Add(noTbaaOption);
		Add(targetOption);
		Add(checkedFfiBoundsOption);
		Add(configurationOption);
		Add(noRestoreOption);

		SetAction((ParseResult parseResult) =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var llvmOnly = parseResult.GetValue(llvmOption);
			var isShared = parseResult.GetValue(sharedOption);
			var emitIrVal = parseResult.GetValue(emitIrOption);
			var optLevel = parseResult.GetValue(optOption) ?? "Os";
			var emitLoweredVal = parseResult.GetValue(emitLoweredOption);
			var noWarnVal = parseResult.GetValue(nowarnOption);
			var legacyVisibilityVal = parseResult.GetValue(legacyVisibilityOption);
			var strictOptionVal = parseResult.GetValue(strictOption);
			var noTbaaVal = parseResult.GetValue(noTbaaOption);
			var targetOsVal = parseResult.GetValue(targetOption);
			var checkedFfiBoundsVal = parseResult.GetValue(checkedFfiBoundsOption);
			var configurationVal = BuildOutputLayout.NormalizeConfiguration(parseResult.GetValue(configurationOption) ?? BuildOutputLayout.DefaultConfiguration);
			var noRestoreVal = parseResult.GetValue(noRestoreOption);

			try
			{
				var restore = _buildRestore.RestoreIfRequired(path, noRestoreVal);
				PrintRestoreSummary(restore, parseResult.GetValue(verboseOption));

				var incremental = TryPrepareIncrementalBuild(
					path, isShared, llvmOnly, emitIrVal, emitLoweredVal, optLevel, noWarnVal,
					legacyVisibilityVal, strictOptionVal, noTbaaVal, targetOsVal, checkedFfiBoundsVal, configurationVal);
				var buildPlan = incremental is { } prepared
					? ProjectBuildPlan.Create(prepared.Graph, prepared.BuildKey, prepared.Configuration)
					: null;
				if (parseResult.GetValue(verboseOption) && buildPlan is not null)
					PrintProjectBuildPlan(buildPlan);

				if (incremental is { } cached && IncrementalBuildState.IsUpToDate(cached.Graph, cached.BuildKey, cached.OutputPath, cached.Configuration))
				{
					Console.WriteLine($"Up-to-date -> {cached.OutputPath}");
					Environment.Exit(0);
					return;
				}

				if (LibraryBuildPipeline.TryBuild(path, isShared, llvmOnly, emitIrVal, emitLoweredVal, parseResult.GetValue(verboseOption), configurationVal, out var packageResult))
				{
					if (incremental is { } libraryBuild)
					{
						IncrementalBuildState.Record(libraryBuild.Graph, libraryBuild.BuildKey, packageResult!.OutputPath, libraryBuild.Configuration);
						ProjectBuildPlan.RecordSuccessful(libraryBuild.Graph, libraryBuild.BuildKey, libraryBuild.Configuration);
					}
					Console.WriteLine($"Built {packageResult!.PackageId} {packageResult.Version} -> {packageResult.OutputPath}");
					Environment.Exit(0);
					return;
				}

				var exitCode = _compilerDriver.Compile(path, llvmOnly, isShared, emitIrVal, optLevel, emitLowered: emitLoweredVal, noWarn: noWarnVal, legacyVisibility: legacyVisibilityVal, strictOption: strictOptionVal, noTbaa: noTbaaVal, targetOs: targetOsVal, checkedFfiBounds: checkedFfiBoundsVal, configuration: configurationVal);
				if (exitCode == 0 && incremental is { } compiledBuild)
				{
					IncrementalBuildState.Record(compiledBuild.Graph, compiledBuild.BuildKey, compiledBuild.OutputPath, compiledBuild.Configuration);
					ProjectBuildPlan.RecordSuccessful(compiledBuild.Graph, compiledBuild.BuildKey, compiledBuild.Configuration);
				}
				Environment.Exit(exitCode);
			}
			catch (PackageException ex)
			{
				Console.Error.WriteLine($"error {ex.Code}: {ex.Message}");
				if (!string.IsNullOrWhiteSpace(ex.Detail))
					Console.Error.WriteLine(ex.Detail);
				Environment.Exit(1);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"error: {ex.Message}");
				Environment.Exit(1);
			}
		});
	}

	private static void PrintProjectBuildPlan(ProjectBuildPlan plan)
	{
		Console.WriteLine("Project graph:");
		foreach (var node in plan.Nodes)
		{
			var name = Path.GetFileNameWithoutExtension(node.Project.ProjectPath);
			var status = node.RequiresBuild
				? node.InputsChanged ? "dirty inputs" : "dirty dependency"
				: "up-to-date";
			Console.WriteLine($"  -> {name}: {status}");
		}
		Console.WriteLine();
	}

	private static void PrintRestoreSummary(RestoreResult? restore, bool verbose)
	{
		if (restore is null)
			return;

		if (restore.LockFileUpdated || restore.InstalledPackages > 0)
		{
			Console.WriteLine($"Restored packages: {restore.InstalledPackages} installed, {restore.CachedPackages} cached.");
			return;
		}

		if (verbose)
			Console.WriteLine("Restore up-to-date.");
	}

	private static IncrementalBuild? TryPrepareIncrementalBuild(
		string path,
		bool forceShared,
		bool llvmOnly,
		bool emitIr,
		bool emitLowered,
		string optLevel,
		string? noWarn,
		bool legacyVisibility,
		bool strictOption,
		bool noTbaa,
		string? targetOs,
		bool checkedFfiBounds,
		string configuration)
	{
		// Output-only modes intentionally bypass the build cache. They are commonly used
		// for diagnostics or tooling and have no stable binary artifact to reuse.
		if (forceShared || llvmOnly || emitIr || emitLowered)
			return null;

		if (!ProjectBuildGraph.TryLoad(path, out var graph) || graph is null)
			return null;

		configuration = BuildOutputLayout.NormalizeConfiguration(configuration);
		var outputPath = ResolveOutputPath(path, configuration);
		if (outputPath is null)
			return null;

		var buildKey = string.Join("|",
			"build-v2",
			$"configuration={configuration}",
			$"opt={optLevel}",
			$"nowarn={noWarn ?? string.Empty}",
			$"legacyVisibility={legacyVisibility}",
			$"strictOption={strictOption}",
			$"noTbaa={noTbaa}",
			$"target={targetOs ?? "host"}",
			$"checkedFfiBounds={checkedFfiBounds}");
		return new IncrementalBuild(graph, buildKey, outputPath, configuration);
	}

	private static string? ResolveOutputPath(string path, string configuration)
	{
		try
		{
			var manifest = ProjectManifest.Load(path);
			if (manifest.IsLibrary)
				return LibraryBuildPipeline.GetOutputPath(manifest, configuration, TargetTriple.HostTriple());
		}
		catch (PackageException ex) when (ex.Code is PackageDiagnosticIds.MissingPackageId or PackageDiagnosticIds.MissingVersion)
		{
			// Ordinary non-package library projects still go through the compiler path below.
		}
		catch (FileNotFoundException)
		{
			return null;
		}

		var project = CompilationProject.Load(path);
		return BuildOutputLayout.GetNativeOutputPath(project.ProjectDirectory, project.OutputName, project.IsShared, configuration);
	}

	private sealed record IncrementalBuild(ProjectBuildGraph Graph, string BuildKey, string OutputPath, string Configuration);
}
