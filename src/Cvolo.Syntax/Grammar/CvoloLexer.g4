lexer grammar CvoloLexer;

channels { COMMENTS }

// Keywords
VAL: 'val';
VAR: 'var';
REF: 'ref';
REFVAR: 'refvar';
EXTERN: 'extern';
RETURN: 'return';
IF: 'if';
ELSE: 'else';
WHILE: 'while';
FOR: 'for';
TRUE: 'true';
FALSE: 'false';
VOID: 'void';
INT: 'int';
DOUBLE: 'double';
BOOL: 'bool';
STRING: 'string';
CHAR: 'char';
STRUCT: 'struct';
UNSAFE: 'unsafe';
PANIC: 'panic';

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

// Literals
StringLiteral: '"' (EscapeSequence | ~["\\\r\n])* '"';
fragment EscapeSequence: '\\' [0nrt"\\];
IntegerLiteral: [0-9]+;
DoubleLiteral: [0-9]+ '.' [0-9]+;
Identifier: [a-zA-Z_][a-zA-Z0-9_]*;

// Whitespace and comments
WS: [ \t\r\n]+ -> skip;
LineComment: '//' ~[\r\n]* -> channel(COMMENTS);
BlockComment: '/*' .*? '*/' -> channel(COMMENTS);
