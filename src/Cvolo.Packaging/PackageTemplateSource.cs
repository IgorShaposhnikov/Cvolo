using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Core.Packages;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Packaging;

/// <summary>
/// Rehydrates source-backed generic templates from Sector 5. Generic bodies cannot be represented
/// by Sector 1 ABI metadata: they must be monomorphized in the consuming compilation after concrete
/// type arguments are known.
/// </summary>
public static class PackageTemplateSource
{
	public static IReadOnlyList<CompilationUnitSyntax> Read(CvlArchive archive, string packageId, string version)
	{
		ArgumentNullException.ThrowIfNull(archive);
		var sourceBuffer = archive.ReadSourceBuffer();
		if (string.IsNullOrEmpty(sourceBuffer))
			return [];

		var parsedUnits = new List<(CompilationUnitSyntax Unit, CompilationContext Context)>();
		foreach (var sourceFile in PackageSourceBundle.Parse(sourceBuffer))
		{
			var filePath = $"<package-source:{packageId}@{version}:{sourceFile.RelativePath}>";
			var context = new CompilationContext(sourceFile.Source, filePath);
			var parser = new AntlrSyntaxParser();
			var unit = parser.Parse(context);
			if (unit is null || parser.Diagnostics.HasErrors)
			{
				var detail = string.Join(Environment.NewLine, parser.Diagnostics.Diagnostics.Select(d => d.Message));
				throw new PackageException(
					PackageDiagnosticIds.InvalidSourceBundle,
					$"Package '{packageId}@{version}' contains invalid Sector 5 source.",
					detail);
			}

			parsedUnits.Add((unit, context));
		}

		var contractNames = CollectPublicContractNames(parsedUnits.Select(item => item.Unit));
		var templates = new List<CompilationUnitSyntax>();
		foreach (var (unit, context) in parsedUnits)
		{
			var ns = unit.NamespaceDeclaration;
			var members = ns is null ? unit.Members : ns.Members;
			var sourceBackedMembers = members.Where(member => IsSourceBackedPublicDeclaration(member, contractNames)).ToArray();
			if (sourceBackedMembers.Length == 0)
				continue;

			NamespaceDeclarationSyntax? templateNamespace = null;
			IReadOnlyList<SyntaxNode> topLevelMembers = sourceBackedMembers;
			if (ns is not null)
			{
				templateNamespace = new NamespaceDeclarationSyntax(ns.Span, ns.Name, ns.Usings, sourceBackedMembers);
				topLevelMembers = [];
			}

			templates.Add(new CompilationUnitSyntax(unit.Span, context, unit.Usings, templateNamespace, topLevelMembers));
		}

		return templates;
	}

	internal static HashSet<string> CollectPublicContractNames(IEnumerable<CompilationUnitSyntax> units)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration;
			var members = ns is null ? unit.Members : ns.Members;
			foreach (var member in members)
			{
				string? name = member switch
				{
					InterfaceDeclarationSyntax type when type.Visibility == Visibility.Public => type.Name,
					ProtocolDeclarationSyntax type when type.Visibility == Visibility.Public => type.Name,
					_ => null
				};

				if (name is null)
					continue;

				names.Add(name);
				if (!string.IsNullOrWhiteSpace(ns?.Name))
					names.Add($"{ns!.Name}.{name}");
			}
		}

		return names;
	}

	internal static bool IsSourceBackedPublicDeclaration(SyntaxNode node, IReadOnlySet<string> contractNames) => node switch
	{
		FunctionDeclarationSyntax function => function.Visibility == Visibility.Public
			&& (function.GenericParameters.Count > 0 || function.Parameters.Any(parameter => ReferencesContract(parameter.Type, contractNames))),
		StructDeclarationSyntax type => type.Visibility == Visibility.Public && type.GenericParameters.Count > 0,
		InterfaceDeclarationSyntax type => type.Visibility == Visibility.Public,
		ProtocolDeclarationSyntax type => type.Visibility == Visibility.Public,
		ExtensionDeclarationSyntax extension => extension.Visibility == Visibility.Public,
		_ => false
	};

	internal static bool ReferencesContract(string typeText, IReadOnlySet<string> contractNames)
	{
		var text = typeText.Trim();
		if (text.StartsWith("refvar ", StringComparison.Ordinal))
			text = text[7..].Trim();
		else if (text.StartsWith("ref ", StringComparison.Ordinal))
			text = text[4..].Trim();

		var genericStart = text.IndexOf('<');
		if (genericStart >= 0)
			text = text[..genericStart].Trim();

		return contractNames.Contains(text);
	}
}
