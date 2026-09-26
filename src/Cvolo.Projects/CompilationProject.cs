using System.Xml.Linq;
using Cvolo.Packaging;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Projects;

public sealed class CompilationProject
{
	public IReadOnlyList<string> SourceFiles { get; }
	public string OutputName { get; }
	public bool IsShared { get; private set; }
	public bool StrictOption { get; }
	public string ProjectDirectory { get; }
	public IReadOnlyList<string> ProjectReferences { get; }
	public bool IsFreestanding { get; }
	public IReadOnlyList<string> RequiredSystemNamespaces { get; }
	public IReadOnlyList<SdkNamespaceMismatch> NamespaceMismatches { get; }

	private CompilationProject(IReadOnlyList<string> sourceFiles, string outputName, bool isShared, string projectDirectory, bool strictOption = false, IReadOnlyList<string>? projectReferences = null, bool isFreestanding = false, IReadOnlyList<string>? requiredSystemNamespaces = null, IReadOnlyList<SdkNamespaceMismatch>? namespaceMismatches = null)
	{
		SourceFiles = sourceFiles;
		OutputName = outputName;
		IsShared = isShared;
		StrictOption = strictOption;
		ProjectDirectory = projectDirectory;
		ProjectReferences = projectReferences ?? [];
		IsFreestanding = isFreestanding;
		RequiredSystemNamespaces = requiredSystemNamespaces ?? [];
		NamespaceMismatches = namespaceMismatches ?? [];
	}

