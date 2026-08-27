lexer grammar CvoloLexer;

channels { COMMENTS }

// Keywords
VAL: 'val';
VAR: 'var';
REFVAR: 'refvar';
HEAP: 'heap';
REF: 'ref';
EXTERN: 'extern';
RETURN: 'return';
IF: 'if';
ELSE: 'else';
WHILE: 'while';
FOR: 'for';
TRUE: 'true';
FALSE: 'false';
NULL: 'null';
VOID: 'void';
INT: 'int';
DOUBLE: 'double';
BOOL: 'bool';
STRING: 'string';
CHAR: 'char';
STRUCT: 'struct';
UNSAFE: 'unsafe';
UNBOUND: 'unbound';
PANIC: 'panic';
EXTENSION: 'extension';
NAMESPACE: 'namespace';
USING: 'using';
GLOBAL: 'global';

// Punctuation
LPAREN: '(';
RPAREN: ')';
LBRACE: '{';
RBRACE: '}';
LBRACK: '[';
RBRACK: ']';
SEMI: ';';
COMMA: ',';
DOT: '.';
COLON: ':';
QMARK: '?';

// Operators
PLUS: '+';
MINUS: '-';
STAR: '*';
DIV: '/';
PERCENT: '%';
AMPERSAND: '&';
PIPE: '|';
CARET: '^';
TILDE: '~';
EXCLAMATION: '!';
ASSIGN: '=';
LT: '<';
GT: '>';
EQ: '==';
NEQ: '!=';
LTE: '<=';
GTE: '>=';
AND: '&&';
OR: '||';
PLUS_ASSIGN: '+=';
MINUS_ASSIGN: '-=';
STAR_ASSIGN: '*=';
DIV_ASSIGN: '/=';
ARROW: '->';
ELLIPSIS: '...';
INC: '++';
DEC: '--';
LSHIFT: '<<';
RSHIFT: '>>';
URSHIFT: '>>>';
LSHIFT_ASSIGN: '<<=';
RSHIFT_ASSIGN: '>>=';
URSHIFT_ASSIGN: '>>>=';
AND_ASSIGN: '&=';
OR_ASSIGN: '|=';
XOR_ASSIGN: '^=';

// Literals
CharLiteral: '\'' (EscapeSequence | ~['\\\r\n]) '\'';
InterpolatedStringLiteral: '$"' (EscapeSequence | ~["\\\r\n])* '"';
StringLiteral: '"' (EscapeSequence | ~["\\\r\n])* '"';
fragment EscapeSequence: '\\' [0nrt"\\];
IntegerLiteral: [0-9]+;
DoubleLiteral: [0-9]+ '.' [0-9]+;
Identifier: [a-zA-Z_][a-zA-Z0-9_]*;

// Whitespace and comments
WS: [ \t\r\n]+ -> skip;
LineComment: '//' ~[\r\n]* -> channel(COMMENTS);
BlockComment: '/*' .*? '*/' -> channel(COMMENTS);
