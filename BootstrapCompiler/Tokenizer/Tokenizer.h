#pragma once
#ifndef TOKENIZER_H
#define TOKENIZER_H

#include "../Includes.h"

typedef enum
{
	// Literals
	TOKEN_IDENTIFIER,
	TOKEN_NUMBER,
	TOKEN_STRING,
	TOKEN_CHAR,

	// Keywords
	TOKEN_INT,
	TOKEN_CHAR_KW,
	TOKEN_VOID,
	TOKEN_STRUCT,
	TOKEN_TYPEDEF,
	TOKEN_ENUM,
	TOKEN_IF,
	TOKEN_ELSE,
	TOKEN_WHILE,
	TOKEN_FOR,
	TOKEN_RETURN,
	TOKEN_SIZEOF,
	TOKEN_BREAK,
	TOKEN_CONTINUE,

	// NEW: Kernel/Driver Keywords
	TOKEN_INLINE,
	TOKEN_STATIC,
	TOKEN_EXTERN,
	TOKEN_VOLATILE,
	TOKEN_CONST,
	TOKEN_UNSIGNED,
	TOKEN_SIGNED,
	TOKEN_LONG,
	TOKEN_SHORT,
	TOKEN_REGISTER,
	TOKEN_ASM,
	TOKEN_PACKED,

	// Operators
	TOKEN_PLUS,
	TOKEN_MINUS,
	TOKEN_STAR,
	TOKEN_SLASH,
	TOKEN_PERCENT,
	TOKEN_AMPERSAND,
	TOKEN_PIPE,
	TOKEN_CARET,
	TOKEN_TILDE,
	TOKEN_EXCLAIM,
	TOKEN_ASSIGN,
	TOKEN_EQUAL,
	TOKEN_NOT_EQUAL,
	TOKEN_LESS,
	TOKEN_GREATER,
	TOKEN_LESS_EQUAL,
	TOKEN_GREATER_EQUAL,
	TOKEN_AND,
	TOKEN_OR,
	TOKEN_LSHIFT,
	TOKEN_RSHIFT,
	TOKEN_PLUS_PLUS,
	TOKEN_MINUS_MINUS,
	TOKEN_PLUS_ASSIGN,
	TOKEN_MINUS_ASSIGN,
	TOKEN_STAR_ASSIGN,
	TOKEN_SLASH_ASSIGN,
	TOKEN_ARROW,

	// Delimiters
	TOKEN_LPAREN,
	TOKEN_RPAREN,
	TOKEN_LBRACE,
	TOKEN_RBRACE,
	TOKEN_LBRACKET,
	TOKEN_RBRACKET,
	TOKEN_SEMICOLON,
	TOKEN_COMMA,
	TOKEN_DOT,
	TOKEN_COLON,
	TOKEN_QUESTION,

	// Preprocessor
	TOKEN_HASH,

	// Special
	TOKEN_EOF,
	TOKEN_ERROR
} Tokens;

typedef struct
{
	Tokens type;
	char* word;
	int    line;
	int    column;
} Token;

typedef struct
{
	char* src;
	size_t offset;
	size_t len;
	int line;
	int column;
} Scanner;

Scanner* scanner_create(char* source);
void scanner_free(Scanner* s);
int is_hex_digit(char c);
char peek(Scanner* s);
char peek_next(Scanner* s);
char advance(Scanner* s);
void skip_whitespace(Scanner* s);
void skip_line_comment(Scanner* s);
void skip_block_comment(Scanner* s);
Token* token_create(Tokens type, char* word, int line, int column);
void token_free(Token* t);
Token* tokenize(Scanner* s);
Token* scan_identifier(Scanner* s);
Token* scan_number(Scanner* s);
Token* scan_string(Scanner* s);
Token* scan_char(Scanner* s);
Tokens check_keyword(const char* word);

#endif // TOKENIZER_H