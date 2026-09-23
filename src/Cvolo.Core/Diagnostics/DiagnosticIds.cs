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
	public const string ExposeUsingNamespaceNotFound = "CVL1050";

	/// <summary>The `catch` operator requires a valid left-hand expression or a `try` block yielding a `Result` shape.</summary>
	public const string CatchRequiresResult = "CVL1051";

	/// <summary>Duplicate `catch` block clause detected for error type `{0}` within this try statement scope.</summary>
	public const string DuplicateCatchClause = "CVL1053";

	/// <summary>`catch` clause `{0}` is unreachable: an earlier clause `{1}` already covers it.</summary>
	public const string CatchUnreachableClause = "CVL1054";

	/// <summary>Error type `{0}` emitted inside this `try` block has no matching `catch` clause.</summary>
	public const string CatchUnhandledErrorType = "CVL1057";

	/// <summary>Lambda catch body must end with a `return` statement.</summary>
	public const string CatchLambdaMissingReturn = "CVL1058";

	/// <summary>`catch` pattern references type `{0}` which is not marked `[Error]`.</summary>
	public const string CatchPatternNotErrorAttribute = "CVL1059";

	/// <summary>Value-pattern `catch ({0}.{1})` requires `{0}` to be an `enum`; `{0}` is a `{kind}`.</summary>
	public const string CatchValuePatternOnNonEnum = "CVL1067";

	/// <summary>`defer {0} { ... }` / `break {0};` refers to a label `{0}` not in scope.</summary>
	public const string LabelNotFoundInScope = "CVL1061";

	/// <summary>Label `{0}` redeclared in the same enclosing scope.</summary>
	public const string DuplicateLabel = "CVL1062";

	/// <summary>Control flow cannot leave a `defer` body (return/break/continue inside `defer`).</summary>
	public const string DeferControlFlowLeak = "CVL1063";

	/// <summary>`defer` cannot be nested inside another `defer`.</summary>
	public const string NestedDefer = "CVL1064";

	/// <summary>`defer` requires a statement or block body.</summary>
	public const string DeferRequiresBody = "CVL1065";

	/// <summary>`break` requires a label in this version.</summary>
	public const string BreakRequiresLabel = "CVL1066";

	/// <summary>The unstructured iteration statement 'break' or 'continue' can only be executed inside an active loop body context.</summary>
	public const string LoopControlOutsideLoop = "CVL1070";

	/// <summary>An unqualified reference to a global variable name is ambiguous between two or more imported namespaces.</summary>
	public const string AmbiguousGlobalReference = "CVL1077";

	/// <summary>A type alias references an underlying type that does not exist.</summary>
	public const string UnknownTypeAlias = "CVL1200";

	/// <summary>A type alias resolves (transitively) to itself.</summary>
	public const string CyclicTypeAlias = "CVL1201";

	/// <summary>A type alias is used as a generic parameter constraint in a `where` clause.</summary>
	public const string AliasAsConstraint = "CVL1202";

	// ── Safe Delegates & Borrowed Closures (CVL13xx) ──

	/// <summary>A lambda expression requires an expected delegate type target (no standalone lambda type).</summary>
	public const string LambdaRequiresExpectedDelegateType = "CVL1300";

	/// <summary>A delegate declaration used a receiver parameter ('ref this'/'refvar this').</summary>
	public const string ReceiverParamInDelegateDeclaration = "CVL1301";

	/// <summary>A function/method group cannot convert to the target delegate type (signature mismatch / no matching overload).</summary>
	public const string InvalidFunctionConversion = "CVL1302";

	/// <summary>The function/method group conversion is ambiguous: multiple overloads match the target delegate signature.</summary>
	public const string AmbiguousFunctionConversion = "CVL1303";

	/// <summary>A lambda parameter type is incompatible with the expected delegate parameter type.</summary>
	public const string LambdaParameterTypeMismatch = "CVL1304";

	/// <summary>A lambda return type is incompatible with the expected delegate return type.</summary>
	public const string LambdaReturnTypeMismatch = "CVL1305";

	/// <summary>Default capture mode cannot snapshot-copy a move-only value ('move' or 'ref' capture required).</summary>
	public const string DefaultModeCaptureOfMoveOnly = "CVL1306";

	/// <summary>The capture value carries a mutable-borrow capability; capture is rejected in every mode.</summary>
	public const string MutableBorrowCapabilityCapture = "CVL1307";

	/// <summary>Slice-typed values cannot be captured by Any closure.</summary>
	public const string SliceCaptureForbidden = "CVL1308";

	/// <summary>Generic-instantiated values with a mutable-borrow capability cannot be captured.</summary>
	public const string GenericCaptureCapability = "CVL1309";

	/// <summary>Capture from a borrowed 'ref'/'refvar' lexical binding is unsupported in this increment.</summary>
	public const string RefBindingCaptureUnsupported = "CVL1310";

	/// <summary>'refvar (...)=>' capture mode is not supported in this increment.</summary>
	public const string RefvarLambdaModeUnsupported = "CVL1311";

	/// <summary>A captured-by-value field is immutable; assignment through a captured snapshot is rejected.</summary>
	public const string CapturedFieldAssignment = "CVL1312";

	/// <summary>A captured-by-value field is immutable; mutation through a captured snapshot is rejected.</summary>
	public const string CapturedFieldMutation = "CVL1313";

	/// <summary>The 'move' capture was already consumed (moved out) and cannot be moved again.</summary>
	public const string MoveOutOfMovedCapture = "CVL1314";

	/// <summary>Use of a move-only value after it was moved into a closure capture.</summary>
	public const string UseAfterMoveOfMoveOnly = "CVL1315";

	/// <summary>A 'ref' closure captures a local whose borrow would escape the closure's scope.</summary>
	public const string RefLambdaBorrowEscapes = "CVL1316";

	/// <summary>A live borrow conflicts with this operation while a 'ref' closure alias may still invoke.</summary>
	public const string LiveBorrowConflict = "CVL1317";

	/// <summary>The capturing closure's environment escapes its owning scope.</summary>
	public const string ClosureEnvironmentEscapes = "CVL1318";

	/// <summary>A safe-delegate parameter is non-escaping by default and cannot be stored into longer-lived storage.</summary>
	public const string DelegateParameterEscapes = "CVL1319";

	/// <summary>Returning a non-escaping safe-delegate parameter from this function is unsupported in this increment.</summary>
	public const string ParameterReturnedUnsupported = "CVL1320";

	/// <summary>A bound-method delegate escapes past the lifetime of its receiver.</summary>
	public const string BoundReceiverLifetimeEscapes = "CVL1321";

	/// <summary>Receiver delegation via 'refvar this' is unsupported for bound-method delegates.</summary>
	public const string RefvarThisDelegation = "CVL1322";

	/// <summary>Capturing a receiver field that holds a mutable-borrow capability is rejected.</summary>
	public const string RefvarThisFieldCapture = "CVL1323";

	/// <summary>A plain delegate type is not default-initializable; an initializer is required.</summary>
	public const string DelegateNotDefaultInitializable = "CVL1324";

	/// <summary>Implicit zero-initialization of a delegate-typed storage is rejected.</summary>
	public const string DelegateImplicitZeroInit = "CVL1325";

	/// <summary>The null literal is not a valid delegate value; use Option.None on `Handler?` instead.</summary>
	public const string NullLiteralForDelegate = "CVL1326";

	/// <summary>No implicit conversion between distinct nominal delegate types.</summary>
	public const string NominalDelegateConversion = "CVL1327";

	/// <summary>A delegate return type bears reference/refvar provenance ('ref T'/'refvar T').</summary>
	public const string DelegateReturnRefType = "CVL1328";

	/// <summary>A delegate return type bears slice provenance (T[]).</summary>
	public const string DelegateReturnSliceType = "CVL1329";

	/// <summary>A delegate return type transitively bears provenance through an aggregate or delegate.</summary>
	public const string DelegateReturnTransitiveProvenance = "CVL1330";

	/// <summary>A generic delegate instantiation produced a provenance-bearing return type.</summary>
	public const string DelegateReturnGenericInstantiation = "CVL1331";

	/// <summary>No implicit conversion between safe and native delegate values.</summary>
	public const string SafeNativeDelegateConversion = "CVL1332";

	/// <summary>Safe and native delegates cannot be reinterpret-cast into one another.</summary>
	public const string SafeNativeDelegateReinterpret = "CVL1333";

	/// <summary>Delegate += / -= multicast operators are not supported in this increment.</summary>
	public const string MulticastDelegateOperator = "CVL1334";

	/// <summary>Delegates cannot be invoked through an arbitrary expression callee in this increment.</summary>
	public const string ExpressionCalleeInvocation = "CVL1335";

	/// <summary>A public delegate declaration does not satisfy the provenance-independent return rule and cannot be part of package API metadata.</summary>
	public const string InvalidPublicDelegateMetadata = "CVL1336";

	/// <summary>A value transitively containing a safe delegate cannot escape via a non-escaping aggregate parameter.</summary>
	public const string AggregateParameterEscape = "CVL1337";

	/// <summary>Extracting a delegate from parameter-origin storage would let it escape; projection/access is rejected.</summary>
	public const string DelegateExtractionEscapes = "CVL1338";

	/// <summary>A non-static safe delegate cannot be stored into a 'refvar'-rooted (write-through) destination.</summary>
	public const string NonStaticThroughRefvarOrigin = "CVL1339";

	/// <summary>A writable aggregate projection rooted in a render/refvar param cannot expose the stored delegate.</summary>
	public const string WritableAggregateProjection = "CVL1340";

	/// <summary>Storing a non-static delegate into mutable slice parameter storage is rejected.</summary>
	public const string MutableSliceParamStorage = "CVL1341";

	/// <summary>A raw unsafe function cannot convert to a safe delegate in this increment.</summary>
	public const string RawUnsafeFunctionConversion = "CVL1342";

	/// <summary>The lambda body violates safe-callable constraints passed down from the expected delegate contract.</summary>
	public const string SafeCallableContractViolation = "CVL1343";

	/// <summary>Optional/fixed-array/generic storage cannot retain a non-static delegate through this write.</summary>
	public const string OptionalFixedArrayGenericStore = "CVL1344";

	/// <summary>A whole-value store of a delegate-bearing aggregate would let a non-static delegate escape.</summary>
	public const string WholeAggregateStoreEscapes = "CVL1345";

	// ── foreach iteration (CVL108x-CVL109x) ──

	/// <summary>Type cannot be traversed via foreach: GetEnumerator method is missing.</summary>
	public const string ForeachNoGetEnumerator = "CVL1080";

	/// <summary>Iterator type returned by GetEnumerator is invalid: missing a bool MoveNext method signature.</summary>
	public const string ForeachMissingMoveNext = "CVL1081";

	/// <summary>Iterator type returned by GetEnumerator is invalid: missing a Current property or method getter.</summary>
	public const string ForeachMissingCurrent = "CVL1082";

	/// <summary>Iterator type returned by GetEnumerator is invalid: MoveNext must return a logical bool type scalar.</summary>
	public const string ForeachMoveNextNotBool = "CVL1083";

	/// <summary>The loop variable is read-only and cannot be reassigned inside the execution block.</summary>
	public const string ForeachReadOnlyAssignment = "CVL1084";

	/// <summary>Explicit loop item type does not match the iterator's underlying Current yield type.</summary>
	public const string ForeachItemTypeMismatch = "CVL1085";

	/// <summary>Cannot bind mutable reference refvar: the iterator's Current property returns by value.</summary>
	public const string ForeachRefVarByValue = "CVL1086";

	/// <summary>Cannot bind mutable reference refvar: the iterator's Current property returns a read-only ref T.</summary>
	public const string ForeachRefVarReadOnlyRef = "CVL1087";

	/// <summary>Escape boundary violation: a reference loop variable cannot cross the lexical boundary of the loop block.</summary>
	public const string ForeachEscapeBoundary = "CVL1088";

	/// <summary>Ambiguous iteration routing: the type exposes multiple conflicting overloads for GetEnumerator.</summary>
	public const string ForeachAmbiguousGetEnumerator = "CVL1089";

	/// <summary>Type cannot be traversed via foreach: GetEnumerator method is inaccessible due to its protection level.</summary>
	public const string ForeachInaccessibleGetEnumerator = "CVL1090";

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

	// ── Unsafe C-ABI Interop (CVLF20xx) ──

	/// <summary>An imported foreign global cannot declare an initializer; its storage is external.</summary>
	public const string ForeignGlobalInitializer = "CVLF2000";

	/// <summary>A standalone foreign global requires [LibraryImport] to bind its native library.</summary>
	public const string ForeignGlobalRequiresLibrary = "CVLF2001";

	/// <summary>[LibraryImport] on an extern-block global belongs on the enclosing block.</summary>
	public const string LibraryImportOnBlockGlobal = "CVLF2002";

	/// <summary>[ImportName] is only valid on imported foreign globals and extern functions.</summary>
	public const string ImportNameOnNonForeignGlobal = "CVLF2003";

	/// <summary>The type of a foreign global is not representable across the C ABI boundary.</summary>
	public const string ForeignGlobalNotAbiSafe = "CVLF2004";

	/// <summary>'&amp;Function' requires an expected native delegate type (assignment or call argument); never valid with 'var'.</summary>
	public const string FunctionAddressRequiresContext = "CVLF2010";

	/// <summary>The address of an overloaded function is ambiguous without an expected native delegate signature.</summary>
	public const string FunctionAddressOverloadAmbiguous = "CVLF2011";

	/// <summary>The function signature (return and parameter types) does not match the expected native delegate.</summary>
	public const string FunctionAddressSignatureMismatch = "CVLF2012";

	/// <summary>The function's calling convention does not match the expected native delegate.</summary>
	public const string FunctionAddressCallingConventionMismatch = "CVLF2013";

	/// <summary>The address of a generic function cannot be taken.</summary>
	public const string FunctionAddressGeneric = "CVLF2014";

	/// <summary>The function is not addressable as a native callback (not a native-ABI, exposed, or imported extern function with an ABI-safe signature).</summary>
	public const string FunctionAddressNotAddressable = "CVLF2015";

	/// <summary>Taking the address of a native-callable function requires an unsafe context.</summary>
	public const string FunctionAddressRequiresUnsafe = "CVLF2016";

	/// <summary>Indirect invocation through an arbitrary expression callee is not supported; only name and member-path callees can be called.</summary>
	public const string IndirectCallExpressionNotSupported = "CVLF2020";

	/// <summary>Invoking a native delegate pointer requires an unsafe context.</summary>
	public const string NativeDelegateInvokeRequiresUnsafe = "CVLF2021";

	/// <summary>Direct calls to a native-ABI function require an unsafe context.</summary>
	public const string NativeAbiFunctionCallRequiresUnsafe = "CVLF2022";

	/// <summary>Casting a native delegate to/from a data pointer requires an unsafe context.</summary>
	public const string NativeDelegateCastRequiresUnsafe = "CVLF2030";

	/// <summary>Explicit zero/null construction of a native delegate requires unsafe capability.</summary>
	public const string NullForNativeDelegate = "CVLF2032";

	/// <summary>A field of an 'unsafe union' is not representable across the C ABI boundary.</summary>
	public const string UnsafeUnionFieldNotAbiSafe = "CVLF2040";

	/// <summary>A raw 'unsafe union' cannot be pattern-matched or switched on (it has no tag).</summary>
	public const string UnsafeUnionTaggedOperation = "CVLF2041";
	public const string UnsafeUnionAccessRequiresUnsafe = "CVLF2043";
	public const string UnsafeUnionGenericUnsupported = "CVLF2044";

	/// <summary>The type is not representable in the requested position at the C ABI boundary.</summary>
	public const string NativeAbiTypeNotRepresentable = "CVLF2050";

	/// <summary>Aggregate type classification for the C ABI is unproven for this type; passing it across the boundary is not allowed.</summary>
	public const string NativeAbiAggregateUnclassified = "CVLF2051";
	public const string NativeAbiResourceBearing = "CVLF2053";
	public const string NativeEnumRequiresExplicitStorage = "CVLF2054";

	/// <summary>The calling convention is not supported for the current target triple.</summary>
	public const string CallingConventionNotSupportedTarget = "CVLF2052";

	/// <summary>A native delegate used at the C ABI boundary must declare a calling convention like 'unsafe "C"'.</summary>
	public const string NativeDelegateMissingCallingConvention = "CVLF2060";

	/// <summary>A native-ABI function's signature is not representable across the C ABI boundary.</summary>
	public const string NativeAbiFunctionSignatureInvalid = "CVLF2061";
	public const string NativeDelegateGenericUnsupported = "CVLF2062";

	/// <summary>'unsafe union' requires at least one field.</summary>
	public const string UnsafeUnionEmpty = "CVLF2042";

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

	/// <summary>A global initializer divides or takes the modulo of an integer constant by zero.</summary>
	public const string ConstantIntegerDivisionByZero = "CVL2404";

	/// <summary>A global variable initializer is not a compile-time constant expression.</summary>
	public const string GlobalInitializerNotConstant = "CVL2405";
}
