parser grammar CvoloParser;

options { tokenVocab = CvoloLexer; }

compilationUnit
    : declaration* EOF
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
    : primitiveType
    | Identifier
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
    | expressionStatement
    | variableDeclaration
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
    : LPAREN type RPAREN expression                     # castExpression
    | MINUS expression                                   # unaryMinusExpression
    | EXCLAMATION expression                             # logicalNotExpression
    | expression (STAR | DIV | PERCENT) expression       # multiplicativeExpression
    | expression (PLUS | MINUS) expression               # additiveExpression
    | expression (LT | GT | LTE | GTE) expression         # relationalExpression
    | expression (EQ | NEQ) expression                    # equalityExpression
    | expression AND expression                           # logicalAndExpression
    | expression OR expression                            # logicalOrExpression
    | expression ASSIGN expression                        # assignmentExpression
    | Identifier LPAREN argumentList? RPAREN              # callExpression
    | Identifier                                          # identifierExpression
    | IntegerLiteral                                      # integerLiteralExpression
    | DoubleLiteral                                       # doubleLiteralExpression
    | StringLiteral                                       # stringLiteralExpression
    | TRUE                                                # booleanLiteralExpression
    | FALSE                                               # booleanLiteralExpression
    | LPAREN expression RPAREN                            # parenthesizedExpression
    ;

argumentList
    : expression (COMMA expression)*
    ;
