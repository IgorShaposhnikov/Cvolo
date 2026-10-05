using System.Text;
using Cvolo.Analysis.Symbols.Base;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

/// <summary>
/// Owns the LLVM <c>DIBuilder</c> for one module and exposes the narrow debug-information surface
/// the code generators need: a compile unit, per-source-file <c>DIFile</c> nodes, per-type
/// <c>DIBasicType</c> nodes, a <c>DISubprogram</c> per function, and per-statement
/// <c>DILocation</c> assignments on the instruction builder.
/// </summary>
/// <remarks>
/// Debug information is emitted entirely at the LLVM IR level. The compiler never writes a
/// <c>.pdb</c> or a <c>.dSYM</c> bundle itself: on Windows the module carries CodeView
/// <c>.debug$S</c> subsections and the linker's <c>/DEBUG</c> switch creates the program database;
/// on ELF targets the module carries DWARF and the object file gains <c>.debug_*</c> sections.
/// This class is the peer of <see cref="TbaaMetadata"/>: an independent, disposable helper owned
/// by <see cref="CodeGenerator"/> and created only when <c>--debug</c> was requested.
/// </remarks>
/// <remarks>
/// <para>
/// Module flags and the compile unit are published from the constructor, before any other debug
/// node is created, so a module that is never finalized still advertises the debug-info version
/// its consumers expect.
/// </para>
/// <para>
/// All debug nodes are cached by their source path or semantic type name. Repeated requests for the
/// same file or type return the identical node, which keeps the emitted module small and keeps the
/// metadata graph a DAG rather than a tree.
/// </para>
/// </remarks>
internal sealed unsafe class DebugMetadata : IDisposable
{
	/// <summary>Metadata version understood by every LLVM release the compiler links against.</summary>
	private const uint DebugInformationVersion = 3;

	/// <summary>DWARF version requested for ELF/Mach-O line tables.</summary>
	private const uint DwarfVersion = 5;

	// DWARF base type encodings (DWARF5 section 7.8).
	private const uint DwarfAteBoolean = 2;
	private const uint DwarfAteFloat = 4;
	private const uint DwarfAteSigned = 5;
	private const uint DwarfAteSignedChar = 6;
	private const uint DwarfAteUnsigned = 7;
	private const uint DwarfAteUnsignedChar = 8;

	private const string UnknownSourceName = "<cvolo>";

	private readonly LLVMContextRef _context;
	private readonly LLVMModuleRef _module;
	private readonly int _nativePointerBits;
	private readonly bool _isOptimized;
	private readonly Dictionary<string, IntPtr> _files = [];
	private readonly Dictionary<string, IntPtr> _basicTypes = [];
	private readonly Dictionary<string, IntPtr> _unspecifiedTypes = [];
	private readonly Dictionary<string, IntPtr> _subroutineTypes = [];
	private LLVMOpaqueDIBuilder* _builder;
	private IntPtr _compileUnit;
	private bool _completed;
	private bool _disposed;

