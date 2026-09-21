using System.Text.Json;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Packaging;

/// <summary>
/// Language-level public API carried in Sector 1 layout metadata. This is compiler metadata,
/// not a C ABI surface: package consumers bind ordinary Cvolo declarations against these
/// synthetic units while Sector 3 supplies executable implementations and data definitions.
/// </summary>
public sealed class PackageApiMetadata
{
	public const string FormatId = "cvolo.package-api.v2";
	private const string LegacyFormatId = "cvolo.package-api.v1";

	public string Format { get; init; } = FormatId;
	public IReadOnlyList<PackageApiUnit> Units { get; init; } = [];
	public IReadOnlyList<NativeLibraryInfo> NativeLibraries { get; init; } = [];

	public static PackageApiMetadata FromCompilationUnits(IEnumerable<CompilationUnitSyntax> units)
	{
		var compilationUnits = units.ToArray();
		var sourceBackedContractNames = PackageTemplateSource.CollectPublicContractNames(compilationUnits);
		var apiUnits = new List<PackageApiUnit>();
		foreach (var unit in compilationUnits)
		{
			var ns = unit.NamespaceDeclaration;
			var members = ns is null ? unit.Members : ns.Members;
			var functions = members
				.OfType<FunctionDeclarationSyntax>()
				.Where(function => function.Visibility == Visibility.Public
					&& function.GenericParameters.Count == 0
					&& !function.Parameters.Any(parameter => PackageTemplateSource.ReferencesContract(parameter.Type, sourceBackedContractNames)))
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

			var structs = members
				.OfType<StructDeclarationSyntax>()
				.Where(type => type.Visibility == Visibility.Public && type.GenericParameters.Count == 0)
				.Select(type => new PackageApiStruct
				{
					Name = type.Name,
					EmbeddedType = type.EmbeddedType,
					Fields = type.Fields.Select(field => new PackageApiStructField(field.Type, field.Name, field.Visibility)).ToArray(),
					Attributes = SerializableAttributeNames(type.Attributes)
				})
				.ToArray();

			var enums = members
				.OfType<EnumDeclarationSyntax>()
				.Where(type => type.Visibility == Visibility.Public)
				.Select(type => new PackageApiEnum
				{
					Name = type.Name,
					StorageType = type.StorageType,
					Attributes = SerializableAttributeNames(type.Attributes),
					Variants = type.Variants.Select(SerializeVariant).ToArray()
				})
				.ToArray();

			var globals = members
				.OfType<GlobalVariableDeclarationSyntax>()
				.Where(global => global.Visibility == Visibility.Public)
				.Select(global => new PackageApiGlobal
				{
					Name = global.Name,
					Type = global.Type,
					IsMutable = global.IsMutable
				})
				.ToArray();

			var delegates = members
				.OfType<DelegateDeclarationSyntax>()
				.Where(type => type.Visibility == Visibility.Public && type.GenericParameters.Count == 0)
				.Select(type => new PackageApiDelegate
				{
					Name = type.Name,
					ReturnType = type.ReturnType,
					Parameters = type.Parameters
						.Select(parameter => new PackageApiParameter(parameter.Type, parameter.Name))
						.ToArray(),
					IsNative = type.IsNative,
					CallingConvention = type.CallingConvention
				})
				.ToArray();

			if (functions.Length == 0 && structs.Length == 0 && enums.Length == 0 && globals.Length == 0 && delegates.Length == 0)
				continue;

			apiUnits.Add(new PackageApiUnit
			{
				Namespace = ns?.Name,
				FileUsings = unit.Usings.Where(u => !u.IsExposed).Select(u => u.NamespaceName).ToArray(),
				NamespaceUsings = ns?.Usings.Where(u => !u.IsExposed).Select(u => u.NamespaceName).ToArray() ?? [],
				Functions = functions,
				Structs = structs,
				Enums = enums,
				Globals = globals,
				Delegates = delegates
			});
		}

		return new PackageApiMetadata
		{
			Units = NormalizeUnits(apiUnits),
			NativeLibraries = CollectNativeLibraries(compilationUnits)
		};
	}

