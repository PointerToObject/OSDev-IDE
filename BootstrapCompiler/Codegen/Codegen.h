#ifndef CODEGEN_H
#define CODEGEN_H

#include "../Parser/Parser.h"

typedef enum {
    TARGET_X86_64_PE,
    TARGET_X86_64_BAREMETAL
} TargetPlatform;

typedef struct CodeGen CodeGen;

CodeGen* codegen_create(const char* output_file, TargetPlatform target);
void codegen_free(CodeGen* cg);
int codegen_new_label(CodeGen* cg);
void emit(CodeGen* cg, const char* fmt, ...);
void codegen_program(CodeGen* cg, AST* program);
void codegen_function(CodeGen* cg, AST* func);
void codegen_statement(CodeGen* cg, AST* stmt);
void codegen_expression(CodeGen* cg, AST* expr);

#endif // CODEGEN_H