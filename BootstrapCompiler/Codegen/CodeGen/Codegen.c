#include "../Codegen.h"

/* ====================== Symbol Table ====================== */

#define MAX_LOCALS 256

typedef struct {
    char* name;
    int offset;  // Offset from ebp (positive for params, negative for locals)
    int size;    // Size in bytes
    int is_param; // 1 if parameter, 0 if local
} Local;

typedef struct {
    Local locals[MAX_LOCALS];
    int count;
    int stack_offset;
} SymbolTable;

void symtab_init(SymbolTable* st) {
    st->count = 0;
    st->stack_offset = 0;
}

int symtab_add(SymbolTable* st, const char* name) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }
    st->stack_offset += 4;  // Each var takes 4 bytes (32-bit)
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = st->stack_offset;
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 0;
    st->count++;
    return st->stack_offset;
}

int symtab_add_array(SymbolTable* st, const char* name, int array_size) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }
    int bytes = array_size > 0 ? array_size : 4;
    st->stack_offset += bytes;
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = st->stack_offset;
    st->locals[st->count].size = bytes;
    st->locals[st->count].is_param = 0;
    st->count++;
    return st->stack_offset;
}

void symtab_add_param(SymbolTable* st, const char* name, int stack_pos) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many parameters\n");
        exit(1);
    }
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = stack_pos;  // Positive offset from ebp
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 1;
    st->count++;
}

Local* symtab_lookup_entry(SymbolTable* st, const char* name) {
    for (int i = 0; i < st->count; i++) {
        if (strcmp(st->locals[i].name, name) == 0) {
            return &st->locals[i];
        }
    }
    return NULL;
}

int symtab_lookup(SymbolTable* st, const char* name) {
    Local* entry = symtab_lookup_entry(st, name);
    if (entry) return entry->offset;
    return -1;  // Not found
}

void symtab_free(SymbolTable* st) {
    for (int i = 0; i < st->count; i++) {
        free(st->locals[i].name);
    }
}

/* ====================== String Literal Tracking ====================== */

typedef struct {
    int id;
    char* value;
} StringLiteral;

/* ====================== CodeGen Setup ====================== */

struct CodeGen {
    FILE* output;
    TargetPlatform target;
    int label_count;
    int string_count;
    SymbolTable symtab;

    // String literal tracking
    StringLiteral* strings;
    int string_capacity;
    int string_list_count;
};

CodeGen* codegen_create(const char* output_file, TargetPlatform target)
{
    CodeGen* cg = (CodeGen*)malloc(sizeof(CodeGen));
    cg->output = fopen(output_file, "w");
    if (!cg->output) {
        fprintf(stderr, "Failed to open output file: %s\n", output_file);
        free(cg);
        return NULL;
    }
    cg->target = target;
    cg->label_count = 0;
    cg->string_count = 0;
    symtab_init(&cg->symtab);

    // Initialize string tracking
    cg->string_capacity = 32;
    cg->strings = (StringLiteral*)malloc(sizeof(StringLiteral) * cg->string_capacity);
    cg->string_list_count = 0;

    return cg;
}

void codegen_free(CodeGen* cg)
{
    if (cg->output) fclose(cg->output);
    symtab_free(&cg->symtab);

    // Free strings
    for (int i = 0; i < cg->string_list_count; i++) {
        free(cg->strings[i].value);
    }
    free(cg->strings);

    free(cg);
}

int codegen_new_label(CodeGen* cg)
{
    return cg->label_count++;
}

void emit(CodeGen* cg, const char* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    vfprintf(cg->output, fmt, args);
    va_end(args);
    fprintf(cg->output, "\n");
}

// Helper to add a string literal and return its ID
int codegen_add_string(CodeGen* cg, const char* value)
{
    if (cg->string_list_count >= cg->string_capacity) {
        cg->string_capacity *= 2;
        cg->strings = (StringLiteral*)realloc(cg->strings,
            sizeof(StringLiteral) * cg->string_capacity);
    }

    int id = cg->string_count++;
    cg->strings[cg->string_list_count].id = id;
    cg->strings[cg->string_list_count].value = _strdup(value);
    cg->string_list_count++;

    return id;
}

