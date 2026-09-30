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

	/// <summary>An associated function (leading dot) is declared outside an extension block.</summary>
	public const string AssociatedFunctionOutsideExtension = "CVL1045";

	/// <summary>An associated function declares an instance receiver (`ref this` / `refvar this`).</summary>
	public const string AssociatedFunctionWithReceiver = "CVL1046";

	/// <summary>An associated function is called through a value instead of through its owner type.</summary>
	public const string AssociatedFunctionCalledThroughValue = "CVL1047";

	/// <summary>A receiver-backed instance extension method is called through its owner type.</summary>
	public const string InstanceExtensionCalledThroughType = "CVL1048";

	/// <summary>An associated function is declared in an `extension Protocol` block.</summary>
	public const string AssociatedFunctionInProtocolExtension = "CVL1049";

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

	public const string UnresolvedFunctionCall = "CVL1078";

	public const string ResultShapeMissingOk = "CVL1091";
	public const string ResultShapeMissingErr = "CVL1092";
	public const string ResultShapeExtraVariant = "CVL1093";
	public const string ResultShapeVoidErr = "CVL1094";
	public const string ResultShapeArguments = "CVL1095";
	public const string ResultShapeInvalidSubstitution = "CVL1096";
	public const string SystemInFreestanding = "CVL1097";
	public const string UnknownSystemNamespace = "CVL1098";
	public const string SdkNamespaceMismatch = "CVL1099";

	/// <summary>A type alias references an underlying type that does not exist.</summary>
	public const string UnknownTypeAlias = "CVL1200";

	/// <summary>A type alias resolves (transitively) to itself.</summary>
	public const string CyclicTypeAlias = "CVL1201";

	/// <summary>A type alias is used as a generic parameter constraint in a `where` clause.</summary>
	public const string AliasAsConstraint = "CVL1202";

	// ── Interfaces & Conformance (CVL15xx) ──

	/// <summary>An extension's conformance clause names an interface that cannot be resolved.</summary>
	public const string ConformanceUnknownInterface = "CVL1500";

	/// <summary>An extension's conformance clause names something that is not an interface (a protocol, a struct, and so on).</summary>
	public const string ConformanceTargetNotInterface = "CVL1501";

	/// <summary>A conforming type does not provide a member the interface requires.</summary>
	public const string ConformanceMissingMember = "CVL1502";

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

	/// <summary>The type referenced by `sizeof` could not be resolved.</summary>
	public const string SizeofInvalidTypeError = "CVL2103";

	/// <summary>The type referenced by `alignof` could not be resolved.</summary>
	public const string AlignofInvalidTypeError = "CVL2104";

	/// <summary>A layout query was applied to `void`, which has no object layout.</summary>
	public const string LayoutQueryOnVoid = "CVL2105";

	/// <summary>The struct or raw-union referenced by `offsetof` could not be resolved.</summary>
	public const string OffsetofInvalidTargetError = "CVL2106";

	/// <summary>An `offsetof` member designator does not name a stored member.</summary>
	public const string OffsetofMissingMember = "CVL2107";

	/// <summary>`offsetof` cannot address a safe/tagged union variant.</summary>
	public const string OffsetofSafeUnionVariant = "CVL2108";

	/// <summary>A layout query was applied to a type with no concrete runtime storage.</summary>
	public const string LayoutQueryIncompleteType = "CVL2109";

	/// <summary>An evaluation was expected to produce a `nuint` value.</summary>
	public const string LayoutConstantNotNuint = "CVL2110";

	/// <summary>A fixed-array dimension is not a valid compile-time integer constant.</summary>
	public const string InvalidFixedArrayConstant = "CVL2111";

	/// <summary>A `builtin` declaration appeared outside the trusted Base SDK source.</summary>
	public const string BuiltinOutsideBase = "CVL2112";

	/// <summary>A `builtin` callable declares a body.</summary>
	public const string BuiltinCallableHasBody = "CVL2113";

	/// <summary>An ordinary constructor or destructor is declaration-only.</summary>
	public const string OrdinaryCallableBodyless = "CVL2114";

	/// <summary>A `builtin` declaration has no matching entry in the builtin catalog.</summary>
	public const string UnknownBuiltinDeclaration = "CVL2115";

	/// <summary>A `builtin` declaration's shape does not match its catalog entry.</summary>
	public const string BuiltinShapeMismatch = "CVL2116";

	/// <summary>The same builtin identity is declared more than once.</summary>
	public const string DuplicateBuiltinDeclaration = "CVL2117";

	/// <summary>A required builtin declaration is missing from the Base SDK.</summary>
	public const string RequiredBuiltinMissing = "CVL2118";

	/// <summary>A compile-time-only builtin type is constructed as a runtime value.</summary>
	public const string CompileTimeBuiltinConstructed = "CVL2119";

	/// <summary>A compile-time-only builtin field or layout was requested as a runtime object.</summary>
	public const string CompileTimeBuiltinFieldAccess = "CVL2120";

	/// <summary>A global initializer divides or takes the modulo of an integer constant by zero.</summary>
	public const string ConstantIntegerDivisionByZero = "CVL2404";

	/// <summary>A global variable initializer is not a compile-time constant expression.</summary>
	public const string GlobalInitializerNotConstant = "CVL2405";

	// ── Operator Overloads (CVL22xx) ──

	/// <summary>`operator =` is declared; assignment is compiler-owned and not overloadable.</summary>
	public const string OperatorAssignmentNotOverloadable = "CVL2200";

	/// <summary>An operator declares a parameter count that does not match its arity.</summary>
	public const string OperatorArityMismatch = "CVL2201";

	/// <summary>An operator is declared in an extension whose owner is not one of the operand types.</summary>
	public const string OperatorOwnerNotOperand = "CVL2202";

	/// <summary>An operator for a foreign type is declared in a project that does not own the type.</summary>
	public const string OperatorForeignOwner = "CVL2203";

	/// <summary>An operator overload declares its own type parameters, which is not supported.</summary>
	public const string OperatorGenericNotSupported = "CVL2204";

	/// <summary>No declared operator overload matches the operand types of an operator expression.</summary>
	public const string OperatorNoMatch = "CVL2205";

	/// <summary>An operator overload declares an instance receiver, which operators never have.</summary>
	public const string OperatorWithReceiver = "CVL2206";

	/// <summary>An operator body uses an implicit instance field; operators have no receiver.</summary>
	public const string OperatorUsesInstanceField = "CVL2207";

	// ── Explicitly named diagnostics (formerly emitted without an id) ──

	/// <summary>'&lt;x&gt;' is already borrowed; cannot borrow multiple elements of the same array</summary>
	public const string IsAlreadyBorrowedCannotBorrowMultipleElementsOfTheSameArray = "CVL4001";

	/// <summary>'&lt;x&gt;' is under an immutable borrow contract while it is being iterated: mutating method calls are not allowed inside the 'foreach' body.</summary>
	public const string IsUnderAnImmutableBorrowContractWhileItIsBeingIteratedMutati = "CVL4002";

	/// <summary>'&lt;x&gt;' is under an immutable borrow contract while it is being iterated: structural mutation is not allowed inside the 'foreach' body.</summary>
	public const string IsUnderAnImmutableBorrowContractWhileItIsBeingIteratedStruct = "CVL4003";

	/// <summary>(&lt;x&gt;,&lt;x&gt;): &lt;x&gt;</summary>
	public const string Diagnostic = "CVL4004";

	/// <summary>Ambiguous implementation of '&lt;x&gt;' for protocol '&lt;x&gt;' on type '&lt;x&gt;': multiple extension methods match the required signature.</summary>
	public const string AmbiguousImplementationOfForProtocolOnTypeMultipleExtensionM = "CVL4005";

	/// <summary>Array elements must have the same type. Expected '&lt;x&gt;', found '&lt;x&gt;'</summary>
	public const string ArrayElementsMustHaveTheSameTypeExpectedFound = "CVL4006";

	/// <summary>Array replication count must be an integer.</summary>
	public const string ArrayReplicationCountMustBeAnInteger = "CVL4007";

	/// <summary>Array size exceeds stack allocation safety threshold</summary>
	public const string ArraySizeExceedsStackAllocationSafetyThreshold = "CVL4008";

	/// <summary>Cannot &lt;x&gt; '&lt;x&gt;' while a field borrow is still active</summary>
	public const string CannotWhileAFieldBorrowIsStillActive = "CVL4009";

	/// <summary>Cannot assign &lt;x&gt;-origin reference to global variable '&lt;x&gt;': only global-origin references may be stored in globals</summary>
	public const string CannotAssignOriginReferenceToGlobalVariableOnlyGlobalOriginR = "CVL4010";

	/// <summary>Cannot assign to immutable variable '&lt;x&gt;'</summary>
	public const string CannotAssignToImmutableVariable = "CVL4011";

	/// <summary>Cannot assign to reference field '&lt;x&gt;' of variable '&lt;x&gt;' in safe code. Use an 'unbound' block or function to modify structural reference fields.</summary>
	public const string CannotAssignToReferenceFieldOfVariableInSafeCodeUseAnUnbound = "CVL4012";

	/// <summary>Cannot borrow '&lt;x&gt;' because an incompatible borrow is already active</summary>
	public const string CannotBorrowBecauseAnIncompatibleBorrowIsAlreadyActive = "CVL4013";

	/// <summary>Cannot cast nullable reference option '&lt;x&gt;' directly to a raw pointer; pattern-match it (switch on 'ref'/'refvar') to extract a non-null reference first.</summary>
	public const string CannotCastNullableReferenceOptionDirectlyToARawPointerPatter = "CVL4014";

	/// <summary>Cannot define a constructor for non-struct type '&lt;x&gt;'.</summary>
	public const string CannotDefineAConstructorForNonStructType = "CVL4015";

	/// <summary>Cannot dereference outside unsafe context.</summary>
	public const string CannotDereferenceOutsideUnsafeContext = "CVL4016";

	/// <summary>Cannot embed generic struct template '&lt;x&gt;' in struct '&lt;x&gt;'.</summary>
	public const string CannotEmbedGenericStructTemplateInStruct = "CVL4017";

	/// <summary>Cannot infer the type of a bare 'default' expression. Use default(T) or declare the variable with an explicit type.</summary>
	public const string CannotInferTheTypeOfABareDefaultExpressionUseDefaultTOrDecla = "CVL4018";

	/// <summary>Cannot initialize field '&lt;x&gt;' of type '&lt;x&gt;' with value of type '&lt;x&gt;'</summary>
	public const string CannotInitializeFieldOfTypeWithValueOfType = "CVL4019";

	/// <summary>Cannot initialize variable of type '&lt;x&gt;' with value of type '&lt;x&gt;'</summary>
	public const string CannotInitializeVariableOfTypeWithValueOfType = "CVL4020";

	/// <summary>Cannot pattern-match '&lt;x&gt; &lt;x&gt;' by value on a nullable reference option; switch on 'ref'/'refvar' to extract the reference safely.</summary>
	public const string CannotPatternMatchByValueOnANullableReferenceOptionSwitchOnR = "CVL4021";

	/// <summary>Cannot resolve the signature of '&lt;x&gt;' for the given type arguments</summary>
	public const string CannotResolveTheSignatureOfForTheGivenTypeArguments = "CVL4022";

	/// <summary>Cannot resolve type '&lt;x&gt;'.</summary>
	public const string CannotResolveType = "CVL4023";

	/// <summary>Cannot return '&lt;x&gt;' by value while a field borrow is still active</summary>
	public const string CannotReturnByValueWhileAFieldBorrowIsStillActive = "CVL4024";

	/// <summary>Cannot return '&lt;x&gt;' by value: reference field '&lt;x&gt;' targets local variable '&lt;x&gt;' (dangling reference)</summary>
	public const string CannotReturnByValueReferenceFieldTargetsLocalVariableDanglin = "CVL4025";

	/// <summary>Cannot return reference to local variable '&lt;x&gt;' (dangling reference)</summary>
	public const string CannotReturnReferenceToLocalVariableDanglingReference = "CVL4026";

	/// <summary>Cannot return value: reference '&lt;x&gt;' targets local variable '&lt;x&gt;' (dangling reference)</summary>
	public const string CannotReturnValueReferenceTargetsLocalVariableDanglingRefere = "CVL4027";

	/// <summary>Cannot take a mutable reference (refvar) of a read-only variable.</summary>
	public const string CannotTakeAMutableReferenceRefvarOfAReadOnlyVariable = "CVL4028";

	/// <summary>Cannot take address outside unsafe context.</summary>
	public const string CannotTakeAddressOutsideUnsafeContext = "CVL4029";

	/// <summary>Cannot use 'var' with reference type in global declaration. Use 'global ref' or 'global refvar' instead.</summary>
	public const string CannotUseVarWithReferenceTypeInGlobalDeclarationUseGlobalRef = "CVL4030";

	/// <summary>Cannot use embed in generic struct template '&lt;x&gt;'.</summary>
	public const string CannotUseEmbedInGenericStructTemplate = "CVL4031";

	/// <summary>Circular embed clause involving struct '&lt;x&gt;'.</summary>
	public const string CircularEmbedClauseInvolvingStruct = "CVL4032";

	/// <summary>Circular protocol inheritance involving '&lt;x&gt;'.</summary>
	public const string CircularProtocolInheritanceInvolving = "CVL4033";

	/// <summary>Constructor '&lt;x&gt;' is not accessible from the current constructor.</summary>
	public const string ConstructorIsNotAccessibleFromTheCurrentConstructor = "CVL4034";

	/// <summary>Constructor name '&lt;x&gt;' must match the extended type '&lt;x&gt;'.</summary>
	public const string ConstructorNameMustMatchTheExtendedType = "CVL4035";

	/// <summary>Could not resolve field type '&lt;x&gt;' during generic instantiation of '&lt;x&gt;'</summary>
	public const string CouldNotResolveFieldTypeDuringGenericInstantiationOf = "CVL4036";

	/// <summary>CyclicDestructorDepthError</summary>
	public const string CyclicDestructorDepthError = "CVL4037";

	/// <summary>Defensive initialization: constructor '&lt;x&gt;' does not initialize field '&lt;x&gt;'.</summary>
	public const string DefensiveInitializationConstructorDoesNotInitializeField = "CVL4038";

	/// <summary>Delegate '&lt;x&gt;' cannot be invoked with argument types (&lt;x&gt;).</summary>
	public const string DelegateCannotBeInvokedWithArgumentTypes = "CVL4039";

	/// <summary>Delegate '&lt;x&gt;' expects &lt;x&gt; argument(s) but received &lt;x&gt;</summary>
	public const string DelegateExpectsArgumentSButReceived = "CVL4040";

	/// <summary>Destructive cast '(&lt;x&gt;)' requires an owning heap handle; '&lt;x&gt;' is a stack value. Allocate it with 'heap &lt;x&gt; &lt;x&gt;' or 'heap &lt;x&gt;(...)', or cast its address with '&amp;&lt;x&gt;'.</summary>
	public const string DestructiveCastRequiresAnOwningHeapHandleIsAStackValueAlloca = "CVL4041";

	/// <summary>Destructor name '&lt;x&gt;' does not match extended type '&lt;x&gt;'.</summary>
	public const string DestructorNameDoesNotMatchExtendedType = "CVL4042";

	/// <summary>Duplicate constructor signature for type '&lt;x&gt;'.</summary>
	public const string DuplicateConstructorSignatureForType = "CVL4043";

	/// <summary>Duplicate definition of '&lt;x&gt;'</summary>
	public const string DuplicateDefinitionOf = "CVL4044";

	/// <summary>Duplicate definition of function '&lt;x&gt;' with a matching parameter signature.</summary>
	public const string DuplicateDefinitionOfFunctionWithAMatchingParameterSignature = "CVL4045";

	/// <summary>Duplicate definition of global variable '&lt;x&gt;'.</summary>
	public const string DuplicateDefinitionOfGlobalVariable = "CVL4046";

	/// <summary>Duplicate destructor definition for type '&lt;x&gt;'.</summary>
	public const string DuplicateDestructorDefinitionForType = "CVL4047";

	/// <summary>Duplicate field '&lt;x&gt;' in struct '&lt;x&gt;'</summary>
	public const string DuplicateFieldInStruct = "CVL4048";

	/// <summary>Duplicate field '&lt;x&gt;' in union '&lt;x&gt;'</summary>
	public const string DuplicateFieldInUnion = "CVL4049";

	/// <summary>Duplicate initializer for field '&lt;x&gt;'</summary>
	public const string DuplicateInitializerForField = "CVL4050";

	/// <summary>Duplicate interface definition '&lt;x&gt;'</summary>
	public const string DuplicateInterfaceDefinition = "CVL4051";

	/// <summary>Duplicate protocol definition '&lt;x&gt;'</summary>
	public const string DuplicateProtocolDefinition = "CVL4052";

	/// <summary>Duplicate symbol '&lt;x&gt;' on type '&lt;x&gt;' in extension blocks.</summary>
	public const string DuplicateSymbolOnTypeInExtensionBlocks = "CVL4053";

	/// <summary>Duplicate type definition '&lt;x&gt;'</summary>
	public const string DuplicateTypeDefinition = "CVL4054";

	/// <summary>Duplicate variant '&lt;x&gt;' in enum '&lt;x&gt;'</summary>
	public const string DuplicateVariantInEnum = "CVL4055";

	/// <summary>Enum '&lt;x&gt;' does not contain variant '&lt;x&gt;'</summary>
	public const string EnumDoesNotContainVariant = "CVL4056";

	/// <summary>Enum '&lt;x&gt;' must contain at least one variant (empty enums are prohibited).</summary>
	public const string EnumMustContainAtLeastOneVariantEmptyEnumsAreProhibited = "CVL4057";

	/// <summary>Enum variants cannot carry a promoted variable.</summary>
	public const string EnumVariantsCannotCarryAPromotedVariable = "CVL4058";

	/// <summary>Extension method '&lt;x&gt;' declares read-only 'ref this' receiver but mutates field(s) of '&lt;x&gt;'.</summary>
	public const string ExtensionMethodDeclaresReadOnlyRefThisReceiverButMutatesFiel = "CVL4059";

	/// <summary>Field '&lt;x&gt;' of struct '&lt;x&gt;' conflicts with embedded field from '&lt;x&gt;'.</summary>
	public const string FieldOfStructConflictsWithEmbeddedFieldFrom = "CVL4060";

	/// <summary>Function '&lt;x&gt;' expects &lt;x&gt; argument(s) but received &lt;x&gt;</summary>
	public const string FunctionExpectsArgumentSButReceived = "CVL4061";

	/// <summary>Function '&lt;x&gt;' expects &lt;x&gt; arguments but received &lt;x&gt;</summary>
	public const string FunctionExpectsArgumentsButReceived = "CVL4062";

	/// <summary>Function '&lt;x&gt;' expects &lt;x&gt; type &lt;x&gt; but received &lt;x&gt;</summary>
	public const string FunctionExpectsTypeButReceived = "CVL4063";

	/// <summary>Function '&lt;x&gt;' expects at least &lt;x&gt; arguments but received &lt;x&gt;</summary>
	public const string FunctionExpectsAtLeastArgumentsButReceived = "CVL4064";

	/// <summary>Function '&lt;x&gt;' expects return type '&lt;x&gt;' but found '&lt;x&gt;'</summary>
	public const string FunctionExpectsReturnTypeButFound = "CVL4065";

	/// <summary>Function '&lt;x&gt;' is declared to return '&lt;x&gt;' but is missing a return statement.</summary>
	public const string FunctionIsDeclaredToReturnButIsMissingAReturnStatement = "CVL4066";

	/// <summary>Function '&lt;x&gt;' must declare a body unless decorated with '[Intrinsic]'.</summary>
	public const string FunctionMustDeclareABodyUnlessDecoratedWithIntrinsic = "CVL4067";

	/// <summary>Generic parameter '&lt;x&gt;' does not have a default value and must be specified</summary>
	public const string GenericParameterDoesNotHaveADefaultValueAndMustBeSpecified = "CVL4068";

	/// <summary>Global delegate '&lt;x&gt;' requires an initializer; delegates are non-null and cannot be default-initialized.</summary>
	public const string GlobalDelegateRequiresAnInitializerDelegatesAreNonNullAndCan = "CVL4069";

	/// <summary>HasFlag expects exactly one argument of the same [Flags] enum type '&lt;x&gt;'.</summary>
	public const string HasFlagExpectsExactlyOneArgumentOfTheSameFlagsEnumType = "CVL4070";

	/// <summary>Heap array allocation size must be an integer.</summary>
	public const string HeapArrayAllocationSizeMustBeAnInteger = "CVL4071";

	/// <summary>Implicit conversion between enum '&lt;x&gt;' and '&lt;x&gt;' is forbidden; use an explicit cast.</summary>
	public const string ImplicitConversionBetweenEnumAndIsForbiddenUseAnExplicitCast = "CVL4072";

	/// <summary>Interface parameter '&lt;x&gt;' of function '&lt;x&gt;' cannot be resolved to a concrete conforming type; argument is abstract interface type '&lt;x&gt;'</summary>
	public const string InterfaceParameterOfFunctionCannotBeResolvedToAConcreteConfo = "CVL4073";

	/// <summary>Interface parameter '&lt;x&gt;' requires a single concrete type, but both '&lt;x&gt;' and '&lt;x&gt;' were passed</summary>
	public const string InterfaceParameterRequiresASingleConcreteTypeButBothAndWereP = "CVL4074";

	/// <summary>Invalid delegate return type '&lt;x&gt;': delegate '&lt;x&gt;' may not return a provenance-bearing type (§3.2).</summary>
	public const string InvalidDelegateReturnTypeDelegateMayNotReturnAProvenanceBear = "CVL4075";

	/// <summary>Invalid underlying storage type '&lt;x&gt;' for [Flags] enum '&lt;x&gt;': [Flags] enums require unsigned storage (uint, ushort, byte, ulong, or char).</summary>
	public const string InvalidUnderlyingStorageTypeForFlagsEnumFlagsEnumsRequireUns = "CVL4076";

	/// <summary>Invalid underlying storage type '&lt;x&gt;' for enum '&lt;x&gt;'. Allowed storage types: int, uint, short, ushort, long, ulong, char, byte, sbyte.</summary>
	public const string InvalidUnderlyingStorageTypeForEnumAllowedStorageTypesIntUin = "CVL4077";

	/// <summary>Method '&lt;x&gt;' is declared to return '&lt;x&gt;' but is missing a return statement.</summary>
	public const string MethodIsDeclaredToReturnButIsMissingAReturnStatement = "CVL4078";

	/// <summary>Method '&lt;x&gt;' must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct '&lt;x&gt;'.</summary>
	public const string MethodMustDeclareRefThisOrRefvarThisReceiverInStrictMutabili = "CVL4079";

	/// <summary>Method '&lt;x&gt;' must declare a body unless decorated with '[Intrinsic]'.</summary>
	public const string MethodMustDeclareABodyUnlessDecoratedWithIntrinsic = "CVL4080";

	/// <summary>Missing initializer for field '&lt;x&gt;' of struct '&lt;x&gt;'</summary>
	public const string MissingInitializerForFieldOfStruct = "CVL4081";

	/// <summary>Name expects no arguments.</summary>
	public const string NameExpectsNoArguments = "CVL4082";

	/// <summary>No constructor of '&lt;x&gt;' matches initializer argument types (&lt;x&gt;).</summary>
	public const string NoConstructorOfMatchesInitializerArgumentTypes = "CVL4083";

	/// <summary>No overload of function '&lt;x&gt;' matches argument types (&lt;x&gt;)</summary>
	public const string NoOverloadOfFunctionMatchesArgumentTypes = "CVL4084";

	/// <summary>Operator '~' cannot be applied to non-[Flags] enum '&lt;x&gt;'.</summary>
	public const string OperatorCannotBeAppliedToNonFlagsEnum = "CVL4085";

	/// <summary>Protocol parameter '&lt;x&gt;' of function '&lt;x&gt;' cannot be resolved to a concrete conforming type; argument is abstract protocol type '&lt;x&gt;'</summary>
	public const string ProtocolParameterOfFunctionCannotBeResolvedToAConcreteConfor = "CVL4086";

	/// <summary>Protocol parameter '&lt;x&gt;' requires a single concrete type, but both '&lt;x&gt;' and '&lt;x&gt;' were passed</summary>
	public const string ProtocolParameterRequiresASingleConcreteTypeButBothAndWerePa = "CVL4087";

	/// <summary>Raw pointer variables cannot be declared outside unsafe context.</summary>
	public const string RawPointerVariablesCannotBeDeclaredOutsideUnsafeContext = "CVL4088";

	/// <summary>Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.</summary>
	public const string ReceiverParameterRefvarThisRefThisIsOnlyAllowedOnExtensionMe = "CVL4089";

	/// <summary>Reference cannot escape unbound scope: cannot assign local reference to global variable '&lt;x&gt;'</summary>
	public const string ReferenceCannotEscapeUnboundScopeCannotAssignLocalReferenceT = "CVL4090";

	/// <summary>Reference cannot escape unbound scope: cannot assign local reference to reference field '&lt;x&gt;' of non-local variable '&lt;x&gt;'</summary>
	public const string ReferenceCannotEscapeUnboundScopeCannotAssignLocalReferenceT2 = "CVL4091";

	/// <summary>Reference type inference requires an initializer</summary>
	public const string ReferenceTypeInferenceRequiresAnInitializer = "CVL4092";

	/// <summary>Struct '&lt;x&gt;' does not contain field '&lt;x&gt;'</summary>
	public const string StructDoesNotContainField = "CVL4093";

	/// <summary>Switch statement is not exhaustive. Missing case for variant '&lt;x&gt;'.</summary>
	public const string SwitchStatementIsNotExhaustiveMissingCaseForVariant = "CVL4094";

	/// <summary>Switch statement target must be a union type.</summary>
	public const string SwitchStatementTargetMustBeAUnionType = "CVL4095";

	/// <summary>Ternary branches must have the same type. Found '&lt;x&gt;' and '&lt;x&gt;'</summary>
	public const string TernaryBranchesMustHaveTheSameTypeFoundAnd = "CVL4096";

	/// <summary>Ternary condition must be 'bool', found '&lt;x&gt;'</summary>
	public const string TernaryConditionMustBeBoolFound = "CVL4097";

	/// <summary>The 'default' branch of a switch over [NonExhaustive] enum '&lt;x&gt;' must terminate with a 'return' but ends in non-terminating statement(s).</summary>
	public const string TheDefaultBranchOfASwitchOverNonExhaustiveEnumMustTerminateW = "CVL4098";

	/// <summary>The 'is' pattern can only be applied to a union type, got '&lt;x&gt;'.</summary>
	public const string TheIsPatternCanOnlyBeAppliedToAUnionTypeGot = "CVL4099";

	/// <summary>The 'null' literal requires a pointer type (Option or raw pointer).</summary>
	public const string TheNullLiteralRequiresAPointerTypeOptionOrRawPointer = "CVL4100";

	/// <summary>The mutable reference binding form 'refvar' cannot be combined with an explicit item type: 'foreach (refvar &lt;x&gt; &lt;x&gt; ...)' is not allowed.</summary>
	public const string TheMutableReferenceBindingFormRefvarCannotBeCombinedWithAnEx = "CVL4101";

	/// <summary>The visibility of generic type instantiation '&lt;x&gt;&lt;&lt;x&gt;</summary>
	public const string TheVisibilityOfGenericTypeInstantiation = "CVL4102";

	/// <summary>Type '&lt;x&gt;' cannot be used with default because it is not a Trivial Copy Type</summary>
	public const string TypeCannotBeUsedWithDefaultBecauseItIsNotATrivialCopyType = "CVL4103";

	/// <summary>Type '&lt;x&gt;' does not conform to interface '&lt;x&gt;' for parameter '&lt;x&gt;'</summary>
	public const string TypeDoesNotConformToInterfaceForParameter = "CVL4104";

	/// <summary>Type '&lt;x&gt;' does not satisfy constraint '&lt;x&gt;' of generic parameter '&lt;x&gt;'.</summary>
	public const string TypeDoesNotSatisfyConstraintOfGenericParameter = "CVL4105";

	/// <summary>Type '&lt;x&gt;' does not satisfy the requires-clause '&lt;x&gt;' of interface '&lt;x&gt;': it does not conform to interface '&lt;x&gt;'.</summary>
	public const string TypeDoesNotSatisfyTheRequiresClauseOfInterfaceItDoesNotConfo = "CVL4106";

	/// <summary>Type '&lt;x&gt;' does not satisfy the requires-clause '&lt;x&gt;' of interface '&lt;x&gt;': missing protocol member '&lt;x&gt;'.</summary>
	public const string TypeDoesNotSatisfyTheRequiresClauseOfInterfaceMissingProtoco = "CVL4107";

	/// <summary>Type '&lt;x&gt;' does not satisfy the requires-clause '&lt;x&gt;' of protocol '&lt;x&gt;'.</summary>
	public const string TypeDoesNotSatisfyTheRequiresClauseOfProtocol = "CVL4108";

	/// <summary>Type '&lt;x&gt;' does not structurally conform to protocol '&lt;x&gt;' for parameter '&lt;x&gt;'</summary>
	public const string TypeDoesNotStructurallyConformToProtocolForParameter = "CVL4109";

	/// <summary>Type '&lt;x&gt;' is an enum; only scoped variant access ('&lt;x&gt;.VariantName') is allowed.</summary>
	public const string TypeIsAnEnumOnlyScopedVariantAccessVariantNameIsAllowed = "CVL4110";

	/// <summary>Type '&lt;x&gt;' is not a struct or union; cannot access member '&lt;x&gt;'</summary>
	public const string TypeIsNotAStructOrUnionCannotAccessMember = "CVL4111";

	/// <summary>Type '&lt;x&gt;' is not a struct type</summary>
	public const string TypeIsNotAStructType = "CVL4112";

	/// <summary>Type alias '&lt;x&gt;' expects &lt;x&gt; type argument(s), but &lt;x&gt; was given.</summary>
	public const string TypeAliasExpectsTypeArgumentSButWasGiven = "CVL4113";

	/// <summary>Undefined variable '&lt;x&gt;'</summary>
	public const string UndefinedVariable = "CVL4114";

	/// <summary>Union '&lt;x&gt;' does not contain variant '&lt;x&gt;'</summary>
	public const string UnionDoesNotContainVariant = "CVL4115";

	/// <summary>Union '&lt;x&gt;' is &lt;x&gt; bytes. Passing by value is forbidden for unions larger than 16 bytes; pass by 'ref'/'refvar' instead.</summary>
	public const string UnionIsBytesPassingByValueIsForbiddenForUnionsLargerThan16By = "CVL4116";

	/// <summary>Union '&lt;x&gt;' is &lt;x&gt; bytes. Returning by value is forbidden for unions larger than 16 bytes; return a 'ref'/'refvar' instead.</summary>
	public const string UnionIsBytesReturningByValueIsForbiddenForUnionsLargerThan16 = "CVL4117";

	/// <summary>Union initialization of '&lt;x&gt;' must specify exactly one variant.</summary>
	public const string UnionInitializationOfMustSpecifyExactlyOneVariant = "CVL4118";

	/// <summary>Unknown contract '&lt;x&gt;' in base clause of interface '&lt;x&gt;'.</summary>
	public const string UnknownContractInBaseClauseOfInterface = "CVL4119";

	/// <summary>Unknown contract '&lt;x&gt;' in constraint for parameter '&lt;x&gt;'.</summary>
	public const string UnknownContractInConstraintForParameter = "CVL4120";

	/// <summary>Unknown contract '&lt;x&gt;' in requires-clause of interface '&lt;x&gt;'.</summary>
	public const string UnknownContractInRequiresClauseOfInterface = "CVL4121";

	/// <summary>Unknown parameter type '&lt;x&gt;'</summary>
	public const string UnknownParameterType = "CVL4122";

	/// <summary>Unknown parameter type '&lt;x&gt;' in delegate declaration '&lt;x&gt;'</summary>
	public const string UnknownParameterTypeInDelegateDeclaration = "CVL4123";

	/// <summary>Unknown protocol '&lt;x&gt;' in base clause of protocol '&lt;x&gt;'.</summary>
	public const string UnknownProtocolInBaseClauseOfProtocol = "CVL4124";

	/// <summary>Unknown return type '&lt;x&gt;'</summary>
	public const string UnknownReturnType = "CVL4125";

	/// <summary>Unknown return type '&lt;x&gt;' in delegate declaration '&lt;x&gt;'</summary>
	public const string UnknownReturnTypeInDelegateDeclaration = "CVL4126";

	/// <summary>Unknown struct '&lt;x&gt;' in embed clause of struct '&lt;x&gt;'.</summary>
	public const string UnknownStructInEmbedClauseOfStruct = "CVL4127";

	/// <summary>Unknown type '&lt;x&gt;'</summary>
	public const string UnknownType = "CVL4128";

	/// <summary>Unknown type '&lt;x&gt;' in &lt;x&gt; expression.</summary>
	public const string UnknownTypeInExpression = "CVL4129";

	/// <summary>Unknown type '&lt;x&gt;' in default expression</summary>
	public const string UnknownTypeInDefaultExpression = "CVL4130";

	/// <summary>Unknown type '&lt;x&gt;' in global variable '&lt;x&gt;'.</summary>
	public const string UnknownTypeInGlobalVariable = "CVL4131";

	/// <summary>Unknown type '&lt;x&gt;' in lambda parameter.</summary>
	public const string UnknownTypeInLambdaParameter = "CVL4132";

	/// <summary>Unknown type '&lt;x&gt;' in type argument list of '&lt;x&gt;'</summary>
	public const string UnknownTypeInTypeArgumentListOf = "CVL4133";

	/// <summary>Unknown type '&lt;x&gt;' inside extension block.</summary>
	public const string UnknownTypeInsideExtensionBlock = "CVL4134";

	/// <summary>Unknown type '&lt;x&gt;' of field '&lt;x&gt;'</summary>
	public const string UnknownTypeOfField = "CVL4135";

	/// <summary>Unknown type '&lt;x&gt;' of field '&lt;x&gt;' in union '&lt;x&gt;'</summary>
	public const string UnknownTypeOfFieldInUnion = "CVL4136";

	/// <summary>Unknown type argument '&lt;x&gt;'</summary>
	public const string UnknownTypeArgument = "CVL4137";

	/// <summary>Use of moved variable '&lt;x&gt;'</summary>
	public const string UseOfMovedVariable = "CVL4138";

	/// <summary>Use of possibly-uninitialized variable '&lt;x&gt;'</summary>
	public const string UseOfPossiblyUninitializedVariable = "CVL4139";

	/// <summary>Variable '&lt;x&gt;' is already declared in this scope</summary>
	public const string VariableIsAlreadyDeclaredInThisScope = "CVL4140";

	/// <summary>Variant '&lt;x&gt;' in [Flags] enum '&lt;x&gt;' collides with an existing value '&lt;x&gt;'.</summary>
	public const string VariantInFlagsEnumCollidesWithAnExistingValue = "CVL4141";

	/// <summary>Variant '&lt;x&gt;' in [Flags] enum '&lt;x&gt;' has value 0 and must be named None, Empty, Unset, or Zero.</summary>
	public const string VariantInFlagsEnumHasValue0AndMustBeNamedNoneEmptyUnsetOrZer = "CVL4142";

	/// <summary>Variant '&lt;x&gt;' in enum '&lt;x&gt;' must be assigned a compile-time constant integer value.</summary>
	public const string VariantInEnumMustBeAssignedACompileTimeConstantIntegerValue = "CVL4143";

	/// <summary>Void variant '&lt;x&gt;' cannot carry a bound variable.</summary>
	public const string VoidVariantCannotCarryABoundVariable = "CVL4144";

	/// <summary>Void variant '&lt;x&gt;' cannot carry a promoted variable.</summary>
	public const string VoidVariantCannotCarryAPromotedVariable = "CVL4145";

	/// <summary>[NonExhaustive] enum '&lt;x&gt;' is consumed from another unit and requires an explicit 'default' or 'case _' branch.</summary>
	public const string NonExhaustiveEnumIsConsumedFromAnotherUnitAndRequiresAnExpli = "CVL4146";

	/// <summary>isReturn ? $"Captured closure environment cannot escape: the lambda captures '&lt;x&gt;</summary>
	public const string IsReturnCapturedClosureEnvironmentCannotEscapeTheLambdaCaptu = "CVL4147";

	/// <summary>isReturn ? $"Reference lambda borrow cannot escape: the lambda borrows '&lt;x&gt;</summary>
	public const string IsReturnReferenceLambdaBorrowCannotEscapeTheLambdaBorrows = "CVL4148";


	// ── Additional explicitly named diagnostics ──

	/// <summary>Unknown warning id '&lt;x&gt;'.</summary>
	public const string UnknownWarningId = "CVL4149";

	/// <summary>Attribute '[SuppressWarning]' requires exactly one string literal argument.</summary>
	public const string AttributeSuppressWarningRequiresExactlyOneStringLiteralArgum = "CVL4150";

	/// <summary>Attribute '[MustUse]' expects at most one string literal argument.</summary>
	public const string AttributeMustUseExpectsAtMostOneStringLiteralArgument = "CVL4151";

	/// <summary>Attribute '[ImportName]' requires exactly one string literal argument naming the native symbol.</summary>
	public const string AttributeImportNameRequiresExactlyOneStringLiteralArgumentNa = "CVL4152";

	/// <summary>Unknown [LibraryImport] named argument '&lt;x&gt;'. Supported names are 'win', 'linux' and 'mac'.</summary>
	public const string UnknownLibraryImportNamedArgumentSupportedNamesAreWinLinuxAn = "CVL4153";

	/// <summary>Attribute '[LibraryImport]' accepts at most one positional argument: the library name.</summary>
	public const string AttributeLibraryImportAcceptsAtMostOnePositionalArgumentTheL = "CVL4154";

	/// <summary>Attribute '[LibraryImport]' arguments must be string literals (the library name, then optional 'win'/'linux'/'mac' native paths).</summary>
	public const string AttributeLibraryImportArgumentsMustBeStringLiteralsTheLibrar = "CVL4155";

	/// <summary>Attribute '[ExposeName]' requires exactly one string literal argument naming the exported symbol.</summary>
	public const string AttributeExposeNameRequiresExactlyOneStringLiteralArgumentNa = "CVL4156";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied in &lt;x&gt; context.</summary>
	public const string AttributeCannotBeAppliedInContext = "CVL4157";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to &lt;x&gt; declarations.</summary>
	public const string AttributeCannotBeAppliedToDeclarations = "CVL4158";

	/// <summary>Duplicate attribute '[&lt;x&gt;]'.</summary>
	public const string DuplicateAttribute = "CVL4159";

	/// <summary>Duplicate attribute '[ExposeName]'.</summary>
	public const string DuplicateAttributeExposeName = "CVL4160";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to expose extern block declarations.</summary>
	public const string AttributeCannotBeAppliedToExposeExternBlockDeclarations = "CVL4161";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to extern block function declarations.</summary>
	public const string AttributeCannotBeAppliedToExternBlockFunctionDeclarations = "CVL4162";

	/// <summary>Duplicate attribute '[ImportName]'.</summary>
	public const string DuplicateAttributeImportName = "CVL4163";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to extern block declarations.</summary>
	public const string AttributeCannotBeAppliedToExternBlockDeclarations = "CVL4164";

	/// <summary>Duplicate attribute '[LibraryImport]'.</summary>
	public const string DuplicateAttributeLibraryImport = "CVL4165";

	/// <summary>Type alias '&lt;x&gt;' conflicts with an existing type name.</summary>
	public const string TypeAliasConflictsWithAnExistingTypeName = "CVL4166";

	/// <summary>Duplicate type alias '&lt;x&gt;'</summary>
	public const string DuplicateTypeAlias = "CVL4167";

	/// <summary>Receiver parameter must be named 'this' (found '&lt;x&gt;').</summary>
	public const string ReceiverParameterMustBeNamedThisFound = "CVL4168";

	/// <summary>Receiver parameter ('refvar this' / 'ref this') must be the first parameter of the method.</summary>
	public const string ReceiverParameterRefvarThisRefThisMustBeTheFirstParameterOfT = "CVL4169";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to extern block global declarations.</summary>
	public const string AttributeCannotBeAppliedToExternBlockGlobalDeclarations = "CVL4170";

	/// <summary>Unknown type '&lt;x&gt;' in foreign global '&lt;x&gt;'.</summary>
	public const string UnknownTypeInForeignGlobal = "CVL4171";

	/// <summary>Attribute '[&lt;x&gt;]' cannot be applied to foreign global declarations.</summary>
	public const string AttributeCannotBeAppliedToForeignGlobalDeclarations = "CVL4172";

	// ── Parser syntax errors ──

	/// <summary>Unexpected token '&lt;x&gt;'.</summary>
	public const string UnexpectedToken = "CVL4173";

	/// <summary>Missing '&lt;x&gt;' at '&lt;x&gt;'.</summary>
	public const string ExpectedToken = "CVL4174";

	/// <summary>Unexpected end of file.</summary>
	public const string UnexpectedEndOfFile = "CVL4175";

}
