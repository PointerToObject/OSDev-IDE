#include "Main.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ====================== AST Printer ====================== */

void print_indent(int indent)
{
    for (int i = 0; i < indent; ++i) printf("  ");
}

const char* node_type_name(Nodes type)
{
    switch (type) {
    case N_PROGRAM: return "PROGRAM";
    case N_FUNCTION: return "FUNCTION";
    case N_RETURN: return "RETURN";
    case N_BLOCK: return "BLOCK";
    case N_INTLIT: return "INT_LITERAL";
    case N_STRING_LIT: return "STRING_LITERAL";
    case N_CHAR_LIT: return "CHAR_LITERAL";
    case N_IDENT: return "IDENTIFIER";
    case N_OPERATOR: return "OPERATOR";
    case N_UNARY: return "UNARY";
    case N_ASSIGN: return "ASSIGN";
    case N_DECL: return "DECLARATION";
    case N_IF: return "IF";
    case N_WHILE: return "WHILE";
    case N_FOR: return "FOR";
    case N_BREAK: return "BREAK";
    case N_CONTINUE: return "CONTINUE";
    case N_CALL: return "CALL";
    case N_ARRAY_ACCESS: return "ARRAY_ACCESS";
    case N_MEMBER_ACCESS: return "MEMBER_ACCESS";
    case N_STRUCT_DECL: return "STRUCT_DECL";
    case N_TYPEDEF: return "TYPEDEF";
    case N_ENUM_DECL: return "ENUM_DECL";
    case N_CAST: return "CAST";
    case N_SIZEOF: return "SIZEOF";
    case N_TERNARY: return "TERNARY";
    default: return "UNKNOWN";
    }
}

void ast_print(AST* node, int indent)
{
    if (!node) {
        print_indent(indent);
        printf("NULL\n");
        return;
    }

    print_indent(indent);
    printf("%s", node_type_name(node->type));

    switch (node->type) {
    case N_INTLIT:
        printf(" %d", node->data.int_lit.value);
        break;
    case N_STRING_LIT:
        printf(" \"%s\"", node->data.string_lit.value);
        break;
    case N_CHAR_LIT:
        printf(" '%c'", node->data.char_lit.value);
        break;
    case N_IDENT:
        printf(" %s", node->data.ident.name);
        break;
    case N_OPERATOR:
        printf(" (op: %d)", node->data.op.op);
        printf("\n");
        ast_print(node->data.op.left, indent + 1);
        ast_print(node->data.op.right, indent + 1);
        return;
    case N_UNARY:
        printf(" (op: %d)", node->data.unary.op);
        printf("\n");
        ast_print(node->data.unary.operand, indent + 1);
        return;
    case N_ASSIGN:
        printf(" %s =", node->data.assign.var_name);
        printf("\n");
        ast_print(node->data.assign.value, indent + 1);
        return;
    case N_DECL:
        printf(" type=%s name=%s ptr_level=%d",
            node->data.decl.type, node->data.decl.name, node->data.decl.pointer_level);
        if (node->data.decl.init_value) {
            printf(" init=");
            printf("\n");
            ast_print(node->data.decl.init_value, indent + 1);
            return;
        }
        break;
    case N_RETURN:
        printf("\n");
        ast_print(node->data.return_stmt.value, indent + 1);
        return;
    case N_FUNCTION:
        printf(" %s %s", node->data.function.return_type, node->data.function.name);
        printf("\n");
        print_indent(indent + 1);
        printf("PARAMS (%zu):\n", node->data.function.param_count);
        for (size_t i = 0; i < node->data.function.param_count; i++)
            ast_print(node->data.function.params[i], indent + 2);
        print_indent(indent + 1);
        printf("BODY:\n");
        ast_print(node->data.function.body, indent + 2);
        return;
    case N_BLOCK:
        printf(" (%zu stmts)\n", node->data.block.count);
        for (size_t i = 0; i < node->data.block.count; i++)
            ast_print(node->data.block.statements[i], indent + 1);
        return;
    case N_IF:
        printf("\n");
        print_indent(indent + 1); printf("COND:\n"); ast_print(node->data.if_stmt.condition, indent + 2);
        print_indent(indent + 1); printf("THEN:\n"); ast_print(node->data.if_stmt.then_block, indent + 2);
        if (node->data.if_stmt.else_block) {
            print_indent(indent + 1); printf("ELSE:\n"); ast_print(node->data.if_stmt.else_block, indent + 2);
        }
        return;
    case N_WHILE:
        printf("\n");
        print_indent(indent + 1); printf("COND:\n"); ast_print(node->data.while_stmt.condition, indent + 2);
        print_indent(indent + 1); printf("BODY:\n"); ast_print(node->data.while_stmt.body, indent + 2);
        return;
    case N_FOR:
        printf("\n");
        print_indent(indent + 1); printf("INIT:\n"); ast_print(node->data.for_stmt.init, indent + 2);
        print_indent(indent + 1); printf("COND:\n"); ast_print(node->data.for_stmt.condition, indent + 2);
        print_indent(indent + 1); printf("INCR:\n"); ast_print(node->data.for_stmt.increment, indent + 2);
        print_indent(indent + 1); printf("BODY:\n"); ast_print(node->data.for_stmt.body, indent + 2);
        return;
    case N_CALL:
        printf(" %s(%zu args)\n", node->data.call.name, node->data.call.arg_count);
        for (size_t i = 0; i < node->data.call.arg_count; i++)
            ast_print(node->data.call.args[i], indent + 1);
        return;
    case N_STRUCT_DECL:
        printf(" %s (%zu members)", node->data.struct_decl.name ? node->data.struct_decl.name : "(anon)",
            node->data.struct_decl.member_count);
        printf("\n");
        for (size_t i = 0; i < node->data.struct_decl.member_count; i++)
            ast_print(node->data.struct_decl.members[i], indent + 1);
        return;
    case N_TYPEDEF:
        printf(" %s -> %s", node->data.typedef_decl.old_name, node->data.typedef_decl.new_name);
        break;
    case N_ENUM_DECL:
        printf(" %s (%zu values)", node->data.enum_decl.name ? node->data.enum_decl.name : "(anon)",
            node->data.enum_decl.value_count);
        printf("\n");
        for (size_t i = 0; i < node->data.enum_decl.value_count; i++)
            ast_print(node->data.enum_decl.values[i], indent + 1);
        return;
    case N_PROGRAM:
        printf(" (%zu functions, %zu globals)\n", node->data.program.func_count, node->data.program.global_count);
        for (size_t i = 0; i < node->data.program.func_count; i++)
            ast_print(node->data.program.functions[i], indent + 1);
        for (size_t i = 0; i < node->data.program.global_count; i++)
            ast_print(node->data.program.globals[i], indent + 1);
        return;
    default:
        break;
    }
    printf("\n");
}

