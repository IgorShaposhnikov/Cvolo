namespace Cvolo.Core.Diagnostics;

/// <summary>
/// Stable identifiers for compiler diagnostics. Reserved prefix families
/// (modeled on the .NET CS/SYSLIB/CA convention):
///
///   CVLxxxx    - core compiler errors/warnings (syntax, semantics, memory)
///   SYSLIBxxxx - deprecations inside the standard library
///   CVLSxxxx   - Cvolo system-library diagnostics (reserved)
///   CVLAxxxx   - code analyzers / linters: quality, style, performance (reserved)
///   CVLDxxxx   - documentation and comment checks (reserved)
///   CVLXxxxx   - extensions / macros / generators (reserved)
///   CVLFxxxx   - FFI and C-ABI interop (reserved)
/// </summary>
public static class DiagnosticIds
{
	/// <summary>'[UnsafeBody]' applied to a body without any unsafe operations.</summary>
	public const string UnsafeBodyNoEffect = "CVL1001";

	/// <summary>Attribute name is not a known intrinsic; accepted and erased, but flagged for likely typos.</summary>
	public const string UnknownAttribute = "CVL1002";

	/// <summary>Large struct (&gt;16 bytes) passed by value; payload is duplicated. Consider passing by ref.</summary>
	public const string LargeCopyWarning = "CVL1003";

	/// <summary>Attribute cannot be applied in the current safety tier.</summary>
	public const string AttributeWrongTier = "CVL1004";

	/// <summary>Raw pointer T* used outside an unsafe context.</summary>
	public const string RawPointerOutsideUnsafe = "CVL1005";

	/// <summary>Dereference '*' used outside an unsafe context.</summary>
	public const string DereferenceOutsideUnsafe = "CVL1006";

	/// <summary>Address-of '&' used outside an unsafe context.</summary>
	public const string AddressOfOutsideUnsafe = "CVL1007";

	/// <summary>Reference cannot escape an unbound scope.</summary>
	public const string RefEscapesUnboundScope = "CVL1008";

	/// <summary>Calling a raw 'unsafe fn' from code that is not in an unsafe context.</summary>
	public const string CallUnsafeFromSafe = "CVL1009";

	/// <summary>'unbound' modifier on a function with no ref/refvar parameters.</summary>
	public const string UnboundNoRefParams = "CVL1010";
	/// <summary>Auto-inference chose mutability for an unmarked extension method.</summary>
	public const string AutoInferMutationWarning = "CVL1011";

	/// <summary>Writing to a ref/refvar structural reference field outside an 'unbound' context.</summary>
	public const string RefFieldMutationInSafe = "CVL1012";
	/// <summary>Return value of a function or type marked '[MustUse]' is ignored.</summary>
	public const string MustUseIgnoredWarning = "CVL1013";

	/// <summary>'[Inline]' and '[NeverInline]' applied to the same declaration.</summary>
	public const string ConflictingInlineAttributes = "CVL1400";

	/// <summary>'[Inline]' on a recursive function; LLVM may ignore the hint.</summary>
	public const string InlineOnRecursiveFunction = "CVL1401";

	/// <summary>A member is accessed outside its allowed visibility scope (file/module/package).</summary>
	public const string InaccessibleMember = "CVL1030";

	/// <summary>An extension member declares a visibility wider than its enclosing extension block.</summary>
	public const string VisibilityExpansionInExtension = "CVL1031";

	/// <summary>A struct literal initializer populates a private field from outside the defining file.</summary>
	public const string PrivateFieldLiteralInit = "CVL1032";

	/// <summary>A global 'extern' declaration is decorated with the 'public' modifier.</summary>
	public const string PublicExtern = "CVL1033";

	/// <summary>A union/Option payload variant is obscured by visibility during pattern matching.</summary>
	public const string HiddenPayloadMatch = "CVL1034";

	/// <summary>An 'unbound' sandbox mutates or traverses a refvar field hidden by visibility.</summary>
	public const string UnboundVisibilityLeak = "CVL1035";

	/// <summary>A public global var exposes a multi-word container without synchronization.</summary>
	public const string MultiWordPublicGlobal = "CVL1036";

