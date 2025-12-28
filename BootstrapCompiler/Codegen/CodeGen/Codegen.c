#include "../Codegen.h"

/* ====================== Symbol Table ====================== */

#define MAX_LOCALS 256
#define MAX_GLOBALS 256

typedef struct {
    char* name;
    int offset;
    int size;
    int is_param;
    char* type_name;
    int pointer_level;
    int element_size;
} Local;

typedef struct {
    Local locals[MAX_LOCALS];
    int count;
    int stack_offset;
} SymbolTable;

typedef struct {
    char* name;
    char* type_name;
    int pointer_level;
    int element_size;
    int is_array;
    int array_size;
} GlobalVar;

typedef struct {
    GlobalVar globals[MAX_GLOBALS];
    int count;
} GlobalTable;

/* ====================== Type Size Helpers ====================== */

static int get_base_type_size(const char* type_name) {
    if (!type_name) return 4;
    const char* base = type_name;
    if (strncmp(type_name, "unsigned ", 9) == 0) base = type_name + 9;
    else if (strncmp(type_name, "signed ", 7) == 0) base = type_name + 7;

    if (strcmp(base, "char") == 0) return 1;
    if (strcmp(base, "short") == 0) return 2;
    if (strcmp(base, "int") == 0) return 4;
    if (strcmp(base, "long") == 0) return 4;
    return 4;
}

static int calc_element_size(const char* type_name, int pointer_level) {
    if (pointer_level > 1) return 4;
    if (pointer_level == 1) return get_base_type_size(type_name);
    return get_base_type_size(type_name);
}

/* ====================== Symbol Table Functions ====================== */

void symtab_init(SymbolTable* st) {
    st->count = 0;
    st->stack_offset = 0;
}

int symtab_add(SymbolTable* st, const char* name) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }
    st->stack_offset += 4;
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = st->stack_offset;
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 0;
    st->locals[st->count].type_name = _strdup("int");
    st->locals[st->count].pointer_level = 0;
    st->locals[st->count].element_size = 4;
    st->count++;
    return st->stack_offset;
}

int symtab_add_typed(SymbolTable* st, const char* name, const char* type_name, int pointer_level) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }
    st->stack_offset += 4;
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = st->stack_offset;
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 0;
    st->locals[st->count].type_name = _strdup(type_name);
    st->locals[st->count].pointer_level = pointer_level;
    st->locals[st->count].element_size = calc_element_size(type_name, pointer_level);
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
    st->locals[st->count].type_name = _strdup("char");
    st->locals[st->count].pointer_level = 0;
    st->locals[st->count].element_size = 1;
    st->count++;
    return st->stack_offset;
}

int symtab_add_array_typed(SymbolTable* st, const char* name, int array_size, const char* type_name, int pointer_level) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }
    int elem_size = get_base_type_size(type_name);
    int bytes = array_size > 0 ? array_size * elem_size : 4;
    st->stack_offset += bytes;
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = st->stack_offset;
    st->locals[st->count].size = bytes;
    st->locals[st->count].is_param = 0;
    st->locals[st->count].type_name = _strdup(type_name);
    st->locals[st->count].pointer_level = pointer_level;
    st->locals[st->count].element_size = elem_size;
    st->count++;
    return st->stack_offset;
}

void symtab_add_param(SymbolTable* st, const char* name, int stack_pos) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many parameters\n");
        exit(1);
    }
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = stack_pos;
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 1;
    st->locals[st->count].type_name = _strdup("int");
    st->locals[st->count].pointer_level = 0;
    st->locals[st->count].element_size = 4;
    st->count++;
}

void symtab_add_param_typed(SymbolTable* st, const char* name, int stack_pos, const char* type_name, int pointer_level) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many parameters\n");
        exit(1);
    }
    st->locals[st->count].name = _strdup(name);
    st->locals[st->count].offset = stack_pos;
    st->locals[st->count].size = 4;
    st->locals[st->count].is_param = 1;
    st->locals[st->count].type_name = _strdup(type_name);
    st->locals[st->count].pointer_level = pointer_level;
    st->locals[st->count].element_size = calc_element_size(type_name, pointer_level);
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
    return -1;
}

