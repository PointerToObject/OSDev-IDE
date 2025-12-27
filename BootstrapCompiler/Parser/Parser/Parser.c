#include "../Parser.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ====================== Parser Basics ====================== */

Parser* parser_create(Token* tokens, size_t len)
{
    Parser* p = (Parser*)malloc(sizeof(Parser));
    p->tokens = tokens;
    p->pos = 0;
    p->len = len;
    return p;
}

void parser_free(Parser* p)
{
    free(p);
}

Token peek_token(Parser* p)
{
    if (p->pos >= p->len) return (Token) { .type = TOKEN_EOF };
    return p->tokens[p->pos];
}

Token peek_ahead(Parser* p, int offset)
{
    size_t pos = p->pos + offset;
    if (pos >= p->len) return (Token) { .type = TOKEN_EOF };
    return p->tokens[pos];
}

Token advance_token(Parser* p)
{
    if (p->pos >= p->len) return (Token) { .type = TOKEN_EOF };
    return p->tokens[p->pos++];
}

int check_token(Parser* p, Tokens type)
{
    return peek_token(p).type == type;
}

int match_token(Parser* p, Tokens type)
{
    if (check_token(p, type))
    {
        advance_token(p);
        return 1;
    }
    return 0;
}

Token expect(Parser* p, Tokens expected)
{
    Token t = peek_token(p);
    if (t.type != expected)
    {
        fprintf(stderr, "Parse error at line %d: expected token %d, got %d\n",
            t.line, expected, t.type);
        exit(1);
    }
    advance_token(p);
    return t;
}

int is_hex_digit(char c) 
{
    return (c >= '0' && c <= '9') ||
        (c >= 'a' && c <= 'f') ||
        (c >= 'A' && c <= 'F');
}

/* ====================== Node Creation ====================== */
/* (Unchanged from your original — assuming these match your AST union in Parser.h) */

AST* create_intlit_node(int value)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_INTLIT;
    node->data.int_lit.value = value;
    return node;
}

AST* create_stringlit_node(char* value)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_STRING_LIT;
    node->data.string_lit.value = _strdup(value);
    return node;
}

AST* create_charlit_node(char value)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_CHAR_LIT;
    node->data.char_lit.value = value;
    return node;
}

AST* create_ident_node(char* name)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_IDENT;
    node->data.ident.name = _strdup(name);
    return node;
}

AST* create_operator_node(Tokens op, AST* left, AST* right)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_OPERATOR;
    node->data.op.op = op;
    node->data.op.left = left;
    node->data.op.right = right;
    return node;
}

AST* create_unary_node(Tokens op, AST* operand)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_UNARY;
    node->data.unary.op = op;
    node->data.unary.operand = operand;
    return node;
}

AST* create_return_node(AST* value)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_RETURN;
    node->data.return_stmt.value = value;
    return node;
}

AST* create_assign_node(char* name, AST* value)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_ASSIGN;
    node->data.assign.var_name = _strdup(name);
    node->data.assign.value = value;
    return node;
}

AST* create_decl_node(char* type, char* name, int pointer_level, AST* init, AST* array_size)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_DECL;
    node->data.decl.type = _strdup(type);
    node->data.decl.name = _strdup(name);
    node->data.decl.pointer_level = pointer_level;
    node->data.decl.init_value = init;
    node->data.decl.array_size = array_size;
    return node;
}

AST* create_block_node()
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_BLOCK;
    node->data.block.statements = NULL;
    node->data.block.count = 0;
    node->data.block.capacity = 0;
    return node;
}

AST* create_function_node(char* return_type, char* name, AST** params, size_t param_count, AST* body)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_FUNCTION;
    node->data.function.return_type = _strdup(return_type);
    node->data.function.name = _strdup(name);
    node->data.function.params = params;
    node->data.function.param_count = param_count;
    node->data.function.body = body;
    return node;
}

AST* create_if_node(AST* condition, AST* then_block, AST* else_block)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_IF;
    node->data.if_stmt.condition = condition;
    node->data.if_stmt.then_block = then_block;
    node->data.if_stmt.else_block = else_block;
    return node;
}

AST* create_while_node(AST* condition, AST* body)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_WHILE;
    node->data.while_stmt.condition = condition;
    node->data.while_stmt.body = body;
    return node;
}

