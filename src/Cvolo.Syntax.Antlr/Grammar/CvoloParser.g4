parser grammar CvoloParser;

options { tokenVocab = CvoloLexer; }

compilationUnit
	: usingDirective* (namespaceDeclaration | declaration*) EOF
	;

usingDirective
	: EXPOSE? USING qualifiedName SEMI
	;

namespaceDeclaration
	: NAMESPACE qualifiedName (SEMI usingDirective* declaration* | LBRACE usingDirective* declaration* RBRACE)
	;

qualifiedName
	: Identifier (DOT Identifier)*
	;

declaration
	: functionDeclaration
	| externGlobalDeclaration
	| externBlockDeclaration
	| externDeclaration
	| exposeExternExportDeclaration
	| exposeExternBlockDeclaration
	| structDeclaration
	| unionDeclaration
	| enumDeclaration
	| extensionDeclaration
	| interfaceDeclaration
	| protocolDeclaration
	| globalVariableDeclaration
	| aliasDeclaration
	| delegateDeclaration
	| delegateBlockDeclaration
	;

aliasDeclaration
	: ALIAS Identifier (LT genericParameterList GT)? ASSIGN type SEMI
	;

delegateDeclaration
	: visibilityModifier? (UNSAFE callingConvention)? DELEGATE type Identifier (LT genericParameterList GT)? LPAREN delegateParameterList? RPAREN SEMI
	;

delegateParameterList
	: delegateParameter (COMMA delegateParameter)*
	;

delegateParameter
	: receiverVarParameter
	| receiverRefParameter
	| type Identifier
	;

delegateBlockDeclaration
	: UNSAFE callingConvention LBRACE delegateDeclaration* RBRACE SEMI?
	;

globalVariableDeclaration
	: visibilityModifier? GLOBAL (VAL | VAR)? type Identifier (ASSIGN expression)? SEMI
	;

visibilityModifier
	: PRIVATE
	| INTERNAL
	| PUBLIC
	;

attributeList
	: LBRACK attribute (COMMA attribute)* RBRACK
	;

attribute
	: qualifiedName (LPAREN attributeArgumentList? RPAREN)?
	;

attributeArgumentList
	: attributeArgument (COMMA attributeArgument)*
	;

attributeArgument
	: Identifier COLON expression
	| expression
	;

functionModifier
	: UNSAFE
	| UNBOUND
	;

functionDeclaration
	: attributeList* visibilityModifier? BUILTIN? ((UNSAFE callingConvention) | functionModifier)? returnType builtinCapableIdentifier (LT typeList GT)? LPAREN parameterList? RPAREN (blockStatement | SEMI)
	;

builtinCapableIdentifier
	: Identifier
	| SIZEOF
	| ALIGNOF
	| OFFSETOF
	| NAMEOF
	| TYPEOF
	| DEFAULT
	;

externDeclaration
	: visibilityModifier? EXTERN returnType Identifier LPAREN externParameterList? RPAREN SEMI
	;

externGlobalDeclaration
	: attributeList* visibilityModifier? EXTERN callingConvention GLOBAL (VAL | VAR)? type Identifier SEMI
	;

externBlockDeclaration
	: attributeList* visibilityModifier? EXTERN callingConvention? LBRACE externBlockMember* RBRACE SEMI?
	;

externBlockMember
	: externBlockFunction
	| externBlockGlobal
	;

externBlockGlobal
	: attributeList* visibilityModifier? GLOBAL (VAL | VAR)? type Identifier SEMI
	;

callingConvention
	: StringLiteral
	;

externBlockFunction
	: attributeList* visibilityModifier? returnType Identifier LPAREN externParameterList? RPAREN SEMI
	;

exposeExternExportDeclaration
	: EXPOSE EXTERN callingConvention returnType Identifier LPAREN parameterList? RPAREN (blockStatement | SEMI)
	;

exposeExternBlockDeclaration
	: attributeList* visibilityModifier? EXPOSE EXTERN callingConvention LBRACE exposeExternFunction* RBRACE SEMI?
	;

exposeExternFunction
	: attributeList* visibilityModifier? returnType Identifier (LT typeList GT)? LPAREN parameterList? RPAREN (blockStatement | SEMI)
	;

structDeclaration
	: attributeList* visibilityModifier? BUILTIN? STRUCT Identifier (LT genericParameterList GT)? (EMBED qualifiedName)? whereClause* LBRACE structField* RBRACE SEMI?
	;

unionDeclaration
	: attributeList* visibilityModifier? BUILTIN? UNSAFE? UNION Identifier (LT genericParameterList GT)? whereClause? LBRACE unionField* RBRACE SEMI?
	;

