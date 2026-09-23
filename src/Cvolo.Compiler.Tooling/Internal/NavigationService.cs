using Cvolo.Analysis;
using Cvolo.Analysis.Completion;
using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.AST.Expressions;
using CoreTextSpan = Cvolo.Core.Diagnostics.TextSpan;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Public entry points for the tooling navigation API. Each call resolves against exactly the
/// snapshot it is given; no newer snapshot, disk state, or mutable workspace state is consulted.
/// </summary>
internal static class NavigationService
{
	internal static SymbolLookupResult? GetSymbol(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
		=> snapshot.GetNavigationIndex().Lookup(document.Id, position);

	internal static IReadOnlyList<SymbolDefinition> GetDefinitions(ProjectSnapshot snapshot, SymbolId symbol)
		=> snapshot.GetNavigationIndex().Definitions(symbol);

	internal static IReadOnlyList<DocumentSymbolInfo> GetDocumentSymbols(ProjectSnapshot snapshot, DocumentId document)
		=> snapshot.GetNavigationIndex().DocumentSymbols(document);
}

/// <summary>
/// Snapshot-scoped index of semantic symbols. Declaration entries are built structurally from the
/// parsed syntax; occurrence resolution delegates to the compiler's own binding
/// (<see cref="CompletionQuery.ResolveSymbol"/>) and is therefore never matched by raw text.
/// </summary>
internal sealed class NavigationIndex
{
	private readonly Guid _token = Guid.NewGuid();
	private readonly ProjectSnapshot _snapshot;
	private readonly AnalyzedProject _analysis;
	private readonly BindingContext? _binderContext;
	private readonly Dictionary<SyntaxNode, Entry> _byDeclaration = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<int, Entry> _byId = new();
	private readonly Dictionary<DocumentId, IReadOnlyList<DocumentSymbolInfo>> _outlines = new();
	private readonly Dictionary<Entry, Entry> _conformanceTargets = new();
	private int _nextId;

	private NavigationIndex(ProjectSnapshot snapshot)
	{
		_snapshot = snapshot;
		_analysis = snapshot.GetAnalysis();
		_binderContext = _analysis.BinderContext;
		BuildDeclarations();
		BuildExternalDeclarations();
		BuildConformanceRedirects();
		BuildOutlines();
	}

	internal static NavigationIndex Build(ProjectSnapshot snapshot) => new(snapshot);

	internal SymbolLookupResult? Lookup(DocumentId document, int position)
	{
		if (!_snapshot.TryGetDocument(document, out _))
			throw new KeyNotFoundException($"Document {document} not found in this snapshot.");

		if (_binderContext is null || !_analysis.UnitsByDocument.TryGetValue(document, out var unit) || unit is null)
			return null;

		ResolvedSymbol? resolved;
		lock (_binderContext)
		{
			resolved = CompletionQuery.ResolveSymbol(_binderContext, unit, position);
		}

		if (resolved is null || !_byDeclaration.TryGetValue(resolved.Declaration, out var entry))
			return null;

		// An extension method that implements an interface member navigates to the interface's
		// declaration (F12). Calls to the method keep the implementation: only the declaration
		// name itself, which is inside the method's own name span, is redirected.
		if (resolved.Declaration is FunctionDeclarationSyntax method &&
			position >= method.NameSpan.Start && position <= method.NameSpan.End &&
			_conformanceTargets.TryGetValue(entry, out var interfaceEntry))
		{
			entry = interfaceEntry;
		}

		return new SymbolLookupResult(
			entry.Id,
			new TextSpan(resolved.SubjectSpan.Start, resolved.SubjectSpan.Length),
			MapKind(resolved.Kind),
			resolved.Name,
			resolved.DisplayText,
			resolved.Documentation,
			NativeInteropFor(resolved.Declaration));
	}

	internal IReadOnlyList<SymbolDefinition> Definitions(SymbolId symbol)
	{
		if (symbol.SnapshotToken != _token)
			return [];

		return _byId.TryGetValue(symbol.Value, out var entry) ? entry.Definitions : [];
	}

	internal IReadOnlyList<DocumentSymbolInfo> DocumentSymbols(DocumentId document)
	{
		if (!_snapshot.TryGetDocument(document, out _))
			throw new KeyNotFoundException($"Document {document} not found in this snapshot.");

		return _outlines.TryGetValue(document, out var outline) ? outline : [];
	}