void symtab_free(SymbolTable* st) {
    for (int i = 0; i < st->count; i++) {
        free(st->locals[i].name);
        if (st->locals[i].type_name) free(st->locals[i].type_name);
    }
    st->count = 0;
    st->stack_offset = 0;
}

/* ====================== Global Table Functions ====================== */

void globtab_init(GlobalTable* gt) {
    gt->count = 0;
}

void globtab_add(GlobalTable* gt, const char* name, const char* type_name, int pointer_level) {
    if (gt->count >= MAX_GLOBALS) {
        fprintf(stderr, "Too many globals\n");
        exit(1);
    }
    gt->globals[gt->count].name = _strdup(name);
    gt->globals[gt->count].type_name = _strdup(type_name);
    gt->globals[gt->count].pointer_level = pointer_level;
    gt->globals[gt->count].element_size = calc_element_size(type_name, pointer_level);
    gt->globals[gt->count].is_array = 0;
    gt->globals[gt->count].array_size = 0;
    gt->count++;
}

void globtab_add_array(GlobalTable* gt, const char* name, const char* type_name, int pointer_level, int array_size) {
    if (gt->count >= MAX_GLOBALS) {
        fprintf(stderr, "Too many globals\n");
        exit(1);
    }
    gt->globals[gt->count].name = _strdup(name);
    gt->globals[gt->count].type_name = _strdup(type_name);
    gt->globals[gt->count].pointer_level = pointer_level;
    gt->globals[gt->count].element_size = get_base_type_size(type_name);
    gt->globals[gt->count].is_array = 1;
    gt->globals[gt->count].array_size = array_size;
    gt->count++;
}

GlobalVar* globtab_lookup(GlobalTable* gt, const char* name) {
    for (int i = 0; i < gt->count; i++) {
        if (strcmp(gt->globals[i].name, name) == 0) {
            return &gt->globals[i];
        }
    }
    return NULL;
}

void globtab_free(GlobalTable* gt) {
    for (int i = 0; i < gt->count; i++) {
        free(gt->globals[i].name);
        free(gt->globals[i].type_name);
    }
    gt->count = 0;
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
    StringLiteral* strings;
    int string_capacity;
    int string_list_count;

    StructInfo* structs;
    int struct_count;
    int struct_capacity;

    GlobalTable globtab;
};

/* ====================== Struct Management ====================== */

void codegen_init_struct_table(CodeGen* cg)
{
    cg->struct_capacity = 32;
    cg->structs = (StructInfo*)malloc(sizeof(StructInfo) * cg->struct_capacity);
    cg->struct_count = 0;
}

void codegen_register_struct(CodeGen* cg, AST* struct_decl)
{
    if (!struct_decl || struct_decl->type != N_STRUCT_DECL) return;
    if (!struct_decl->data.struct_decl.name) return;

    if (cg->struct_count >= cg->struct_capacity) {
        cg->struct_capacity *= 2;
        cg->structs = (StructInfo*)realloc(cg->structs,
            sizeof(StructInfo) * cg->struct_capacity);
    }

    StructInfo* info = &cg->structs[cg->struct_count++];
    info->name = _strdup(struct_decl->data.struct_decl.name);
    info->member_count = (int)struct_decl->data.struct_decl.member_count;
    info->members = (StructMember*)malloc(sizeof(StructMember) * info->member_count);

    int offset = 0;
    for (size_t i = 0; i < struct_decl->data.struct_decl.member_count; i++) {
        AST* member = struct_decl->data.struct_decl.members[i];
        if (member->type == N_DECL) {
            info->members[i].name = _strdup(member->data.decl.name);
            info->members[i].offset = offset;

            int size = 4;
            if (member->data.decl.array_size) {
                if (member->data.decl.array_size->type == N_INTLIT) {
                    size = member->data.decl.array_size->data.int_lit.value;
                }
            }
            info->members[i].size = size;
            offset += size;
        }
    }
    info->total_size = offset;
}