	/// <summary>
	/// Creates the module's debug-information builder, publishes the required module flags, and
	/// attaches the module's <c>DICompileUnit</c> to <c>!llvm.dbg.cu</c>.
	/// </summary>
	/// <param name="context">LLVM context owning <paramref name="module"/>.</param>
	/// <param name="module">Module that receives the debug metadata.</param>
	/// <param name="options">Emission configuration supplied by the compilation driver.</param>
	/// <param name="nativePointerBits">Pointer width of the active data layout, used to size the
	/// <c>nint</c>/<c>nuint</c> debug types.</param>
	/// <exception cref="InvalidOperationException">
	/// Thrown when the module has no target data layout, which means
	/// <see cref="TargetLayout.Apply"/> has not run yet.
	/// </exception>
	public DebugMetadata(LLVMContextRef context, LLVMModuleRef module, DebugInfoOptions options, int nativePointerBits)
	{
		var triple = module.Target;
		var dataLayout = module.DataLayout;
		if (string.IsNullOrEmpty(triple) || string.IsNullOrEmpty(dataLayout))
		{
			throw new InvalidOperationException(
				"Debug info requires a target data layout; call TargetLayout.Apply before creating DebugMetadata. " +
				$"(target='{triple}', dataLayout='{dataLayout}')");
		}

		_context = context;
		_module = module;
		_isOptimized = options.IsOptimized;
		_nativePointerBits = nativePointerBits > 0 ? nativePointerBits : 64;
		_builder = LLVMSharp.Interop.LLVM.CreateDIBuilder(module);

		// Module flags first: a consumer that reads !llvm.module.flags must always find the debug
		// metadata version, and on Windows the CodeView switch tells the backend that the module
		// carries CodeView debug data rather than DWARF.
		AddModuleFlag("Debug Info Version", DebugInformationVersion);
		if (IsWindowsMsvcTriple(triple))
			AddModuleFlag("CodeView", 1);
		else
			AddModuleFlag("Dwarf Version", DwarfVersion);

		var primaryFile = GetOrCreateFile(options.PrimarySourcePath);
		_compileUnit = CreateCompileUnit(primaryFile, options.Producer, options.IsOptimized);
		AddNamedMetadataOperand("llvm.dbg.cu", _compileUnit);
	}

	/// <summary>The module's <c>DICompileUnit</c>, used as the lexical scope of every subprogram.</summary>
	public IntPtr CompileUnit => _compileUnit;