	private static IReadOnlyList<NativeLibraryInfo> CollectNativeLibraries(IEnumerable<CompilationUnitSyntax> units)
	{
		var libraries = new Dictionary<string, NativeLibraryInfo>(StringComparer.Ordinal);
		foreach (var unit in units)
		{
			var ns = unit.NamespaceDeclaration;
			var members = ns is null ? unit.Members : ns.Members;
			foreach (var block in members.OfType<ExternBlockSyntax>())
			{
				foreach (var attr in block.Attributes.Where(attribute => string.Equals(attribute.Name, "LibraryImport", StringComparison.Ordinal)))
				{
					var libraryName = ExtractStringArgument(attr, null);
					if (string.IsNullOrWhiteSpace(libraryName))
						continue;

					libraries[libraryName] = new NativeLibraryInfo(
						libraryName,
						ExtractStringArgument(attr, "win"),
						ExtractStringArgument(attr, "linux"),
						ExtractStringArgument(attr, "mac"));
				}
			}
		}

		return libraries.Values.ToArray();
	}

	private static string? ExtractStringArgument(AttributeSyntax attr, string? name)
	{
		for (var i = 0; i < attr.Arguments.Count; i++)
		{
			var argumentName = attr.ArgumentNames.Count > i ? attr.ArgumentNames[i] : null;
			if (!string.Equals(argumentName, name, StringComparison.Ordinal))
				continue;

			return attr.Arguments[i] is StringLiteralExpressionSyntax literal ? literal.Value : null;
		}

		return null;
	}

	private static IReadOnlyList<PackageApiUnit> NormalizeUnits(IReadOnlyList<PackageApiUnit> units) => units
		.GroupBy(unit => (
			unit.Namespace,
			FileUsings: string.Join("\u001f", unit.FileUsings),
			NamespaceUsings: string.Join("\u001f", unit.NamespaceUsings)))
		.Select(group => new PackageApiUnit
		{
			Namespace = group.Key.Namespace,
			FileUsings = group.First().FileUsings,
			NamespaceUsings = group.First().NamespaceUsings,
			Functions = group.SelectMany(unit => unit.Functions)
				.DistinctBy(function => (
					function.Name,
					Parameters: string.Join("\u001f", function.Parameters.Select(parameter => parameter.Type)),
					function.Modifier))
				.ToArray(),
			Structs = group.SelectMany(unit => unit.Structs)
				.DistinctBy(type => type.Name)
				.ToArray(),
			Enums = group.SelectMany(unit => unit.Enums)
				.DistinctBy(type => type.Name)
				.ToArray(),
			Globals = group.SelectMany(unit => unit.Globals)
				.DistinctBy(global => global.Name)
				.ToArray(),
			Delegates = group.SelectMany(unit => unit.Delegates)
				.DistinctBy(type => type.Name)
				.ToArray()
		})
		.ToArray();

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
			return api is not null && (string.Equals(api.Format, FormatId, StringComparison.Ordinal)
				|| string.Equals(api.Format, LegacyFormatId, StringComparison.Ordinal))
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
			var members = new List<SyntaxNode>();

			members.AddRange(unit.Structs.Select(type => (SyntaxNode)new StructDeclarationSyntax(
				span,
				type.Name,
				[],
				type.Fields.Select(field => new StructFieldSyntax(span, field.Type, field.Name, field.Visibility)).ToArray(),
				type.EmbeddedType,
				CreateAttributes(type.Attributes, span),
				Visibility.Public)));

			members.AddRange(unit.Enums.Select(type => (SyntaxNode)new EnumDeclarationSyntax(
				span,
				type.Name,
				type.StorageType,
				type.Variants.Select(variant => new EnumVariantDeclarationSyntax(span, variant.Name, CreateEnumValue(variant, span))).ToArray(),
				CreateAttributes(type.Attributes, span),
				Visibility.Public)));

