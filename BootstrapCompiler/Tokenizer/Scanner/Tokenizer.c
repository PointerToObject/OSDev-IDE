#include "../Tokenizer.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

Scanner* scanner_create(char* source)
{
    Scanner* s = (Scanner*)malloc(sizeof(Scanner));
    s->src = source;
    s->offset = 0;
    s->len = strlen(source);
    s->line = 1;
    s->column = 1;
    return s;
}

void scanner_free(Scanner* s)
{
    free(s);
}

char peek(Scanner* s)
{
    if (s->offset >= s->len) return '\0';
    return s->src[s->offset];
}

char peek_next(Scanner* s)
{
    if (s->offset + 1 >= s->len) return '\0';
    return s->src[s->offset + 1];
}

char advance(Scanner* s)
{
    if (s->offset >= s->len) return '\0';
    char c = s->src[s->offset++];
    if (c == '\n')
    {
        s->line++;
        s->column = 1;
    }
    else
    {
        s->column++;
    }
    return c;
}

void skip_line_comment(Scanner* s);
void skip_block_comment(Scanner* s);

void skip_whitespace(Scanner* s)
{
    while (1)
    {
        char c = peek(s);
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
        {
            advance(s);
        }
        else if (c == '/' && peek_next(s) == '/')
        {
            skip_line_comment(s);
        }
        else if (c == '/' && peek_next(s) == '*')
        {
            skip_block_comment(s);
        }
        else
        {
            break;
        }
    }
}

void skip_line_comment(Scanner* s)
{
    while (peek(s) != '\n' && peek(s) != '\0')
        advance(s);
    if (peek(s) == '\n') advance(s);
}

void skip_block_comment(Scanner* s)
{
    advance(s); // '/'
    advance(s); // '*'

    while (peek(s) != '\0')
    {
        if (peek(s) == '*' && peek_next(s) == '/')
        {
            advance(s); // '*'
            advance(s); // '/'
            return;
        }
        advance(s);
    }
}

void skip_preprocessor_line(Scanner* s)
{
    while (peek(s) != '\n' && peek(s) != '\0')
        advance(s);
    if (peek(s) == '\n') advance(s);
}

Token* token_create(Tokens type, char* word, int line, int column)
{
    Token* t = (Token*)malloc(sizeof(Token));
    t->type = type;
    t->word = word;
    t->line = line;
    t->column = column;
    return t;
}

void token_free(Token* t)
{
    if (t->word) free(t->word);
    free(t);
}

Tokens check_keyword(const char* word)
{
    if (strcmp(word, "int") == 0) return TOKEN_INT;
    if (strcmp(word, "char") == 0) return TOKEN_CHAR_KW;
    if (strcmp(word, "void") == 0) return TOKEN_VOID;
    if (strcmp(word, "struct") == 0) return TOKEN_STRUCT;
    if (strcmp(word, "typedef") == 0) return TOKEN_TYPEDEF;
    if (strcmp(word, "enum") == 0) return TOKEN_ENUM;
    if (strcmp(word, "if") == 0) return TOKEN_IF;
    if (strcmp(word, "else") == 0) return TOKEN_ELSE;
    if (strcmp(word, "while") == 0) return TOKEN_WHILE;
    if (strcmp(word, "for") == 0) return TOKEN_FOR;
    if (strcmp(word, "return") == 0) return TOKEN_RETURN;
    if (strcmp(word, "sizeof") == 0) return TOKEN_SIZEOF;
    if (strcmp(word, "break") == 0) return TOKEN_BREAK;
    if (strcmp(word, "continue") == 0) return TOKEN_CONTINUE;
    return TOKEN_IDENTIFIER;
}

Token* scan_identifier(Scanner* s)
{
    int start = s->offset;
    int line = s->line;
    int column = s->column;

    while (isalnum(peek(s)) || peek(s) == '_')
        advance(s);

    int len = s->offset - start;
    char* word = (char*)malloc(len + 1);
    memcpy(word, s->src + start, len);
    word[len] = '\0';

    Tokens type = check_keyword(word);
    return token_create(type, word, line, column);
}