// Emit all collected strings
void codegen_emit_strings(CodeGen* cg)
{
    if (cg->string_list_count > 0) {
        for (int i = 0; i < cg->string_list_count; i++) {
            emit(cg, "str%d db `%s`,0",
                cg->strings[i].id,
                cg->strings[i].value);
        }
    }
}

/* ====================== Bare Metal x86-32 Prologue (minimal) ====================== */

void codegen_baremetal_prologue(CodeGen* cg)
{
    emit(cg, "[BITS 32]");
    emit(cg, "");
    emit(cg, "[org 0x1000]");
    emit(cg, "");
    emit(cg, "section .text");
    emit(cg, "global kernel_main");
    emit(cg, "");
    emit(cg, "    jmp kernel_main");
    emit(cg, "");
    // Runtime is now emitted later — nothing else here
}

/* ====================== Runtime Emission (moved from prologue) ====================== */

static void codegen_emit_runtime(CodeGen* cg)
{
    // Built-in print runtime — emitted AFTER user functions
    emit(cg, "print_string:");
    emit(cg, "    pusha");
    emit(cg, "    mov esi, [esp+32]");
    emit(cg, "    mov edi, 0xB8000");
    emit(cg, "    mov ax,0x10");
    emit(cg, ".print_loop:");
    emit(cg, "    lodsb");
    emit(cg, "    test al,al");
    emit(cg, "    jz .done");
    emit(cg, "    mov ah,0x0F");
    emit(cg, "    stosw");
    emit(cg, "    jmp .print_loop");
    emit(cg, ".done:");
    emit(cg, "    popa");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_fmt:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push ebx");
    emit(cg, "    push esi");
    emit(cg, "    push edi");
    emit(cg, "    mov esi, [ebp+8]");
    emit(cg, "    mov edi, 0xB8000");
    emit(cg, "    lea ebx, [ebp+12]");
    emit(cg, ".loop:");
    emit(cg, "    lodsb");
    emit(cg, "    test al, al");
    emit(cg, "    jz .done");
    emit(cg, "    cmp al, 10");
    emit(cg, "    je .loop");
    emit(cg, "    cmp al, '%%'");
    emit(cg, "    jne .print");
    emit(cg, "    lodsb");
    emit(cg, "    cmp al, 'c'");
    emit(cg, "    je .char");
    emit(cg, "    cmp al, 'd'");
    emit(cg, "    je .decimal");
    emit(cg, "    cmp al, 'x'");
    emit(cg, "    je .hex");
    emit(cg, "    cmp al, 'f'");
    emit(cg, "    je .float");
    emit(cg, "    mov al, '?'");
    emit(cg, ".print:");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    jmp .loop");
    emit(cg, ".char:");
    emit(cg, "    mov al, [ebx]");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    add ebx, 4");
    emit(cg, "    jmp .loop");
    emit(cg, ".decimal:");
    emit(cg, "    mov eax, [ebx]");
    emit(cg, "    call print_int");
    emit(cg, "    add ebx, 4");
    emit(cg, "    jmp .loop");
    emit(cg, ".hex:");
    emit(cg, "    mov eax, [ebx]");
    emit(cg, "    call print_hex8");
    emit(cg, "    add ebx, 4");
    emit(cg, "    jmp .loop");
    emit(cg, ".float:");
    emit(cg, "    fld dword [ebx]");
    emit(cg, "    call print_float");
    emit(cg, "    add ebx, 4");
    emit(cg, "    jmp .loop");
    emit(cg, ".done:");
    emit(cg, "    pop edi");
    emit(cg, "    pop esi");
    emit(cg, "    pop ebx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_int:");
    emit(cg, "    push ebx");
    emit(cg, "    push ecx");
    emit(cg, "    push edx");
    emit(cg, "    mov ebx, 10");
    emit(cg, "    xor ecx, ecx");
    emit(cg, "    test eax, eax");
    emit(cg, "    jnz .div");
    emit(cg, "    push eax");
    emit(cg, "    inc ecx");
    emit(cg, "    jmp .print");
    emit(cg, ".div:");
    emit(cg, "    xor edx, edx");
    emit(cg, "    div ebx");
    emit(cg, "    push edx");
    emit(cg, "    inc ecx");
    emit(cg, "    test eax, eax");
    emit(cg, "    jnz .div");
    emit(cg, ".print:");
    emit(cg, "    pop eax");
    emit(cg, "    add al, '0'");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    loop .print");
    emit(cg, "    pop edx");
    emit(cg, "    pop ecx");
    emit(cg, "    pop ebx");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_hex8:");
    emit(cg, "    push ebx");
    emit(cg, "    push ecx");
    emit(cg, "    push edx");
    emit(cg, "    mov ecx, 8");
    emit(cg, ".hloop:");
    emit(cg, "    rol eax, 4");
    emit(cg, "    mov ebx, eax");
    emit(cg, "    and ebx, 0xF");
    emit(cg, "    mov dl, [hex_chars + ebx]");
    emit(cg, "    push eax");
    emit(cg, "    mov al, dl");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    pop eax");
    emit(cg, "    loop .hloop");
    emit(cg, "    pop edx");
    emit(cg, "    pop ecx");
    emit(cg, "    pop ebx");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_float:");
    emit(cg, "    push ebx");
    emit(cg, "    push ecx");
    emit(cg, "    push edx");
    emit(cg, "    push esi");
    emit(cg, "    sub esp, 12");
    emit(cg, "    fst dword [esp]");
    emit(cg, "    mov eax, [esp]");
    emit(cg, "    test eax, 0x80000000");
    emit(cg, "    jz .pos");
    emit(cg, "    mov al, '-'");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    fchs");
    emit(cg, ".pos:");
    emit(cg, "    fld st0");
    emit(cg, "    mov dword [esp+8], 0x3F000000");
    emit(cg, "    fld dword [esp+8]");
    emit(cg, "    fsubp st1, st0");
    emit(cg, "    fistp dword [esp]");
    emit(cg, "    mov eax, [esp]");
    emit(cg, "    fild dword [esp]");
    emit(cg, "    fsubp st1, st0");
    emit(cg, "    mov eax, [esp]");
    emit(cg, "    call print_int");
    emit(cg, "    mov al, '.'");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    mov dword [esp+4], 1000000");
    emit(cg, "    fild dword [esp+4]");
    emit(cg, "    fmulp st1, st0");
    emit(cg, "    fistp dword [esp]");
    emit(cg, "    mov eax, [esp]");
    emit(cg, "    test eax, eax");
    emit(cg, "    jns .got_frac");
    emit(cg, "    neg eax");
    emit(cg, ".got_frac:");
    emit(cg, "    mov esi, 100000");
    emit(cg, ".frac_loop:");
    emit(cg, "    xor edx, edx");
    emit(cg, "    div esi");
    emit(cg, "    add al, '0'");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    stosw");
    emit(cg, "    mov eax, edx");
    emit(cg, "    mov edx, esi");
    emit(cg, "    mov esi, 10");
    emit(cg, "    push eax");
    emit(cg, "    mov eax, edx");
    emit(cg, "    xor edx, edx");
    emit(cg, "    div esi");
    emit(cg, "    mov esi, eax");
    emit(cg, "    pop eax");
    emit(cg, "    test esi, esi");
    emit(cg, "    jnz .frac_loop");
    emit(cg, "    add esp, 12");
    emit(cg, "    pop esi");
    emit(cg, "    pop edx");
    emit(cg, "    pop ecx");
    emit(cg, "    pop ebx");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "hex_chars db '0123456789ABCDEF'");
    emit(cg, "");
}