	/// <summary>Friend verification failed for an [InternalsVisibleTo] package claim.</summary>
	public const string FriendSpoofing = "CVL1037";

	/// <summary>A generic instantiation exposes a type argument with more restrictive visibility than the host.</summary>
	public const string GenericVisibilityLeak = "CVL1038";

	/// <summary>A private or anonymous symbol is forced into a public export path.</summary>
	public const string PrivateSymbolExport = "CVL1039";

	/// <summary>Default value for generic parameter must be a Trivial Copy Type.</summary>
	public const string DefaultMustBeTrivialCopy = "CVL1040";

	/// <summary>Generic parameter does not have a default value and must be specified.</summary>
	public const string GenericParameterNoDefault = "CVL1041";

	/// <summary>Default type does not satisfy constraints of generic parameter.</summary>
	public const string DefaultTypeConstraintMismatch = "CVL1042";

	/// <summary>A constructor's delegating initializer `this(...)` forms a delegation cycle.</summary>
	public const string CyclicConstructorDelegation = "CVL1043";

	/// <summary>A delegating constructor's body is not empty after `this(...)`.</summary>
	public const string NonEmptyDelegatingConstructorBody = "CVL1044";

	/// <summary>A `ref`/`refvar` type argument is used for a generic type other than an Option-shaped union.</summary>
	public const string RefTypeArgumentNotAllowed = "CVL1103";

	/// <summary>Optional type syntax `T?` is used although `--strict-option` disables it.</summary>
	public const string OptionalSyntaxDisabled = "CVL1100";

	/// <summary>Multiple `?` tokens in a row (e.g. `T??`) are not allowed.</summary>
	public const string OptionalTypeChainForbidden = "CVL1101";

	/// <summary>Optional type syntax `?` is applied to `void` or a function type.</summary>
	public const string OptionalOnVoid = "CVL1102";

	/// <summary>`null` is assigned/initialized on a `T?`-declared variable in safe code.</summary>
	public const string NullForOptionalType = "CVL1104";

	/// <summary>'expose using' directive used outside a namespace declaration.</summary>
	public const string ExposeUsingOutsideNamespace = "CVL1060";

	/// <summary>'expose using' targets a namespace that cannot be resolved.</summary>
	public const string ExposeUsingNamespaceNotFound = "CVL1061";

	/// <summary>A type alias references an underlying type that does not exist.</summary>
	public const string UnknownTypeAlias = "CVL1200";

	/// <summary>A type alias resolves (transitively) to itself.</summary>
	public const string CyclicTypeAlias = "CVL1201";

	/// <summary>A type alias is used as a generic parameter constraint in a `where` clause.</summary>
	public const string AliasAsConstraint = "CVL1202";

	// ── FFI / C-ABI interop (CVLFxxxx) ──

	/// <summary>Unknown calling convention string. Expected "C" or "system".</summary>
	public const string UnknownCallingConvention = "CVL1700";

	/// <summary>[LibraryImport] applied to a non-extern-block declaration.</summary>
	public const string LibraryImportOnNonBlock = "CVL1701";

	/// <summary>[ImportName] applied outside an extern block.</summary>
	public const string ImportNameOutsideBlock = "CVL1702";

	/// <summary>[LibraryImport] used inside an extern block (place it on the block itself).</summary>
	public const string LibraryImportInsideBlock = "CVL1703";

	/// <summary>Native library could not be resolved by the linker.</summary>
	public const string NativeLibraryUnresolved = "CVL1704";

	// ── Native Binary Export (CVL18xx) ──

	/// <summary>An exported function contains a value interface parameter across a binary ABI boundary.</summary>
	public const string ExposedInterfaceParameter = "CVL1801";

	/// <summary>Two exported functions share the same export symbol name in module scope.</summary>
	public const string DuplicateExportSymbol = "CVL1802";

	/// <summary>[ExposeName] applied to a function outside an `expose extern` scope.</summary>
	public const string ExposeNameOutsideExport = "CVL1803";

	/// <summary>Modifier 'extern' applied to a function with a body outside an 'expose extern' block.</summary>
	public const string ExternWithBodyOutsideExposeBlock = "CVL1806";