AST* create_for_node(AST* init, AST* condition, AST* increment, AST* body)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_FOR;
    node->data.for_stmt.init = init;
    node->data.for_stmt.condition = condition;
    node->data.for_stmt.increment = increment;
    node->data.for_stmt.body = body;
    return node;
}

AST* create_call_node(char* name, AST** args, size_t arg_count)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_CALL;
    node->data.call.name = _strdup(name);
    node->data.call.args = args;
    node->data.call.arg_count = arg_count;
    return node;
}

AST* create_array_access_node(AST* array, AST* index)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_ARRAY_ACCESS;
    node->data.array_access.array = array;
    node->data.array_access.index = index;
    return node;
}

AST* create_member_access_node(AST* object, char* member, int is_arrow)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_MEMBER_ACCESS;
    node->data.member_access.object = object;
    node->data.member_access.member = _strdup(member);
    node->data.member_access.is_arrow = is_arrow;
    return node;
}

AST* create_struct_decl_node(char* name, AST** members, size_t member_count)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_STRUCT_DECL;
    node->data.struct_decl.name = name ? _strdup(name) : NULL;
    node->data.struct_decl.members = members;
    node->data.struct_decl.member_count = member_count;
    return node;
}

AST* create_typedef_node(char* old_name, char* new_name)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_TYPEDEF;
    node->data.typedef_decl.old_name = _strdup(old_name);
    node->data.typedef_decl.new_name = _strdup(new_name);
    return node;
}

AST* create_enum_decl_node(char* name, AST** values, size_t value_count)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_ENUM_DECL;
    node->data.enum_decl.name = name ? _strdup(name) : NULL;
    node->data.enum_decl.values = values;
    node->data.enum_decl.value_count = value_count;
    return node;
}

AST* create_cast_node(char* type, AST* expr)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_CAST;
    node->data.cast.type = _strdup(type);
    node->data.cast.expr = expr;
    return node;
}

AST* create_sizeof_node(AST* expr)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_SIZEOF;
    node->data.sizeof_expr.expr = expr;
    return node;
}

AST* create_ternary_node(AST* condition, AST* true_expr, AST* false_expr)
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_TERNARY;
    node->data.ternary.condition = condition;
    node->data.ternary.true_expr = true_expr;
    node->data.ternary.false_expr = false_expr;
    return node;
}

AST* create_program_node()
{
    AST* node = (AST*)malloc(sizeof(AST));
    node->type = N_PROGRAM;
    node->data.program.functions = NULL;
    node->data.program.globals = NULL;
    node->data.program.func_count = 0;
    node->data.program.func_capacity = 0;
    node->data.program.global_count = 0;
    node->data.program.global_capacity = 0;
    return node;
}

void block_add_statement(AST* block, AST* stmt)
{
    if (block->type != N_BLOCK) return;

    if (block->data.block.count >= block->data.block.capacity)
    {
        size_t new_cap = block->data.block.capacity == 0 ? 8 : block->data.block.capacity * 2;
        block->data.block.statements = (AST**)realloc(block->data.block.statements, new_cap * sizeof(AST*));
        block->data.block.capacity = new_cap;
    }
    block->data.block.statements[block->data.block.count++] = stmt;
}

void program_add_function(AST* program, AST* func)
{
    if (program->type != N_PROGRAM) return;

    if (program->data.program.func_count >= program->data.program.func_capacity)
    {
        size_t new_cap = program->data.program.func_capacity == 0 ? 8 : program->data.program.func_capacity * 2;
        program->data.program.functions = (AST**)realloc(program->data.program.functions, new_cap * sizeof(AST*));
        program->data.program.func_capacity = new_cap;
    }
    program->data.program.functions[program->data.program.func_count++] = func;
}

void program_add_global(AST* program, AST* global)
{
    if (program->type != N_PROGRAM) return;

    if (program->data.program.global_count >= program->data.program.global_capacity)
    {
        size_t new_cap = program->data.program.global_capacity == 0 ? 8 : program->data.program.global_capacity * 2;
        program->data.program.globals = (AST**)realloc(program->data.program.globals, new_cap * sizeof(AST*));
        program->data.program.global_capacity = new_cap;
    }
    program->data.program.globals[program->data.program.global_count++] = global;
}

/* ====================== Expression Parsing ====================== */