/* ====================== Expression Code Generation ====================== */

void codegen_expression(CodeGen* cg, AST* expr)
{
    if (!expr) {
        emit(cg, "    xor eax, eax     ; NULL expression");
        return;
    }

    switch (expr->type) {
    case N_INTLIT:
        emit(cg, "    mov eax, %d", expr->data.int_lit.value);
        break;

    case N_STRING_LIT:
    {
        int str_id = codegen_add_string(cg, expr->data.string_lit.value);
        emit(cg, "    mov eax, str%d", str_id);
        break;
    }

    case N_CHAR_LIT:
        emit(cg, "    mov eax, %d  ; '%c'",
            expr->data.char_lit.value, expr->data.char_lit.value);
        break;

    case N_IDENT:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.ident.name);
        if (entry) {
            if (entry->is_param) {
                emit(cg, "    mov eax, [ebp + %d]  ; Load param %s",
                    entry->offset, expr->data.ident.name);
            }
            else {
                emit(cg, "    mov eax, [ebp - %d]  ; Load local %s",
                    entry->offset, expr->data.ident.name);
            }
        }
        else {
            // Global variable
            emit(cg, "    mov eax, [%s]  ; Load global %s",
                expr->data.ident.name, expr->data.ident.name);
        }
        break;
    }

    case N_OPERATOR:
    {
        // Special case for array assignment: arr[i] = value
        if (expr->data.op.op == TOKEN_ASSIGN && expr->data.op.left->type == N_ARRAY_ACCESS) {
            AST* arr_access = expr->data.op.left;

            // Evaluate array base
            codegen_expression(cg, arr_access->data.array_access.array);
            emit(cg, "    mov edi, eax     ; Array base in edi");

            // Evaluate index
            codegen_expression(cg, arr_access->data.array_access.index);
            emit(cg, "    add edi, eax     ; Add index to base");

            // Evaluate value to store
            codegen_expression(cg, expr->data.op.right);
            emit(cg, "    mov [edi], al    ; Store byte");
            break;
        }

        // Generate left operand (result in eax)
        codegen_expression(cg, expr->data.op.left);
        emit(cg, "    push eax         ; Save left operand");

        // Generate right operand (result in eax)
        codegen_expression(cg, expr->data.op.right);
        emit(cg, "    mov ebx, eax     ; Right operand in ebx");
        emit(cg, "    pop eax          ; Restore left operand");

        // Perform operation
        switch (expr->data.op.op) {
        case TOKEN_PLUS:
            emit(cg, "    add eax, ebx");
            break;
        case TOKEN_MINUS:
            emit(cg, "    sub eax, ebx");
            break;
        case TOKEN_STAR:
            emit(cg, "    imul eax, ebx");
            break;
        case TOKEN_SLASH:
            emit(cg, "    cdq              ; Sign-extend eax to edx:eax");
            emit(cg, "    idiv ebx         ; eax = eax / ebx");
            break;
        case TOKEN_PERCENT:
            emit(cg, "    cdq");
            emit(cg, "    idiv ebx");
            emit(cg, "    mov eax, edx  ; Remainder in edx");
            break;
        case TOKEN_LSHIFT:
            emit(cg, "    mov ecx, ebx");
            emit(cg, "    shl eax, cl");
            break;
        case TOKEN_RSHIFT:
            emit(cg, "    mov ecx, ebx");
            emit(cg, "    sar eax, cl");
            break;
        case TOKEN_AMPERSAND:
            emit(cg, "    and eax, ebx");
            break;
        case TOKEN_PIPE:
            emit(cg, "    or eax, ebx");
            break;
        case TOKEN_CARET:
            emit(cg, "    xor eax, ebx");
            break;
        case TOKEN_EQUAL:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    sete al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_NOT_EQUAL:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    setne al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_LESS:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    setl al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_GREATER:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    setg al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_LESS_EQUAL:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    setle al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_GREATER_EQUAL:
            emit(cg, "    cmp eax, ebx");
            emit(cg, "    setge al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_AND:
            emit(cg, "    test eax, eax");
            emit(cg, "    setne al");
            emit(cg, "    test ebx, ebx");
            emit(cg, "    setne bl");
            emit(cg, "    and al, bl");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_OR:
            emit(cg, "    test eax, eax");
            emit(cg, "    setne al");
            emit(cg, "    test ebx, ebx");
            emit(cg, "    setne bl");
            emit(cg, "    or al, bl");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_ASSIGN:
            // Regular assignment (not array)
            emit(cg, "    mov [eax], ebx");
            emit(cg, "    mov eax, ebx");
            break;
        default:
            emit(cg, "    ; TODO: Operator %d", expr->data.op.op);
            break;
        }
        break;
    }

    case N_CALL:
    {
        emit(cg, "    ; Call function: %s", expr->data.call.name);

        // Push arguments in reverse order (right to left)
        for (int i = expr->data.call.arg_count - 1; i >= 0; i--) {
            codegen_expression(cg, expr->data.call.args[i]);
            emit(cg, "    push eax         ; Push arg %d", i);
        }

        emit(cg, "    call %s", expr->data.call.name);

        // Clean up stack
        if (expr->data.call.arg_count > 0) {
            emit(cg, "    add esp, %d      ; Clean %d args",
                expr->data.call.arg_count * 4, expr->data.call.arg_count);
        }
        break;
    }

    case N_ASSIGN:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.assign.var_name);
        if (entry) {
            codegen_expression(cg, expr->data.assign.value);
            if (entry->is_param) {
                emit(cg, "    mov [ebp + %d], eax  ; Store to param %s",
                    entry->offset, expr->data.assign.var_name);
            }
            else {
                emit(cg, "    mov [ebp - %d], eax  ; Store to local %s",
                    entry->offset, expr->data.assign.var_name);
            }
        }
        else {
            // Global variable
            codegen_expression(cg, expr->data.assign.value);
            emit(cg, "    mov [%s], eax  ; Store to global %s",
                expr->data.assign.var_name, expr->data.assign.var_name);
        }
        break;
    }

    case N_ARRAY_ACCESS:
    {
        // Check if array is a local variable
        Local* entry = symtab_lookup_entry(&cg->symtab,
            expr->data.array_access.array->data.ident.name);

        if (entry && !entry->is_param) {
            // Local array - compute address
            emit(cg, "    lea eax, [ebp - %d]  ; Load array base address",
                entry->offset);
            emit(cg, "    push eax  ; Save array base");
            codegen_expression(cg, expr->data.array_access.index);
            emit(cg, "    pop ebx   ; Restore array base");
            emit(cg, "    add eax, ebx  ; Compute element address");
            emit(cg, "    movzx eax, byte [eax]  ; Load byte");
        }
        else {
            // Pointer or global array - dereference
            codegen_expression(cg, expr->data.array_access.array);
            emit(cg, "    push eax  ; Save array ptr");
            codegen_expression(cg, expr->data.array_access.index);
            emit(cg, "    pop ebx   ; Restore array ptr");
            emit(cg, "    add eax, ebx  ; Compute address");
            emit(cg, "    movzx eax, byte [eax]  ; Load byte");
        }
        break;
    }

    case N_CAST:
    {
        // Check if it's a constant integer cast like (char*)0xB8000
        if (expr->data.cast.expr->type == N_INTLIT) {
            emit(cg, "    mov eax, 0x%X  ; Cast constant",
                expr->data.cast.expr->data.int_lit.value);
        }
        else {
            codegen_expression(cg, expr->data.cast.expr);
        }
        break;
    }

    case N_UNARY:
    {
        codegen_expression(cg, expr->data.unary.operand);
        switch (expr->data.unary.op) {
        case TOKEN_MINUS:
            emit(cg, "    neg eax");
            break;
        case TOKEN_TILDE:
            emit(cg, "    not eax");
            break;
        case TOKEN_EXCLAIM:
            emit(cg, "    test eax, eax");
            emit(cg, "    setz al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_STAR:  // Dereference
            emit(cg, "    mov eax, [eax]");
            break;
        case TOKEN_AMPERSAND:  // Address-of
        {
            AST* target = expr->data.unary.operand;
            if (target->type == N_IDENT) {
                Local* entry = symtab_lookup_entry(&cg->symtab, target->data.ident.name);
                if (entry) {
                    if (entry->is_param) {
                        emit(cg, "    lea eax, [ebp + %d]  ; Address of param %s",
                            entry->offset, target->data.ident.name);
                    }
                    else {
                        emit(cg, "    lea eax, [ebp - %d]  ; Address of local %s",
                            entry->offset, target->data.ident.name);
                    }
                }
                else {
                    // Global variable
                    emit(cg, "    mov eax, %s  ; Address of global %s",
                        target->data.ident.name, target->data.ident.name);
                }
            }
            else {
                emit(cg, "    ; TODO: Address-of complex expression");
                codegen_expression(cg, target);
            }
            break;
        }

        default:
            emit(cg, "    ; TODO: Unary operator %d", expr->data.unary.op);
            break;
        }
        break;
    }

    default:
        emit(cg, "    ; TODO: Expression type %d", expr->type);
        emit(cg, "    xor eax, eax");
        break;
    }
}