/* ====================== Main ====================== */

int main(int argc, char** argv)
{
    if (argc != 4 || strcmp(argv[2], "-o") != 0) {
        fprintf(stderr, "Usage: %s <input.c> -o <output.asm>\n", argv[0]);
        return 1;
    }

    const char* input_file = argv[1];
    const char* output_file = argv[3];

    // Read source file
    FILE* f = fopen(input_file, "rb");
    if (!f) {
        fprintf(stderr, "Error: Cannot open input file '%s'\n", input_file);
        return 1;
    }
    fseek(f, 0, SEEK_END);
    long file_size = ftell(f);
    fseek(f, 0, SEEK_SET);

    char* src = (char*)malloc(file_size + 1);
    if (!src) {
        fclose(f);
        fprintf(stderr, "Error: Out of memory\n");
        return 1;
    }
    fread(src, 1, file_size, f);
    fclose(f);
    src[file_size] = '\0';

    printf("=== SOURCE CODE ===\n%s\n", src);

    // Tokenize
    Scanner* scanner = scanner_create(src);
    size_t capacity = 128;
    size_t token_count = 0;
    Token** token_ptrs = (Token**)malloc(capacity * sizeof(Token*));

    Token* temp;
    while (1) {
        temp = tokenize(scanner);

        if (token_count >= capacity) {
            capacity *= 2;
            token_ptrs = (Token**)realloc(token_ptrs, capacity * sizeof(Token*));
        }

        token_ptrs[token_count++] = temp;

        if (temp->type == TOKEN_EOF || temp->type == TOKEN_ERROR) {
            break;
        }
    }

    scanner_free(scanner);

    if (token_ptrs[token_count - 1]->type == TOKEN_ERROR) {
        fprintf(stderr, "Tokenization failed at line %d, column %d\n",
            token_ptrs[token_count - 1]->line,
            token_ptrs[token_count - 1]->column);
        fprintf(stderr, "Error token: '%s'\n",
            token_ptrs[token_count - 1]->word ? token_ptrs[token_count - 1]->word : "(null)");

        for (size_t i = 0; i < token_count; i++) {
            token_free(token_ptrs[i]);
        }
        free(token_ptrs);
        free(src);
        return 1;
    }

    printf("Successfully tokenized %zu tokens!\n\n", token_count);

    // Parse
    Token* tokens = (Token*)malloc(token_count * sizeof(Token));
    for (size_t i = 0; i < token_count; i++) {
        tokens[i] = *token_ptrs[i];
    }

    Parser* parser = parser_create(tokens, token_count);
    AST* program = parse_program(parser);
    parser_free(parser);

    printf("=== AST DUMP ===\n");
    ast_print(program, 0);
    printf("\n");

    // Generate code
    printf("=== CODE GENERATION ===\n");
    CodeGen* cg = codegen_create(output_file, TARGET_X86_64_PE);
    if (!cg) {
        fprintf(stderr, "Failed to create code generator\n");
        return 1;
    }

    codegen_program(cg, program);
    codegen_free(cg);

    printf("Generated assembly written to: %s\n\n", output_file);

    // Read and display the generated assembly
    printf("=== GENERATED ASSEMBLY ===\n");
    FILE* asm_file = fopen(output_file, "r");
    if (asm_file) {
        char line[512];
        while (fgets(line, sizeof(line), asm_file)) {
            printf("%s", line);
        }
        fclose(asm_file);
    }
    else {
        fprintf(stderr, "Could not open %s for reading\n", output_file);
    }

    // Cleanup
    for (size_t i = 0; i < token_count; i++) {
        token_free(token_ptrs[i]);
    }
    free(token_ptrs);
    free(tokens);
    ast_free(program);
    free(src);

    printf("\n=== COMPILATION COMPLETE ===\n");

    return 0;
}