Token* scan_number(Scanner* s)
{
    int start = s->offset;
    int line = s->line;
    int column = s->column;
    char num_buffer[64] = { 0 };
    int num_idx = 0;
    int value = 0;

    // Check for hexadecimal: 0x or 0X
    if (peek(s) == '0' && (peek_next(s) == 'x' || peek_next(s) == 'X'))
    {
        advance(s); // '0'
        advance(s); // 'x' or 'X'

        // Read hex digits
        while (isxdigit(peek(s)))
        {
            num_buffer[num_idx++] = advance(s);
        }
        num_buffer[num_idx] = '\0';

        // Convert hex to decimal
        value = (int)strtol(num_buffer, NULL, 16);

        // Create word as decimal string
        char* word = (char*)malloc(32);
        sprintf(word, "%d", value);
        return token_create(TOKEN_NUMBER, word, line, column);
    }
    // Regular decimal number
    else
    {
        while (isdigit(peek(s)))
            advance(s);

        int len = s->offset - start;
        char* word = (char*)malloc(len + 1);
        memcpy(word, s->src + start, len);
        word[len] = '\0';

        return token_create(TOKEN_NUMBER, word, line, column);
    }
}

Token* scan_string(Scanner* s)
{
    int line = s->line;
    int column = s->column;
    advance(s); // opening "

    int start = s->offset;

    while (peek(s) != '"' && peek(s) != '\0' && peek(s) != '\n')
    {
        if (peek(s) == '\\')
        {
            advance(s); // backslash
            if (peek(s) != '\0') advance(s); // escaped char
        }
        else
        {
            advance(s);
        }
    }

    int len = s->offset - start;
    char* word = (char*)malloc(len + 1);
    memcpy(word, s->src + start, len);
    word[len] = '\0';

    if (peek(s) != '"')
    {
        // Unterminated string
        return token_create(TOKEN_ERROR, word, line, column);
    }

    advance(s); // closing "
    return token_create(TOKEN_STRING, word, line, column);
}

Token* scan_char(Scanner* s)
{
    int line = s->line;
    int column = s->column;
    advance(s); // opening '

    int start = s->offset;

    if (peek(s) == '\\')
    {
        advance(s);
        if (peek(s) != '\0') advance(s);
    }
    else if (peek(s) != '\0' && peek(s) != '\n' && peek(s) != '\'')
    {
        advance(s);
    }

    int len = s->offset - start;
    char* word = (char*)malloc(len + 1);
    memcpy(word, s->src + start, len);
    word[len] = '\0';

    if (peek(s) != '\'')
    {
        return token_create(TOKEN_ERROR, word, line, column);
    }

    advance(s); // closing '
    return token_create(TOKEN_CHAR, word, line, column);
}