	private void BuildDeclarations()
	{
		foreach (var (documentId, unit) in _analysis.UnitsByDocument)
		{
			if (unit is null)
				continue;

			var source = _snapshot.GetDocument(documentId).Text.ToString();
			foreach (var member in Members(unit))
				IndexMember(documentId, source, member);
		}

		AliasExternBlockGlobalDeclarations();
	}

	private void AliasExternBlockGlobalDeclarations()
	{
		if (_binderContext is null)
			return;

		foreach (var (synthetic, _) in _binderContext.GlobalVariables)
		{
			if (_byDeclaration.ContainsKey(synthetic))
				continue;

			foreach (var (declaration, entry) in _byDeclaration.ToArray())
			{
				if (declaration is GlobalVariableDeclarationSyntax parsed
					&& parsed.Name == synthetic.Name
					&& parsed.Span.Start == synthetic.Span.Start
					&& parsed.Span.Length == synthetic.Span.Length)
				{
					_byDeclaration[synthetic] = entry;
					break;
				}
			}
		}
	}

	/// <summary>
	/// Adds package API declarations to the snapshot navigation identity map. External declarations
	/// intentionally have no local <see cref="SymbolDefinition"/> because they originate in package
	/// metadata rather than an editable workspace document.
	/// </summary>
	private void BuildExternalDeclarations()
	{
		foreach (var external in _snapshot.ExternalUnits)
		{
			foreach (var member in Members(external.Unit))
				IndexExternalMember(member);
		}
	}

