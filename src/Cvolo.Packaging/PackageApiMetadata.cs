using System.Text.Json;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Packaging;

/// <summary>
/// Language-level public API carried in Sector 1 layout metadata. This is compiler metadata,
/// not a C ABI surface: package consumers bind ordinary Cvolo calls against these declarations
/// while Sector 3 supplies the implementation at link time.
/// </summary>
public sealed class PackageApiMetadata
{
	public const string FormatId = "cvolo.package-api.v1";

	public string Format { get; init; } = FormatId;
	public IReadOnlyList<PackageApiUnit> Units { get; init; } = [];

	public static PackageApiMetadata FromCompilationUnits(IEnumerable<CompilationUnitSyntax> units)
	{
		var apiUnits = new List<PackageApiUnit>();
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration;
			var members = ns is null ? unit.Members : ns.Members;
			var functions = members
				.OfType<FunctionDeclarationSyntax>()
				.Where(function => function.Visibility == Visibility.Public && function.GenericParameters.Count == 0)
				.Select(function => new PackageApiFunction
				{
					Name = function.Name,
					ReturnType = function.ReturnType,
					GenericParameters = function.GenericParameters.ToArray(),
					Parameters = function.Parameters
						.Select(parameter => new PackageApiParameter(parameter.Type, parameter.Name))
						.ToArray(),
					Modifier = function.Modifier
				})
				.ToArray();

			if (functions.Length == 0)
				continue;

			apiUnits.Add(new PackageApiUnit
			{
				Namespace = ns?.Name,
				FileUsings = unit.Usings.Where(u => !u.IsExposed).Select(u => u.NamespaceName).ToArray(),
				NamespaceUsings = ns?.Usings.Where(u => !u.IsExposed).Select(u => u.NamespaceName).ToArray() ?? [],
				Functions = functions
			});
		}

		return new PackageApiMetadata { Units = apiUnits };
	}

	public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);

	public static PackageApiMetadata Read(Cvolo.Core.Packages.CvlArchive archive)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var metadata = archive.Manifest.LayoutMetadata.Span;
		if (metadata.IsEmpty)
			return new PackageApiMetadata();

		try
		{
			var api = JsonSerializer.Deserialize<PackageApiMetadata>(metadata);
			return api is not null && string.Equals(api.Format, FormatId, StringComparison.Ordinal)
				? api
				: new PackageApiMetadata();
		}
		catch (JsonException)
		{
			// Older packages used "{}" as the layout metadata placeholder. Treat unknown
			// metadata as having no language API rather than turning it into a container error.
			return new PackageApiMetadata();
		}
	}

	public IReadOnlyList<CompilationUnitSyntax> CreateCompilationUnits(string packageId, string version)
	{
		var result = new List<CompilationUnitSyntax>(Units.Count);
		for (var i = 0; i < Units.Count; i++)
		{
			var unit = Units[i];
			var span = new TextSpan(0, 0);
			var filePath = $"<package:{packageId}@{version}:{i}>";
			var context = new CompilationContext(string.Empty, filePath);
			var functions = unit.Functions.Select(function =>
				(SyntaxNode)new FunctionDeclarationSyntax(
					span,
					function.ReturnType,
					function.Name,
					function.GenericParameters,
					function.Parameters.Select(parameter => new ParameterSyntax(span, parameter.Type, parameter.Name)).ToArray(),
					null!,
					modifier: function.Modifier,
					visibility: Visibility.Public)).ToArray();

			var fileUsings = unit.FileUsings.Select(ns => new UsingDirectiveSyntax(span, ns)).ToArray();
			NamespaceDeclarationSyntax? namespaceDeclaration = null;
			IReadOnlyList<SyntaxNode> topLevelMembers = functions;
			if (!string.IsNullOrWhiteSpace(unit.Namespace))
			{
				var namespaceUsings = unit.NamespaceUsings.Select(ns => new UsingDirectiveSyntax(span, ns)).ToArray();
				namespaceDeclaration = new NamespaceDeclarationSyntax(span, unit.Namespace!, namespaceUsings, functions);
				topLevelMembers = [];
			}

			result.Add(new CompilationUnitSyntax(span, context, fileUsings, namespaceDeclaration, topLevelMembers));
		}

		return result;
	}
}

public sealed class PackageApiUnit
{
	public string? Namespace { get; init; }
	public IReadOnlyList<string> FileUsings { get; init; } = [];
	public IReadOnlyList<string> NamespaceUsings { get; init; } = [];
	public IReadOnlyList<PackageApiFunction> Functions { get; init; } = [];
}

public sealed class PackageApiFunction
{
	public string Name { get; init; } = string.Empty;
	public string ReturnType { get; init; } = "void";
	public IReadOnlyList<string> GenericParameters { get; init; } = [];
	public IReadOnlyList<PackageApiParameter> Parameters { get; init; } = [];
	public SafetyTier? Modifier { get; init; }
}

public sealed record PackageApiParameter(string Type, string Name);