StructInfo* codegen_find_struct(CodeGen* cg, const char* name)
{
    const char* search_name = name;
    if (strncmp(name, "struct ", 7) == 0) {
        search_name = name + 7;
    }

    for (int i = 0; i < cg->struct_count; i++) {
        if (strcmp(cg->structs[i].name, search_name) == 0) {
            return &cg->structs[i];
        }
    }
    return NULL;
}

int codegen_get_member_offset(CodeGen* cg, const char* struct_name, const char* member_name)
{
    StructInfo* info = codegen_find_struct(cg, struct_name);
    if (!info) return -1;

    for (int i = 0; i < info->member_count; i++) {
        if (strcmp(info->members[i].name, member_name) == 0) {
            return info->members[i].offset;
        }
    }
    return -1;
}

void codegen_free_struct_table(CodeGen* cg)
{
    for (int i = 0; i < cg->struct_count; i++) {
        free(cg->structs[i].name);
        for (int j = 0; j < cg->structs[i].member_count; j++) {
            free(cg->structs[i].members[j].name);
        }
        free(cg->structs[i].members);
    }
    free(cg->structs);
}

/* ====================== CodeGen Create/Free ====================== */

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
    globtab_init(&cg->globtab);
    cg->string_capacity = 32;
    cg->strings = (StringLiteral*)malloc(sizeof(StringLiteral) * cg->string_capacity);
    cg->string_list_count = 0;

    codegen_init_struct_table(cg);

    return cg;
}

void codegen_free(CodeGen* cg)
{
    if (cg->output) fclose(cg->output);
    symtab_free(&cg->symtab);
    globtab_free(&cg->globtab);
    for (int i = 0; i < cg->string_list_count; i++) {
        free(cg->strings[i].value);
    }
    free(cg->strings);

    codegen_free_struct_table(cg);

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

/* ====================== Get element size for variable ====================== */

static int get_element_size_for_var(CodeGen* cg, const char* var_name) {
    Local* local = symtab_lookup_entry(&cg->symtab, var_name);
    if (local) return local->element_size;

    GlobalVar* global = globtab_lookup(&cg->globtab, var_name);
    if (global) return global->element_size;

    return 1;
}

/* ====================== Emit helpers for sized operations ====================== */

static void emit_scale_index(CodeGen* cg, int element_size) {
    if (element_size == 2) {
        emit(cg, "    shl eax, 1        ; Scale index by 2 (short)");
    }
    else if (element_size == 4) {
        emit(cg, "    shl eax, 2        ; Scale index by 4 (int)");
    }
    else if (element_size > 4) {
        emit(cg, "    imul eax, %d      ; Scale index by %d", element_size, element_size);
    }
}

static void emit_load_sized(CodeGen* cg, int element_size) {
    if (element_size == 1) {
        emit(cg, "    movzx eax, byte [eax]  ; Load byte");
    }
    else if (element_size == 2) {
        emit(cg, "    movzx eax, word [eax]  ; Load word");
    }
    else {
        emit(cg, "    mov eax, [eax]         ; Load dword");
    }
}

static void emit_store_sized(CodeGen* cg, int element_size, const char* dest_reg) {
    if (element_size == 1) {
        emit(cg, "    mov [%s], al           ; Store byte", dest_reg);
    }
    else if (element_size == 2) {
        emit(cg, "    mov [%s], ax           ; Store word", dest_reg);
    }
    else {
        emit(cg, "    mov [%s], eax          ; Store dword", dest_reg);
    }
}

/* ====================== Bare Metal x86-32 Prologue ====================== */

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
}

/* ====================== Kernel Runtime Functions ====================== */

