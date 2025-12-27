#pragma once
#ifndef PARSER_H
#define PARSER_H

#include "../Tokenizer/Tokenizer.h"

typedef struct AST AST;

typedef struct
{
	Token* tokens;
	size_t pos;
	size_t len;
} Parser;

typedef enum
{
	N_PROGRAM,
	N_FUNCTION,
	N_RETURN,
	N_BLOCK,
	N_INTLIT,
	N_IDENT,
	N_OPERATOR,
	N_ASSIGN,
	N_DECL,
	N_IF,
	N_WHILE,
	N_FOR,
	N_BREAK,
	N_CONTINUE,
	N_CALL,
	N_UNARY,
	N_ARRAY_ACCESS,
	N_MEMBER_ACCESS,
	N_STRUCT_DECL,
	N_TYPEDEF,
	N_ENUM_DECL,
	N_CAST,
	N_SIZEOF,
	N_STRING_LIT,
	N_CHAR_LIT,
	N_TERNARY
} Nodes;

typedef struct
{
	char* return_type;
	char* name;
	AST** params;
	size_t param_count;
	AST* body;
} FunctionNode;

typedef struct
{
	AST* value;
} ReturnNode;

typedef struct
{
	AST** statements;
	size_t count;
	size_t capacity;
} BlockNode;

typedef struct
{
	int value;
} IntLitNode;

typedef struct
{
	char* value;
} StringLitNode;

typedef struct
{
	char value;
} CharLitNode;

typedef struct
{
	char* name;
} IdentNode;

typedef struct
{
	Tokens op;
	AST* left;
	AST* right;
} OperatorNode;

typedef struct
{
	Tokens op;
	AST* operand;
} UnaryNode;

typedef struct
{
	char* var_name;
	AST* value;
} AssignNode;

typedef struct
{
	char* type;
	char* name;
	int pointer_level;
	AST* init_value;
	AST* array_size;
} DeclNode;

typedef struct
{
	AST* condition;
	AST* then_block;
	AST* else_block;
} IfNode;

typedef struct
{
	AST* condition;
	AST* body;
} WhileNode;

typedef struct
{
	AST* init;
	AST* condition;
	AST* increment;
	AST* body;
} ForNode;

typedef struct
{
	char* name;
	AST** args;
	size_t arg_count;
} CallNode;

typedef struct
{
	AST* array;
	AST* index;
} ArrayAccessNode;

typedef struct
{
	AST* object;
	char* member;
	int is_arrow;
} MemberAccessNode;

typedef struct
{
	char* name;
	AST** members;
	size_t member_count;
} StructDeclNode;

typedef struct
{
	char* old_name;
	char* new_name;
} TypedefNode;

typedef struct
{
	char* name;
	AST** values;
	size_t value_count;
} EnumDeclNode;

typedef struct
{
	char* type;
	AST* expr;
} CastNode;

typedef struct
{
	AST* expr;
} SizeofNode;

typedef struct
{
	AST* condition;
	AST* true_expr;
	AST* false_expr;
} TernaryNode;

typedef struct
{
	AST** functions;
	AST** globals;
	size_t func_count;
	size_t func_capacity;
	size_t global_count;
	size_t global_capacity;
} ProgramNode;

typedef struct AST
{
	Nodes type;
	union
	{
		IntLitNode int_lit;
		StringLitNode string_lit;
		CharLitNode char_lit;
		IdentNode ident;
		OperatorNode op;
		UnaryNode unary;
		AssignNode assign;
		DeclNode decl;
		ReturnNode return_stmt;
		BlockNode block;
		FunctionNode function;
		ProgramNode program;
		IfNode if_stmt;
		WhileNode while_stmt;
		ForNode for_stmt;
		CallNode call;
		ArrayAccessNode array_access;
		MemberAccessNode member_access;
		StructDeclNode struct_decl;
		TypedefNode typedef_decl;
		EnumDeclNode enum_decl;
		CastNode cast;
		SizeofNode sizeof_expr;
		TernaryNode ternary;
	} data;
} AST;

Parser* parser_create(Token* tokens, size_t len);
void parser_free(Parser* p);
Token peek_token(Parser* p);
Token peek_ahead(Parser* p, int offset);
Token advance_token(Parser* p);
int check_token(Parser* p, Tokens type);
int match_token(Parser* p, Tokens type);
Token expect(Parser* p, Tokens expected);

AST* create_intlit_node(int value);
AST* create_stringlit_node(char* value);
AST* create_charlit_node(char value);
AST* create_ident_node(char* name);
AST* create_operator_node(Tokens op, AST* left, AST* right);
AST* create_unary_node(Tokens op, AST* operand);
AST* create_return_node(AST* value);
AST* create_assign_node(char* name, AST* value);
AST* create_decl_node(char* type, char* name, int pointer_level, AST* init, AST* array_size);
AST* create_block_node();
AST* create_function_node(char* return_type, char* name, AST** params, size_t param_count, AST* body);
AST* create_if_node(AST* condition, AST* then_block, AST* else_block);
AST* create_while_node(AST* condition, AST* body);
AST* create_for_node(AST* init, AST* condition, AST* increment, AST* body);
AST* create_call_node(char* name, AST** args, size_t arg_count);
AST* create_array_access_node(AST* array, AST* index);
AST* create_member_access_node(AST* object, char* member, int is_arrow);
AST* create_struct_decl_node(char* name, AST** members, size_t member_count);
AST* create_typedef_node(char* old_name, char* new_name);
AST* create_enum_decl_node(char* name, AST** values, size_t value_count);
AST* create_cast_node(char* type, AST* expr);
AST* create_sizeof_node(AST* expr);
AST* create_ternary_node(AST* condition, AST* true_expr, AST* false_expr);
AST* create_program_node();

void block_add_statement(AST* block, AST* stmt);
void program_add_function(AST* program, AST* func);
void program_add_global(AST* program, AST* global);

AST* parse_primary(Parser* p);
AST* parse_postfix(Parser* p);
AST* parse_unary(Parser* p);
AST* parse_multiplicative(Parser* p);
AST* parse_additive(Parser* p);
AST* parse_shift(Parser* p);
AST* parse_relational(Parser* p);
AST* parse_equality(Parser* p);
AST* parse_bitwise_and(Parser* p);
AST* parse_bitwise_xor(Parser* p);
AST* parse_bitwise_or(Parser* p);
AST* parse_logical_and(Parser* p);
AST* parse_logical_or(Parser* p);
AST* parse_ternary(Parser* p);
AST* parse_assignment(Parser* p);
AST* parse_expression(Parser* p);
AST* parse_declaration(Parser* p);
AST* parse_statement(Parser* p);
AST* parse_block(Parser* p);
AST* parse_if_statement(Parser* p);
AST* parse_while_statement(Parser* p);
AST* parse_for_statement(Parser* p);
AST* parse_struct_declaration(Parser* p);
AST* parse_typedef(Parser* p);
AST* parse_enum_declaration(Parser* p);
AST* parse_function(Parser* p);
AST* parse_program(Parser* p);

void ast_free(AST* node);

#endif // !PARSER_H