/* ====================== Statement Code Generation ====================== */

void codegen_statement(CodeGen* cg, AST* stmt)
{
    if (!stmt) return;

    switch (stmt->type) {
    case N_DECL:
    {
        emit(cg, "    ; Declare variable: %s", stmt->data.decl.name);
        int offset;

        if (stmt->data.decl.array_size && stmt->data.decl.array_size->type == N_INTLIT) {
            int arr_size = stmt->data.decl.array_size->data.int_lit.value;
            offset = symtab_add_array(&cg->symtab, stmt->data.decl.name, arr_size);
            emit(cg, "    ; Array %s[%d] at [ebp - %d]",
                stmt->data.decl.name, arr_size, offset);
        }
        else {
            offset = symtab_add(&cg->symtab, stmt->data.decl.name);
        }

        if (stmt->data.decl.init_value) {
            codegen_expression(cg, stmt->data.decl.init_value);
            emit(cg, "    mov [ebp - %d], eax  ; Initialize %s",
                offset, stmt->data.decl.name);
        }
        break;
    }

    case N_RETURN:
        emit(cg, "    ; Return statement");
        if (stmt->data.return_stmt.value) {
            codegen_expression(cg, stmt->data.return_stmt.value);
        }
        else {
            emit(cg, "    xor eax, eax");
        }
        emit(cg, "    jmp .epilogue");
        break;

    case N_BLOCK:
        for (size_t i = 0; i < stmt->data.block.count; i++) {
            codegen_statement(cg, stmt->data.block.statements[i]);
        }
        break;

    case N_IF:
    {
        int label_else = codegen_new_label(cg);
        int label_end = codegen_new_label(cg);

        emit(cg, "    ; If statement");
        codegen_expression(cg, stmt->data.if_stmt.condition);
        emit(cg, "    test eax, eax");
        emit(cg, "    jz .L%d", stmt->data.if_stmt.else_block ? label_else : label_end);

        codegen_statement(cg, stmt->data.if_stmt.then_block);

        if (stmt->data.if_stmt.else_block) {
            emit(cg, "    jmp .L%d", label_end);
            emit(cg, ".L%d:", label_else);
            codegen_statement(cg, stmt->data.if_stmt.else_block);
        }

        emit(cg, ".L%d:", label_end);
        break;
    }

    case N_WHILE:
    {
        int label_start = codegen_new_label(cg);
        int label_end = codegen_new_label(cg);

        emit(cg, ".L%d:  ; While loop start", label_start);
        codegen_expression(cg, stmt->data.while_stmt.condition);
        emit(cg, "    test eax, eax");
        emit(cg, "    jz .L%d", label_end);

        codegen_statement(cg, stmt->data.while_stmt.body);

        emit(cg, "    jmp .L%d", label_start);
        emit(cg, ".L%d:  ; While loop end", label_end);
        break;
    }

    case N_FOR:
    {
        int label_start = codegen_new_label(cg);
        int label_end = codegen_new_label(cg);

        emit(cg, "    ; For loop");
        if (stmt->data.for_stmt.init) {
            codegen_statement(cg, stmt->data.for_stmt.init);
        }

        emit(cg, ".L%d:  ; For loop start", label_start);
        if (stmt->data.for_stmt.condition) {
            codegen_expression(cg, stmt->data.for_stmt.condition);
            emit(cg, "    test eax, eax");
            emit(cg, "    jz .L%d", label_end);
        }

        codegen_statement(cg, stmt->data.for_stmt.body);

        if (stmt->data.for_stmt.increment) {
            codegen_expression(cg, stmt->data.for_stmt.increment);
        }

        emit(cg, "    jmp .L%d", label_start);
        emit(cg, ".L%d:  ; For loop end", label_end);
        break;
    }

    case N_ASSIGN:
        codegen_expression(cg, stmt);
        break;

    case N_BREAK:
        emit(cg, "    ; TODO: Break statement");
        break;

    case N_CONTINUE:
        emit(cg, "    ; TODO: Continue statement");
        break;

    default:
        // Try to evaluate as expression statement
        codegen_expression(cg, stmt);
        break;
    }
}

