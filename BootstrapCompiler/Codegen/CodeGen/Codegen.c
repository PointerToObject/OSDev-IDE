#include "../Codegen.h"

/* ====================== Symbol Table ====================== */

#define MAX_LOCALS 256
#define MAX_GLOBALS 256
#define MAX_LOOP_DEPTH 32

typedef struct {
    char* name;
    int offset;
    int size;
    int is_param;
    char* type_name;      // Full type including "struct Foo"
    int pointer_level;
    int element_size;
    int is_array;         // NEW: Track if this is an array
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

/* ====================== Loop Label Stack (for break/continue) ====================== */

typedef struct {
    int break_label;
    int continue_label;
} LoopContext;

static LoopContext loop_stack[MAX_LOOP_DEPTH];
static int loop_depth = 0;

static void push_loop(int break_lbl, int continue_lbl) {
    if (loop_depth >= MAX_LOOP_DEPTH) {
        fprintf(stderr, "Loop nesting too deep\n");
        return;
    }
    loop_stack[loop_depth].break_label = break_lbl;
    loop_stack[loop_depth].continue_label = continue_lbl;
    loop_depth++;
}

static void pop_loop(void) {
    if (loop_depth > 0) loop_depth--;
}

static int get_break_label(void) {
    if (loop_depth == 0) return -1;
    return loop_stack[loop_depth - 1].break_label;
}

static int get_continue_label(void) {
    if (loop_depth == 0) return -1;
    return loop_stack[loop_depth - 1].continue_label;
}

/* ====================== Type Size Helpers ====================== */

static int get_base_type_size(const char* type_name) {
    if (!type_name) return 4;

    // Make a copy we can modify
    char base[64];
    strncpy(base, type_name, 63);
    base[63] = '\0';

    // Skip qualifiers
    char* p = base;
    if (strncmp(p, "unsigned ", 9) == 0) p += 9;
    else if (strncmp(p, "signed ", 7) == 0) p += 7;
    else if (strncmp(p, "const ", 6) == 0) p += 6;
    else if (strncmp(p, "volatile ", 9) == 0) p += 9;

    // Strip pointer asterisks and spaces from end
    size_t len = strlen(p);
    while (len > 0 && (p[len - 1] == '*' || p[len - 1] == ' ')) {
        p[--len] = '\0';
    }

    if (strcmp(p, "char") == 0) return 1;
    if (strcmp(p, "short") == 0) return 2;
    if (strcmp(p, "int") == 0) return 4;
    if (strcmp(p, "long") == 0) return 4;
    if (strcmp(p, "void") == 0) return 1;  // void* arithmetic uses 1

    // Struct types - will be resolved later
    if (strncmp(p, "struct ", 7) == 0) return 4;  // Default, fixed by struct lookup

    return 4;
}

static int calc_element_size(const char* type_name, int pointer_level) {
    if (pointer_level > 1) return 4;  // Pointer to pointer = 4 bytes
    if (pointer_level == 1) return get_base_type_size(type_name);
    return get_base_type_size(type_name);
}

/* ====================== Symbol Table Functions ====================== */

static void symtab_init(SymbolTable* st) {
    st->count = 0;
    st->stack_offset = 0;
}

// Forward declaration - need access to CodeGen for struct lookup
static CodeGen* g_current_cg = NULL;

static int symtab_add_typed(SymbolTable* st, const char* name, const char* type_name,
    int pointer_level, int is_array, int array_count) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many local variables\n");
        exit(1);
    }

    int elem_size;
    int total_size;

    // Check if this is a struct type
    if (pointer_level == 0 && strncmp(type_name, "struct ", 7) == 0) {
        // Look up struct size
        StructInfo* sinfo = NULL;
        if (g_current_cg) {
            sinfo = codegen_find_struct(g_current_cg, type_name + 7);
        }
        if (sinfo) {
            elem_size = sinfo->total_size;
        }
        else {
            elem_size = 4;  // Fallback
            fprintf(stderr, "Warning: Unknown struct '%s', using size 4\n", type_name);
        }
    }
    else {
        elem_size = get_base_type_size(type_name);
    }

    if (pointer_level > 0) {
        // Pointers are always 4 bytes
        total_size = 4;
    }
    else if (is_array && array_count > 0) {
        total_size = elem_size * array_count;
    }
    else {
        total_size = elem_size;
    }

    // Align to 4 bytes
    total_size = (total_size + 3) & ~3;

    st->stack_offset += total_size;

    Local* local = &st->locals[st->count];
    local->name = _strdup(name);
    local->offset = st->stack_offset;
    local->size = total_size;
    local->is_param = 0;
    local->type_name = _strdup(type_name);
    local->pointer_level = pointer_level;
    // For pointers: element_size is size of what we point TO
    // char* -> element_size = 1, int* -> element_size = 4, char** -> element_size = 4
    if (pointer_level > 1) {
        local->element_size = 4;  // Pointer to pointer = 4 bytes
    }
    else if (pointer_level == 1) {
        local->element_size = get_base_type_size(type_name);  // Size of pointed-to type
    }
    else {
        local->element_size = elem_size;
    }
    local->is_array = is_array;

    st->count++;
    return st->stack_offset;
}

static void symtab_add_param_typed(SymbolTable* st, const char* name, int stack_pos,
    const char* type_name, int pointer_level) {
    if (st->count >= MAX_LOCALS) {
        fprintf(stderr, "Too many parameters\n");
        exit(1);
    }

    Local* local = &st->locals[st->count];
    local->name = _strdup(name);
    local->offset = stack_pos;
    local->size = 4;
    local->is_param = 1;
    local->type_name = _strdup(type_name);
    local->pointer_level = pointer_level;
    // For pointers: element_size is size of what we point TO
    if (pointer_level > 1) {
        local->element_size = 4;  // Pointer to pointer
    }
    else if (pointer_level == 1) {
        local->element_size = get_base_type_size(type_name);
    }
    else {
        local->element_size = get_base_type_size(type_name);
    }
    local->is_array = 0;

    st->count++;
}