Token* tokenize(Scanner* s)
{
    skip_whitespace(s);

    char c = peek(s);
    if (c == '\0')
        return token_create(TOKEN_EOF, NULL, s->line, s->column);

    int line = s->line;
    int column = s->column;

    // Preprocessor directive: skip entire line
    if (c == '#')
    {
        skip_preprocessor_line(s);
        return tokenize(s); // recurse to get next token
    }

    // Identifiers / keywords
    if (isalpha(c) || c == '_')
        return scan_identifier(s);

    // Numbers
    if (isdigit(c))
        return scan_number(s);

    // Strings
    if (c == '"')
        return scan_string(s);

    // Chars
    if (c == '\'')
        return scan_char(s);

    // Multi-char operators
    if (c == '+')
    {
        advance(s);
        if (peek(s) == '+') { advance(s); return token_create(TOKEN_PLUS_PLUS, _strdup("++"), line, column); }
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_PLUS_ASSIGN, _strdup("+="), line, column); }
        return token_create(TOKEN_PLUS, _strdup("+"), line, column);
    }

    if (c == '-')
    {
        advance(s);
        if (peek(s) == '-') { advance(s); return token_create(TOKEN_MINUS_MINUS, _strdup("--"), line, column); }
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_MINUS_ASSIGN, _strdup("-="), line, column); }
        if (peek(s) == '>') { advance(s); return token_create(TOKEN_ARROW, _strdup("->"), line, column); }
        return token_create(TOKEN_MINUS, _strdup("-"), line, column);
    }

    if (c == '*')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_STAR_ASSIGN, _strdup("*="), line, column); }
        return token_create(TOKEN_STAR, _strdup("*"), line, column);
    }

    if (c == '/')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_SLASH_ASSIGN, _strdup("/="), line, column); }
        return token_create(TOKEN_SLASH, _strdup("/"), line, column);
    }

    if (c == '=')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_EQUAL, _strdup("=="), line, column); }
        return token_create(TOKEN_ASSIGN, _strdup("="), line, column);
    }

    if (c == '!')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_NOT_EQUAL, _strdup("!="), line, column); }
        return token_create(TOKEN_EXCLAIM, _strdup("!"), line, column);
    }

    if (c == '<')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_LESS_EQUAL, _strdup("<="), line, column); }
        if (peek(s) == '<') { advance(s); return token_create(TOKEN_LSHIFT, _strdup("<<"), line, column); }
        return token_create(TOKEN_LESS, _strdup("<"), line, column);
    }

    if (c == '>')
    {
        advance(s);
        if (peek(s) == '=') { advance(s); return token_create(TOKEN_GREATER_EQUAL, _strdup(">="), line, column); }
        if (peek(s) == '>') { advance(s); return token_create(TOKEN_RSHIFT, _strdup(">>"), line, column); }
        return token_create(TOKEN_GREATER, _strdup(">"), line, column);
    }

    if (c == '&')
    {
        advance(s);
        if (peek(s) == '&') { advance(s); return token_create(TOKEN_AND, _strdup("&&"), line, column); }
        return token_create(TOKEN_AMPERSAND, _strdup("&"), line, column);
    }

    if (c == '|')
    {
        advance(s);
        if (peek(s) == '|') { advance(s); return token_create(TOKEN_OR, _strdup("||"), line, column); }
        return token_create(TOKEN_PIPE, _strdup("|"), line, column);
    }

    // Single-char tokens
    advance(s);
    switch (c)
    {
    case '(': return token_create(TOKEN_LPAREN, _strdup("("), line, column);
    case ')': return token_create(TOKEN_RPAREN, _strdup(")"), line, column);
    case '{': return token_create(TOKEN_LBRACE, _strdup("{"), line, column);
    case '}': return token_create(TOKEN_RBRACE, _strdup("}"), line, column);
    case '[': return token_create(TOKEN_LBRACKET, _strdup("["), line, column);
    case ']': return token_create(TOKEN_RBRACKET, _strdup("]"), line, column);
    case ';': return token_create(TOKEN_SEMICOLON, _strdup(";"), line, column);
    case ',': return token_create(TOKEN_COMMA, _strdup(","), line, column);
    case '.': return token_create(TOKEN_DOT, _strdup("."), line, column);
    case ':': return token_create(TOKEN_COLON, _strdup(":"), line, column);
    case '?': return token_create(TOKEN_QUESTION, _strdup("?"), line, column);
    case '%': return token_create(TOKEN_PERCENT, _strdup("%"), line, column);
    case '^': return token_create(TOKEN_CARET, _strdup("^"), line, column);
    case '~': return token_create(TOKEN_TILDE, _strdup("~"), line, column);
    default:
    {
        char* bad = (char*)malloc(2);
        bad[0] = c;
        bad[1] = '\0';
        return token_create(TOKEN_ERROR, bad, line, column);
    }
    }
}

/* ====================== Main ====================== */