			members.AddRange(unit.Globals.Select(global => (SyntaxNode)new GlobalVariableDeclarationSyntax(
				span,
				global.Type,
				global.Name,
				null,
				global.IsMutable,
				Visibility.Public)));

			members.AddRange(unit.Functions.Select(function =>
				(SyntaxNode)new FunctionDeclarationSyntax(
					span,
					function.ReturnType,
					function.Name,
					function.GenericParameters,
					function.Parameters.Select(parameter => new ParameterSyntax(span, parameter.Type, parameter.Name)).ToArray(),
					null!,
					modifier: function.Modifier,
					visibility: Visibility.Public)));

			members.AddRange(unit.Delegates.Select(type =>
				(SyntaxNode)new DelegateDeclarationSyntax(
					span,
					type.ReturnType,
					type.Name,
					[],
					type.Parameters.Select(parameter => new ParameterSyntax(span, parameter.Type, parameter.Name)).ToArray(),
					visibility: Visibility.Public,
					isNative: type.IsNative,
					callingConvention: type.CallingConvention)));

			var fileUsings = unit.FileUsings.Select(ns => new UsingDirectiveSyntax(span, ns)).ToArray();
			NamespaceDeclarationSyntax? namespaceDeclaration = null;
			IReadOnlyList<SyntaxNode> topLevelMembers = members;
			if (!string.IsNullOrWhiteSpace(unit.Namespace))
			{
				var namespaceUsings = unit.NamespaceUsings.Select(ns => new UsingDirectiveSyntax(span, ns)).ToArray();
				namespaceDeclaration = new NamespaceDeclarationSyntax(span, unit.Namespace!, namespaceUsings, members);
				topLevelMembers = [];
			}

			result.Add(new CompilationUnitSyntax(span, context, fileUsings, namespaceDeclaration, topLevelMembers));
		}

		return result;
	}

	private static IReadOnlyList<string> SerializableAttributeNames(IReadOnlyList<AttributeSyntax> attributes) =>
		attributes.Where(attribute => attribute.Arguments.Count == 0).Select(attribute => attribute.Name).ToArray();

	private static IReadOnlyList<AttributeSyntax> CreateAttributes(IReadOnlyList<string> names, TextSpan span) =>
		names.Select(name => new AttributeSyntax(span, name, [])).ToArray();

	private static PackageApiEnumVariant SerializeVariant(EnumVariantDeclarationSyntax variant) =>
		new() { Name = variant.Name, Value = SerializeEnumExpression(variant.Value) };

	private static PackageApiEnumExpression? SerializeEnumExpression(ExpressionSyntax? expression) => expression switch
	{
		null => null,
		IntegerLiteralExpressionSyntax integer => new PackageApiEnumExpression
		{
			Kind = "integer",
			IntegerValue = integer.Value,
			IntegerType = integer.LiteralType
		},
		IdentifierExpressionSyntax identifier => new PackageApiEnumExpression
		{
			Kind = "identifier",
			Identifier = identifier.Name
		},
		UnaryExpressionSyntax unary => new PackageApiEnumExpression
		{
			Kind = "unary",
			Operator = unary.Operator,
			Left = SerializeEnumExpression(unary.Operand)
		},
		BinaryExpressionSyntax binary => new PackageApiEnumExpression
		{
			Kind = "binary",
			Operator = binary.Operator,
			Left = SerializeEnumExpression(binary.Left),
			Right = SerializeEnumExpression(binary.Right)
		},
		_ => throw new PackageException("PACKAGE_API", "Unsupported public enum constant expression.",
			$"Public enum metadata currently supports integer, identifier, unary, and binary constant expressions; found {expression.Kind}.")
	};

	private static ExpressionSyntax? CreateEnumValue(PackageApiEnumVariant variant, TextSpan span) =>
		CreateEnumExpression(variant.Value, span);

	private static ExpressionSyntax? CreateEnumExpression(PackageApiEnumExpression? expression, TextSpan span)
	{
		if (expression is null)
			return null;
		return expression.Kind switch
		{
			"integer" when expression.IntegerValue is not null => new IntegerLiteralExpressionSyntax(span, expression.IntegerValue.Value, expression.IntegerType),
			"identifier" when !string.IsNullOrWhiteSpace(expression.Identifier) => new IdentifierExpressionSyntax(span, expression.Identifier!),
			"unary" when expression.Left is not null => new UnaryExpressionSyntax(span, expression.Operator ?? string.Empty, CreateEnumExpression(expression.Left, span)!),
			"binary" when expression.Left is not null && expression.Right is not null => new BinaryExpressionSyntax(
				span,
				CreateEnumExpression(expression.Left, span)!,
				expression.Operator ?? string.Empty,
				CreateEnumExpression(expression.Right, span)!),
			_ => throw new PackageException("PACKAGE_API", "Malformed public enum metadata.")
		};
	}

}