static Local* symtab_lookup_entry(SymbolTable* st, const char* name) {
    for (int i = 0; i < st->count; i++) {
        if (strcmp(st->locals[i].name, name) == 0) {
            return &st->locals[i];
        }
    }
    return NULL;
}

static void symtab_free(SymbolTable* st) {
    for (int i = 0; i < st->count; i++) {
        free(st->locals[i].name);
        if (st->locals[i].type_name) free(st->locals[i].type_name);
    }
    st->count = 0;
    st->stack_offset = 0;
}

/* ====================== Global Table Functions ====================== */

static void globtab_init(GlobalTable* gt) {
    gt->count = 0;
}

static void globtab_add(GlobalTable* gt, const char* name, const char* type_name,
    int pointer_level, int is_array, int array_size) {
    if (gt->count >= MAX_GLOBALS) {
        fprintf(stderr, "Too many globals\n");
        exit(1);
    }
    GlobalVar* gv = &gt->globals[gt->count];
    gv->name = _strdup(name);
    gv->type_name = _strdup(type_name);
    gv->pointer_level = pointer_level;
    gv->element_size = get_base_type_size(type_name);
    gv->is_array = is_array;
    gv->array_size = array_size;
    gt->count++;
}

static GlobalVar* globtab_lookup(GlobalTable* gt, const char* name) {
    for (int i = 0; i < gt->count; i++) {
        if (strcmp(gt->globals[i].name, name) == 0) {
            return &gt->globals[i];
        }
    }
    return NULL;
}

static void globtab_free(GlobalTable* gt) {
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

/* ====================== CodeGen Structure ====================== */

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

void codegen_init_struct_table(CodeGen* cg) {
    cg->struct_capacity = 32;
    cg->structs = (StructInfo*)malloc(sizeof(StructInfo) * cg->struct_capacity);
    cg->struct_count = 0;
}

void codegen_register_struct(CodeGen* cg, AST* struct_decl) {
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

            int size;
            if (member->data.decl.pointer_level > 0) {
                size = 4;  // All pointers are 4 bytes
            }
            else if (member->data.decl.array_size) {
                if (member->data.decl.array_size->type == N_INTLIT) {
                    int arr_count = member->data.decl.array_size->data.int_lit.value;
                    int elem_size = get_base_type_size(member->data.decl.type);
                    size = arr_count * elem_size;
                }
                else {
                    size = 4;
                }
            }
            else {
                size = get_base_type_size(member->data.decl.type);
            }

            info->members[i].size = size;
            offset += size;

            // Align to 4 bytes for next member
            offset = (offset + 3) & ~3;
        }
    }
    info->total_size = offset;
}

