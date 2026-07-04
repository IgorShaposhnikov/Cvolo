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
	;

functionDeclaration
	: returnType Identifier LPAREN parameterList? RPAREN blockStatement
	;

externDeclaration
	: EXTERN returnType Identifier LPAREN externParameterList? RPAREN SEMI
	;

structDeclaration
	: STRUCT Identifier LBRACE structField* RBRACE SEMI?
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
	: type Identifier
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
	| blockStatement
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
	| REFVAR Identifier ASSIGN expression SEMI          // <-- Add this (inferred mutable ref)
	| REF Identifier ASSIGN expression SEMI             // <-- Add this (inferred read-only ref)
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
	: REF expression                                        							# borrowExpression
	| expression DOT Identifier                             							# memberAccessExpression
	| expression LBRACK expression RBRACK												# indexExpression
	| expression INC                                        							# postfixIncrementExpression
	| expression DEC                                        							# postfixDecrementExpression
	| LPAREN type RPAREN expression                         							# castExpression
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
	| expression ASSIGN expression														# assignmentExpression
	| expression (PLUS_ASSIGN | MINUS_ASSIGN | STAR_ASSIGN | DIV_ASSIGN | AND_ASSIGN | OR_ASSIGN | XOR_ASSIGN | LSHIFT_ASSIGN | RSHIFT_ASSIGN | URSHIFT_ASSIGN) expression		# compoundAssignmentExpression
	| Identifier LPAREN argumentList? RPAREN                							# callExpression
	| HEAP expression                                       							# heapAllocationExpression
	| LBRACE (expression (COMMA expression)*)? RBRACE									# arrayInitializationExpression
	| Identifier LBRACE structInitializerList? RBRACE       							# structInitializationExpression
	| Identifier                                            							# identifierExpression
	| IntegerLiteral                                        							# integerLiteralExpression
	| DoubleLiteral                                         							# doubleLiteralExpression
	| StringLiteral                                         							# stringLiteralExpression
	| TRUE                                                  							# booleanLiteralExpression
	| FALSE                                                 							# booleanLiteralExpression
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
