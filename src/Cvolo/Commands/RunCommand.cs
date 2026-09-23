using System.CommandLine;
using Cvolo.Drivers;
using Cvolo.Packaging;

namespace Cvolo.Commands;

internal sealed class RunCommand : Command
{
	private readonly ICompilerDriver _compilerDriver;
	private readonly PackageBuildRestoreService _buildRestore;

	public RunCommand(ICompilerDriver compilerDriver, PackageBuildRestoreService buildRestore) : base("run", "Compiles your Cvolo project and immediately executes the output binary.")
	{
		_compilerDriver = compilerDriver;
		_buildRestore = buildRestore;

		var pathArg = new Argument<string>("path") { Description = "The path to the Cvolo source file, directory, or .cvlproj file." };
		var optOption = new Option<string>("--optimize", "Os", "-O") { Description = "Select optimization level (O0, O1, O2, O3, Os, Oz)" };
		var emitLoweredOption = new Option<bool>("--emit-lowered", "-l") { Description = "Print lowered Cvolo source code directly to stdout" };
		var nowarnOption = new Option<string>("--nowarn") { Description = "Comma-separated diagnostic ids whose warnings are suppressed (e.g. CVL1003)" };
		var warnOption = new Option<bool>("--warn") { Description = "Show warnings during compilation (hidden by default in run)" };
		var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show verbose compiler debug information (implies --warn)" };
		var legacyVisibilityOption = new Option<bool>("--legacy-visibility") { Description = "Disable the visibility system and treat all declarations as public (v0.2.0-alpha behavior)" };
		var strictOption = new Option<bool>("--strict-option") { Description = "Disable the '?' optional type syntax; require explicit Option<T> types" };
		var noTbaaOption = new Option<bool>("--no-tbaa") { Description = "Disable generation of !tbaa alias-analysis metadata nodes" };
		var noStdlibOption = new Option<bool>("--no-stdlib") { Description = "Compile without automatically including the Cvolo standard library." };
		var targetOption = new Option<string>("--target") { Description = "Target OS for native library resolution (host, windows, linux, macos)." };
		var checkedFfiBoundsOption = new Option<bool>("--checked-ffi-bounds") { Description = "Generate explicit null-check prologues in expose extern functions for debug builds" };
		var configurationOption = new Option<string>("--configuration", "-c", BuildOutputLayout.DefaultConfiguration) { Description = "Build configuration (Debug or Release)." };
		var noRestoreOption = new Option<bool>("--no-restore") { Description = "Do not restore package dependencies before running; require an existing in-sync lock and cache." };

		Add(pathArg);
		Add(optOption);
		Add(emitLoweredOption);
		Add(nowarnOption);
		Add(warnOption);
		Add(verboseOption);
		Add(legacyVisibilityOption);
		Add(strictOption);
		Add(noTbaaOption);
		Add(noStdlibOption);
		Add(targetOption);
		Add(checkedFfiBoundsOption);
		Add(configurationOption);
		Add(noRestoreOption);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var optLevel = parseResult.GetValue(optOption) ?? "Os";
			var emitLoweredVal = parseResult.GetValue(emitLoweredOption);
			var noWarnVal = parseResult.GetValue(nowarnOption);
			var warnVal = parseResult.GetValue(warnOption);
			var verboseVal = parseResult.GetValue(verboseOption);
			var legacyVisibilityVal = parseResult.GetValue(legacyVisibilityOption);
			var strictOptionVal = parseResult.GetValue(strictOption);
			var noTbaaVal = parseResult.GetValue(noTbaaOption);
			var noStdlibVal = parseResult.GetValue(noStdlibOption);
			var targetOsVal = parseResult.GetValue(targetOption);
			var checkedFfiBoundsVal = parseResult.GetValue(checkedFfiBoundsOption);
			var configurationVal = BuildOutputLayout.NormalizeConfiguration(parseResult.GetValue(configurationOption) ?? BuildOutputLayout.DefaultConfiguration);
			var noRestoreVal = parseResult.GetValue(noRestoreOption);
			if (verboseVal)
			{
				warnVal = true;
			}

			try
			{
				var restore = _buildRestore.RestoreIfRequired(path, noRestoreVal);
				if (restore is not null && (restore.LockFileUpdated || restore.InstalledPackages > 0))
					Console.WriteLine($"Restored packages: {restore.InstalledPackages} installed, {restore.CachedPackages} cached.");

				ProjectBuildGraph? projectGraph = null;
				string? projectBuildKey = null;
				var useProjectReferenceArtifacts = false;
				if (!emitLoweredVal && ProjectBuildGraph.TryLoad(path, out projectGraph) && projectGraph is not null)
				{
					projectBuildKey = string.Join("|",
						"build-v2",
						$"configuration={configurationVal}",
						$"opt={optLevel}",
						$"nowarn={noWarnVal ?? string.Empty}",
						$"legacyVisibility={legacyVisibilityVal}",
						$"strictOption={strictOptionVal}",
						$"noTbaa={noTbaaVal}",
						$"noStdlib={noStdlibVal}",
						$"target={targetOsVal ?? "host"}",
						$"checkedFfiBounds={checkedFfiBoundsVal}");
					var plan = ProjectBuildPlan.Create(projectGraph, projectBuildKey, configurationVal);
					var projectReferences = ProjectReferenceBuildPipeline.Prepare(projectGraph, plan, projectBuildKey, configurationVal, verboseVal);
					useProjectReferenceArtifacts = projectReferences.UseArtifacts;
				}

				var exitCode = _compilerDriver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel, checkOnly: false, runAfterCompile: true, verbose: verboseVal, emitLowered: emitLoweredVal, noWarn: noWarnVal, suppressWarnings: !warnVal, legacyVisibility: legacyVisibilityVal, strictOption: strictOptionVal, noTbaa: noTbaaVal, targetOs: targetOsVal, checkedFfiBounds: checkedFfiBoundsVal, configuration: configurationVal, useProjectReferencePackages: useProjectReferenceArtifacts, noStdlib: noStdlibVal);
				if (exitCode == 0 && projectGraph is not null && projectBuildKey is not null)
					ProjectBuildPlan.RecordSuccessful(projectGraph, projectBuildKey, configurationVal);
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
}