	/// <summary>
	/// Package API declarations participate in semantic lookup but do not have a local document
	/// definition. Indexing their syntax nodes still gives hover/navigation a stable snapshot-local
	/// symbol identity; GetDefinitions correctly returns an empty list for such external symbols.
	/// </summary>
	private void IndexExternalMember(SyntaxNode node)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax function:
				RegisterExternal(function, function.Name, ToolingSymbolKind.Function);
				foreach (var parameter in function.Parameters)
					RegisterExternal(parameter, parameter.Name, ToolingSymbolKind.Parameter);
				break;
			case StructDeclarationSyntax structDeclaration:
				RegisterExternal(structDeclaration, structDeclaration.Name, ToolingSymbolKind.Struct);
				foreach (var field in structDeclaration.Fields)
					RegisterExternal(field, field.Name, ToolingSymbolKind.Field, structDeclaration.Name);
				break;
			case UnionDeclarationSyntax unionDeclaration:
				RegisterExternal(unionDeclaration, unionDeclaration.Name, ToolingSymbolKind.Union);
				foreach (var field in unionDeclaration.Fields)
					RegisterExternal(field, field.Name, ToolingSymbolKind.Field, unionDeclaration.Name);
				break;
			case EnumDeclarationSyntax enumDeclaration:
				RegisterExternal(enumDeclaration, enumDeclaration.Name, ToolingSymbolKind.Enum);
				foreach (var variant in enumDeclaration.Variants)
					RegisterExternal(variant, variant.Name, ToolingSymbolKind.EnumMember, enumDeclaration.Name);
				break;
			case DelegateDeclarationSyntax delegateDeclaration:
				RegisterExternal(delegateDeclaration, delegateDeclaration.Name, ToolingSymbolKind.Delegate);
				break;
			case GlobalVariableDeclarationSyntax global:
				RegisterExternal(global, global.Name, ToolingSymbolKind.Global);
				break;
			case TypeAliasDeclarationSyntax typeAlias:
				RegisterExternal(typeAlias, typeAlias.Name, ToolingSymbolKind.TypeAlias);
				break;
		}
	}

	/// <summary>
	/// Links each extension method that satisfies an interface conformance to the interface
	/// member declaration it implements, so go-to-definition on the method name lands on the
	/// contract. The match mirrors the compiler's own conformance check (name, return type and
	/// parameter types); unresolved or structural matches are skipped.
	/// </summary>
	private void BuildConformanceRedirects()
	{
		if (_binderContext is null)
			return;

		foreach (var (_, unit) in _analysis.UnitsByDocument)
		{
			if (unit is null)
				continue;

			foreach (var member in Members(unit))
			{
				if (member is not ExtensionDeclarationSyntax extension || string.IsNullOrEmpty(extension.ConformsTo))
					continue;

				if (ResolveInterfaceDeclaration(unit, extension.ConformsTo!) is not { } interfaceDeclaration)
					continue;

				foreach (var method in extension.Methods)
				{
					if (!_byDeclaration.TryGetValue(method, out var methodEntry))
						continue;

					var interfaceMember = interfaceDeclaration.Members.FirstOrDefault(candidate => Implements(method, candidate));
					if (interfaceMember is not null && _byDeclaration.TryGetValue(interfaceMember, out var interfaceEntry))
						_conformanceTargets[methodEntry] = interfaceEntry;
				}
			}
		}
	}

	private InterfaceDeclarationSyntax? ResolveInterfaceDeclaration(CompilationUnitSyntax unit, string conformsTo)
	{
		var context = _binderContext!;
		lock (context)
		{
			var previousUnit = context.CurrentUnit;
			var previousNamespace = context.CurrentNamespace;
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			try
			{
				if (context.ResolveType(context.NormalizeGenericName(conformsTo)) is not InterfaceTypeSymbol interfaceSymbol)
					return null;

				return context.InterfaceTemplates.TryGetValue(interfaceSymbol.Name, out var declaration) ? declaration : null;
			}
			finally
			{
				context.CurrentUnit = previousUnit;
				context.CurrentNamespace = previousNamespace;
			}
		}
	}

	private static bool Implements(FunctionDeclarationSyntax method, InterfaceMethodDeclarationSyntax member)
	{
		if (method.Name != member.Name || method.ReturnType != member.ReturnType)
			return false;

		if (method.Parameters.Count != member.Parameters.Count)
			return false;

		for (var i = 0; i < method.Parameters.Count; i++)
		{
			if (method.Parameters[i].Type != member.Parameters[i].Type)
				return false;
		}

		return true;
	}

	private void IndexMember(DocumentId documentId, string source, SyntaxNode node)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax function:
				Register(documentId, source, function, function.Name, ToolingSymbolKind.Function, function.NameSpan);
				IndexParameters(documentId, source, function.Parameters);
				IndexLocals(documentId, source, function.Body);
				break;

			case ConstructorDeclarationSyntax constructor:
				Register(documentId, source, constructor, constructor.StructName, ToolingSymbolKind.Constructor, constructor.NameSpan);
				IndexParameters(documentId, source, constructor.Parameters);
				IndexLocals(documentId, source, constructor.Body);
				break;

			case ExtensionDeclarationSyntax extension:
				{
					var owner = Leaf(extension.ExtendedTypeName);
					Register(documentId, source, extension, $"extension {owner}", ToolingSymbolKind.OtherType, null);
					foreach (var method in extension.Methods)
					{
						Register(documentId, source, method, method.Name, method.Name.StartsWith('~') ? ToolingSymbolKind.Destructor : ToolingSymbolKind.ExtensionMethod, method.NameSpan, owner);
						IndexParameters(documentId, source, method.Parameters, owner);
						IndexLocals(documentId, source, method.Body);
					}

					foreach (var constructor in extension.Constructors)
					{
						Register(documentId, source, constructor, constructor.StructName, ToolingSymbolKind.Constructor, constructor.NameSpan, owner);
						IndexParameters(documentId, source, constructor.Parameters, owner);
						IndexLocals(documentId, source, constructor.Body);
					}

					foreach (var destructor in extension.Destructors)
						Register(documentId, source, destructor, destructor.StructName, ToolingSymbolKind.Destructor, null, owner);
					break;
				}

			case StructDeclarationSyntax structDeclaration:
				Register(documentId, source, structDeclaration, structDeclaration.Name, ToolingSymbolKind.Struct, null);
				foreach (var field in structDeclaration.Fields)
					Register(documentId, source, field, field.Name, ToolingSymbolKind.Field, null, structDeclaration.Name);
				break;

			case UnionDeclarationSyntax unionDeclaration:
				Register(documentId, source, unionDeclaration, unionDeclaration.Name, ToolingSymbolKind.Union, null);
				foreach (var field in unionDeclaration.Fields)
					Register(documentId, source, field, field.Name, ToolingSymbolKind.Field, null, unionDeclaration.Name);
				break;

			case EnumDeclarationSyntax enumDeclaration:
				Register(documentId, source, enumDeclaration, enumDeclaration.Name, ToolingSymbolKind.Enum, null);
				foreach (var variant in enumDeclaration.Variants)
					Register(documentId, source, variant, variant.Name, ToolingSymbolKind.EnumMember, null, enumDeclaration.Name);
				break;

			case InterfaceDeclarationSyntax interfaceDeclaration:
				Register(documentId, source, interfaceDeclaration, interfaceDeclaration.Name, ToolingSymbolKind.Interface, null);
				foreach (var member in interfaceDeclaration.Members)
					Register(documentId, source, member, member.Name, ToolingSymbolKind.Method, null, interfaceDeclaration.Name);
				break;

			case ProtocolDeclarationSyntax protocolDeclaration:
				Register(documentId, source, protocolDeclaration, protocolDeclaration.Name, ToolingSymbolKind.Protocol, null);
				foreach (var member in protocolDeclaration.Members)
					Register(documentId, source, member, member.Name, ToolingSymbolKind.Method, null, protocolDeclaration.Name);
				break;

			case DelegateDeclarationSyntax delegateDeclaration:
				Register(documentId, source, delegateDeclaration, delegateDeclaration.Name, ToolingSymbolKind.Delegate, null);
				break;

			case TypeAliasDeclarationSyntax typeAlias:
				Register(documentId, source, typeAlias, typeAlias.Name, ToolingSymbolKind.TypeAlias, null);
				break;

			case GlobalVariableDeclarationSyntax global:
				Register(documentId, source, global, global.Name, ToolingSymbolKind.Global, null);
				break;

			case ExternDeclarationSyntax externDeclaration:
				Register(documentId, source, externDeclaration, externDeclaration.Name, ToolingSymbolKind.Function, null);
				break;

			case ExternBlockSyntax externBlock:
				foreach (var function in externBlock.Functions)
					Register(documentId, source, function, function.Name, ToolingSymbolKind.Function, null);
				foreach (var global in externBlock.Globals)
					Register(documentId, source, global, global.Name, ToolingSymbolKind.Global, null);
				break;
		}
	}

	private void IndexParameters(DocumentId documentId, string source, IReadOnlyList<ParameterSyntax> parameters, string? owner = null)
	{
		foreach (var parameter in parameters)
			Register(documentId, source, parameter, parameter.Name, ToolingSymbolKind.Parameter, null, owner);
	}

	private void IndexLocals(DocumentId documentId, string source, SyntaxNode? node)
	{
		if (node is null)
			return;

		switch (node)
		{
			case VariableDeclarationSyntax variable:
				Register(documentId, source, variable, variable.Name, ToolingSymbolKind.Local, null);
				break;
			case ForEachStatementSyntax forEach:
				Register(documentId, source, forEach, forEach.ItemName, ToolingSymbolKind.Local, null);
				break;
			case CatchClauseSyntax clause when clause.BindingName is not null:
				Register(documentId, source, clause, clause.BindingName, ToolingSymbolKind.Local, null);
				break;
			case SwitchCaseSyntax switchCase when switchCase.VariableName is not null:
				Register(documentId, source, switchCase, switchCase.VariableName, ToolingSymbolKind.Local, null);
				break;
		}

		foreach (var child in node.GetChildren())
			IndexLocals(documentId, source, child);
	}

	private Entry Register(DocumentId documentId, string source, SyntaxNode declaration, string name, ToolingSymbolKind kind, CoreTextSpan? selection, string? owner = null)
	{
		if (_byDeclaration.TryGetValue(declaration, out var existing))
			return existing;

		var selectionSpan = selection is { } precise && precise.Start >= 0 && precise.Length >= 0
			? new TextSpan(precise.Start, precise.Length)
			: ComputeNameSpan(source, declaration.Span, name);

		var declarationSpan = new TextSpan(Math.Max(declaration.Span.Start, 0), Math.Max(declaration.Span.Length, 0));
		var entry = new Entry(new SymbolId(_token, _nextId++), name, kind, owner);
		entry.Definitions.Add(new SymbolDefinition(documentId, declarationSpan, selectionSpan));
		_byDeclaration[declaration] = entry;
		_byId[entry.Id.Value] = entry;
		return entry;
	}

	private Entry RegisterExternal(SyntaxNode declaration, string name, ToolingSymbolKind kind, string? owner = null)
	{
		if (_byDeclaration.TryGetValue(declaration, out var existing))
			return existing;

		var entry = new Entry(new SymbolId(_token, _nextId++), name, kind, owner);
		_byDeclaration[declaration] = entry;
		_byId[entry.Id.Value] = entry;
		return entry;
	}

	private NativeInteropMetadata? NativeInteropFor(SyntaxNode declaration)
	{
		if (declaration is DelegateDeclarationSyntax { IsNative: true } nativeDelegate)
		{
			return new NativeInteropMetadata(
				NativeInteropKind.NativeDelegate,
				nativeDelegate.CallingConvention ?? "C");
		}

		if (declaration is UnionDeclarationSyntax { IsUnsafe: true })
			return new NativeInteropMetadata(NativeInteropKind.RawUnion);

		if (declaration is not GlobalVariableDeclarationSyntax global)
			return null;

		var boundGlobal = _binderContext?.GlobalVariables
			.FirstOrDefault(pair => ReferenceEquals(pair.Node, global)).Symbol;
		if (!global.IsForeign && boundGlobal?.IsForeign != true)
			return null;
		string? libraryName = boundGlobal?.LibraryName;
		string? importName = boundGlobal?.ImportName;
		string? winPath = boundGlobal?.WinPath;
		string? linuxPath = boundGlobal?.LinuxPath;
		string? macPath = boundGlobal?.MacPath;
		foreach (var attribute in global.Attributes)
		{
			if (attribute.Name is "ImportName" or "ImportNameAttribute")
			{
				if (attribute.Arguments.FirstOrDefault() is StringLiteralExpressionSyntax imported)
					importName = imported.Value;
				continue;
			}

			if (attribute.Name is not ("LibraryImport" or "LibraryImportAttribute"))
				continue;

			for (var i = 0; i < attribute.Arguments.Count; i++)
			{
				if (attribute.Arguments[i] is not StringLiteralExpressionSyntax literal)
					continue;
				var argumentName = i < attribute.ArgumentNames.Count ? attribute.ArgumentNames[i] : null;
				switch (argumentName)
				{
					case null when i == 0: libraryName = literal.Value; break;
					case "win": winPath = literal.Value; break;
					case "linux": linuxPath = literal.Value; break;
					case "mac": macPath = literal.Value; break;
				}
			}
		}

		return new NativeInteropMetadata(
			NativeInteropKind.ForeignGlobal,
			boundGlobal?.CallingConvention ?? global.CallingConvention ?? "C",
			importName,
			libraryName,
			winPath,
			linuxPath,
			macPath);
	}

	private void BuildOutlines()
	{
		foreach (var (documentId, unit) in _analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				_outlines[documentId] = [];
				continue;
			}

			var source = _snapshot.GetDocument(documentId).Text.ToString();
			var roots = new List<DocumentSymbolInfo>();
			foreach (var member in Members(unit))
			{
				if (member is ExternBlockSyntax externBlock)
				{
					foreach (var child in externBlock.Functions.Cast<SyntaxNode>().Concat(externBlock.Globals))
					{
						if (MakeOutline(source, child) is { } externItem)
							roots.Add(externItem);
					}
					continue;
				}

				if (MakeOutline(source, member) is { } item)
					roots.Add(item);
			}

			_outlines[documentId] = [.. roots.OrderBy(root => root.Range.Start)];
		}
	}

	private DocumentSymbolInfo? MakeOutline(string source, SyntaxNode node)
	{
		if (!_byDeclaration.TryGetValue(node, out var entry))
			return null;

		var children = new List<DocumentSymbolInfo>();
		foreach (var child in OutlineChildren(node))
		{
			if (MakeOutline(source, child) is { } childItem)
				children.Add(childItem);
		}

		return new DocumentSymbolInfo(
			entry.Id,
			entry.Name,
			CompletionQuery.DescribeDeclaration(node, entry.Name),
			entry.Kind,
			new TextSpan(node.Span.Start, node.Span.Length),
			entry.Definitions[0].SelectionSpan,
			[.. children.OrderBy(child => child.Range.Start)]);
	}

	private static IEnumerable<SyntaxNode> OutlineChildren(SyntaxNode node) => node switch
	{
		StructDeclarationSyntax structDeclaration => structDeclaration.Fields,
		UnionDeclarationSyntax unionDeclaration => unionDeclaration.Fields,
		EnumDeclarationSyntax enumDeclaration => enumDeclaration.Variants,
		InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.Members,
		ProtocolDeclarationSyntax protocolDeclaration => protocolDeclaration.Members,
		ExtensionDeclarationSyntax extension => ExtensionMembers(extension),
		_ => [],
	};

	private static IEnumerable<SyntaxNode> ExtensionMembers(ExtensionDeclarationSyntax extension)
	{
		foreach (var method in extension.Methods)
			yield return method;
		foreach (var constructor in extension.Constructors)
			yield return constructor;
		foreach (var destructor in extension.Destructors)
			yield return destructor;
	}

	private static IEnumerable<SyntaxNode> Members(CompilationUnitSyntax unit)
		=> unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;

	private static TextSpan ComputeNameSpan(string source, CoreTextSpan range, string name)
	{
		var start = Math.Clamp(range.Start, 0, source.Length);
		var end = Math.Clamp(range.End, start, source.Length);
		if (end <= start || name.Length == 0)
			return new TextSpan(start, 0);

		var slice = source[start..end];
		var index = slice.IndexOf(name, StringComparison.Ordinal);
		return index < 0
			? new TextSpan(start, Math.Min(name.Length, end - start))
			: new TextSpan(start + index, name.Length);
	}

	internal static ToolingSymbolKind MapKind(ResolvedSymbolKind kind) => kind switch
	{
		ResolvedSymbolKind.Namespace => ToolingSymbolKind.Namespace,
		ResolvedSymbolKind.Module => ToolingSymbolKind.Module,
		ResolvedSymbolKind.Struct => ToolingSymbolKind.Struct,
		ResolvedSymbolKind.Union => ToolingSymbolKind.Union,
		ResolvedSymbolKind.Enum => ToolingSymbolKind.Enum,
		ResolvedSymbolKind.EnumMember => ToolingSymbolKind.EnumMember,
		ResolvedSymbolKind.Interface => ToolingSymbolKind.Interface,
		ResolvedSymbolKind.Protocol => ToolingSymbolKind.Protocol,
		ResolvedSymbolKind.Delegate => ToolingSymbolKind.Delegate,
		ResolvedSymbolKind.TypeAlias => ToolingSymbolKind.TypeAlias,
		ResolvedSymbolKind.TypeParameter => ToolingSymbolKind.TypeParameter,
		ResolvedSymbolKind.Function => ToolingSymbolKind.Function,
		ResolvedSymbolKind.Method => ToolingSymbolKind.Method,
		ResolvedSymbolKind.ExtensionMethod => ToolingSymbolKind.ExtensionMethod,
		ResolvedSymbolKind.Constructor => ToolingSymbolKind.Constructor,
		ResolvedSymbolKind.Destructor => ToolingSymbolKind.Destructor,
		ResolvedSymbolKind.Field => ToolingSymbolKind.Field,
		ResolvedSymbolKind.Parameter => ToolingSymbolKind.Parameter,
		ResolvedSymbolKind.Local => ToolingSymbolKind.Local,
		ResolvedSymbolKind.Global => ToolingSymbolKind.Global,
		ResolvedSymbolKind.Constant => ToolingSymbolKind.Constant,
		ResolvedSymbolKind.Operator => ToolingSymbolKind.Operator,
		ResolvedSymbolKind.OtherType => ToolingSymbolKind.OtherType,
		_ => ToolingSymbolKind.Unknown,
	};

	private static string Leaf(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private sealed class Entry(SymbolId id, string name, ToolingSymbolKind kind, string? ownerTypeName)
	{
		public SymbolId Id { get; } = id;
		public string Name { get; } = name;
		public ToolingSymbolKind Kind { get; } = kind;
		public string? OwnerTypeName { get; } = ownerTypeName;
		public List<SymbolDefinition> Definitions { get; } = [];
	}
}