static AST* parse_primary(Parser* p)
{
    Token t = peek_token(p);

    if (t.type == TOKEN_NUMBER)
    {
        advance_token(p);
        return create_intlit_node(atoi(t.word));
    }
    if (t.type == TOKEN_STRING)
    {
        advance_token(p);
        return create_stringlit_node(t.word);
    }
    if (t.type == TOKEN_CHAR)
    {
        advance_token(p);
        return create_charlit_node(t.word[0]);
    }
    if (t.type == TOKEN_IDENTIFIER)
    {
        advance_token(p);
        return create_ident_node(t.word);
    }
    if (t.type == TOKEN_LPAREN)
    {
        advance_token(p);

        // Check for cast: (Type*) or (Type)
        // Lookahead to see if it's a cast or just parenthesized expression
        Token next = peek_token(p);
        if (next.type == TOKEN_INT || next.type == TOKEN_CHAR_KW ||
            next.type == TOKEN_VOID || next.type == TOKEN_IDENTIFIER)
        {
            // Might be a cast - check if there's a ) or *) coming
            size_t saved = p->pos;

            Token type_tok = advance_token(p);
            int stars = 0;
            while (match_token(p, TOKEN_STAR)) stars++;

            if (check_token(p, TOKEN_RPAREN))
            {
                // It's a cast! (Type) or (Type*)
                expect(p, TOKEN_RPAREN);
                AST* expr = parse_unary(p);
                return create_cast_node(type_tok.word, expr);
            }

            // Not a cast, restore and parse as parenthesized expression
            p->pos = saved;
        }

        // Regular parenthesized expression
        AST* expr = parse_expression(p);
        expect(p, TOKEN_RPAREN);
        return expr;
    }
    if (t.type == TOKEN_SIZEOF)
    {
        advance_token(p);
        expect(p, TOKEN_LPAREN);

        // Check if it's sizeof(type) or sizeof(expression)
        Token next = peek_token(p);
        if (next.type == TOKEN_INT || next.type == TOKEN_CHAR_KW ||
            next.type == TOKEN_VOID || next.type == TOKEN_STRUCT)
        {
            // sizeof(type) or sizeof(struct Name)
            char* type_str = NULL;

            if (next.type == TOKEN_STRUCT)
            {
                advance_token(p); // consume 'struct'
                Token struct_name = expect(p, TOKEN_IDENTIFIER);
                size_t len = strlen("struct ") + strlen(struct_name.word) + 1;
                type_str = (char*)malloc(len);
                strcpy(type_str, "struct ");
                strcat(type_str, struct_name.word);
            }
            else
            {
                Token type_tok = advance_token(p);
                type_str = _strdup(type_tok.word);
            }

            expect(p, TOKEN_RPAREN);

            // Create a special identifier node for the type
            AST* type_node = create_ident_node(type_str);
            free(type_str);
            return create_sizeof_node(type_node);
        }

        // sizeof(expression)
        AST* expr = parse_expression(p);
        expect(p, TOKEN_RPAREN);
        return create_sizeof_node(expr);
    }

    fprintf(stderr, "Unexpected token in primary: %d at line %d\n", t.type, t.line);
    exit(1);
}

static AST* parse_postfix(Parser* p)
{
    AST* expr = parse_primary(p);

    while (1)
    {
        Token t = peek_token(p);

        if (t.type == TOKEN_LPAREN)  // function call
        {
            advance_token(p);
            AST** args = NULL;
            size_t arg_count = 0, cap = 0;

            if (!check_token(p, TOKEN_RPAREN))
            {
                do
                {
                    if (arg_count >= cap)
                    {
                        cap = cap == 0 ? 4 : cap * 2;
                        args = (AST**)realloc(args, cap * sizeof(AST*));
                    }
                    args[arg_count++] = parse_assignment(p);
                } while (match_token(p, TOKEN_COMMA));
            }
            expect(p, TOKEN_RPAREN);

            if (expr->type == N_IDENT)
            {
                char* name = expr->data.ident.name;
                free(expr);
                expr = create_call_node(name, args, arg_count);
            }
            else
            {
                // Cleanup on error
                for (size_t i = 0; i < arg_count; i++) ast_free(args[i]);
                free(args);
                fprintf(stderr, "Function pointer calls not supported\n");
                exit(1);
            }
        }
        else if (t.type == TOKEN_LBRACKET)  // array access
        {
            advance_token(p);
            AST* index = parse_expression(p);
            expect(p, TOKEN_RBRACKET);
            expr = create_array_access_node(expr, index);
        }
        else if (t.type == TOKEN_DOT || t.type == TOKEN_ARROW)
        {
            int is_arrow = (t.type == TOKEN_ARROW);
            advance_token(p);
            Token member = expect(p, TOKEN_IDENTIFIER);
            expr = create_member_access_node(expr, member.word, is_arrow);
        }
        else if (t.type == TOKEN_PLUS_PLUS || t.type == TOKEN_MINUS_MINUS)
        {
            advance_token(p);
            // Treat postfix ++/-- as unary (common simplification)
            expr = create_unary_node(t.type, expr);
        }
        else
        {
            break;
        }
    }
    return expr;
}