StructInfo* codegen_find_struct(CodeGen* cg, const char* name) {
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

int codegen_get_member_offset(CodeGen* cg, const char* struct_name, const char* member_name) {
    StructInfo* info = codegen_find_struct(cg, struct_name);
    if (!info) return -1;

    for (int i = 0; i < info->member_count; i++) {
        if (strcmp(info->members[i].name, member_name) == 0) {
            return info->members[i].offset;
        }
    }
    return -1;
}

static int codegen_get_member_size(CodeGen* cg, const char* struct_name, const char* member_name) {
    StructInfo* info = codegen_find_struct(cg, struct_name);
    if (!info) return 4;

    for (int i = 0; i < info->member_count; i++) {
        if (strcmp(info->members[i].name, member_name) == 0) {
            return info->members[i].size;
        }
    }
    return 4;
}

void codegen_free_struct_table(CodeGen* cg) {
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

CodeGen* codegen_create(const char* output_file, TargetPlatform target) {
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
    loop_depth = 0;  // Reset loop stack

    return cg;
}

void codegen_free(CodeGen* cg) {
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

int codegen_new_label(CodeGen* cg) {
    return cg->label_count++;
}

void emit(CodeGen* cg, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    vfprintf(cg->output, fmt, args);
    va_end(args);
    fprintf(cg->output, "\n");
}

int codegen_add_string(CodeGen* cg, const char* value) {
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

void codegen_emit_strings(CodeGen* cg) {
    if (cg->string_list_count > 0) {
        for (int i = 0; i < cg->string_list_count; i++) {
            // Escape backticks and other special chars for NASM
            emit(cg, "str%d db `%s`,0", cg->strings[i].id, cg->strings[i].value);
        }
    }
}

/* ====================== Type Resolution Helpers ====================== */

// Get the struct type name for a variable (returns NULL if not a struct)
static const char* get_var_struct_type(CodeGen* cg, const char* var_name) {
    Local* local = symtab_lookup_entry(&cg->symtab, var_name);
    if (local && local->type_name) {
        if (strncmp(local->type_name, "struct ", 7) == 0) {
            return local->type_name + 7;  // Return just the struct name
        }
    }

    GlobalVar* global = globtab_lookup(&cg->globtab, var_name);
    if (global && global->type_name) {
        if (strncmp(global->type_name, "struct ", 7) == 0) {
            return global->type_name + 7;
        }
    }

    return NULL;
}

// Get element size for array/pointer variable
static int get_element_size_for_var(CodeGen* cg, const char* var_name) {
    Local* local = symtab_lookup_entry(&cg->symtab, var_name);
    if (local) return local->element_size;

    GlobalVar* global = globtab_lookup(&cg->globtab, var_name);
    if (global) return global->element_size;

    return 1;  // Default to byte access
}

/* ====================== Emit helpers for sized operations ====================== */

static void emit_scale_index(CodeGen* cg, int element_size) {
    if (element_size == 1) {
        // No scaling needed
    }
    else if (element_size == 2) {
        emit(cg, "    shl eax, 1        ; Scale index by 2");
    }
    else if (element_size == 4) {
        emit(cg, "    shl eax, 2        ; Scale index by 4");
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

static void codegen_baremetal_prologue(CodeGen* cg) {
    emit(cg, "[BITS 32]");
    emit(cg, "");
    emit(cg, "[org 0x8000]");
    emit(cg, "");
    emit(cg, "section .text");
    emit(cg, "global _start");
    emit(cg, "global kernel_main");
    emit(cg, "");
    emit(cg, "_start:");
    emit(cg, "    jmp kernel_main");
    emit(cg, "");
}

/* ====================== Runtime Functions ====================== */

static void codegen_emit_port_io_runtime(CodeGen* cg) {
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

static void codegen_emit_interrupt_runtime(CodeGen* cg) {
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

static void codegen_emit_memory_runtime(CodeGen* cg) {
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

    emit(cg, "memcmp:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push esi");
    emit(cg, "    push edi");
    emit(cg, "    push ecx");
    emit(cg, "    mov esi, [ebp+8]   ; s1");
    emit(cg, "    mov edi, [ebp+12]  ; s2");
    emit(cg, "    mov ecx, [ebp+16]  ; n");
    emit(cg, "    xor eax, eax");
    emit(cg, "    repe cmpsb");
    emit(cg, "    je .memcmp_equal");
    emit(cg, "    movzx eax, byte [esi-1]");
    emit(cg, "    movzx edx, byte [edi-1]");
    emit(cg, "    sub eax, edx");
    emit(cg, ".memcmp_equal:");
    emit(cg, "    pop ecx");
    emit(cg, "    pop edi");
    emit(cg, "    pop esi");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");
}

static void codegen_emit_runtime(CodeGen* cg) {
    // VGA text mode printing
    emit(cg, "; ========== VGA Text Mode ==========");
    emit(cg, "");

    emit(cg, "print_char:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push ebx");
    emit(cg, "    mov eax, [vga_cursor]");
    emit(cg, "    mov ebx, 0xB8000");
    emit(cg, "    mov cl, [ebp+8]      ; char");
    emit(cg, "    mov ch, 0x0F         ; white on black");
    emit(cg, "    mov [ebx + eax*2], cx");
    emit(cg, "    inc dword [vga_cursor]");
    emit(cg, "    pop ebx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_string:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push esi");
    emit(cg, "    push ebx");
    emit(cg, "    mov esi, [ebp+8]     ; string ptr");
    emit(cg, "    mov ebx, 0xB8000");
    emit(cg, ".ps_loop:");
    emit(cg, "    lodsb");
    emit(cg, "    test al, al");
    emit(cg, "    jz .ps_done");
    emit(cg, "    mov edi, [vga_cursor]");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    mov [ebx + edi*2], ax");
    emit(cg, "    inc dword [vga_cursor]");
    emit(cg, "    jmp .ps_loop");
    emit(cg, ".ps_done:");
    emit(cg, "    pop ebx");
    emit(cg, "    pop esi");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_hex:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push ebx");
    emit(cg, "    push ecx");
    emit(cg, "    push edx");
    emit(cg, "    mov eax, [ebp+8]");
    emit(cg, "    mov ecx, 8");
    emit(cg, "    mov ebx, 0xB8000");
    emit(cg, ".ph_loop:");
    emit(cg, "    rol eax, 4");
    emit(cg, "    mov edx, eax");
    emit(cg, "    and edx, 0xF");
    emit(cg, "    mov dl, [hex_chars + edx]");
    emit(cg, "    push eax");
    emit(cg, "    mov edi, [vga_cursor]");
    emit(cg, "    mov dh, 0x0F");
    emit(cg, "    mov [ebx + edi*2], dx");
    emit(cg, "    inc dword [vga_cursor]");
    emit(cg, "    pop eax");
    emit(cg, "    loop .ph_loop");
    emit(cg, "    pop edx");
    emit(cg, "    pop ecx");
    emit(cg, "    pop ebx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "print_int:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    push ebx");
    emit(cg, "    push ecx");
    emit(cg, "    push edx");
    emit(cg, "    push esi");
    emit(cg, "    mov eax, [ebp+8]");
    emit(cg, "    mov esi, 0xB8000");
    emit(cg, "    test eax, eax");
    emit(cg, "    jns .pi_positive");
    emit(cg, "    ; Print minus sign");
    emit(cg, "    push eax");
    emit(cg, "    mov edi, [vga_cursor]");
    emit(cg, "    mov word [esi + edi*2], 0x0F2D");
    emit(cg, "    inc dword [vga_cursor]");
    emit(cg, "    pop eax");
    emit(cg, "    neg eax");
    emit(cg, ".pi_positive:");
    emit(cg, "    mov ebx, 10");
    emit(cg, "    xor ecx, ecx");
    emit(cg, "    test eax, eax");
    emit(cg, "    jnz .pi_div");
    emit(cg, "    push 0");
    emit(cg, "    inc ecx");
    emit(cg, "    jmp .pi_print");
    emit(cg, ".pi_div:");
    emit(cg, "    xor edx, edx");
    emit(cg, "    div ebx");
    emit(cg, "    push edx");
    emit(cg, "    inc ecx");
    emit(cg, "    test eax, eax");
    emit(cg, "    jnz .pi_div");
    emit(cg, ".pi_print:");
    emit(cg, "    pop eax");
    emit(cg, "    add al, '0'");
    emit(cg, "    mov ah, 0x0F");
    emit(cg, "    mov edi, [vga_cursor]");
    emit(cg, "    mov [esi + edi*2], ax");
    emit(cg, "    inc dword [vga_cursor]");
    emit(cg, "    loop .pi_print");
    emit(cg, "    pop esi");
    emit(cg, "    pop edx");
    emit(cg, "    pop ecx");
    emit(cg, "    pop ebx");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "set_cursor:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov eax, [ebp+8]");
    emit(cg, "    mov [vga_cursor], eax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "get_cursor:");
    emit(cg, "    mov eax, [vga_cursor]");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "newline:");
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");
    emit(cg, "    mov eax, [vga_cursor]");
    emit(cg, "    mov ebx, 80");
    emit(cg, "    xor edx, edx");
    emit(cg, "    div ebx");
    emit(cg, "    inc eax");
    emit(cg, "    imul eax, 80");
    emit(cg, "    mov [vga_cursor], eax");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "clear_screen:");
    emit(cg, "    push edi");
    emit(cg, "    push ecx");
    emit(cg, "    mov edi, 0xB8000");
    emit(cg, "    mov ecx, 2000");
    emit(cg, "    mov ax, 0x0F20      ; white space");
    emit(cg, "    rep stosw");
    emit(cg, "    mov dword [vga_cursor], 0");
    emit(cg, "    pop ecx");
    emit(cg, "    pop edi");
    emit(cg, "    ret");
    emit(cg, "");

    emit(cg, "hex_chars db '0123456789ABCDEF'");
    emit(cg, "");
}

/* ====================== Address Generation (for lvalues) ====================== */

// Generate code that leaves the ADDRESS of an lvalue in EAX
static void codegen_lvalue_address(CodeGen* cg, AST* expr);

static void codegen_lvalue_address(CodeGen* cg, AST* expr) {
    if (!expr) return;

    switch (expr->type) {
    case N_IDENT:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.ident.name);
        if (entry) {
            if (entry->is_param) {
                emit(cg, "    lea eax, [ebp + %d]  ; Address of param %s",
                    entry->offset, expr->data.ident.name);
            }
            else {
                emit(cg, "    lea eax, [ebp - %d]  ; Address of local %s",
                    entry->offset, expr->data.ident.name);
            }
        }
        else {
            emit(cg, "    mov eax, %s  ; Address of global %s",
                expr->data.ident.name, expr->data.ident.name);
        }
        break;
    }

    case N_ARRAY_ACCESS:
    {
        AST* arr = expr->data.array_access.array;
        AST* idx = expr->data.array_access.index;

        int element_size = 1;
        if (arr->type == N_IDENT) {
            element_size = get_element_size_for_var(cg, arr->data.ident.name);
        }

        // Get base address
        if (arr->type == N_IDENT) {
            Local* entry = symtab_lookup_entry(&cg->symtab, arr->data.ident.name);
            if (entry && entry->is_array && !entry->is_param) {
                emit(cg, "    lea eax, [ebp - %d]  ; Array base", entry->offset);
            }
            else if (entry) {
                if (entry->is_param) {
                    emit(cg, "    mov eax, [ebp + %d]  ; Load pointer param", entry->offset);
                }
                else {
                    emit(cg, "    mov eax, [ebp - %d]  ; Load pointer local", entry->offset);
                }
            }
            else {
                GlobalVar* gv = globtab_lookup(&cg->globtab, arr->data.ident.name);
                if (gv && gv->is_array) {
                    emit(cg, "    mov eax, %s  ; Array address", arr->data.ident.name);
                }
                else {
                    emit(cg, "    mov eax, [%s]  ; Load global pointer", arr->data.ident.name);
                }
            }
        }
        else {
            codegen_expression(cg, arr);
        }

        emit(cg, "    push eax  ; Save base");
        codegen_expression(cg, idx);
        emit_scale_index(cg, element_size);
        emit(cg, "    pop ebx  ; Restore base");
        emit(cg, "    add eax, ebx  ; Compute element address");
        break;
    }

    case N_UNARY:
        if (expr->data.unary.op == TOKEN_STAR) {
            // *ptr - address is the value of ptr
            codegen_expression(cg, expr->data.unary.operand);
        }
        break;

    case N_MEMBER_ACCESS:
    {
        AST* obj = expr->data.member_access.object;
        char* member = expr->data.member_access.member;
        int is_arrow = expr->data.member_access.is_arrow;

        const char* struct_type = NULL;
        if (obj->type == N_IDENT) {
            struct_type = get_var_struct_type(cg, obj->data.ident.name);
        }

        if (!struct_type) {
            emit(cg, "    ; WARNING: Unknown struct type for member access");
            emit(cg, "    xor eax, eax");
            return;
        }

        int offset = codegen_get_member_offset(cg, struct_type, member);
        if (offset < 0) {
            emit(cg, "    ; WARNING: Member '%s' not found in struct '%s'", member, struct_type);
            emit(cg, "    xor eax, eax");
            return;
        }

        if (is_arrow) {
            codegen_expression(cg, obj);  // Get pointer value
            emit(cg, "    add eax, %d  ; Offset to member %s", offset, member);
        }
        else {
            codegen_lvalue_address(cg, obj);  // Get struct address
            emit(cg, "    add eax, %d  ; Offset to member %s", offset, member);
        }
        break;
    }

    default:
        emit(cg, "    ; ERROR: Cannot take address of expression type %d", expr->type);
        break;
    }
}

/* ====================== Expression Code Generation ====================== */

void codegen_expression(CodeGen* cg, AST* expr) {
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
        emit(cg, "    mov eax, %d  ; char '%c'",
            (unsigned char)expr->data.char_lit.value,
            expr->data.char_lit.value >= 32 && expr->data.char_lit.value < 127
            ? expr->data.char_lit.value : '?');
        break;

    case N_IDENT:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.ident.name);
        if (entry) {
            if (entry->is_array && !entry->is_param) {
                // Local array - return address
                emit(cg, "    lea eax, [ebp - %d]  ; Address of array %s",
                    entry->offset, expr->data.ident.name);
            }
            else if (entry->is_param) {
                emit(cg, "    mov eax, [ebp + %d]  ; Param %s",
                    entry->offset, expr->data.ident.name);
            }
            else {
                emit(cg, "    mov eax, [ebp - %d]  ; Local %s",
                    entry->offset, expr->data.ident.name);
            }
        }
        else {
            GlobalVar* gv = globtab_lookup(&cg->globtab, expr->data.ident.name);
            if (gv && gv->is_array) {
                emit(cg, "    mov eax, %s  ; Address of global array",
                    expr->data.ident.name);
            }
            else {
                emit(cg, "    mov eax, [%s]  ; Global %s",
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

        const char* struct_type = NULL;
        if (obj->type == N_IDENT) {
            struct_type = get_var_struct_type(cg, obj->data.ident.name);
        }

        if (!struct_type) {
            emit(cg, "    ; WARNING: Cannot determine struct type");
            emit(cg, "    xor eax, eax");
            break;
        }

        int offset = codegen_get_member_offset(cg, struct_type, member);
        int mem_size = codegen_get_member_size(cg, struct_type, member);

        if (offset < 0) {
            emit(cg, "    ; WARNING: Member '%s' not found", member);
            emit(cg, "    xor eax, eax");
            break;
        }

        if (is_arrow) {
            // ptr->member
            codegen_expression(cg, obj);
            if (mem_size == 1) {
                emit(cg, "    movzx eax, byte [eax + %d]  ; %s->%s (byte)",
                    offset, obj->data.ident.name, member);
            }
            else if (mem_size == 2) {
                emit(cg, "    movzx eax, word [eax + %d]  ; %s->%s (word)",
                    offset, obj->data.ident.name, member);
            }
            else {
                emit(cg, "    mov eax, [eax + %d]  ; %s->%s",
                    offset, obj->data.ident.name, member);
            }
        }
        else {
            // struct.member
            if (obj->type == N_IDENT) {
                Local* entry = symtab_lookup_entry(&cg->symtab, obj->data.ident.name);
                if (entry) {
                    int base = entry->is_param ? entry->offset : -entry->offset;
                    char* sign = entry->is_param ? "+" : "-";
                    if (mem_size == 1) {
                        emit(cg, "    movzx eax, byte [ebp %s %d + %d]  ; %s.%s",
                            sign, entry->offset, offset, obj->data.ident.name, member);
                    }
                    else if (mem_size == 2) {
                        emit(cg, "    movzx eax, word [ebp %s %d + %d]  ; %s.%s",
                            sign, entry->offset, offset, obj->data.ident.name, member);
                    }
                    else {
                        emit(cg, "    mov eax, [ebp %s %d + %d]  ; %s.%s",
                            sign, entry->offset, offset, obj->data.ident.name, member);
                    }
                }
                else {
                    if (mem_size == 1) {
                        emit(cg, "    movzx eax, byte [%s + %d]  ; %s.%s",
                            obj->data.ident.name, offset, obj->data.ident.name, member);
                    }
                    else {
                        emit(cg, "    mov eax, [%s + %d]  ; %s.%s",
                            obj->data.ident.name, offset, obj->data.ident.name, member);
                    }
                }
            }
        }
        break;
    }

    case N_OPERATOR:
    {
        Tokens op = expr->data.op.op;
        AST* left = expr->data.op.left;
        AST* right = expr->data.op.right;

        // Handle assignment to member access
        if (op == TOKEN_ASSIGN && left->type == N_MEMBER_ACCESS) {
            codegen_expression(cg, right);
            emit(cg, "    push eax  ; Save value");
            codegen_lvalue_address(cg, left);
            emit(cg, "    mov ebx, eax  ; Address in ebx");
            emit(cg, "    pop eax  ; Restore value");
            emit(cg, "    mov [ebx], eax  ; Store");
            break;
        }

        // Handle assignment to array element
        if (op == TOKEN_ASSIGN && left->type == N_ARRAY_ACCESS) {
            codegen_expression(cg, right);
            emit(cg, "    push eax  ; Save value");
            codegen_lvalue_address(cg, left);
            emit(cg, "    mov ebx, eax  ; Element address in ebx");
            emit(cg, "    pop eax  ; Restore value");

            int element_size = 1;
            if (left->data.array_access.array->type == N_IDENT) {
                element_size = get_element_size_for_var(cg,
                    left->data.array_access.array->data.ident.name);
            }
            emit_store_sized(cg, element_size, "ebx");
            break;
        }

        // Handle assignment to dereferenced pointer
        if (op == TOKEN_ASSIGN && left->type == N_UNARY &&
            left->data.unary.op == TOKEN_STAR) {
            codegen_expression(cg, right);
            emit(cg, "    push eax  ; Save value");
            codegen_expression(cg, left->data.unary.operand);
            emit(cg, "    mov ebx, eax  ; Address in ebx");
            emit(cg, "    pop eax  ; Restore value");
            emit(cg, "    mov [ebx], eax  ; Store through pointer");
            break;
        }

        // Handle compound assignment operators
        if (op == TOKEN_PLUS_ASSIGN || op == TOKEN_MINUS_ASSIGN ||
            op == TOKEN_STAR_ASSIGN || op == TOKEN_SLASH_ASSIGN) {

            codegen_lvalue_address(cg, left);
            emit(cg, "    push eax  ; Save address");
            emit(cg, "    mov eax, [eax]  ; Load current value");
            emit(cg, "    push eax  ; Save current value");

            codegen_expression(cg, right);
            emit(cg, "    mov ebx, eax  ; Right value in ebx");
            emit(cg, "    pop eax  ; Restore current value");

            switch (op) {
            case TOKEN_PLUS_ASSIGN:
                emit(cg, "    add eax, ebx");
                break;
            case TOKEN_MINUS_ASSIGN:
                emit(cg, "    sub eax, ebx");
                break;
            case TOKEN_STAR_ASSIGN:
                emit(cg, "    imul eax, ebx");
                break;
            case TOKEN_SLASH_ASSIGN:
                emit(cg, "    cdq");
                emit(cg, "    idiv ebx");
                break;
            default:
                break;
            }

            emit(cg, "    pop ebx  ; Restore address");
            emit(cg, "    mov [ebx], eax  ; Store result");
            break;
        }

        // Regular binary operators
        codegen_expression(cg, left);
        emit(cg, "    push eax         ; Save left operand");
        codegen_expression(cg, right);
        emit(cg, "    mov ebx, eax     ; Right in ebx");
        emit(cg, "    pop eax          ; Left in eax");

        switch (op) {
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
            emit(cg, "    cdq");
            emit(cg, "    idiv ebx");
            break;
        case TOKEN_PERCENT:
            emit(cg, "    cdq");
            emit(cg, "    idiv ebx");
            emit(cg, "    mov eax, edx  ; Remainder");
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
            emit(cg, "    or eax, ebx");
            emit(cg, "    setne al");
            emit(cg, "    movzx eax, al");
            break;
        case TOKEN_ASSIGN:
            emit(cg, "    mov [eax], ebx");
            emit(cg, "    mov eax, ebx");
            break;
        default:
            emit(cg, "    ; Unknown operator %d", op);
            break;
        }
        break;
    }

    case N_CALL:
    {
        emit(cg, "    ; Call %s", expr->data.call.name);

        // Push arguments right to left (cdecl)
        for (int i = (int)expr->data.call.arg_count - 1; i >= 0; i--) {
            codegen_expression(cg, expr->data.call.args[i]);
            emit(cg, "    push eax         ; Arg %d", i);
        }

        emit(cg, "    call %s", expr->data.call.name);

        // Caller cleans stack
        if (expr->data.call.arg_count > 0) {
            emit(cg, "    add esp, %d      ; Clean %zu args",
                (int)expr->data.call.arg_count * 4, expr->data.call.arg_count);
        }
        break;
    }

    case N_ASSIGN:
    {
        Local* entry = symtab_lookup_entry(&cg->symtab, expr->data.assign.var_name);
        codegen_expression(cg, expr->data.assign.value);

        if (entry) {
            if (entry->is_param) {
                emit(cg, "    mov [ebp + %d], eax  ; Param %s",
                    entry->offset, expr->data.assign.var_name);
            }
            else {
                emit(cg, "    mov [ebp - %d], eax  ; Local %s",
                    entry->offset, expr->data.assign.var_name);
            }
        }
        else {
            emit(cg, "    mov [%s], eax  ; Global %s",
                expr->data.assign.var_name, expr->data.assign.var_name);
        }
        break;
    }

    case N_ARRAY_ACCESS:
    {
        AST* arr = expr->data.array_access.array;

        int element_size = 1;
        if (arr->type == N_IDENT) {
            element_size = get_element_size_for_var(cg, arr->data.ident.name);
        }

        codegen_lvalue_address(cg, expr);
        emit_load_sized(cg, element_size);
        break;
    }

    case N_CAST:
    {
        codegen_expression(cg, expr->data.cast.expr);
        // Type casts in C don't change the bits, just the interpretation
        // For now, just pass through
        break;
    }

    case N_UNARY:
    {
        Tokens op = expr->data.unary.op;
        AST* operand = expr->data.unary.operand;

        // Address-of operator
        if (op == TOKEN_AMPERSAND) {
            codegen_lvalue_address(cg, operand);
            break;
        }

        // Prefix increment/decrement
        if (op == TOKEN_PLUS_PLUS || op == TOKEN_MINUS_MINUS) {
            codegen_lvalue_address(cg, operand);
            emit(cg, "    mov ebx, eax  ; Save address");
            emit(cg, "    mov eax, [ebx]  ; Load value");
            if (op == TOKEN_PLUS_PLUS) {
                emit(cg, "    inc eax  ; Prefix increment");
            }
            else {
                emit(cg, "    dec eax  ; Prefix decrement");
            }
            emit(cg, "    mov [ebx], eax  ; Store back");
            break;
        }

        // Other unary operators
        codegen_expression(cg, operand);

        switch (op) {
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
            emit(cg, "    mov eax, [eax]  ; Dereference");
            break;
        default:
            emit(cg, "    ; Unknown unary %d", op);
            break;
        }
        break;
    }

    case N_TERNARY:
    {
        int lbl_false = codegen_new_label(cg);
        int lbl_end = codegen_new_label(cg);

        codegen_expression(cg, expr->data.ternary.condition);
        emit(cg, "    test eax, eax");
        emit(cg, "    jz .L%d", lbl_false);

        codegen_expression(cg, expr->data.ternary.true_expr);
        emit(cg, "    jmp .L%d", lbl_end);

        emit(cg, ".L%d:", lbl_false);
        codegen_expression(cg, expr->data.ternary.false_expr);

        emit(cg, ".L%d:", lbl_end);
        break;
    }

    case N_SIZEOF:
    {
        // Simplified sizeof - returns 4 for most things
        // In a full implementation, we'd compute actual type sizes
        AST* target = expr->data.sizeof_expr.expr;
        int size = 4;

        if (target->type == N_IDENT) {
            // Check if it's a type name
            const char* name = target->data.ident.name;
            if (strcmp(name, "char") == 0) size = 1;
            else if (strcmp(name, "short") == 0) size = 2;
            else if (strcmp(name, "int") == 0) size = 4;
            else if (strcmp(name, "long") == 0) size = 4;
            else if (strncmp(name, "struct ", 7) == 0) {
                StructInfo* info = codegen_find_struct(cg, name + 7);
                if (info) size = info->total_size;
            }
        }

        emit(cg, "    mov eax, %d  ; sizeof", size);
        break;
    }

    default:
        emit(cg, "    ; TODO: Expression type %d", expr->type);
        emit(cg, "    xor eax, eax");
        break;
    }
}

/* ====================== Statement Code Generation ====================== */

void codegen_statement(CodeGen* cg, AST* stmt) {
    if (!stmt) return;

    switch (stmt->type) {
    case N_DECL:
    {
        int is_array = (stmt->data.decl.array_size != NULL);
        int array_count = 0;

        if (is_array && stmt->data.decl.array_size->type == N_INTLIT) {
            array_count = stmt->data.decl.array_size->data.int_lit.value;
        }

        int offset = symtab_add_typed(&cg->symtab,
            stmt->data.decl.name,
            stmt->data.decl.type,
            stmt->data.decl.pointer_level,
            is_array,
            array_count);

        emit(cg, "    ; Declare %s at [ebp - %d]", stmt->data.decl.name, offset);

        if (stmt->data.decl.init_value) {
            codegen_expression(cg, stmt->data.decl.init_value);
            emit(cg, "    mov [ebp - %d], eax", offset);
        }
        break;
    }

    case N_RETURN:
        emit(cg, "    ; Return");
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
        int lbl_else = codegen_new_label(cg);
        int lbl_end = codegen_new_label(cg);

        emit(cg, "    ; If");
        codegen_expression(cg, stmt->data.if_stmt.condition);
        emit(cg, "    test eax, eax");

        if (stmt->data.if_stmt.else_block) {
            emit(cg, "    jz .L%d", lbl_else);
            codegen_statement(cg, stmt->data.if_stmt.then_block);
            emit(cg, "    jmp .L%d", lbl_end);
            emit(cg, ".L%d:", lbl_else);
            codegen_statement(cg, stmt->data.if_stmt.else_block);
            emit(cg, ".L%d:", lbl_end);
        }
        else {
            emit(cg, "    jz .L%d", lbl_end);
            codegen_statement(cg, stmt->data.if_stmt.then_block);
            emit(cg, ".L%d:", lbl_end);
        }
        break;
    }

    case N_WHILE:
    {
        int lbl_start = codegen_new_label(cg);
        int lbl_end = codegen_new_label(cg);

        push_loop(lbl_end, lbl_start);

        emit(cg, ".L%d:  ; While start", lbl_start);
        codegen_expression(cg, stmt->data.while_stmt.condition);
        emit(cg, "    test eax, eax");
        emit(cg, "    jz .L%d", lbl_end);

        codegen_statement(cg, stmt->data.while_stmt.body);

        emit(cg, "    jmp .L%d", lbl_start);
        emit(cg, ".L%d:  ; While end", lbl_end);

        pop_loop();
        break;
    }

    case N_FOR:
    {
        int lbl_start = codegen_new_label(cg);
        int lbl_cont = codegen_new_label(cg);
        int lbl_end = codegen_new_label(cg);

        push_loop(lbl_end, lbl_cont);

        emit(cg, "    ; For loop");
        if (stmt->data.for_stmt.init) {
            codegen_statement(cg, stmt->data.for_stmt.init);
        }

        emit(cg, ".L%d:  ; For condition", lbl_start);
        if (stmt->data.for_stmt.condition) {
            codegen_expression(cg, stmt->data.for_stmt.condition);
            emit(cg, "    test eax, eax");
            emit(cg, "    jz .L%d", lbl_end);
        }

        codegen_statement(cg, stmt->data.for_stmt.body);

        emit(cg, ".L%d:  ; For increment", lbl_cont);
        if (stmt->data.for_stmt.increment) {
            codegen_expression(cg, stmt->data.for_stmt.increment);
        }

        emit(cg, "    jmp .L%d", lbl_start);
        emit(cg, ".L%d:  ; For end", lbl_end);

        pop_loop();
        break;
    }

    case N_BREAK:
    {
        int lbl = get_break_label();
        if (lbl >= 0) {
            emit(cg, "    jmp .L%d  ; Break", lbl);
        }
        else {
            emit(cg, "    ; ERROR: Break outside loop");
        }
        break;
    }

    case N_CONTINUE:
    {
        int lbl = get_continue_label();
        if (lbl >= 0) {
            emit(cg, "    jmp .L%d  ; Continue", lbl);
        }
        else {
            emit(cg, "    ; ERROR: Continue outside loop");
        }
        break;
    }

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

    case N_ASSIGN:
    case N_CALL:
    case N_OPERATOR:
        codegen_expression(cg, stmt);
        break;

    default:
        codegen_expression(cg, stmt);
        break;
    }
}

/* ====================== Function Code Generation ====================== */

static int is_runtime_function(const char* name) {
    // VGA functions (runtime provides these)
    if (strcmp(name, "print_char") == 0) return 1;
    if (strcmp(name, "print_string") == 0) return 1;
    if (strcmp(name, "print_hex") == 0) return 1;
    if (strcmp(name, "print_int") == 0) return 1;
    if (strcmp(name, "set_cursor") == 0) return 1;
    if (strcmp(name, "get_cursor") == 0) return 1;
    if (strcmp(name, "newline") == 0) return 1;
    if (strcmp(name, "clear_screen") == 0) return 1;

    // Port I/O
    if (strcmp(name, "outb") == 0) return 1;
    if (strcmp(name, "inb") == 0) return 1;
    if (strcmp(name, "outw") == 0) return 1;
    if (strcmp(name, "inw") == 0) return 1;
    if (strcmp(name, "outl") == 0) return 1;
    if (strcmp(name, "inl") == 0) return 1;

    // Interrupt control
    if (strcmp(name, "disable_interrupts") == 0) return 1;
    if (strcmp(name, "enable_interrupts") == 0) return 1;
    if (strcmp(name, "cli_func") == 0) return 1;
    if (strcmp(name, "sti_func") == 0) return 1;
    if (strcmp(name, "halt") == 0) return 1;
    if (strcmp(name, "read_cr0") == 0) return 1;
    if (strcmp(name, "write_cr0") == 0) return 1;
    if (strcmp(name, "read_cr3") == 0) return 1;
    if (strcmp(name, "write_cr3") == 0) return 1;

    // Memory operations
    if (strcmp(name, "memcpy") == 0) return 1;
    if (strcmp(name, "memset") == 0) return 1;
    if (strcmp(name, "memcmp") == 0) return 1;

    return 0;
}


void codegen_function(CodeGen* cg, AST* func) {
    codegen_function_correct(cg, func);
}

void codegen_function_correct(CodeGen* cg, AST* func) {
    if (!func || func->type != N_FUNCTION) return;
    if (!func->data.function.body) return;  // Forward declaration

    // ADD THESE 4 LINES:
    if (is_runtime_function(func->data.function.name)) {
        return;  // Skip runtime functions
    }

    // Set global for struct lookup in symtab_add_typed
    g_current_cg = cg;

    // Reset symbol table for this function
    symtab_free(&cg->symtab);
    symtab_init(&cg->symtab);
    loop_depth = 0;

    emit(cg, "");
    emit(cg, "; ========== Function: %s ==========", func->data.function.name);
    emit(cg, "%s:", func->data.function.name);
    emit(cg, "    push ebp");
    emit(cg, "    mov ebp, esp");

    // Register parameters (cdecl: pushed right to left, so first param at ebp+8)
    for (size_t i = 0; i < func->data.function.param_count; i++) {
        AST* param = func->data.function.params[i];
        if (param->type == N_DECL) {
            int stack_pos = 8 + ((int)i * 4);
            symtab_add_param_typed(&cg->symtab,
                param->data.decl.name,
                stack_pos,
                param->data.decl.type,
                param->data.decl.pointer_level);
            emit(cg, "    ; Param %zu: %s at [ebp + %d]", i, param->data.decl.name, stack_pos);
        }
    }

    // Reserve stack space (we'll use a fixed amount for simplicity)
    emit(cg, "    sub esp, 512     ; Reserve stack");

    // Generate body
    codegen_statement(cg, func->data.function.body);

    // Epilogue (jumped to by return statements)
    emit(cg, ".epilogue:");
    emit(cg, "    mov esp, ebp");
    emit(cg, "    pop ebp");
    emit(cg, "    ret");
}

/* ====================== Program Code Generation ====================== */

void codegen_program(CodeGen* cg, AST* program) {
    if (!program || program->type != N_PROGRAM) return;

    // First pass: register structs and collect global info
    for (size_t i = 0; i < program->data.program.global_count; i++) {
        AST* global = program->data.program.globals[i];

        if (global->type == N_STRUCT_DECL) {
            codegen_register_struct(cg, global);
        }
        else if (global->type == N_DECL) {
            int is_array = (global->data.decl.array_size != NULL);
            int array_size = 0;
            if (is_array && global->data.decl.array_size->type == N_INTLIT) {
                array_size = global->data.decl.array_size->data.int_lit.value;
            }
            globtab_add(&cg->globtab,
                global->data.decl.name,
                global->data.decl.type,
                global->data.decl.pointer_level,
                is_array,
                array_size);
        }
    }

    // Emit prologue
    codegen_baremetal_prologue(cg);

    // Emit functions
    for (size_t i = 0; i < program->data.program.func_count; i++) {
        AST* func = program->data.program.functions[i];
        if (func->data.function.body != NULL) {
            codegen_function_correct(cg, func);
        }
    }

    // Emit runtime support
    codegen_emit_runtime(cg);
    codegen_emit_port_io_runtime(cg);
    codegen_emit_interrupt_runtime(cg);
    codegen_emit_memory_runtime(cg);

    // Emit data section
    emit(cg, "");
    emit(cg, "section .data");
    emit(cg, "align 4");

    // String literals
    codegen_emit_strings(cg);

    // Global variables
    for (size_t i = 0; i < program->data.program.global_count; i++) {
        AST* global = program->data.program.globals[i];
        if (global->type == N_DECL) {
            if (global->data.decl.array_size &&
                global->data.decl.array_size->type == N_INTLIT) {
                int arr_size = global->data.decl.array_size->data.int_lit.value;
                int elem_size = get_base_type_size(global->data.decl.type);
                int total = arr_size * elem_size;
                emit(cg, "%s: times %d db 0  ; array[%d]",
                    global->data.decl.name, total, arr_size);
            }
            else {
                int init_val = 0;
                if (global->data.decl.init_value) {
                    if (global->data.decl.init_value->type == N_INTLIT) {
                        init_val = global->data.decl.init_value->data.int_lit.value;
                    }
                    else if (global->data.decl.init_value->type == N_CAST &&
                        global->data.decl.init_value->data.cast.expr->type == N_INTLIT) {
                        init_val = global->data.decl.init_value->data.cast.expr->data.int_lit.value;
                    }
                }
                emit(cg, "%s dd %d", global->data.decl.name, init_val);
            }
        }
    }

    // Runtime variables
    emit(cg, "vga_cursor dd 0");
    emit(cg, "");
    emit(cg, "; End of generated code");
}