	/// <summary>Structure used in C-ABI boundary must have a fixed sequential layout.</summary>
	public const string CAbiStructLayoutInvalid = "CVL1807";

	// ── Compilation Target (CVL50xx) ──

	/// <summary>Program does not contain a static 'main' method suitable for an entry point.</summary>
	public const string MissingEntryPoint = "CVL5001";

	// ── Literals / Lexer (CVL19xx) ──

	/// <summary>A literal `double` is assigned to a `float` target; a `f` suffix is required.</summary>
	public const string DoubleLiteralToFloatAssignment = "CVL1900";

	/// <summary>Unknown floating-point suffix (e.g. `1.0m`).</summary>
	public const string UnknownFloatSuffix = "CVL1901";

	/// <summary>The integer literal does not fit in the type implied by its suffix (or the target type).</summary>
	public const string IntegerLiteralTooLarge = "CVL1902";

	/// <summary>Invalid integer suffix (e.g. `42x`).</summary>
	public const string InvalidIntegerSuffix = "CVL1903";

	/// <summary>A digit separator `_` appears at the start or end of a numeric literal.</summary>
	public const string InvalidDigitSeparator = "CVL1904";

	/// <summary>A character literal has no character between the quotes (`''`).</summary>
	public const string EmptyCharacterLiteral = "CVL1905";

	/// <summary>Unknown escape sequence in a character/string literal, e.g. `'\q'`.</summary>
	public const string UnknownEscapeSequence = "CVL1906";

	/// <summary>A character literal contains more than one character.</summary>
	public const string MultipleCharacterLiteral = "CVL1907";

	/// <summary>A hex escape `\xNN` is outside the 0-255 range.</summary>
	public const string HexEscapeOutOfRange = "CVL1908";

	/// <summary>The `null` literal is used outside an unsafe context.</summary>
	public const string NullOutsideUnsafeContext = "CVL1909";

	/// <summary>Cannot assign `null` to a safe reference (`ref`/`refvar`).</summary>
	public const string NullToSafeReference = "CVL1910";

	// ── Strings (CVL20xx) ──

	/// <summary>A raw string literal is missing its closing quote.</summary>
	public const string UnbalancedRawStringLiteral = "CVL2000";

	/// <summary>The `+` operator is only allowed between compile-time constant strings.</summary>
	public const string DynamicStringConcatenation = "CVL2001";

	/// <summary>Casting a `string` to `char*` is only allowed inside unsafe contexts.</summary>
	public const string StringToCharPointerOutsideUnsafe = "CVL2002";

	// ── Inline Assembly (CVL16xx) ──

	/// <summary>`asm` can only be used inside `unsafe` contexts.</summary>
	public const string AsmOutsideUnsafeContext = "CVL1600";

	/// <summary>The operand constraint string is not a valid LLVM constraint.</summary>
	public const string InvalidAsmConstraint = "CVL1601";

	/// <summary>The clobber list contains an unknown register name.</summary>
	public const string InvalidAsmClobber = "CVL1602";

	/// <summary>`asm&lt;T&gt;` with a result type requires exactly one output operand.</summary>
	public const string AsmResultRequiresOneOutput = "CVL1603";

	/// <summary>An output operand of an `asm` must be an l-value (assignable).</summary>
	public const string AsmOutputNotLValue = "CVL1604";

	/// <summary>The operand type does not match the requested register/size of the constraint.</summary>
	public const string AsmOperandTypeMismatch = "CVL1605";

	/// <summary>Unknown register in the clobber list.</summary>
	public const string UnknownAsmClobberRegister = "CVL1606";

	// ── Compile-Time Operators (CVL21xx) ──

	/// <summary>The symbol referenced by `nameof` could not be resolved in the current context.</summary>
	public const string NameofInvalidSymbolError = "CVL2100";

	/// <summary>The type referenced by `typeof` could not be resolved.</summary>
	public const string TypeofInvalidTypeError = "CVL2101";

	/// <summary>`nameof` was applied to an expression with no valid identifier.</summary>
	public const string NameofExpressionInvalid = "CVL2102";
}
