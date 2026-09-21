using Cvolo.Analysis.Passes.Declaration;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes;

public sealed class DeclarationPass(BindingContext context)
{
	private readonly TypeAliasValidator _aliases = new(context);
	private readonly TypeDeclarationRegistrar _types = new(context);
	private readonly ContractHierarchyLinker _contracts = new(context);
	private readonly EmbedLinker _embeds = new(context);
	private readonly EmbeddedMethodPromoter _embeddedMethods = new(context);
	private readonly DestructorValidator _destructors = new(context);
	private readonly GenericDefaultConstraintValidator _genericDefaults = new(context);
	private readonly GenericDefaultCopyValidator _genericDefaultCopies = new(context);
	private readonly FunctionDeclarationRegistrar _functions = new(context);
	private readonly GlobalVariableRegistrar _globals = new(context);
	private readonly ExposeUsingValidator _exposeUsings = new(context);
	private ExtensionRegistrar? _extensions;
	private ExtensionRegistrar Extensions => _extensions ??= new(context, _functions, _destructors, _genericDefaultCopies);
	/// <summary>
	/// The compiler's built-in attribute names, without the optional <c>Attribute</c> suffix, in
	/// declaration order. Tooling surfaces use this forwarding property to preserve the existing
	/// <see cref="DeclarationPass"/> API while attribute semantics live in <see cref="AttributeValidator"/>.
	/// </summary>
	public static IReadOnlyCollection<string> IntrinsicAttributeNames => AttributeValidator.IntrinsicAttributeNames;



	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		_exposeUsings.Process(units);

		// Pass 0a-pre: Register all type aliases before any type/field/parameter resolution
		// so struct fields and function signatures may reference them. Names are reserved
		// now; underlying types are validated eagerly in Pass 0a-post (below).
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var aliasMembers = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in aliasMembers)
			{
				if (member is TypeAliasDeclarationSyntax aliasDecl)
					_aliases.Declare(aliasDecl);
			}
		}

		// Pass 0a: Register all Struct/Union/Interface/Protocol raw symbols across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					_types.DeclareStruct(structDecl);
				else if (member is UnionDeclarationSyntax unionDecl)
					_types.DeclareUnion(unionDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					_types.DeclareInterface(interfaceDecl);
				else if (member is ProtocolDeclarationSyntax protocolDecl)
					_types.DeclareProtocol(protocolDecl);
				else if (member is EnumDeclarationSyntax enumDecl)
					_types.DeclareEnum(enumDecl);
				else if (member is DelegateDeclarationSyntax delegateDecl)
					_types.DeclareDelegate(delegateDecl);
			}
		}

		// Pass 0a-post: Eagerly validate every type alias now that all real type names are
		// registered — alias-vs-type conflicts, unresolvable targets (CVL1200), alias cycles
		// (CVL1201), and aliases in `where` constraints (CVL1202).
		_aliases.Validate(units);

		// Pass 0b: Link contract hierarchy (`:` base clauses) after every raw contract symbol exists.
		_contracts.Link(units);

		// Pass 0c: Link `struct T embed Base` clauses — validate the embedded type,
		// detect cycles/generics, and rebuild struct symbols with the embedded
		// fields flattened at the FRONT of their layout.
		_embeds.Link(units);

		// Pass 1: Register all Function/Extern signatures across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
					_functions.DeclareFunction(func);
				else if (member is ExternDeclarationSyntax ext)
					_functions.DeclareExternFunction(ext);
				else if (member is ExternBlockSyntax extBlock)
					_functions.DeclareExternBlock(extBlock);
				else if (member is ExposeExternBlockSyntax exportBlock)
					_functions.DeclareExposeExternBlock(exportBlock);
				else if (member is ExtensionDeclarationSyntax extDecl)
					Extensions.Declare(extDecl);
				else if (member is GlobalVariableDeclarationSyntax globalDecl)
					_globals.Declare(globalDecl);
			}
		}

		// Pass 1.5: Promote embedded-type extension methods onto every struct that
		// embeds them — `w.TakeDamage(20)` on a struct that `embed`s BaseEntity
		// resolves BaseEntity's extension with the outer struct as `this`.
		_embeddedMethods.Promote(units);

		// Pass 2: Enforce the destructor nesting-depth limit (Memory & Safety §2). Run after every
		// struct symbol (embeds included) is fully materialized so the transitive ownership graph
		// is complete.
		_destructors.ValidateDepth(units);

		// Pass 2.5: Validate that default generic parameter types satisfy their `where X : T`
		// constraints (CVL1042). This must run AFTER Pass 1 registers extension methods —
		// structural protocol conformance is proven by the presence of the extended methods
		// on the concrete type — so it cannot live inside Pass 0a's DeclareStruct.
		_genericDefaults.Validate(units);
	}


}