public sealed class PackageApiUnit
{
	public string? Namespace { get; init; }
	public IReadOnlyList<string> FileUsings { get; init; } = [];
	public IReadOnlyList<string> NamespaceUsings { get; init; } = [];
	public IReadOnlyList<PackageApiFunction> Functions { get; init; } = [];
	public IReadOnlyList<PackageApiStruct> Structs { get; init; } = [];
	public IReadOnlyList<PackageApiEnum> Enums { get; init; } = [];
	public IReadOnlyList<PackageApiGlobal> Globals { get; init; } = [];
	public IReadOnlyList<PackageApiDelegate> Delegates { get; init; } = [];
}

public sealed class PackageApiFunction
{
	public string Name { get; init; } = string.Empty;
	public string ReturnType { get; init; } = "void";
	public IReadOnlyList<string> GenericParameters { get; init; } = [];
	public IReadOnlyList<PackageApiParameter> Parameters { get; init; } = [];
	public SafetyTier? Modifier { get; init; }
}

public sealed class PackageApiStruct
{
	public string Name { get; init; } = string.Empty;
	public string? EmbeddedType { get; init; }
	public IReadOnlyList<string> Attributes { get; init; } = [];
	public IReadOnlyList<PackageApiStructField> Fields { get; init; } = [];
}

public sealed class PackageApiEnum
{
	public string Name { get; init; } = string.Empty;
	public string? StorageType { get; init; }
	public IReadOnlyList<string> Attributes { get; init; } = [];
	public IReadOnlyList<PackageApiEnumVariant> Variants { get; init; } = [];
}

public sealed class PackageApiGlobal
{
	public string Name { get; init; } = string.Empty;
	public string Type { get; init; } = string.Empty;
	public bool IsMutable { get; init; }
}

public sealed class PackageApiDelegate
{
	public string Name { get; init; } = string.Empty;
	public string ReturnType { get; init; } = "void";
	public IReadOnlyList<PackageApiParameter> Parameters { get; init; } = [];
	public bool IsNative { get; init; }
	public string? CallingConvention { get; init; }
}

public sealed class PackageApiEnumVariant
{
	public string Name { get; init; } = string.Empty;
	public PackageApiEnumExpression? Value { get; init; }
}

public sealed class PackageApiEnumExpression
{
	public string Kind { get; init; } = string.Empty;
	public ulong? IntegerValue { get; init; }
	public string? IntegerType { get; init; }
	public string? Identifier { get; init; }
	public string? Operator { get; init; }
	public PackageApiEnumExpression? Left { get; init; }
	public PackageApiEnumExpression? Right { get; init; }
}

public sealed record PackageApiStructField(string Type, string Name, Visibility Visibility);
public sealed record PackageApiParameter(string Type, string Name);
