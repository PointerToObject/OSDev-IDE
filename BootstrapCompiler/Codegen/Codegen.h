#ifndef CODEGEN_H
#define CODEGEN_H

#include "../Parser/Parser.h"
#include <stdarg.h>

typedef enum {
    TARGET_X86_64_PE,
    TARGET_X86_64_BAREMETAL
} TargetPlatform;

// Struct member information
typedef struct {
    char* name;
    int offset;
    int size;
} StructMember;

// Struct type information
typedef struct {
    char* name;
    StructMember* members;
    int member_count;
    int total_size;
} StructInfo;

typedef struct CodeGen CodeGen;

// Core CodeGen functions
CodeGen* codegen_create(const char* output_file, TargetPlatform target);
void codegen_free(CodeGen* cg);
int codegen_new_label(CodeGen* cg);
void emit(CodeGen* cg, const char* fmt, ...);

// Code generation entry points
void codegen_program(CodeGen* cg, AST* program);
void codegen_function(CodeGen* cg, AST* func);
void codegen_function_correct(CodeGen* cg, AST* func);
void codegen_statement(CodeGen* cg, AST* stmt);
void codegen_expression(CodeGen* cg, AST* expr);

// Struct management functions
void codegen_init_struct_table(CodeGen* cg);
void codegen_register_struct(CodeGen* cg, AST* struct_decl);
void codegen_free_struct_table(CodeGen* cg);
StructInfo* codegen_find_struct(CodeGen* cg, const char* name);
int codegen_get_member_offset(CodeGen* cg, const char* struct_name, const char* member_name);

// String literal management
int codegen_add_string(CodeGen* cg, const char* value);
void codegen_emit_strings(CodeGen* cg);

#endif // CODEGEN_H