	public static CompilationProject Load(string inputPath, string? compilerBaseDir = null, bool forceShared = false, bool mergeProjectReferences = true, bool includeSystem = true, IReadOnlyDictionary<string, string>? sourceOverrides = null, bool freestanding = false)
	{
		List<string> sourceFiles = [];
		var outputName = "main";
		var isShared = forceShared;
		var strictOption = false;
		var projectFreestanding = false;
		var projectReferences = new List<string>();

		var projectDir = Directory.Exists(inputPath) ? Path.GetFullPath(inputPath) : Path.GetDirectoryName(Path.GetFullPath(inputPath))!;

		// 1. Locate exactly one active "libraries" folder. A local library tree that
		// contains the input is authoritative; otherwise the bundled/compiler root is used.
		var localLibraryRoot = FindStandardLibraryPath(projectDir);
		var stdLibFullPath = localLibraryRoot is not null && IsPathUnder(projectDir, localLibraryRoot)
			? localLibraryRoot
			: FindStandardLibraryPath(compilerBaseDir ?? AppContext.BaseDirectory) ?? FindStandardLibraryPath(Directory.GetCurrentDirectory());

		// 2. Add user project files. A directory containing one .cvlproj is treated the
		// same as passing that project file explicitly, which keeps `cvolo run App`
		// and `cvolo run App/App.cvlproj` equivalent.
		string? projectFilePath = null;
		if (inputPath.EndsWith(".cvlproj", StringComparison.OrdinalIgnoreCase) && File.Exists(inputPath))
		{
			projectFilePath = Path.GetFullPath(inputPath);
		}
		else if (Directory.Exists(inputPath))
		{
			var candidates = Directory.GetFiles(Path.GetFullPath(inputPath), "*.cvlproj", SearchOption.TopDirectoryOnly);
			if (candidates.Length > 1)
				throw new InvalidOperationException($"Multiple .cvlproj files found in '{inputPath}'. Pass the project file explicitly.");
			projectFilePath = candidates.SingleOrDefault();
		}

		if (projectFilePath is not null)
		{
			var projDir = Path.GetDirectoryName(projectFilePath)!;
			projectDir = projDir;

			try
			{
				var graph = ProjectGraph.Load(projectFilePath);
				foreach (var node in graph.Nodes)
				{
					var isRoot = string.Equals(node.ProjectPath, graph.RootProjectPath, StringComparison.OrdinalIgnoreCase);
					if (isRoot || mergeProjectReferences)
					{
						foreach (var sourceFile in node.SourceFiles)
						{
							if (!sourceFiles.Contains(sourceFile, StringComparer.OrdinalIgnoreCase))
								sourceFiles.Add(sourceFile);
						}
					}

					if (!isRoot)
						projectReferences.Add(node.ProjectPath);
				}

				var xml = XDocument.Load(projectFilePath);
				var assemblyName = xml.Root?.Element("PropertyGroup")?.Element("AssemblyName")?.Value;
				var outputType = xml.Root?.Element("PropertyGroup")?.Element("OutputType")?.Value;
				var strictOptionValue = xml.Root?.Element("PropertyGroup")?.Element("StrictOption")?.Value;
				var freestandingValue = xml.Root?.Element("PropertyGroup")?.Element("Freestanding")?.Value;

				if (!string.IsNullOrEmpty(assemblyName))
					outputName = assemblyName;
				else
					outputName = Path.GetFileNameWithoutExtension(projectFilePath);

				if (outputType == "Library")
					isShared = true;

				if (string.Equals(strictOptionValue, "true", StringComparison.OrdinalIgnoreCase))
					strictOption = true;

				if (string.Equals(freestandingValue, "true", StringComparison.OrdinalIgnoreCase))
					projectFreestanding = true;
			}
			catch (Exception ex) when (ex is not FileNotFoundException && ex is not InvalidOperationException)
			{
				outputName = Path.GetFileNameWithoutExtension(projectFilePath);
			}
		}
		else if (Directory.Exists(inputPath))
		{
			var root = Path.GetFullPath(inputPath);
			var nestedProjectDirectories = Directory.GetFiles(root, "*.cvlproj", SearchOption.AllDirectories)
				.Select(Path.GetDirectoryName)
				.OfType<string>()
				.Select(Path.GetFullPath)
				.ToList();
			var files = Directory.GetFiles(root, "*.cvl", SearchOption.AllDirectories)
				.Where(file => !nestedProjectDirectories.Any(projectDirectory =>
				{
					var relative = Path.GetRelativePath(projectDirectory, file);
					return !Path.IsPathRooted(relative)
						&& relative != ".."
						&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
						&& !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
				}))
				.ToList();
			sourceFiles.AddRange(files);
			outputName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
		}
		else if (File.Exists(inputPath))
		{
			outputName = Path.GetFileNameWithoutExtension(inputPath);

			// An explicit .cvl file compiles on its own (plus the standard library).
			if (string.Equals(Path.GetExtension(inputPath), ".cvl", StringComparison.OrdinalIgnoreCase))
			{
				sourceFiles.Add(Path.GetFullPath(inputPath));
			}
			else
			{
				var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
				var files = Directory.GetFiles(dir, "*.cvl", SearchOption.AllDirectories).ToList();
				foreach (var file in files)
				{
					if (!sourceFiles.Contains(file))
						sourceFiles.Add(file);
				}
			}
		}
		else
		{
			throw new FileNotFoundException($"Input path '{inputPath}' not found");
		}

		var effectiveFreestanding = freestanding || projectFreestanding;
		SdkResolution? sdkResolution = null;
		if (stdLibFullPath is not null && Directory.Exists(stdLibFullPath))
		{
			sdkResolution = SdkLibraryResolver.Resolve(stdLibFullPath, sourceFiles, includeSystem && !effectiveFreestanding, sourceOverrides);
			sourceFiles.InsertRange(0, sdkResolution.Sources);
		}

		var seenSourceFiles = new HashSet<string>(
			OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
		var uniqueSourceFiles = new List<string>(sourceFiles.Count);
		foreach (var sourceFile in sourceFiles)
		{
			if (seenSourceFiles.Add(Path.GetFullPath(sourceFile)))
			{
				uniqueSourceFiles.Add(sourceFile);
			}
		}

		sourceFiles = uniqueSourceFiles;

		if (sourceFiles.Count == 0)
		{
			throw new InvalidOperationException($"No Cvolo source files found in '{inputPath}'");
		}

		return new CompilationProject(
			sourceFiles,
			outputName,
			isShared,
			projectDir,
			strictOption,
			projectReferences,
			effectiveFreestanding,
			sdkResolution?.RequiredSystemNamespaces,
			sdkResolution?.NamespaceMismatches);
	}

	public static void CreateNewProject(string projectName)
	{
		var projectDir = Path.GetFullPath(projectName);
		if (Directory.Exists(projectDir))
		{
			throw new InvalidOperationException($"Directory '{projectName}' already exists");
		}

		Directory.CreateDirectory(projectDir);

		// 1. Create .cvlproj file (Uses $$""" to allow literal { } braces)
		var projFile = Path.Combine(projectDir, $"{projectName}.cvlproj");
		var projXml = $$"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>{{projectName}}</AssemblyName>
  </PropertyGroup>
</Project>
""";
		File.WriteAllText(projFile, projXml);

		// 2. Create Geometry.cvl (Uses $$""" to allow literal { } braces)
		var geomFile = Path.Combine(projectDir, "Geometry.cvl");
		var geomSource = $$"""
namespace {{projectName}}.Geometry;

struct Point {
	int X;
	int Y;
}
""";
		File.WriteAllText(geomFile, geomSource);

		// 3. Create Main.cvl (Uses $$""" to allow literal { } braces)
		var mainFile = Path.Combine(projectDir, "Main.cvl");
		var mainSource = $$"""
using {{projectName}}.Geometry;

extern void printf(string format, ...);

int main() {
	printf("Hello Cvolo Project!\n");

	Point p = Point { X: 10, Y: 20 };
	printf("Point coords: X = %d, Y = %d\n", p.X, p.Y);

	return 0;
}
""";
		File.WriteAllText(mainFile, mainSource);

		Console.WriteLine($"Created Cvolo project '{projectName}' successfully.");
		Console.WriteLine($"To compile: dotnet run --project src/Cvolo -- {projectName}/{projectName}.cvlproj");
	}

	private static bool IsPathUnder(string path, string root)
	{
		var fullPath = Path.GetFullPath(path);
		var fullRoot = Path.GetFullPath(root);
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (fullPath.Equals(fullRoot, comparison))
			return true;

		var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
			? fullRoot
			: fullRoot + Path.DirectorySeparatorChar;
		return fullPath.StartsWith(rootWithSeparator, comparison);
	}

	private static string? FindStandardLibraryPath(string startDir)
	{
		var dir = new DirectoryInfo(startDir);
		while (dir != null)
		{
			var libPath = Path.Combine(dir.FullName, "libraries");
			if (Directory.Exists(libPath))
			{
				return libPath;
			}

			dir = dir.Parent;
		}

		return null;
	}
}