/* ====================== Function Code Generation ====================== */

void codegen_function_correct(CodeGen* cg, AST* func)
{
    if (!func || func->type != N_FUNCTION) return;

    symtab_free(&cg->symtab);
    symtab_init(&cg->symtab);

    emit(cg, "");
    emit(cg, "; Function: %s", func->data.function.name);
    emit(cg, "%s:", func->data.function.name);
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");

    // Add parameters to symbol table
    for (size_t i = 0; i < func->data.function.param_count; i++) {
        AST* param = func->data.function.params[i];
        if (param->type == N_DECL) {
            symtab_add_param(&cg->symtab, param->data.decl.name, 8 + (i * 4));
        }
    }

    // Reserve generous stack space (will optimize later)
    emit(cg, "    sub esp, 512     ; Reserve stack space");

    // Generate function body
    if (func->data.function.body) {
        codegen_statement(cg, func->data.function.body);
    }

    emit(cg, ".epilogue:");
    emit(cg, "    mov esp, ebp");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
}

/* ====================== Program Code Generation ====================== */

void codegen_program(CodeGen* cg, AST* program)
{
    if (!program || program->type != N_PROGRAM) return;

    // Emit minimal header only
    codegen_baremetal_prologue(cg);

    // Emit all user functions (kernel_main will be first due to your current ordering)
    for (size_t i = 0; i < program->data.program.func_count; i++) {
        codegen_function_correct(cg, program->data.program.functions[i]);
    }

    // Emit runtime AFTER all user code
    codegen_emit_runtime(cg);

    // Data section
    emit(cg, "");
    emit(cg, "section .data");
    codegen_emit_strings(cg);

    // Emit globals
    for (size_t i = 0; i < program->data.program.global_count; i++) {
        AST* global = program->data.program.globals[i];
        if (global->type == N_DECL) {
            int init_val = 0;
            if (global->data.decl.init_value &&
                global->data.decl.init_value->type == N_INTLIT) {
                init_val = global->data.decl.init_value->data.int_lit.value;
            }
            emit(cg, "%s dd %d", global->data.decl.name, init_val);
        }
    }

    emit(cg, "vga_cursor dd 0");
    emit(cg, "");
    emit(cg, "; Variables (initialized to zero)");
    emit(cg, "");
    emit(cg, "; End of kernel");
}