unionField
	: visibilityModifier? type Identifier SEMI
	;

enumDeclaration
	: attributeList* visibilityModifier? BUILTIN? ENUM Identifier (COLON type)? LBRACE enumVariant (COMMA enumVariant)* RBRACE SEMI?
	;

enumVariant
	: Identifier (ASSIGN expression)?
	;

extensionDeclaration
	: visibilityModifier? EXTENSION Identifier (LT genericParameterList GT)? (COLON qualifiedName)? whereClause* LBRACE extensionMember* RBRACE SEMI?
	;

extensionMember
	: extensionFunctionDeclaration
	| operatorDeclaration
	| destructorDeclaration
	| constructorDeclaration
	;

// An operator overload is a receiverless associated callable. It needs no leading dot: 'operator'
// is already unambiguous. Operators are never generic and never declare a receiver, so the
// parameter list is mandatory and plain (an operator must declare at least one operand, so the
// empty form is only reachable from metadata round-trips and is rejected by declaration analysis).
// The body may be omitted for declarations imported from package metadata. ASSIGN is listed only
// so that 'operator =' produces a dedicated diagnostic instead of a bare syntax error; assignment
// is compiler-owned and not overloadable.
operatorDeclaration
	: attributeList* visibilityModifier? returnType OPERATOR overloadableOperator LPAREN parameterList? RPAREN (blockStatement | SEMI)
	;

overloadableOperator
	: PLUS | MINUS | STAR | DIV | PERCENT
	| EQ | NEQ
	| LT | LTE | GT | GTE
	| AMPERSAND | PIPE | CARET
	| LSHIFT
	| GT GT
	| EXCLAMATION | TILDE
	| ASSIGN
	;

// Members of an extension block accept an optional leading DOT. The DOT marks an
// associated (receiverless) function; without it the member is an instance
// extension method that receives a synthetic 'this' parameter. The top-level
// functionDeclaration rule deliberately has no DOT so a leading dot stays a
// syntax error outside extension blocks.
extensionFunctionDeclaration
	: attributeList* visibilityModifier? BUILTIN? ((UNSAFE callingConvention) | functionModifier)? returnType DOT? builtinCapableIdentifier (LT typeList GT)? LPAREN parameterList? RPAREN (blockStatement | SEMI)
	;

interfaceDeclaration
	: attributeList* visibilityModifier? INTERFACE Identifier (LT genericParameterList GT)? (COLON qualifiedName (COMMA qualifiedName)*)? (FOR type)? LBRACE interfaceMember* RBRACE SEMI?
	;

interfaceMember
	: returnType Identifier LPAREN parameterList? RPAREN SEMI
	;

protocolDeclaration
	: attributeList* visibilityModifier? PROTOCOL Identifier (LT genericParameterList GT)? (COLON qualifiedName (COMMA qualifiedName)*)? (FOR type)? LBRACE protocolMember* RBRACE SEMI?
	;

protocolMember
	: returnType Identifier LPAREN parameterList? RPAREN SEMI
	;

destructorDeclaration
	: attributeList* visibilityModifier? BUILTIN? TILDE Identifier LPAREN RPAREN (blockStatement | SEMI)
	;

constructorDeclaration
	: attributeList* visibilityModifier? BUILTIN? Identifier LPAREN parameterList? RPAREN (COLON Identifier LPAREN argumentList? RPAREN)? (blockStatement | SEMI)
	;

structField
	: visibilityModifier? type Identifier SEMI
	;

returnType
	: VOID
	| type
	;

type
	: primitiveType                                     # baseType
	| qualifiedName                                     # qualifiedType
	| Identifier                                        # identifierType
	| type LBRACK RBRACK                                # sliceType
	| type LBRACK expression RBRACK                     # arrayType
	| type LT typeList GT                               # genericInstantiationType
	| type STAR                                         # pointerType
	| type QMARK                                       # optionalType
	| REFVAR type                                       # refVarType
	| REF type                                          # readOnlyRefType
	;

primitiveType
	: INT
	| UINT
	| LONG
	| ULONG
	| SHORT
	| USHORT
	| BYTE
	| SBYTE
	| NINT
	| NUINT
	| FLOAT
	| DOUBLE
	| BOOL
	| STRING
	| CHAR
	| VOID
	;

parameterList
	: parameter (COMMA parameter)*
	;

parameter
	: receiverVarParameter
	| receiverRefParameter
	| attributeList* type Identifier
	;

receiverVarParameter
	: REFVAR Identifier
	;

receiverRefParameter
	: REF Identifier
	;