static void codegen_emit_port_io_runtime(CodeGen* cg)
{
    emit(cg, "; ========== Port I/O Functions ==========");
    emit(cg, "");

    emit(cg, "outb:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    mov al, [ebp+12]   ; value");
    emit(cg, "    out dx, al");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "inb:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    xor eax, eax");
    emit(cg, "    in al, dx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "outw:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    mov ax, [ebp+12]   ; value");
    emit(cg, "    out dx, ax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "inw:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    xor eax, eax");
    emit(cg, "    in ax, dx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "outl:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    mov eax, [ebp+12]  ; value");
    emit(cg, "    out dx, eax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "inl:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov dx, [ebp+8]    ; port");
    emit(cg, "    in eax, dx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");
}

static void codegen_emit_interrupt_runtime(CodeGen* cg)
{
    emit(cg, "; ========== Interrupt Control ==========");
    emit(cg, "");

    emit(cg, "disable_interrupts:");
    emit(cg, "cli_func:");
    emit(cg, "    cli");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "enable_interrupts:");
    emit(cg, "sti_func:");
    emit(cg, "    sti");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "halt:");
    emit(cg, "    hlt");
    emit(cg, "    jmp halt");
    emit(cg, "");

    emit(cg, "read_cr0:");
    emit(cg, "    mov eax, cr0");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "write_cr0:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov eax, [ebp+8]");
    emit(cg, "    mov cr0, eax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "read_cr3:");
    emit(cg, "    mov eax, cr3");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "write_cr3:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov eax, [ebp+8]");
    emit(cg, "    mov cr3, eax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");
}

static void codegen_emit_memory_runtime(CodeGen* cg)
{
    emit(cg, "; ========== Memory Operations ==========");
    emit(cg, "");

    emit(cg, "memcpy:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push esi");
    emit(cg, "    push edi");
    emit(cg, "    push ecx");
    emit(cg, "    mov edi, [ebp+8]   ; dest");
    emit(cg, "    mov esi, [ebp+12]  ; src");
    emit(cg, "    mov ecx, [ebp+16]  ; count");
    emit(cg, "    rep movsb");
    emit(cg, "    mov eax, [ebp+8]   ; return dest");
    emit(cg, "    pop ecx");
    emit(cg, "    pop edi");
    emit(cg, "    pop esi");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "memset:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push edi");
    emit(cg, "    push ecx");
    emit(cg, "    mov edi, [ebp+8]   ; dest");
    emit(cg, "    mov al, [ebp+12]   ; value");
    emit(cg, "    mov ecx, [ebp+16]  ; count");
    emit(cg, "    rep stosb");
    emit(cg, "    mov eax, [ebp+8]   ; return dest");
    emit(cg, "    pop ecx");
    emit(cg, "    pop edi");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");
}

