parser grammar CvoloParser;

options { tokenVocab = CvoloLexer; }

compilationUnit
	: usingDirective* (namespaceDeclaration | declaration*) EOF
	;

usingDirective
	: USING qualifiedName SEMI
	;

namespaceDeclaration
	: NAMESPACE qualifiedName (SEMI usingDirective* declaration* | LBRACE usingDirective* declaration* RBRACE)
	;

qualifiedName
	: Identifier (DOT Identifier)*
	;

declaration
	: functionDeclaration
	| externDeclaration
	| structDeclaration
	| extensionDeclaration
	| globalVariableDeclaration
	;

globalVariableDeclaration
	: GLOBAL (VAL | VAR)? type Identifier (ASSIGN expression)? SEMI
	;

attributeList
	: LBRACK attribute (COMMA attribute)* RBRACK
	;

attribute
	: qualifiedName (LPAREN argumentList? RPAREN)?
	;

functionModifier
	: UNSAFE
	| UNBOUND
	;

functionDeclaration
	: attributeList* functionModifier? returnType Identifier (LT typeList GT)? LPAREN parameterList? RPAREN blockStatement
	;

externDeclaration
	: EXTERN returnType Identifier LPAREN externParameterList? RPAREN SEMI
	;

structDeclaration
	: attributeList* STRUCT Identifier (LT genericParameterList GT)? LBRACE structField* RBRACE SEMI?
	;

extensionDeclaration
	: EXTENSION Identifier (LT genericParameterList GT)? LBRACE (functionDeclaration | destructorDeclaration | constructorDeclaration)* RBRACE SEMI?
	;

destructorDeclaration
	: attributeList* TILDE Identifier LPAREN RPAREN blockStatement SEMI?
	;

constructorDeclaration
	: attributeList* Identifier LPAREN parameterList? RPAREN blockStatement SEMI?
	;

structField
	: type Identifier SEMI
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
	| type LBRACK IntegerLiteral RBRACK                 # arrayType
	| type LT typeList GT                               # genericInstantiationType
	| type STAR                                         # pointerType
	| REFVAR type                                       # refVarType
	| REF type                                          # readOnlyRefType
	;

primitiveType
	: INT
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
	: attributeList* type Identifier
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
	| unsafeBlockStatement
	| blockStatement
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
	: WHILE LPAREN expression RPAREN statement
	;

forStatement
	: FOR LPAREN variableDeclaration expression SEMI expression RPAREN statement
	;

expression
	: expression DOT Identifier                             							# memberAccessExpression
	| expression ARROW Identifier                          							# arrowMemberAccessExpression
	| expression LBRACK expression RBRACK												# indexExpression
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
	| expression (LSHIFT | RSHIFT | URSHIFT) expression									# shiftExpression
	| expression (LT | GT | LTE | GTE) expression										# relationalExpression
	| expression (EQ | NEQ) expression													# equalityExpression
	| expression AMPERSAND expression													# bitwiseAndExpression
	| expression CARET expression														# bitwiseXorExpression
	| expression PIPE expression														# bitwiseOrExpression
	| expression AND expression															# logicalAndExpression
	| expression OR expression															# logicalOrExpression
	| expression QMARK expression COLON expression										# ternaryExpression
	| expression ASSIGN expression														# assignmentExpression
	| expression (PLUS_ASSIGN | MINUS_ASSIGN | STAR_ASSIGN | DIV_ASSIGN | AND_ASSIGN | OR_ASSIGN | XOR_ASSIGN | LSHIFT_ASSIGN | RSHIFT_ASSIGN | URSHIFT_ASSIGN) expression		# compoundAssignmentExpression
	| qualifiedName (LT typeList GT)? LPAREN argumentList? RPAREN						# callExpression
	| HEAP expression                                       							# heapAllocationExpression
	| HEAP type LBRACK expression RBRACK                                                # heapArrayAllocationExpression
	| LBRACE (expression (COMMA expression)*)? RBRACE									# arrayInitializationExpression
	| Identifier (LT typeList GT)? LBRACE structInitializerList? RBRACE					# structInitializationExpression
	| LPAREN structInitializerList RPAREN												# parenthesizedStructInitializer
	| Identifier                                            							# identifierExpression
	| IntegerLiteral                                        							# integerLiteralExpression
	| DoubleLiteral                                         							# doubleLiteralExpression
	| StringLiteral                                         							# stringLiteralExpression
	| InterpolatedStringLiteral															# interpolatedStringExpression
	| CharLiteral																		# charLiteralExpression
	| TRUE                                                  							# booleanLiteralExpression
	| FALSE                                                 							# booleanLiteralExpression
	| NULL                                                  							# nullLiteralExpression
	| LPAREN expression RPAREN                              							# parenthesizedExpression
	;

argumentList
	: expression (COMMA expression)*
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

typeList
	: type (COMMA type)*
	;