externParameterList
	: externParameter (COMMA externParameter)*
	;

externParameter
	: type Identifier
	| ELLIPSIS
	;

blockStatement
	: LBRACE statement* RBRACE
	;

statement
	: returnStatement
	| variableDeclaration
	| expressionStatement
	| ifStatement
	| whileStatement
	| forStatement
	| forEachStatement
	| labeledBlockStatement
	| unsafeBlockStatement
	| switchStatement
	| blockStatement
	| deferStatement
	| tryStatement
	| controlExitStatement
	;

labeledBlockStatement
	: Identifier COLON blockStatement
	;

deferStatement
	: DEFER (blockStatement | Identifier blockStatement | expressionStatement)
	;

tryStatement
	: TRY blockStatement catchClause* finallyClause?
	;

finallyClause
	: FINALLY blockStatement
	;

catchClause
	: CATCH LPAREN qualifiedName RPAREN blockStatement                # catchValueOrTypeClause
	| CATCH LPAREN qualifiedName Identifier RPAREN blockStatement     # catchTypedClause
	| CATCH blockStatement                                           # catchBareClause
	;

unsafeBlockStatement
	: UNSAFE blockStatement
	;

returnStatement
	: RETURN expression? SEMI
	;

expressionStatement
	: expression SEMI
	;

variableDeclaration
	: (VAL | VAR) type? Identifier (ASSIGN expression)? SEMI
	| type Identifier (ASSIGN expression)? SEMI
	| REFVAR Identifier ASSIGN expression SEMI
	| REF Identifier ASSIGN expression SEMI
	| (VAL | VAR) type Identifier LPAREN structInitializerList RPAREN SEMI
	| type Identifier LPAREN structInitializerList RPAREN SEMI
	| (VAL | VAR) type Identifier LPAREN expression RPAREN SEMI
	| type Identifier LPAREN expression RPAREN SEMI
	;

ifStatement
	: IF LPAREN expression RPAREN statement (ELSE statement)?
	;

whileStatement
	: (Identifier COLON)? WHILE LPAREN expression RPAREN statement
	;

forStatement
	: (Identifier COLON)? FOR LPAREN variableDeclaration expression SEMI expression RPAREN statement
	;

forEachStatement
	: (Identifier COLON)? FOREACH LPAREN forEachBinding IN expression RPAREN blockStatement
	;

forEachBinding
	: (VAL | VAR) type? Identifier
	| type Identifier
	| REFVAR Identifier
	;

controlExitStatement
	: BREAK Identifier? SEMI
	| CONTINUE Identifier? SEMI
	;