static AST* parse_unary(Parser* p)
{
    Token t = peek_token(p);
    if (t.type == TOKEN_PLUS_PLUS || t.type == TOKEN_MINUS_MINUS ||
        t.type == TOKEN_AMPERSAND || t.type == TOKEN_STAR ||
        t.type == TOKEN_PLUS || t.type == TOKEN_MINUS ||
        t.type == TOKEN_TILDE || t.type == TOKEN_EXCLAIM)
    {
        advance_token(p);
        AST* operand = parse_unary(p);
        return create_unary_node(t.type, operand);
    }
    return parse_postfix(p);
}

/* Precedence climbing functions (unchanged) */
static AST* parse_multiplicative(Parser* p)
{
    AST* left = parse_unary(p);
    while (1)
    {
        Token t = peek_token(p);
        if (t.type == TOKEN_STAR || t.type == TOKEN_SLASH || t.type == TOKEN_PERCENT)
        {
            advance_token(p);
            AST* right = parse_unary(p);
            left = create_operator_node(t.type, left, right);
        }
        else break;
    }
    return left;
}

static AST* parse_additive(Parser* p) { AST* left = parse_multiplicative(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_PLUS || t.type == TOKEN_MINUS) { advance_token(p); AST* right = parse_multiplicative(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_shift(Parser* p) { AST* left = parse_additive(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_LSHIFT || t.type == TOKEN_RSHIFT) { advance_token(p); AST* right = parse_additive(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_relational(Parser* p) { AST* left = parse_shift(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_LESS || t.type == TOKEN_GREATER || t.type == TOKEN_LESS_EQUAL || t.type == TOKEN_GREATER_EQUAL) { advance_token(p); AST* right = parse_shift(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_equality(Parser* p) { AST* left = parse_relational(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_EQUAL || t.type == TOKEN_NOT_EQUAL) { advance_token(p); AST* right = parse_relational(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_bitwise_and(Parser* p) { AST* left = parse_equality(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_AMPERSAND) { advance_token(p); AST* right = parse_equality(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_bitwise_xor(Parser* p) { AST* left = parse_bitwise_and(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_CARET) { advance_token(p); AST* right = parse_bitwise_and(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_bitwise_or(Parser* p) { AST* left = parse_bitwise_xor(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_PIPE) { advance_token(p); AST* right = parse_bitwise_xor(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_logical_and(Parser* p) { AST* left = parse_bitwise_or(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_AND) { advance_token(p); AST* right = parse_bitwise_or(p); left = create_operator_node(t.type, left, right); } else break; } return left; }
static AST* parse_logical_or(Parser* p) { AST* left = parse_logical_and(p); while (1) { Token t = peek_token(p); if (t.type == TOKEN_OR) { advance_token(p); AST* right = parse_logical_and(p); left = create_operator_node(t.type, left, right); } else break; } return left; }

static AST* parse_ternary(Parser* p)
{
    AST* cond = parse_logical_or(p);
    if (match_token(p, TOKEN_QUESTION))
    {
        AST* true_expr = parse_expression(p);
        expect(p, TOKEN_COLON);
        AST* false_expr = parse_ternary(p);
        return create_ternary_node(cond, true_expr, false_expr);
    }
    return cond;
}

static AST* parse_assignment(Parser* p)
{
    AST* left = parse_ternary(p);
    Token t = peek_token(p);
    if (t.type == TOKEN_ASSIGN || t.type == TOKEN_PLUS_ASSIGN || t.type == TOKEN_MINUS_ASSIGN ||
        t.type == TOKEN_STAR_ASSIGN || t.type == TOKEN_SLASH_ASSIGN)
    {
        advance_token(p);
        AST* right = parse_assignment(p);
        if (left->type == N_IDENT)
        {
            char* name = left->data.ident.name;
            free(left);
            return create_assign_node(name, right);
        }
        return create_operator_node(t.type, left, right);
    }
    return left;
}

AST* parse_expression(Parser* p)
{
    return parse_assignment(p);
}

/* ====================== Statements ====================== */

static AST* parse_declaration(Parser* p)
{
    char* type_str = NULL;

    // Handle struct types: struct Node* x;
    if (check_token(p, TOKEN_STRUCT))
    {
        advance_token(p); // consume 'struct'
        Token struct_name = expect(p, TOKEN_IDENTIFIER);
        // Build type string "struct Name" - SAFE allocation
        size_t len = strlen("struct ") + strlen(struct_name.word) + 1;
        type_str = (char*)malloc(len);
        strcpy(type_str, "struct ");
        strcat(type_str, struct_name.word);
    }
    else
    {
        // Regular type: int, char, void, etc.
        Token type_tok = advance_token(p);
        type_str = _strdup(type_tok.word);
    }

    int ptr_level = 0;
    while (match_token(p, TOKEN_STAR)) ptr_level++;

    Token name_tok = expect(p, TOKEN_IDENTIFIER);
    char* name_str = _strdup(name_tok.word);

    AST* array_size = NULL;
    if (match_token(p, TOKEN_LBRACKET))
    {
        if (!check_token(p, TOKEN_RBRACKET))
            array_size = parse_expression(p);
        expect(p, TOKEN_RBRACKET);
    }

    AST* init = NULL;
    if (match_token(p, TOKEN_ASSIGN))
        init = parse_expression(p);

    expect(p, TOKEN_SEMICOLON);

    return create_decl_node(type_str, name_str, ptr_level, init, array_size);
}

static AST* parse_block(Parser* p)
{
    expect(p, TOKEN_LBRACE);
    AST* block = create_block_node();

    while (!check_token(p, TOKEN_RBRACE) && peek_token(p).type != TOKEN_EOF)
        block_add_statement(block, parse_statement(p));

    expect(p, TOKEN_RBRACE);
    return block;
}

static AST* parse_if_statement(Parser* p)
{
    expect(p, TOKEN_IF);
    expect(p, TOKEN_LPAREN);
    AST* cond = parse_expression(p);
    expect(p, TOKEN_RPAREN);
    AST* then_b = parse_statement(p);
    AST* else_b = NULL;
    if (match_token(p, TOKEN_ELSE))
        else_b = parse_statement(p);
    return create_if_node(cond, then_b, else_b);
}

static AST* parse_while_statement(Parser* p)
{
    expect(p, TOKEN_WHILE);
    expect(p, TOKEN_LPAREN);
    AST* cond = parse_expression(p);
    expect(p, TOKEN_RPAREN);
    AST* body = parse_statement(p);
    return create_while_node(cond, body);
}

static AST* parse_for_statement(Parser* p)
{
    expect(p, TOKEN_FOR);
    expect(p, TOKEN_LPAREN);

    AST* init = NULL;
    if (!check_token(p, TOKEN_SEMICOLON))
    {
        if (peek_token(p).type == TOKEN_INT || peek_token(p).type == TOKEN_CHAR_KW || peek_token(p).type == TOKEN_VOID)
            init = parse_declaration(p);
        else
        {
            init = parse_expression(p);
            expect(p, TOKEN_SEMICOLON);
        }
    }
    else advance_token(p);

    AST* cond = NULL;
    if (!check_token(p, TOKEN_SEMICOLON))
        cond = parse_expression(p);
    expect(p, TOKEN_SEMICOLON);

    AST* incr = NULL;
    if (!check_token(p, TOKEN_RPAREN))
        incr = parse_expression(p);
    expect(p, TOKEN_RPAREN);

    AST* body = parse_statement(p);
    return create_for_node(init, cond, incr, body);
}

static AST* parse_return_statement(Parser* p)
{
    expect(p, TOKEN_RETURN);
    AST* value = NULL;
    if (!check_token(p, TOKEN_SEMICOLON))
        value = parse_expression(p);
    expect(p, TOKEN_SEMICOLON);
    return create_return_node(value);
}

static AST* parse_statement(Parser* p)
{
    Token t = peek_token(p);

    if (t.type == TOKEN_LBRACE) return parse_block(p);
    if (t.type == TOKEN_IF) return parse_if_statement(p);
    if (t.type == TOKEN_WHILE) return parse_while_statement(p);
    if (t.type == TOKEN_FOR) return parse_for_statement(p);
    if (t.type == TOKEN_RETURN) return parse_return_statement(p);

    //  ADD THESE:
    if (t.type == TOKEN_BREAK)
    {
        advance_token(p);
        expect(p, TOKEN_SEMICOLON);
        AST* node = (AST*)malloc(sizeof(AST));
        node->type = N_BREAK;
        return node;
    }

    if (t.type == TOKEN_CONTINUE)
    {
        advance_token(p);
        expect(p, TOKEN_SEMICOLON);
        AST* node = (AST*)malloc(sizeof(AST));
        node->type = N_CONTINUE;
        return node;
    }

    // Local declaration
    if (t.type == TOKEN_INT || t.type == TOKEN_CHAR_KW || t.type == TOKEN_VOID ||
        t.type == TOKEN_STRUCT || t.type == TOKEN_ENUM)
        return parse_declaration(p);

    // Expression statement
    AST* expr = parse_expression(p);
    expect(p, TOKEN_SEMICOLON);
    return expr;
}

/* ====================== Top-level ====================== */



static AST* parse_struct_declaration(Parser* p)
{
    expect(p, TOKEN_STRUCT);
    char* name = NULL;
    if (check_token(p, TOKEN_IDENTIFIER))
        name = _strdup(advance_token(p).word);

    // Forward declaration: struct Name;
    if (check_token(p, TOKEN_SEMICOLON))
    {
        expect(p, TOKEN_SEMICOLON);
        return create_struct_decl_node(name, NULL, 0);
    }

    // Full definition: struct Name { ... };
    expect(p, TOKEN_LBRACE);

    AST** members = NULL;
    size_t count = 0, cap = 0;

    while (!check_token(p, TOKEN_RBRACE))
    {
        if (count >= cap)
        {
            cap = cap == 0 ? 4 : cap * 2;
            members = (AST**)realloc(members, cap * sizeof(AST*));
        }
        members[count++] = parse_declaration(p);
    }

    expect(p, TOKEN_RBRACE);
    expect(p, TOKEN_SEMICOLON);

    return create_struct_decl_node(name, members, count);
}

static AST* parse_typedef(Parser* p)
{
    expect(p, TOKEN_TYPEDEF);

    // Handle: typedef struct { ... } Alias;
    if (check_token(p, TOKEN_STRUCT))
    {
        advance_token(p); // consume 'struct'

        // Check if it's anonymous: typedef struct { ... } Name;
        if (check_token(p, TOKEN_LBRACE))
        {
            // Parse the struct body
            expect(p, TOKEN_LBRACE);

            AST** members = NULL;
            size_t count = 0, cap = 0;

            while (!check_token(p, TOKEN_RBRACE))
            {
                if (count >= cap)
                {
                    cap = cap == 0 ? 4 : cap * 2;
                    members = (AST**)realloc(members, cap * sizeof(AST*));
                }
                members[count++] = parse_declaration(p);
            }

            expect(p, TOKEN_RBRACE);
            Token alias = expect(p, TOKEN_IDENTIFIER);
            expect(p, TOKEN_SEMICOLON);

            // Create a typedef that references the anonymous struct
            return create_typedef_node("struct", alias.word);
        }
        else
        {
            // typedef struct Name Alias;
            Token old_name = expect(p, TOKEN_IDENTIFIER);
            Token new_name = expect(p, TOKEN_IDENTIFIER);
            expect(p, TOKEN_SEMICOLON);

            char* old = _strdup(old_name.word);
            char* new_n = _strdup(new_name.word);
            return create_typedef_node(old, new_n);
        }
    }

    // Handle regular typedef: typedef int MyInt;
    Token type_tok = advance_token(p);
    char* type_str = _strdup(type_tok.word);

    int ptr_level = 0;
    while (match_token(p, TOKEN_STAR)) ptr_level++;

    Token alias = expect(p, TOKEN_IDENTIFIER);
    expect(p, TOKEN_SEMICOLON);

    return create_typedef_node(type_str, alias.word);
}

static AST* parse_enum_declaration(Parser* p)
{
    printf("DEBUG: Entering parse_enum_declaration at pos %zu, token type %d\n", p->pos, peek_token(p).type);
    expect(p, TOKEN_ENUM);
    char* name = NULL;
    if (check_token(p, TOKEN_IDENTIFIER))
        name = _strdup(advance_token(p).word);

    expect(p, TOKEN_LBRACE);

    AST** values = NULL;
    size_t count = 0, cap = 0;

    while (!check_token(p, TOKEN_RBRACE))
    {
        Token id = expect(p, TOKEN_IDENTIFIER);
        AST* val = NULL;  // Don't create node yet!

        if (match_token(p, TOKEN_ASSIGN))
        {
            // Create assignment node with value
            val = create_assign_node(id.word, parse_expression(p));
        }
        else
        {
            // Just an identifier
            val = create_ident_node(id.word);
        }

        if (count >= cap)
        {
            cap = cap == 0 ? 4 : cap * 2;
            values = (AST**)realloc(values, cap * sizeof(AST*));
        }
        values[count++] = val;

        // Consume comma if present, but don't require it before RBRACE
        if (!check_token(p, TOKEN_RBRACE))
        {
            expect(p, TOKEN_COMMA);
        }
        else
        {
            match_token(p, TOKEN_COMMA);  // Optional trailing comma
        }
    }

    expect(p, TOKEN_RBRACE);
    expect(p, TOKEN_SEMICOLON);

    return create_enum_decl_node(name, values, count);
}

static AST* parse_function(Parser* p)
{
    Token ret_tok = advance_token(p);
    char* ret_type = _strdup(ret_tok.word);

    int ptr_level = 0;
    while (match_token(p, TOKEN_STAR)) ptr_level++;

    Token name_tok = expect(p, TOKEN_IDENTIFIER);
    char* name = _strdup(name_tok.word);

    expect(p, TOKEN_LPAREN);

    AST** params = NULL;
    size_t pcount = 0, pcap = 0;

    if (!check_token(p, TOKEN_RPAREN))
    {
        do
        {
            Token ptok = advance_token(p);
            char* ptype = _strdup(ptok.word);

            int pptr = 0;
            while (match_token(p, TOKEN_STAR)) pptr++;

            char* pname = "";
            if (check_token(p, TOKEN_IDENTIFIER))
                pname = _strdup(advance_token(p).word);

            AST* arr_sz = NULL;
            if (match_token(p, TOKEN_LBRACKET))
            {
                if (!check_token(p, TOKEN_RBRACKET))
                    arr_sz = parse_expression(p);
                expect(p, TOKEN_RBRACKET);
            }

            if (pcount >= pcap)
            {
                pcap = pcap == 0 ? 4 : pcap * 2;
                params = (AST**)realloc(params, pcap * sizeof(AST*));
            }
            params[pcount++] = create_decl_node(ptype, pname, pptr, NULL, arr_sz);
        } while (match_token(p, TOKEN_COMMA));
    }

    expect(p, TOKEN_RPAREN);

    // Check if it's a prototype (;) or definition ({)
    if (check_token(p, TOKEN_SEMICOLON))
    {
        expect(p, TOKEN_SEMICOLON);
        // Function prototype - create function with NULL body
        return create_function_node(ret_type, name, params, pcount, NULL);
    }

    // Function definition with body
    AST* body = parse_block(p);
    return create_function_node(ret_type, name, params, pcount, body);
}

AST* parse_program(Parser* p)
{
    AST* prog = create_program_node();

    while (peek_token(p).type != TOKEN_EOF)
    {
        Token t = peek_token(p);
        printf("DEBUG parse_program: pos=%zu, token=%d, line=%d\n", p->pos, t.type, t.line);

        if (t.type == TOKEN_STRUCT)
            program_add_global(prog, parse_struct_declaration(p));
        else if (t.type == TOKEN_TYPEDEF)
            program_add_global(prog, parse_typedef(p));
        else if (t.type == TOKEN_ENUM)
            program_add_global(prog, parse_enum_declaration(p));
        else
        {
            size_t saved_pos = p->pos;
            advance_token(p);
            while (match_token(p, TOKEN_STAR));
            int is_func = check_token(p, TOKEN_IDENTIFIER) && peek_ahead(p, 1).type == TOKEN_LPAREN;
            p->pos = saved_pos;

            if (is_func)
                program_add_function(prog, parse_function(p));
            else
                program_add_global(prog, parse_declaration(p));
        }
    }

    return prog;
}

/* ====================== Memory Cleanup ====================== */

void ast_free(AST* node)
{
    if (!node) return;

    switch (node->type)
    {
    case N_STRING_LIT: free(node->data.string_lit.value); break;
    case N_IDENT: free(node->data.ident.name); break;
    case N_OPERATOR:
        ast_free(node->data.op.left);
        ast_free(node->data.op.right);
        break;
    case N_UNARY: ast_free(node->data.unary.operand); break;
    case N_ASSIGN:
        free(node->data.assign.var_name);
        ast_free(node->data.assign.value);
        break;
    case N_DECL:
        free(node->data.decl.type);
        free(node->data.decl.name);
        ast_free(node->data.decl.init_value);
        ast_free(node->data.decl.array_size);
        break;
    case N_RETURN: ast_free(node->data.return_stmt.value); break;
    case N_BLOCK:
        for (size_t i = 0; i < node->data.block.count; i++)
            ast_free(node->data.block.statements[i]);
        free(node->data.block.statements);
        break;
    case N_FUNCTION:
        free(node->data.function.return_type);
        free(node->data.function.name);
        for (size_t i = 0; i < node->data.function.param_count; i++)
            ast_free(node->data.function.params[i]);
        free(node->data.function.params);
        ast_free(node->data.function.body);
        break;
    case N_IF:
        ast_free(node->data.if_stmt.condition);
        ast_free(node->data.if_stmt.then_block);
        ast_free(node->data.if_stmt.else_block);
        break;
    case N_WHILE:
        ast_free(node->data.while_stmt.condition);
        ast_free(node->data.while_stmt.body);
        break;
    case N_FOR:
        ast_free(node->data.for_stmt.init);
        ast_free(node->data.for_stmt.condition);
        ast_free(node->data.for_stmt.increment);
        ast_free(node->data.for_stmt.body);
        break;
    case N_CALL:
        free(node->data.call.name);
        for (size_t i = 0; i < node->data.call.arg_count; i++)
            ast_free(node->data.call.args[i]);
        free(node->data.call.args);
        break;
    case N_ARRAY_ACCESS:
        ast_free(node->data.array_access.array);
        ast_free(node->data.array_access.index);
        break;
    case N_MEMBER_ACCESS:
        ast_free(node->data.member_access.object);
        free(node->data.member_access.member);
        break;
    case N_STRUCT_DECL:
        if (node->data.struct_decl.name) free(node->data.struct_decl.name);
        for (size_t i = 0; i < node->data.struct_decl.member_count; i++)
            ast_free(node->data.struct_decl.members[i]);
        free(node->data.struct_decl.members);
        break;
    case N_TYPEDEF:
        free(node->data.typedef_decl.old_name);
        free(node->data.typedef_decl.new_name);
        break;
    case N_ENUM_DECL:
        if (node->data.enum_decl.name) free(node->data.enum_decl.name);
        for (size_t i = 0; i < node->data.enum_decl.value_count; i++)
            ast_free(node->data.enum_decl.values[i]);
        free(node->data.enum_decl.values);
        break;
    case N_CAST:
        free(node->data.cast.type);
        ast_free(node->data.cast.expr);
        break;
    case N_SIZEOF: ast_free(node->data.sizeof_expr.expr); break;
    case N_TERNARY:
        ast_free(node->data.ternary.condition);
        ast_free(node->data.ternary.true_expr);
        ast_free(node->data.ternary.false_expr);
        break;
    case N_PROGRAM:
        for (size_t i = 0; i < node->data.program.func_count; i++)
            ast_free(node->data.program.functions[i]);
        free(node->data.program.functions);
        for (size_t i = 0; i < node->data.program.global_count; i++)
            ast_free(node->data.program.globals[i]);
        free(node->data.program.globals);
        break;
    default:
        break;
    }
    free(node);
}