static void codegen_emit_runtime(CodeGen* cg)
{
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
            (unsigned char)expr->data.char_lit.value,
            expr->data.char_lit.value >= 32 ? expr->data.char_lit.value : '?');
        break;

    case N_IDENT:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.ident.name);
        if (entry) {
            if (!entry->is_param && entry->size > 4 && entry->pointer_level == 0) {
                emit(cg, "    lea eax, [ebp - %d]  ; Address of array %s",
                    entry->offset, expr->data.ident.name);
            }
            else if (entry->is_param) {
                emit(cg, "    mov eax, [ebp + %d]  ; Load param %s",
                    entry->offset, expr->data.ident.name);
            }
            else {
                emit(cg, "    mov eax, [ebp - %d]  ; Load local %s",
                    entry->offset, expr->data.ident.name);
            }
        }
        else {
            GlobalVar* gv = globtab_lookup(&cg->globtab, expr->data.ident.name);
            if (gv && gv->is_array) {
                emit(cg, "    mov eax, %s  ; Address of global array %s",
                    expr->data.ident.name, expr->data.ident.name);
            }
            else {
                emit(cg, "    mov eax, [%s]  ; Load global %s",
                    expr->data.ident.name, expr->data.ident.name);
            }
        }
        break;
    }

    case N_MEMBER_ACCESS:
    {
        AST* obj = expr->data.member_access.object;
        char* member = expr->data.member_access.member;
        int is_arrow = expr->data.member_access.is_arrow;

        char* struct_type = NULL;
        if (obj->type == N_IDENT) {
            if (strcmp(obj->data.ident.name, "cursor") == 0) {
                struct_type = "Cursor";
            }
            else if (strstr(obj->data.ident.name, "buffer") != NULL ||
                strcmp(obj->data.ident.name, "rb") == 0) {
                struct_type = "RingBuffer";
            }
            else if (strstr(obj->data.ident.name, "task") != NULL) {
                struct_type = "Task";
            }
            else if (strstr(obj->data.ident.name, "current") != NULL ||
                strstr(obj->data.ident.name, "block") != NULL ||
                strstr(obj->data.ident.name, "next") != NULL) {
                struct_type = "MemBlock";
            }
        }

        if (!struct_type) {
            emit(cg, "    ; WARNING: Cannot determine struct type for %s.%s",
                obj->type == N_IDENT ? obj->data.ident.name : "expr", member);
            emit(cg, "    xor eax, eax");
            break;
        }

        int offset = codegen_get_member_offset(cg, struct_type, member);
        if (offset < 0) {
            emit(cg, "    ; WARNING: Member '%s' not found in struct '%s'", member, struct_type);
            emit(cg, "    xor eax, eax");
            break;
        }

        if (is_arrow) {
            codegen_expression(cg, obj);
            emit(cg, "    mov eax, [eax + %d]  ; Load %s->%s", offset,
                obj->type == N_IDENT ? obj->data.ident.name : "ptr", member);
        }
        else {
            if (obj->type == N_IDENT) {
                Local* entry = symtab_lookup_entry(&cg->symtab, obj->data.ident.name);
                if (entry) {
                    if (entry->is_param) {
                        emit(cg, "    mov eax, [ebp + %d + %d]  ; Load %s.%s",
                            entry->offset, offset, obj->data.ident.name, member);
                    }
                    else {
                        emit(cg, "    mov eax, [ebp - %d + %d]  ; Load %s.%s",
                            entry->offset, offset, obj->data.ident.name, member);
                    }
                }
                else {
                    emit(cg, "    mov eax, [%s + %d]  ; Load %s.%s",
                        obj->data.ident.name, offset, obj->data.ident.name, member);
                }
            }
        }
        break;
    }

    case N_OPERATOR:
    {
        if (expr->data.op.op == TOKEN_ASSIGN &&
            expr->data.op.left->type == N_MEMBER_ACCESS) {

            AST* member_access = expr->data.op.left;
            AST* obj = member_access->data.member_access.object;
            char* member = member_access->data.member_access.member;
            int is_arrow = member_access->data.member_access.is_arrow;

            char* struct_type = NULL;
            if (obj->type == N_IDENT) {
                if (strcmp(obj->data.ident.name, "cursor") == 0) {
                    struct_type = "Cursor";
                }
                else if (strstr(obj->data.ident.name, "buffer") != NULL) {
                    struct_type = "RingBuffer";
                }
                else if (strstr(obj->data.ident.name, "task") != NULL) {
                    struct_type = "Task";
                }
                else if (strstr(obj->data.ident.name, "current") != NULL ||
                    strstr(obj->data.ident.name, "block") != NULL ||
                    strstr(obj->data.ident.name, "new_block") != NULL) {
                    struct_type = "MemBlock";
                }
            }

            if (struct_type) {
                int offset = codegen_get_member_offset(cg, struct_type, member);
                if (offset >= 0) {
                    codegen_expression(cg, expr->data.op.right);
                    emit(cg, "    push eax  ; Save value");

                    if (is_arrow) {
                        codegen_expression(cg, obj);
                        emit(cg, "    mov ebx, eax  ; Pointer in ebx");
                        emit(cg, "    pop eax  ; Restore value");
                        emit(cg, "    mov [ebx + %d], eax  ; Store to ->%s", offset, member);
                    }
                    else {
                        if (obj->type == N_IDENT) {
                            Local* entry = symtab_lookup_entry(&cg->symtab, obj->data.ident.name);
                            emit(cg, "    pop eax  ; Restore value");
                            if (entry) {
                                if (entry->is_param) {
                                    emit(cg, "    mov [ebp + %d + %d], eax  ; Store to %s.%s",
                                        entry->offset, offset, obj->data.ident.name, member);
                                }
                                else {
                                    emit(cg, "    mov [ebp - %d + %d], eax  ; Store to %s.%s",
                                        entry->offset, offset, obj->data.ident.name, member);
                                }
                            }
                            else {
                                emit(cg, "    mov [%s + %d], eax  ; Store to %s.%s",
                                    obj->data.ident.name, offset, obj->data.ident.name, member);
                            }
                        }
                    }
                    break;
                }
            }
        }

        if (expr->data.op.op == TOKEN_ASSIGN && expr->data.op.left->type == N_ARRAY_ACCESS) {
            AST* arr_access = expr->data.op.left;
            AST* array_expr = arr_access->data.array_access.array;

            int element_size = 1;
            if (array_expr->type == N_IDENT) {
                element_size = get_element_size_for_var(cg, array_expr->data.ident.name);
            }

            if (array_expr->type == N_IDENT) {
                Local* entry = symtab_lookup_entry(&cg->symtab, array_expr->data.ident.name);
                if (entry && !entry->is_param && entry->pointer_level == 0 && entry->size > 4) {
                    emit(cg, "    lea eax, [ebp - %d]  ; Load array base address", entry->offset);
                    emit(cg, "    mov edi, eax     ; Array base in edi");
                }
                else {
                    codegen_expression(cg, array_expr);
                    emit(cg, "    mov edi, eax     ; Pointer value in edi");
                }
            }
            else {
                codegen_expression(cg, array_expr);
                emit(cg, "    mov edi, eax     ; Array base in edi");
            }

            codegen_expression(cg, arr_access->data.array_access.index);
            emit_scale_index(cg, element_size);
            emit(cg, "    add edi, eax     ; Add scaled index to base");
            codegen_expression(cg, expr->data.op.right);
            emit_store_sized(cg, element_size, "edi");
            break;
        }

        codegen_expression(cg, expr->data.op.left);
        emit(cg, "    push eax         ; Save left operand");
        codegen_expression(cg, expr->data.op.right);
        emit(cg, "    mov ebx, eax     ; Right operand in ebx");
        emit(cg, "    pop eax          ; Restore left operand");

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
        for (int i = (int)expr->data.call.arg_count - 1; i >= 0; i--) {
            codegen_expression(cg, expr->data.call.args[i]);
            emit(cg, "    push eax         ; Push arg %d", i);
        }
        emit(cg, "    call %s", expr->data.call.name);
        if (expr->data.call.arg_count > 0) {
            emit(cg, "    add esp, %d      ; Clean %d args",
                (int)expr->data.call.arg_count * 4, (int)expr->data.call.arg_count);
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
            codegen_expression(cg, expr->data.assign.value);
            emit(cg, "    mov [%s], eax  ; Store to global %s",
                expr->data.assign.var_name, expr->data.assign.var_name);
        }
        break;
    }

    case N_ARRAY_ACCESS:
    {
        AST* array_expr = expr->data.array_access.array;

        int element_size = 1;
        if (array_expr->type == N_IDENT) {
            element_size = get_element_size_for_var(cg, array_expr->data.ident.name);
        }

        if (array_expr->type == N_IDENT) {
            Local* entry = symtab_lookup_entry(&cg->symtab, array_expr->data.ident.name);

            if (entry && !entry->is_param && entry->pointer_level == 0 && entry->size > 4) {
                emit(cg, "    lea eax, [ebp - %d]  ; Load array base address", entry->offset);
                emit(cg, "    push eax  ; Save array base");
                codegen_expression(cg, expr->data.array_access.index);
                emit_scale_index(cg, element_size);
                emit(cg, "    pop ebx   ; Restore array base");
                emit(cg, "    add eax, ebx  ; Compute element address");
                emit_load_sized(cg, element_size);
            }
            else {
                codegen_expression(cg, array_expr);
                emit(cg, "    push eax  ; Save pointer value");
                codegen_expression(cg, expr->data.array_access.index);
                emit_scale_index(cg, element_size);
                emit(cg, "    pop ebx   ; Restore pointer");
                emit(cg, "    add eax, ebx  ; Compute address");
                emit_load_sized(cg, element_size);
            }
        }
        else {
            codegen_expression(cg, array_expr);
            emit(cg, "    push eax  ; Save array ptr");
            codegen_expression(cg, expr->data.array_access.index);
            emit_scale_index(cg, element_size);
            emit(cg, "    pop ebx   ; Restore array ptr");
            emit(cg, "    add eax, ebx  ; Compute address");
            emit_load_sized(cg, element_size);
        }
        break;
    }

    case N_CAST:
    {
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
        if (expr->data.unary.op == TOKEN_AMPERSAND) {
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
                    emit(cg, "    mov eax, %s  ; Address of global %s",
                        target->data.ident.name, target->data.ident.name);
                }
            }
            else if (target->type == N_ARRAY_ACCESS) {
                AST* arr = target->data.array_access.array;
                AST* idx = target->data.array_access.index;

                int element_size = 1;
                if (arr->type == N_IDENT) {
                    element_size = get_element_size_for_var(cg, arr->data.ident.name);
                }

                if (arr->type == N_IDENT) {
                    Local* entry = symtab_lookup_entry(&cg->symtab, arr->data.ident.name);
                    if (entry && !entry->is_param && entry->size > 4) {
                        emit(cg, "    lea eax, [ebp - %d]  ; Load array base address", entry->offset);
                    }
                    else {
                        codegen_expression(cg, arr);
                    }
                }
                else {
                    codegen_expression(cg, arr);
                }
                emit(cg, "    push eax  ; Save array base");
                codegen_expression(cg, idx);
                emit_scale_index(cg, element_size);
                emit(cg, "    pop ebx   ; Restore array base");
                emit(cg, "    add eax, ebx  ; Compute element address");
            }
            else {
                emit(cg, "    ; TODO: Address-of complex expression");
                codegen_expression(cg, target);
            }
            break;
        }

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
        case TOKEN_STAR:
            emit(cg, "    mov eax, [eax]");
            break;
        default:
            emit(cg, "    ; TODO: Unary operator %d", expr->data.unary.op);
            break;
        }
        break;
    }

    case N_TERNARY:
    {
        int label_false = codegen_new_label(cg);
        int label_end = codegen_new_label(cg);

        codegen_expression(cg, expr->data.ternary.condition);
        emit(cg, "    test eax, eax");
        emit(cg, "    jz .L%d", label_false);

        codegen_expression(cg, expr->data.ternary.true_expr);
        emit(cg, "    jmp .L%d", label_end);

        emit(cg, ".L%d:", label_false);
        codegen_expression(cg, expr->data.ternary.false_expr);

        emit(cg, ".L%d:", label_end);
        break;
    }

    case N_SIZEOF:
    {
        emit(cg, "    mov eax, 4  ; sizeof (simplified)");
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
            offset = symtab_add_array_typed(&cg->symtab, stmt->data.decl.name, arr_size,
                stmt->data.decl.type, stmt->data.decl.pointer_level);
            emit(cg, "    ; Array %s[%d] at [ebp - %d]",
                stmt->data.decl.name, arr_size, offset);
        }
        else {
            offset = symtab_add_typed(&cg->symtab, stmt->data.decl.name,
                stmt->data.decl.type, stmt->data.decl.pointer_level);
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

    case N_ASM:
    {
        emit(cg, "    ; Inline assembly");
        if (stmt->data.asm_stmt.assembly_code) {
            char* asm_copy = _strdup(stmt->data.asm_stmt.assembly_code);
            char* line = strtok(asm_copy, "\n");
            while (line) {
                while (*line == ' ' || *line == '\t') line++;
                if (*line) {
                    emit(cg, "    %s", line);
                }
                line = strtok(NULL, "\n");
            }
            free(asm_copy);
        }
        break;
    }

    default:
        codegen_expression(cg, stmt);
        break;
    }
}