expression
	: expression DOT Identifier                             							# memberAccessExpression
	| expression ARROW Identifier                          							# arrowMemberAccessExpression
	| expression LBRACK expression RBRACK												# indexExpression
	| lambdaCaptureMode? LPAREN lambdaParameterList? RPAREN FAT_ARROW (expression | blockStatement)	# lambdaExpression
	| (REF | REFVAR) expression                                        					# borrowExpression
	| expression INC                                        							# postfixIncrementExpression
	| expression DEC                                        							# postfixDecrementExpression
	| LPAREN type RPAREN expression                         							# castExpression
	| STAR expression                                       							# dereferenceExpression
	| AMPERSAND expression                                  							# addressOfExpression
	| MINUS expression                                      							# unaryMinusExpression
	| EXCLAMATION expression                                							# logicalNotExpression
	| TILDE expression																	# bitwiseNotExpression
	| INC expression                                        							# prefixIncrementExpression
	| DEC expression                                        							# prefixDecrementExpression
	| expression (STAR | DIV | PERCENT) expression          							# multiplicativeExpression
	| expression (PLUS | MINUS) expression                  							# additiveExpression
	| expression LSHIFT expression														# leftShiftExpression
	| expression GT GT GT expression													# arithmeticRightShiftExpression
	| expression GT GT expression														# rightShiftExpression
	| expression (LT | GT | LTE | GTE) expression										# relationalExpression
	| expression (EQ | NEQ) expression													# equalityExpression
	| expression IS Identifier (Identifier)?											# isPatternExpression
	| expression AMPERSAND expression													# bitwiseAndExpression
	| expression CARET expression														# bitwiseXorExpression
	| expression PIPE expression														# bitwiseOrExpression
	| expression AND expression															# logicalAndExpression
	| expression OR expression															# logicalOrExpression
	| expression CATCH expression # catchExpression
	| expression CATCH LPAREN Identifier RPAREN FAT_ARROW blockStatement # catchLambdaExpression
	| expression QMARK expression COLON expression										# ternaryExpression
	| expression ASSIGN expression														# assignmentExpression
	| expression (PLUS_ASSIGN | MINUS_ASSIGN | STAR_ASSIGN | DIV_ASSIGN | AND_ASSIGN | OR_ASSIGN | XOR_ASSIGN | LSHIFT_ASSIGN | RSHIFT_ASSIGN | URSHIFT_ASSIGN) expression		# compoundAssignmentExpression
	| qualifiedName (LT typeList GT)? LPAREN argumentList? RPAREN						# callExpression
	| qualifiedName LT typeList GT DOT builtinCapableIdentifier (LT typeList GT)? LPAREN argumentList? RPAREN	# genericTypeQualifiedCallExpression
	| ASM asmOption* (LT type GT)? LPAREN StringLiteral (COMMA asmArgument)* RPAREN	# asmExpression
	| NAMEOF LPAREN expression RPAREN												# nameofExpression
	| TYPEOF LPAREN type RPAREN														# typeofExpression
	| SIZEOF LT type GT LPAREN RPAREN												# sizeofExpression
	| ALIGNOF LT type GT LPAREN RPAREN												# alignofExpression
	| OFFSETOF LT type GT LPAREN memberDesignator RPAREN							# offsetofExpression
	| HEAP expression                                       							# heapAllocationExpression
	| HEAP type LBRACK expression RBRACK                                                # heapArrayAllocationExpression
	| LBRACE (expression (COMMA expression)*)? RBRACE									# arrayInitializationExpression
	| Identifier (LT typeList GT)? LBRACE structInitializerList? RBRACE					# structInitializationExpression
	| LPAREN structInitializerList RPAREN												# parenthesizedStructInitializer
	| Identifier                                            							# identifierExpression
	| IntegerLiteral                                        							# integerLiteralExpression
	| DoubleLiteral                                         							# doubleLiteralExpression
	| StringLiteral                                         							# stringLiteralExpression
	| RawStringLiteral										# rawStringExpression
	| BadRawStringLiteral									# badRawStringExpression
	| BadInterpolatedRawStringLiteral						# badRawStringExpression
	| InterpolatedStringLiteral															# interpolatedStringExpression
	| InterpolatedRawStringLiteral							# interpolatedRawStringExpression
	| CharLiteral																		# charLiteralExpression
	| BadEmptyCharLiteral																# badEmptyCharLiteralExpression
	| BadCharLiteral																	# badCharLiteralExpression
	| BadIntegerSuffix																	# badIntegerSuffixExpression
	| BadLeadingUnderscoreNumber															# badLeadingUnderscoreExpression
	| TrailingUnderInteger																# badIntegerSeparatorExpression
	| BadDoubleLiteral																	# badDoubleLiteralExpression
	| TRUE																				# booleanLiteralExpression
	| FALSE																				# booleanLiteralExpression
	| NULL																				# nullLiteralExpression
	| VOID																				# voidLiteralExpression
	| DEFAULT LPAREN type RPAREN															# defaultExpression
	| DEFAULT																				# defaultExpression
	| LPAREN expression RPAREN															# parenthesizedExpression
	;

lambdaCaptureMode
	: MOVE
	| REF
	| REFVAR
	;

lambdaParameterList
	: lambdaParameter (COMMA lambdaParameter)*
	;

lambdaParameter
	: Identifier
	| type Identifier
	;

argumentList
	: expression (COMMA expression)*
	;

memberDesignator
	: Identifier (DOT Identifier)*
	;

asmOption
	: VOLATILE
	| ALIGNSTACK
	| INTEL
	;

asmArgument
	: (LBRACK Identifier RBRACK)? StringLiteral COLON expression	# asmOperandArgument
	| StringLiteral													# asmClobberArgument
	| asmOption														# asmOptionArgument
	;

structInitializerList
	: structMemberInitializer (COMMA structMemberInitializer)*
	;

structMemberInitializer
	: Identifier COLON expression
	;

genericParameterList
	: Identifier (COMMA Identifier)*
	;

whereClause
    : WHERE whereConstraint ((COMMA | WHERE)? whereConstraint)*
    ;

whereConstraint
    : DEFAULT Identifier COLON type                # defaultWhereConstraint
    | Identifier COLON qualifiedName               # interfaceWhereConstraint
    ;

typeList
	: type (COMMA type)*
	;

switchStatement
	: SWITCH LPAREN expression RPAREN LBRACE switchCase* RBRACE
	;

switchCase
	: CASE pattern COLON statement*
	| DEFAULT COLON statement*
	;

pattern
	: Identifier Identifier                                 # variantPattern
	| Identifier                                            # constantPattern
	;