	/// <summary>
	/// Returns the cached <c>DIFile</c> for <paramref name="sourcePath"/>, creating it from the file
	/// name and containing directory on first use.
	/// </summary>
	public IntPtr GetOrCreateFile(string? sourcePath)
	{
		if (string.IsNullOrEmpty(sourcePath))
			sourcePath = UnknownSourceName;

		if (_files.TryGetValue(sourcePath, out var cached))
			return cached;

		SplitSourcePath(sourcePath!, out var fileName, out var directory);

		var nameBytes = Utf8(fileName);
		var directoryBytes = Utf8(directory);
		IntPtr file;
		fixed (byte* namePtr = nameBytes)
		fixed (byte* directoryPtr = directoryBytes)
		{
			file = (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateFile(
				_builder,
				(sbyte*)namePtr,
				(UIntPtr)nameBytes.Length,
				(sbyte*)directoryPtr,
				(UIntPtr)directoryBytes.Length);
		}

		_files[sourcePath!] = file;
		return file;
	}

	/// <summary>
	/// Returns a cached <c>DIBasicType</c> with the given name, bit size, and DWARF base encoding.
	/// </summary>
	public IntPtr GetOrCreateBasicType(string name, ulong sizeInBits, uint encoding)
	{
		var key = $"{name}/{sizeInBits}/{encoding}";
		if (_basicTypes.TryGetValue(key, out var cached))
			return cached;

		var nameBytes = Utf8(name);
		IntPtr type;
		fixed (byte* namePtr = nameBytes)
		{
			type = (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateBasicType(
				_builder,
				(sbyte*)namePtr,
				(UIntPtr)nameBytes.Length,
				sizeInBits,
				encoding,
				LLVMDIFlags.LLVMDIFlagZero);
		}

		_basicTypes[key] = type;
		return type;
	}

	/// <summary>
	/// Maps a Cvolo semantic type onto a debug type. Primitives receive an exact
	/// <c>DIBasicType</c>; every other type (structs, unions, enums, interfaces, slices, pointers,
	/// strings, and <c>void</c>) receives an unnamed-type placeholder. Modeling Cvolo's aggregate
	/// and reference layouts in DWARF is out of scope for this increment.
	/// </summary>
	public IntPtr GetOrCreateType(TypeSymbol? type)
	{
		var name = type?.Name ?? "void";
		if (TryResolvePrimitive(name, out var sizeInBits, out var encoding))
			return GetOrCreateBasicType(name, sizeInBits, encoding);

		return GetOrCreateUnspecifiedType(name);
	}

	/// <summary>Returns a cached opaque (<c>DIUnspecifiedType</c>) placeholder for a non-primitive type.</summary>
	public IntPtr GetOrCreateUnspecifiedType(string name)
	{
		if (_unspecifiedTypes.TryGetValue(name, out var cached))
			return cached;

		var nameBytes = Utf8(name);
		IntPtr type;
		fixed (byte* namePtr = nameBytes)
		{
			type = (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateUnspecifiedType(_builder, (sbyte*)namePtr, (UIntPtr)nameBytes.Length);
		}

		_unspecifiedTypes[name] = type;
		return type;
	}

	/// <summary>
	/// Returns the cached <c>DISubroutineType</c> describing <paramref name="returnType"/> followed
	/// by <paramref name="parameterTypes"/>. A <c>void</c> return is represented by a null first
	/// element, as DWARF requires.
	/// </summary>
	public IntPtr GetOrCreateSubroutineType(IntPtr file, TypeSymbol? returnType, IReadOnlyList<TypeSymbol>? parameterTypes)
	{
		var returnName = returnType?.Name ?? "void";
		var key = $"{(long)file}:{returnName}";
		foreach (var parameter in parameterTypes ?? [])
			key = $"{key},{parameter?.Name ?? "?"}";

		if (_subroutineTypes.TryGetValue(key, out var cached))
			return cached;

		// DWARF type lists are laid out as [return, parameters...]; void is a null entry.
		var elementCount = (parameterTypes?.Count ?? 0) + 1;
		var elements = new LLVMOpaqueMetadata*[elementCount];
		elements[0] = default;
		if (!IsVoid(returnName))
			elements[0] = (LLVMOpaqueMetadata*)GetOrCreateType(returnType);

		if (parameterTypes is not null)
		{
			for (var i = 0; i < parameterTypes.Count; i++)
				elements[i + 1] = (LLVMOpaqueMetadata*)GetOrCreateType(parameterTypes[i]);
		}

		IntPtr subroutineType;
		fixed (LLVMOpaqueMetadata** elementsPtr = elements)
		{
			subroutineType = (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateSubroutineType(
				_builder,
				(LLVMOpaqueMetadata*)file,
				elementsPtr,
				(uint)elementCount,
				LLVMDIFlags.LLVMDIFlagPrototyped);
		}

		_subroutineTypes[key] = subroutineType;
		return subroutineType;
	}

	/// <summary>
	/// Creates the <c>DISubprogram</c> for one function definition. The subprogram is scoped to the
	/// module's compile unit and finalized by <see cref="Complete"/>, not here.
	/// </summary>
	/// <param name="name">Source-level function name shown by debuggers.</param>
	/// <param name="linkageName">Mangled symbol name the subprogram is keyed by.</param>
	/// <param name="file">The <c>DIFile</c> holding the declaration.</param>
	/// <param name="line">1-based line of the function name.</param>
	/// <param name="subroutineType">The function's <c>DISubroutineType</c>.</param>
	/// <param name="isLocal">True when the symbol is not externally visible.</param>
	public IntPtr CreateFunction(string name, string linkageName, IntPtr file, uint line, IntPtr subroutineType, bool isLocal)
	{
		var nameBytes = Utf8(name);
		var linkageBytes = Utf8(linkageName);

		IntPtr subprogram;
		fixed (byte* namePtr = nameBytes)
		fixed (byte* linkagePtr = linkageBytes)
		{
			subprogram = (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateFunction(
				_builder,
				(LLVMOpaqueMetadata*)_compileUnit,
				(sbyte*)namePtr,
				(UIntPtr)nameBytes.Length,
				(sbyte*)linkagePtr,
				(UIntPtr)linkageBytes.Length,
				(LLVMOpaqueMetadata*)file,
				line,
				(LLVMOpaqueMetadata*)subroutineType,
				isLocal ? 1 : 0,
				1,
				line,
				LLVMDIFlags.LLVMDIFlagPrototyped,
				_isOptimized ? 1 : 0);
		}

		return subprogram;
	}

	/// <summary>Binds <paramref name="subprogram"/> to <paramref name="function"/> as its <c>!dbg</c> scope.</summary>
	public void AttachSubprogram(LLVMValueRef function, IntPtr subprogram) =>
		LLVMSharp.Interop.LLVM.SetSubprogram(function, (LLVMOpaqueMetadata*)subprogram);

	/// <summary>
	/// Assigns the builder's current debug location, so every instruction emitted next carries a
	/// <c>!dbg</c> reference to this source position within <paramref name="scope"/>.
	/// </summary>
	public void SetLocation(LLVMBuilderRef builder, uint line, uint column, IntPtr scope)
	{
		var location = LLVMSharp.Interop.LLVM.DIBuilderCreateDebugLocation(
			(LLVMOpaqueContext*)_context.Handle,
			line,
			column,
			(LLVMOpaqueMetadata*)scope,
			default);
		builder.CurrentDebugLocation = (LLVMValueRef)LLVMSharp.Interop.LLVM.MetadataAsValue((LLVMOpaqueContext*)_context.Handle, location);
	}

	/// <summary>
	/// Clears the builder's current debug location. Required before emitting compiler-generated code
	/// that has no source position (delegate thunks, runtime intrinsics) so that such instructions
	/// never inherit an unrelated <c>!dbg</c>.
	/// </summary>
	public void ClearLocation(LLVMBuilderRef builder) => builder.CurrentDebugLocation = default;

	/// <summary>
	/// Restores a previously captured builder location. Used by emitters that temporarily clear the
	/// location (delegate thunks) and must hand the caller's location back intact.
	/// </summary>
	public void RestoreLocation(LLVMBuilderRef builder, LLVMValueRef location) =>
		builder.CurrentDebugLocation = location;

	/// <summary>
	/// Finalizes the builder, which completes every <c>DISubprogram</c> created through this instance
	/// and publishes them under <c>!llvm.dbg.cu</c>. Idempotent.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when LLVM rejects the debug metadata.</exception>
	public void Complete()
	{
		if (_completed || _disposed)
			return;

		try
		{
			LLVMSharp.Interop.LLVM.DIBuilderFinalize(_builder);
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException(
				string.Format(System.Globalization.CultureInfo.InvariantCulture,
					"Debug metadata finalization failed: {0}.", ex.Message),
				ex);
		}

		_completed = true;
	}

	/// <summary>
	/// Finalizes the builder and releases it. Must run before the module is disposed.
	/// </summary>
	public void Dispose()
	{
		if (_disposed)
			return;

		Complete();

		if (_builder is not null)
		{
			LLVMSharp.Interop.LLVM.DisposeDIBuilder(_builder);
			_builder = null;
		}

		_disposed = true;
	}

	/// <summary>
	/// Splits a source path into the <c>DIFile</c> file-name and directory pair. Both separators are
	/// handled so the emitted paths stay correct when a Windows path is inspected by ELF tooling.
	/// </summary>
	internal static void SplitSourcePath(string sourcePath, out string fileName, out string directory)
	{
		var trimmed = sourcePath;
		if (trimmed.Length == 0)
		{
			fileName = UnknownSourceName;
			directory = ".";
			return;
		}

		var separator = trimmed.LastIndexOfAny(['/', '\\']);
		if (separator < 0)
		{
			fileName = trimmed;
			directory = ".";
			return;
		}

		fileName = trimmed[(separator + 1)..];
		if (fileName.Length == 0)
			fileName = UnknownSourceName;

		directory = separator == 0 ? trimmed[..1] : trimmed[..separator];
		if (directory.Length == 0)
			directory = ".";
	}

	private static bool IsWindowsMsvcTriple(string triple) =>
		triple.EndsWith("-windows-msvc", StringComparison.Ordinal);

	private static bool IsVoid(string typeName) =>
		typeName.Length == 0 || string.Equals(typeName, "void", StringComparison.Ordinal);

	private bool TryResolvePrimitive(string name, out ulong sizeInBits, out uint encoding)
	{
		switch (name)
		{
			case "bool":
				sizeInBits = 8; encoding = DwarfAteBoolean; return true;
			case "sbyte":
				sizeInBits = 8; encoding = DwarfAteSignedChar; return true;
			case "byte":
				sizeInBits = 8; encoding = DwarfAteUnsignedChar; return true;
			case "char":
				sizeInBits = 8; encoding = DwarfAteUnsignedChar; return true;
			case "short":
				sizeInBits = 16; encoding = DwarfAteSigned; return true;
			case "ushort":
				sizeInBits = 16; encoding = DwarfAteUnsigned; return true;
			case "int":
				sizeInBits = 32; encoding = DwarfAteSigned; return true;
			case "uint":
				sizeInBits = 32; encoding = DwarfAteUnsigned; return true;
			case "long":
				sizeInBits = 64; encoding = DwarfAteSigned; return true;
			case "ulong":
				sizeInBits = 64; encoding = DwarfAteUnsigned; return true;
			case "nint":
				sizeInBits = (ulong)_nativePointerBits; encoding = DwarfAteSigned; return true;
			case "nuint":
				sizeInBits = (ulong)_nativePointerBits; encoding = DwarfAteUnsigned; return true;
			case "float":
				sizeInBits = 32; encoding = DwarfAteFloat; return true;
			case "double":
				sizeInBits = 64; encoding = DwarfAteFloat; return true;
			default:
				sizeInBits = 0; encoding = 0; return false;
		}
	}

	private void AddModuleFlag(string key, uint value)
	{
		var keyBytes = Utf8Z(key);
		IntPtr constant;
		fixed (byte* keyPtr = keyBytes)
		{
			constant = (IntPtr)LLVMSharp.Interop.LLVM.ValueAsMetadata(
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, value, false));
			LLVMSharp.Interop.LLVM.AddModuleFlag(
				_module,
				LLVMModuleFlagBehavior.LLVMModuleFlagBehaviorWarning,
				(sbyte*)keyPtr,
				(UIntPtr)(keyBytes.Length - 1),
				(LLVMOpaqueMetadata*)constant);
		}
	}

	private void AddNamedMetadataOperand(string name, IntPtr metadata)
	{
		// This entry point has no length parameter, so the key must be NUL terminated.
		var nameBytes = Utf8Z(name);
		unsafe
		{
			fixed (byte* namePtr = nameBytes)
			{
				LLVMSharp.Interop.LLVM.AddNamedMetadataOperand(
					_module,
					(sbyte*)namePtr,
					LLVMSharp.Interop.LLVM.MetadataAsValue((LLVMOpaqueContext*)_context.Handle, (LLVMOpaqueMetadata*)metadata));
			}
		}
	}

	private IntPtr CreateCompileUnit(IntPtr file, string producer, bool isOptimized)
	{
		var producerBytes = Utf8Z(producer);
		unsafe
		{
			fixed (byte* producerPtr = producerBytes)
			{
				return (IntPtr)LLVMSharp.Interop.LLVM.DIBuilderCreateCompileUnit(
					_builder,
					LLVMDWARFSourceLanguage.LLVMDWARFSourceLanguageC_plus_plus,
					(LLVMOpaqueMetadata*)file,
					(sbyte*)producerPtr,
					(UIntPtr)(producerBytes.Length - 1),
					isOptimized ? 1 : 0,
					default,
					UIntPtr.Zero,
					0,
					default,
					UIntPtr.Zero,
					LLVMDWARFEmissionKind.LLVMDWARFEmissionFull,
					0,
					0,
					0,
					default,
					UIntPtr.Zero,
					default,
					UIntPtr.Zero);
			}
		}
	}

	private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

	private static byte[] Utf8Z(string value)
	{
		var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
		Encoding.UTF8.GetBytes(value, bytes);
		bytes[^1] = 0;
		return bytes;
	}
}