/* ====================== Function Code Generation ====================== */

void codegen_function_correct(CodeGen* cg, AST* func)
{
    if (!func || func->type != N_FUNCTION) return;

    if (func->data.function.body == NULL) {
        return;
    }

    symtab_free(&cg->symtab);
    symtab_init(&cg->symtab);

    emit(cg, "");
    emit(cg, "; Function: %s", func->data.function.name);
    emit(cg, "%s:", func->data.function.name);
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");

    for (size_t i = 0; i < func->data.function.param_count; i++) {
        AST* param = func->data.function.params[i];
        if (param->type == N_DECL) {
            symtab_add_param_typed(&cg->symtab, param->data.decl.name, 8 + ((int)i * 4),
                param->data.decl.type, param->data.decl.pointer_level);
        }
    }

    emit(cg, "    sub esp, 512     ; Reserve stack space");

    codegen_statement(cg, func->data.function.body);

    emit(cg, ".epilogue:");
    emit(cg, "    mov esp, ebp");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
}

void codegen_function(CodeGen* cg, AST* func)
{
    codegen_function_correct(cg, func);
}

/* ====================== Program Code Generation ====================== */

void codegen_program(CodeGen* cg, AST* program)
{
    if (!program || program->type != N_PROGRAM) return;

    // First pass: register structs and globals
    for (size_t i = 0; i < program->data.program.global_count; i++) {
        AST* global = program->data.program.globals[i];
        if (global->type == N_STRUCT_DECL) {
            codegen_register_struct(cg, global);
        }
        else if (global->type == N_DECL) {
            if (global->data.decl.array_size && global->data.decl.array_size->type == N_INTLIT) {
                int arr_size = global->data.decl.array_size->data.int_lit.value;
                globtab_add_array(&cg->globtab, global->data.decl.name,
                    global->data.decl.type, global->data.decl.pointer_level, arr_size);
            }
            else {
                globtab_add(&cg->globtab, global->data.decl.name,
                    global->data.decl.type, global->data.decl.pointer_level);
            }
        }
    }

    codegen_baremetal_prologue(cg);

    for (size_t i = 0; i < program->data.program.func_count; i++) {
        AST* func = program->data.program.functions[i];
        if (func->data.function.body != NULL) {
            codegen_function_correct(cg, func);
        }
    }

    codegen_emit_runtime(cg);
    codegen_emit_port_io_runtime(cg);
    codegen_emit_interrupt_runtime(cg);
    codegen_emit_memory_runtime(cg);

    emit(cg, "");
    emit(cg, "section .data");
    codegen_emit_strings(cg);

    for (size_t i = 0; i < program->data.program.global_count; i++) {
        AST* global = program->data.program.globals[i];
        if (global->type == N_DECL) {
            if (global->data.decl.array_size && global->data.decl.array_size->type == N_INTLIT) {
                int arr_size = global->data.decl.array_size->data.int_lit.value;
                int elem_size = get_base_type_size(global->data.decl.type);
                int total_bytes = arr_size * elem_size;
                emit(cg, "%s: times %d db 0  ; array[%d]",
                    global->data.decl.name, total_bytes, arr_size);
            }
            else {
                int init_val = 0;
                if (global->data.decl.init_value) {
                    if (global->data.decl.init_value->type == N_INTLIT) {
                        init_val = global->data.decl.init_value->data.int_lit.value;
                    }
                    else if (global->data.decl.init_value->type == N_CAST) {
                        AST* cast_expr = global->data.decl.init_value->data.cast.expr;
                        if (cast_expr->type == N_INTLIT) {
                            init_val = cast_expr->data.int_lit.value;
                        }
                    }
                }
                emit(cg, "%s dd %d", global->data.decl.name, init_val);
            }
        }
    }

    emit(cg, "vga_cursor dd 0");
    emit(cg, "");
    emit(cg, "; End of kernel");
}