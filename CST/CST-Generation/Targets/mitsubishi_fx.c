/*
 * Mitsubishi FX1S backend for CST.
 *
 * Lowers C to Mitsubishi Instruction List (IL/STL) — the native language of
 * the FX1S/FX1N/FX2N family. Output is a single .txt file the user pastes
 * into GX Developer's Instruction List editor (or, eventually, the IDE
 * downloads directly via the FX serial protocol).
 *
 * I/O binding is via top-level pragma comments the user puts at the top of
 * their source file:
 *
 *     // CST_IO: X0 -> PumpIN
 *     // CST_IO: Y0 -> XV1_open
 *
 * Variables named in those pragmas are wired directly to the X/Y pins of
 * the PLC. All other variables map to D registers (16-bit data) or M bits
 * (boolean memory) at codegen time.
 *
 * Constraints (FX1S):
 *   - 2K program steps total
 *   - 128 D registers (D0..D127)
 *   - 384 M bits (M0..M383)
 *   - 64 timers (T0..T63)
 *   - 16-bit signed integer arithmetic only — no float
 *   - No recursion (no stack)
 *   - Functions lower to subroutines (CALL P0 / SRET)
 *
 * THIS FILE = first cut. Handles: I/O pragma, globals, simple assignments,
 * comparisons, if/else, basic math. Functions, while/for, runtime shims
 * land in subsequent passes.
 */

#include "../../Includes.h"
#include "../../Parser/Parser.h"
#include "target.h"

/* ==================== I/O Pragma Table ==================== */

#define MAX_IO_BINDINGS 32

typedef struct {
    char  pin_kind;        /* 'X' = input, 'Y' = output */
    int   pin_num;         /* 0..15 */
    char* name;            /* C identifier */
} IoBinding;

static IoBinding g_io[MAX_IO_BINDINGS];
static size_t    g_io_count = 0;

static void io_clear(void) {
    for (size_t i = 0; i < g_io_count; i++) free(g_io[i].name);
    g_io_count = 0;
}

static void io_add(char kind, int num, const char* name) {
    if (g_io_count >= MAX_IO_BINDINGS) return;
    g_io[g_io_count].pin_kind = kind;
    g_io[g_io_count].pin_num  = num;
    g_io[g_io_count].name     = _strdup(name);
    g_io_count++;
}

/* Lookup: name → "X0" / "Y3" / NULL if not pin-bound. Returns static buf. */
static const char* io_lookup(const char* name) {
    static char buf[8];
    if (!name) return NULL;
    for (size_t i = 0; i < g_io_count; i++) {
        if (strcmp(g_io[i].name, name) == 0) {
            snprintf(buf, sizeof(buf), "%c%d", g_io[i].pin_kind, g_io[i].pin_num);
            return buf;
        }
    }
    return NULL;
}

/* Parse the I/O pragmas out of the source. The preprocessor strips C
 * comments before we see anything, but it DOESN'T strip line comments.
 * The pragmas live inside `// CST_IO: ...` lines — we re-scan the
 * preprocessed source for them. The compiler driver passes the raw source
 * via a global hook (see g_fx_source_hint below). */

static const char* g_fx_source_hint = NULL;   /* set by Main.c before codegen */

void cst_fx_set_source(const char* src) { g_fx_source_hint = src; }

static void io_parse_from_source(const char* src) {
    io_clear();
    if (!src) return;
    const char* p = src;
    int in_block = 0;
    while (*p) {
        /* Skip C block comments. `// CST_IO:` lines that happen to live inside
         * a header's documentation block must NOT bind I/O. */
        if (in_block) {
            if (p[0] == '*' && p[1] == '/') { in_block = 0; p += 2; continue; }
            p++; continue;
        }
        if (p[0] == '/' && p[1] == '*') { in_block = 1; p += 2; continue; }

        /* find next "//" line comment */
        if (p[0] == '/' && p[1] == '/') {
            const char* line = p + 2;
            while (*line == ' ' || *line == '\t') line++;
            if (strncmp(line, "CST_IO:", 7) == 0) {
                line += 7;
                while (*line == ' ' || *line == '\t') line++;
                /* expect: X<n> -> name  OR  Y<n> -> name */
                char kind = *line;
                if (kind == 'X' || kind == 'Y' || kind == 'M') {
                    line++;
                    int num = 0;
                    while (isdigit((unsigned char)*line)) {
                        num = num * 10 + (*line - '0');
                        line++;
                    }
                    while (*line == ' ' || *line == '\t') line++;
                    if (line[0] == '-' && line[1] == '>') {
                        line += 2;
                        while (*line == ' ' || *line == '\t') line++;
                        char name[64];
                        size_t n = 0;
                        while ((isalnum((unsigned char)*line) || *line == '_') &&
                               n < sizeof(name) - 1) {
                            name[n++] = *line++;
                        }
                        name[n] = '\0';
                        if (n > 0) io_add(kind, num, name);
                    }
                }
            }
            /* skip rest of line */
            while (*p && *p != '\n') p++;
        }
        if (*p) p++;
    }
}

/* ==================== Struct Definitions ==================== */

/* Member layout for a struct type: name → list of field names in declared order.
 * Field i lives at offset i within the instance's D-block. (16-bit fields only;
 * we don't yet support struct-of-struct, arrays-in-struct, or sized fields.) */

#define MAX_STRUCT_DEFS 32
#define MAX_MEMBERS_PER_STRUCT 32

typedef struct {
    char* type_name;
    char* member_names[MAX_MEMBERS_PER_STRUCT];
    int   member_count;
} FxStructDef;

static FxStructDef g_struct_defs[MAX_STRUCT_DEFS];
static size_t      g_struct_def_count = 0;

static void struct_defs_clear(void) {
    for (size_t i = 0; i < g_struct_def_count; i++) {
        free(g_struct_defs[i].type_name);
        for (int j = 0; j < g_struct_defs[i].member_count; j++)
            free(g_struct_defs[i].member_names[j]);
    }
    g_struct_def_count = 0;
}

static const FxStructDef* struct_def_find(const char* type_name) {
    if (!type_name) return NULL;
    /* Strip leading "struct " for search */
    if (strncmp(type_name, "struct ", 7) == 0) type_name += 7;
    for (size_t i = 0; i < g_struct_def_count; i++)
        if (strcmp(g_struct_defs[i].type_name, type_name) == 0)
            return &g_struct_defs[i];
    return NULL;
}

static int struct_def_member_offset(const FxStructDef* def, const char* member) {
    if (!def) return -1;
    for (int i = 0; i < def->member_count; i++)
        if (strcmp(def->member_names[i], member) == 0) return i;
    return -1;
}

static void register_struct_defs(AST* root) {
    struct_defs_clear();
    if (!root || root->type != N_PROGRAM) return;
    ProgramNode* prog = &root->data.program;
    for (size_t i = 0; i < prog->global_count; i++) {
        AST* g = prog->globals[i];
        if (!g || g->type != N_STRUCT_DECL) continue;
        if (!g->data.struct_decl.name) continue;
        if (g_struct_def_count >= MAX_STRUCT_DEFS) break;

        FxStructDef* def = &g_struct_defs[g_struct_def_count++];
        def->type_name = _strdup(g->data.struct_decl.name);
        def->member_count = 0;

        for (size_t k = 0; k < g->data.struct_decl.member_count &&
                           def->member_count < MAX_MEMBERS_PER_STRUCT; k++) {
            AST* m = g->data.struct_decl.members[k];
            if (!m || m->type != N_DECL || !m->data.decl.name) continue;
            def->member_names[def->member_count++] = _strdup(m->data.decl.name);
        }
    }
}

/* ==================== Struct Instance Allocator ==================== */

#define MAX_STRUCT_INSTS 128

typedef struct {
    char* var_name;
    char* type_name;
    int   base_d;
    int   member_count;
} FxStructInst;

static FxStructInst g_struct_insts[MAX_STRUCT_INSTS];
static size_t       g_struct_inst_count = 0;

static void struct_insts_clear(void) {
    for (size_t i = 0; i < g_struct_inst_count; i++) {
        free(g_struct_insts[i].var_name);
        free(g_struct_insts[i].type_name);
    }
    g_struct_inst_count = 0;
}

static FxStructInst* struct_inst_find(const char* var_name) {
    if (!var_name) return NULL;
    for (size_t i = 0; i < g_struct_inst_count; i++)
        if (strcmp(g_struct_insts[i].var_name, var_name) == 0)
            return &g_struct_insts[i];
    return NULL;
}

/* ==================== Symbol Allocator ==================== */

/* Maps C identifiers to FX register addresses.
 * - int globals/locals  → D<n> (D0..D127)
 * - bool variables      → M<n> (M0..M383)
 * - timer instances     → T<n>
 * - I/O-bound names     → handled by io_lookup, not in this table
 *
 * Functions are P-labels (P0..P63).
 */

typedef struct {
    char* name;
    char  kind;     /* 'D', 'M', 'T', 'P' */
    int   addr;
} SymEntry;

#define MAX_SYMS 256
static SymEntry g_syms[MAX_SYMS];
static size_t   g_sym_count = 0;

static int g_next_d = 0;
static int g_next_m = 0;
static int g_next_t = 0;
static int g_next_p = 0;

static void syms_reset(void) {
    for (size_t i = 0; i < g_sym_count; i++) free(g_syms[i].name);
    g_sym_count = 0;
    g_next_d = g_next_m = g_next_t = g_next_p = 0;
    struct_insts_clear();
}

/* Allocate a D-register block of `n` consecutive registers for `var_name`
 * of struct type `type_name`. Returns the base D-register address. */
static int alloc_struct_inst(const char* var_name, const char* type_name, int n) {
    if (g_struct_inst_count >= MAX_STRUCT_INSTS) return -1;
    int base = g_next_d;
    g_next_d += n;
    FxStructInst* inst = &g_struct_insts[g_struct_inst_count++];
    inst->var_name = _strdup(var_name);
    inst->type_name = _strdup(type_name);
    inst->base_d = base;
    inst->member_count = n;
    return base;
}

/* ==================== Function Lookup ==================== */

static AST* g_program_root = NULL;

static AST* find_function(const char* name) {
    if (!name || !g_program_root || g_program_root->type != N_PROGRAM) return NULL;
    ProgramNode* prog = &g_program_root->data.program;
    for (size_t i = 0; i < prog->func_count; i++) {
        AST* f = prog->functions[i];
        if (f && f->type == N_FUNCTION && f->data.function.name &&
            strcmp(f->data.function.name, name) == 0)
            return f;
    }
    return NULL;
}

/* ==================== Parameter Substitution (for inlining) ==================== */

#define MAX_SUBSTS 32

typedef enum {
    SUBST_ALIAS_VAR,    /* param refers to a caller-scope variable (struct ptr → &ident) */
    SUBST_INT_CONST,    /* param is a known constant int */
    SUBST_INT_DREG      /* param is in a specific D register */
} SubstKind;

typedef struct {
    char* param_name;
    SubstKind kind;
    char* alias_name;   /* for SUBST_ALIAS_VAR */
    int   int_value;    /* for SUBST_INT_CONST */
    int   d_reg;        /* for SUBST_INT_DREG */
} FxSubst;

static FxSubst g_substs[MAX_SUBSTS];
static int     g_subst_count = 0;

static void subst_clear(void) {
    for (int i = 0; i < g_subst_count; i++) {
        free(g_substs[i].param_name);
        free(g_substs[i].alias_name);
    }
    g_subst_count = 0;
}

static int subst_save_count(void) { return g_subst_count; }

static void subst_restore_count(int n) {
    while (g_subst_count > n) {
        g_subst_count--;
        free(g_substs[g_subst_count].param_name);
        free(g_substs[g_subst_count].alias_name);
        g_substs[g_subst_count].param_name = NULL;
        g_substs[g_subst_count].alias_name = NULL;
    }
}

static FxSubst* subst_find(const char* name) {
    if (!name) return NULL;
    /* Walk backwards so the most recent binding wins (handles re-entrant inlines). */
    for (int i = g_subst_count - 1; i >= 0; i--)
        if (strcmp(g_substs[i].param_name, name) == 0) return &g_substs[i];
    return NULL;
}

static void subst_push_alias(const char* param, const char* alias) {
    if (g_subst_count >= MAX_SUBSTS) return;
    g_substs[g_subst_count].param_name = _strdup(param);
    g_substs[g_subst_count].kind = SUBST_ALIAS_VAR;
    g_substs[g_subst_count].alias_name = _strdup(alias);
    g_substs[g_subst_count].int_value = 0;
    g_substs[g_subst_count].d_reg = -1;
    g_subst_count++;
}

static void subst_push_const(const char* param, int value) {
    if (g_subst_count >= MAX_SUBSTS) return;
    g_substs[g_subst_count].param_name = _strdup(param);
    g_substs[g_subst_count].kind = SUBST_INT_CONST;
    g_substs[g_subst_count].alias_name = NULL;
    g_substs[g_subst_count].int_value = value;
    g_substs[g_subst_count].d_reg = -1;
    g_subst_count++;
}

static void subst_push_dreg(const char* param, int d_reg) {
    if (g_subst_count >= MAX_SUBSTS) return;
    g_substs[g_subst_count].param_name = _strdup(param);
    g_substs[g_subst_count].kind = SUBST_INT_DREG;
    g_substs[g_subst_count].alias_name = NULL;
    g_substs[g_subst_count].int_value = 0;
    g_substs[g_subst_count].d_reg = d_reg;
    g_subst_count++;
}

/* ==================== Inline-Return Context ==================== */

/* While inlining a function, `return X;` lowers to:
 *   MOV X to g_inline_result_d
 *   CJ to P<g_inline_end_label>
 * After the body, we emit P<g_inline_end_label>: as the join point. */
static int g_inline_result_d  = -1;
static int g_inline_end_label = -1;

static SymEntry* sym_find(const char* name) {
    if (!name) return NULL;
    for (size_t i = 0; i < g_sym_count; i++)
        if (strcmp(g_syms[i].name, name) == 0) return &g_syms[i];
    return NULL;
}

static SymEntry* sym_add(const char* name, char kind, int addr) {
    if (g_sym_count >= MAX_SYMS) return NULL;
    g_syms[g_sym_count].name = _strdup(name);
    g_syms[g_sym_count].kind = kind;
    g_syms[g_sym_count].addr = addr;
    return &g_syms[g_sym_count++];
}

/* Allocate a D register for `name` if not already mapped. Returns address. */
static int alloc_d(const char* name) {
    SymEntry* s = sym_find(name);
    if (s && s->kind == 'D') return s->addr;
    int addr = g_next_d++;
    sym_add(name, 'D', addr);
    return addr;
}

/* For runtime types (cst_time_t, cst_redge_t, cst_fedge_t) the user declares
 * an instance variable which we map to a T or M index. These tables track
 * which C var maps to which FX runtime resource. */
typedef struct {
    char* var_name;
    int   t_index;     /* T0..T63 */
} FxTimerInst;

typedef struct {
    char* var_name;
    int   m_index;     /* prev-state M bit, used for edge detect */
    int   q_m_index;   /* M bit set TRUE for one scan on edge */
    int   is_falling;
} FxEdgeInst;

typedef struct {
    char* var_name;
    int   c_index;     /* C0..C15 (FX1S 16-bit counters) */
    int   is_down;
} FxCounterInst;

#define MAX_TIMER_INSTS   32
#define MAX_EDGE_INSTS    32
#define MAX_COUNTER_INSTS 16
static FxTimerInst   g_timer_insts[MAX_TIMER_INSTS];
static size_t        g_timer_inst_count = 0;
static FxEdgeInst    g_edge_insts[MAX_EDGE_INSTS];
static size_t        g_edge_inst_count = 0;
static FxCounterInst g_counter_insts[MAX_COUNTER_INSTS];
static size_t        g_counter_inst_count = 0;
static int           g_next_c = 0;

static void runtime_insts_clear(void) {
    for (size_t i = 0; i < g_timer_inst_count; i++)   free(g_timer_insts[i].var_name);
    for (size_t i = 0; i < g_edge_inst_count;  i++)   free(g_edge_insts[i].var_name);
    for (size_t i = 0; i < g_counter_inst_count; i++) free(g_counter_insts[i].var_name);
    g_timer_inst_count = 0;
    g_edge_inst_count = 0;
    g_counter_inst_count = 0;
    g_next_c = 0;
}

static FxCounterInst* counter_inst_find(const char* name) {
    if (!name) return NULL;
    for (size_t i = 0; i < g_counter_inst_count; i++)
        if (strcmp(g_counter_insts[i].var_name, name) == 0)
            return &g_counter_insts[i];
    return NULL;
}

static FxCounterInst* counter_inst_alloc(const char* name, int is_down) {
    if (g_counter_inst_count >= MAX_COUNTER_INSTS) return NULL;
    if (g_next_c >= 16) return NULL;
    FxCounterInst* ci = &g_counter_insts[g_counter_inst_count++];
    ci->var_name = _strdup(name);
    ci->c_index = g_next_c++;
    ci->is_down = is_down;
    return ci;
}

static FxTimerInst* timer_inst_find(const char* name) {
    if (!name) return NULL;
    for (size_t i = 0; i < g_timer_inst_count; i++)
        if (strcmp(g_timer_insts[i].var_name, name) == 0)
            return &g_timer_insts[i];
    return NULL;
}

static FxTimerInst* timer_inst_alloc(const char* name) {
    if (g_timer_inst_count >= MAX_TIMER_INSTS) return NULL;
    if (g_next_t >= 64) return NULL;
    FxTimerInst* t = &g_timer_insts[g_timer_inst_count++];
    t->var_name = _strdup(name);
    t->t_index = g_next_t++;
    return t;
}

static FxEdgeInst* edge_inst_find(const char* name) {
    if (!name) return NULL;
    for (size_t i = 0; i < g_edge_inst_count; i++)
        if (strcmp(g_edge_insts[i].var_name, name) == 0)
            return &g_edge_insts[i];
    return NULL;
}

static FxEdgeInst* edge_inst_alloc(const char* name, int is_falling) {
    if (g_edge_inst_count >= MAX_EDGE_INSTS) return NULL;
    FxEdgeInst* e = &g_edge_insts[g_edge_inst_count++];
    e->var_name = _strdup(name);
    e->m_index = g_next_m++;     /* prev-state */
    e->q_m_index = g_next_m++;   /* fired-this-scan */
    e->is_falling = is_falling;
    return e;
}

/* Render a name as an FX operand: "X0" / "Y3" / "D5" / "M2" / "K42" (const subst).
 * Honors active parameter substitutions from inlined function calls. */
static const char* render_operand(const char* name) {
    static char buf[16];

    /* Active substitution? (param name from an inlined function) */
    FxSubst* s = subst_find(name);
    if (s) {
        switch (s->kind) {
            case SUBST_INT_CONST:
                snprintf(buf, sizeof(buf), "K%d", s->int_value);
                return buf;
            case SUBST_INT_DREG:
                snprintf(buf, sizeof(buf), "D%d", s->d_reg);
                return buf;
            case SUBST_ALIAS_VAR:
                /* Param refers to caller's variable — recurse with alias. */
                return render_operand(s->alias_name);
        }
    }

    /* I/O-bound names take precedence over D-register allocation */
    const char* io = io_lookup(name);
    if (io) {
        strncpy(buf, io, sizeof(buf) - 1);
        buf[sizeof(buf) - 1] = '\0';
        return buf;
    }

    /* Otherwise allocate (or look up) a D register */
    int addr = alloc_d(name);
    snprintf(buf, sizeof(buf), "D%d", addr);
    return buf;
}

/* For N_MEMBER_ACCESS like `XV1.actuated` or `v->actuated` — resolve the
 * struct instance (honoring param substitution), look up the member offset,
 * and return "D<base+offset>". Returns NULL if the access can't be resolved. */
static const char* render_member_access(AST* node) {
    if (!node || node->type != N_MEMBER_ACCESS) return NULL;
    AST* obj = node->data.member_access.object;
    if (!obj || obj->type != N_IDENT) return NULL;

    const char* var_name = obj->data.ident.name;
    /* Honor alias substitution: callee parameter `v` may resolve to caller's `XV1`. */
    FxSubst* s = subst_find(var_name);
    if (s && s->kind == SUBST_ALIAS_VAR) var_name = s->alias_name;

    FxStructInst* inst = struct_inst_find(var_name);
    if (!inst) return NULL;
    const FxStructDef* def = struct_def_find(inst->type_name);
    if (!def) return NULL;
    int off = struct_def_member_offset(def, node->data.member_access.member);
    if (off < 0) return NULL;

    static char buf[16];
    snprintf(buf, sizeof(buf), "D%d", inst->base_d + off);
    return buf;
}

/* ==================== IL Emitter ==================== */

typedef struct {
    FILE* fp;
    int   step_count;     /* track program memory usage */
    int   error_count;
} FxCtx;

static void fxe(FxCtx* c, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vfprintf(c->fp, fmt, ap);
    va_end(ap);
    fputc('\n', c->fp);
    c->step_count++;
}

static void fx_comment(FxCtx* c, const char* fmt, ...) {
    fputs("; ", c->fp);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(c->fp, fmt, ap);
    va_end(ap);
    fputc('\n', c->fp);
}

/* Forward decls */
static void emit_stmt(FxCtx* c, AST* node);
static void emit_expr_to_d(FxCtx* c, AST* node, int dest_d);

/* Get the integer value of a constant integer expression, or -1 + sentinel. */
static int try_const_int(AST* node, int* out) {
    if (!node) return 0;
    if (node->type == N_INTLIT) { *out = node->data.int_lit.value; return 1; }
    if (node->type == N_UNARY && node->data.unary.op == TOKEN_MINUS &&
        node->data.unary.operand && node->data.unary.operand->type == N_INTLIT) {
        *out = -node->data.unary.operand->data.int_lit.value;
        return 1;
    }
    return 0;
}

/* Render the FX operand for a value-expression that's either a literal,
 * a plain ident, or a struct member access. Returns 1 on success.
 * Complex expressions (arithmetic, calls) need to be prelowered. */
static int render_value_into(FxCtx* c, AST* node, char* out, size_t n) {
    int k;
    if (try_const_int(node, &k)) {
        snprintf(out, n, "K%d", k);
        return 1;
    }
    if (node && node->type == N_IDENT) {
        snprintf(out, n, "%s", render_operand(node->data.ident.name));
        return 1;
    }
    if (node && node->type == N_MEMBER_ACCESS) {
        const char* m = render_member_access(node);
        if (m) { snprintf(out, n, "%s", m); return 1; }
    }
    (void)c;
    return 0;
}

/* Forward decl — function inlining produces a result D register. */
static int emit_inline_call(FxCtx* c, AST* call_node);

/* Lower a comparison `lhs <op> rhs` to a sequence that leaves the boolean
 * result in M bit `dest_m`. Uses FX's CMP instruction which sets 3 bits:
 *   M+0 = lhs > rhs
 *   M+1 = lhs = rhs
 *   M+2 = lhs < rhs
 * We OR the appropriate ones into dest_m. */
static void emit_compare(FxCtx* c, Tokens op, AST* lhs, AST* rhs, int dest_m) {
    char l[16], r[16];
    if (!render_value_into(c, lhs, l, sizeof(l)) ||
        !render_value_into(c, rhs, r, sizeof(r))) {
        fx_comment(c, "TODO: compare with non-trivial operand");
        c->error_count++;
        return;
    }
    int scratch_m = g_next_m;
    g_next_m += 3;
    fxe(c, "CMP   %s %s M%d", l, r, scratch_m);
    int gt = scratch_m, eq = scratch_m + 1, lt = scratch_m + 2;
    /* OUT is unconditional on the accumulator state — no RST needed. The
     * GE / LE / NE forms use LDI of the COMPLEMENTARY bit since CMP's three
     * outputs are mutually exclusive (lt/eq/gt partition the result space). */
    switch (op) {
        case TOKEN_GREATER:        fxe(c, "LD    M%d", gt); fxe(c, "OUT   M%d", dest_m); break;
        case TOKEN_LESS:           fxe(c, "LD    M%d", lt); fxe(c, "OUT   M%d", dest_m); break;
        case TOKEN_EQUAL:          fxe(c, "LD    M%d", eq); fxe(c, "OUT   M%d", dest_m); break;
        case TOKEN_NOT_EQUAL:      fxe(c, "LDI   M%d", eq); fxe(c, "OUT   M%d", dest_m); break;
        case TOKEN_GREATER_EQUAL:  fxe(c, "LDI   M%d", lt); fxe(c, "OUT   M%d", dest_m); break;
        case TOKEN_LESS_EQUAL:     fxe(c, "LDI   M%d", gt); fxe(c, "OUT   M%d", dest_m); break;
        default:
            fx_comment(c, "TODO: unsupported compare op %d", op);
            c->error_count++;
            break;
    }
}

/* Lower a boolean-context expression (an if/while condition) to a sequence
 * that leaves the result in M bit `dest_m`.
 *
 * Accepts:
 *   - relational expressions (a < b)
 *   - logical && / ||
 *   - logical !
 *   - bare integer (truthy: x != 0)
 *   - bare ident bound to X<n> (read input directly)
 */
static void emit_bool_into(FxCtx* c, AST* node, int dest_m) {
    if (!node) {
        fxe(c, "RST   M%d", dest_m);
        return;
    }
    /* Constant: K0 → false, anything else → true. M8000 is FX's "always on"
     * special relay — the canonical way to set/clear an M bit unconditionally. */
    int k;
    if (try_const_int(node, &k)) {
        if (k == 0) fxe(c, "RST   M%d", dest_m);
        else        { fxe(c, "LD    M8000"); fxe(c, "OUT   M%d", dest_m); }
        return;
    }
    if (node->type == N_OPERATOR) {
        Tokens op = node->data.op.op;
        if (op == TOKEN_GREATER || op == TOKEN_LESS ||
            op == TOKEN_EQUAL || op == TOKEN_NOT_EQUAL ||
            op == TOKEN_GREATER_EQUAL || op == TOKEN_LESS_EQUAL) {
            emit_compare(c, op, node->data.op.left, node->data.op.right, dest_m);
            return;
        }
        if (op == TOKEN_AND) {
            int la = g_next_m++; int rb = g_next_m++;
            emit_bool_into(c, node->data.op.left,  la);
            emit_bool_into(c, node->data.op.right, rb);
            fxe(c, "LD    M%d", la);
            fxe(c, "AND   M%d", rb);
            fxe(c, "OUT   M%d", dest_m);
            return;
        }
        if (op == TOKEN_OR) {
            int la = g_next_m++; int rb = g_next_m++;
            emit_bool_into(c, node->data.op.left,  la);
            emit_bool_into(c, node->data.op.right, rb);
            fxe(c, "LD    M%d", la);
            fxe(c, "OR    M%d", rb);
            fxe(c, "OUT   M%d", dest_m);
            return;
        }
    }
    if (node->type == N_UNARY && node->data.unary.op == TOKEN_EXCLAIM) {
        int inner = g_next_m++;
        emit_bool_into(c, node->data.unary.operand, inner);
        fxe(c, "LDI   M%d", inner);
        fxe(c, "OUT   M%d", dest_m);
        return;
    }
    if (node->type == N_IDENT) {
        const char* op = render_operand(node->data.ident.name);
        if (op[0] == 'K') {
            /* Substituted to a constant — use the const path. */
            int v = atoi(op + 1);
            if (v == 0) fxe(c, "RST   M%d", dest_m);
            else        { fxe(c, "LD    M8000"); fxe(c, "OUT   M%d", dest_m); }
            return;
        }
        if (op[0] == 'X' || op[0] == 'Y' || op[0] == 'M') {
            fxe(c, "LD    %s", op);
            fxe(c, "OUT   M%d", dest_m);
            return;
        }
        /* D register — truthy = nonzero. */
        int scratch = g_next_m; g_next_m += 3;
        fxe(c, "CMP   %s K0 M%d", op, scratch);
        fxe(c, "LDI   M%d", scratch + 1);
        fxe(c, "OUT   M%d", dest_m);
        return;
    }
    if (node->type == N_MEMBER_ACCESS) {
        const char* m = render_member_access(node);
        if (m) {
            int scratch = g_next_m; g_next_m += 3;
            fxe(c, "CMP   %s K0 M%d", m, scratch);
            fxe(c, "LDI   M%d", scratch + 1);
            fxe(c, "OUT   M%d", dest_m);
            return;
        }
    }
    if (node->type == N_CALL) {
        const char* fname = node->data.call.name;
        /* Runtime BOOL accessors: directly mirror their backing M/T bit into dest_m. */
        if (fname && node->data.call.arg_count == 1) {
            AST* arg = node->data.call.args[0];
            if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND &&
                arg->data.unary.operand && arg->data.unary.operand->type == N_IDENT) {
                const char* iname = arg->data.unary.operand->data.ident.name;
                if (strcmp(fname, "cst_timer_done") == 0) {
                    FxTimerInst* ti = timer_inst_find(iname);
                    if (ti) { fxe(c, "LD    T%d", ti->t_index); fxe(c, "OUT   M%d", dest_m); return; }
                }
                if (strcmp(fname, "cst_redge_fired") == 0 ||
                    strcmp(fname, "cst_fedge_fired") == 0) {
                    FxEdgeInst* ei = edge_inst_find(iname);
                    if (ei) { fxe(c, "LD    M%d", ei->q_m_index); fxe(c, "OUT   M%d", dest_m); return; }
                }
                if (strcmp(fname, "cst_ctu_done") == 0 || strcmp(fname, "cst_ctd_done") == 0) {
                    FxCounterInst* ci = counter_inst_find(iname);
                    if (ci) { fxe(c, "LD    C%d", ci->c_index); fxe(c, "OUT   M%d", dest_m); return; }
                }
            }
        }
        int rd = emit_inline_call(c, node);
        if (rd >= 0) {
            int scratch = g_next_m; g_next_m += 3;
            fxe(c, "CMP   D%d K0 M%d", rd, scratch);
            fxe(c, "LDI   M%d", scratch + 1);
            fxe(c, "OUT   M%d", dest_m);
            return;
        }
    }
    /* Fallback: load as int, compare to 0. */
    int tmp_d = g_next_d++;
    emit_expr_to_d(c, node, tmp_d);
    int scratch = g_next_m; g_next_m += 3;
    fxe(c, "CMP   D%d K0 M%d", tmp_d, scratch);
    fxe(c, "LD    M%d", scratch);
    fxe(c, "OR    M%d", scratch + 2);
    fxe(c, "OUT   M%d", dest_m);
}

/* Evaluate `node` and store its 16-bit integer result in D<dest_d>. */
static void emit_expr_to_d(FxCtx* c, AST* node, int dest_d) {
    if (!node) return;
    int k;
    if (try_const_int(node, &k)) {
        fxe(c, "MOV   K%d D%d", k, dest_d);
        return;
    }
    if (node->type == N_IDENT) {
        const char* op = render_operand(node->data.ident.name);
        fxe(c, "MOV   %s D%d", op, dest_d);
        return;
    }
    if (node->type == N_MEMBER_ACCESS) {
        const char* m = render_member_access(node);
        if (m) { fxe(c, "MOV   %s D%d", m, dest_d); return; }
        fx_comment(c, "TODO: unresolved member access");
        c->error_count++;
        return;
    }
    if (node->type == N_CALL) {
        const char* fname = node->data.call.name;
        /* Counter value accessor: MOV C<n> D<dest>  (C registers can be used as int sources) */
        if (fname && (strcmp(fname, "cst_ctu_value") == 0 || strcmp(fname, "cst_ctd_value") == 0) &&
            node->data.call.arg_count == 1) {
            AST* arg = node->data.call.args[0];
            if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND &&
                arg->data.unary.operand && arg->data.unary.operand->type == N_IDENT) {
                FxCounterInst* ci = counter_inst_find(arg->data.unary.operand->data.ident.name);
                if (ci) { fxe(c, "MOV   C%d D%d", ci->c_index, dest_d); return; }
            }
        }
        int rd = emit_inline_call(c, node);
        if (rd >= 0) {
            if (rd != dest_d) fxe(c, "MOV   D%d D%d", rd, dest_d);
            return;
        }
        fx_comment(c, "TODO: failed to inline call '%s'", node->data.call.name);
        c->error_count++;
        return;
    }
    if (node->type == N_OPERATOR) {
        Tokens op = node->data.op.op;
        AST* L = node->data.op.left;
        AST* R = node->data.op.right;
        char ls[16], rs[16];
        int  l_ok = render_value_into(c, L, ls, sizeof(ls));
        int  r_ok = render_value_into(c, R, rs, sizeof(rs));
        if (!l_ok) {
            int t = g_next_d++;
            emit_expr_to_d(c, L, t);
            snprintf(ls, sizeof(ls), "D%d", t); l_ok = 1;
        }
        if (!r_ok) {
            int t = g_next_d++;
            emit_expr_to_d(c, R, t);
            snprintf(rs, sizeof(rs), "D%d", t); r_ok = 1;
        }
        switch (op) {
            case TOKEN_PLUS:    fxe(c, "ADD   %s %s D%d", ls, rs, dest_d); return;
            case TOKEN_MINUS:   fxe(c, "SUB   %s %s D%d", ls, rs, dest_d); return;
            case TOKEN_STAR:    fxe(c, "MUL   %s %s D%d", ls, rs, dest_d); return;
            case TOKEN_SLASH:   fxe(c, "DIV   %s %s D%d", ls, rs, dest_d); return;
            case TOKEN_PERCENT: fxe(c, "DIV   %s %s D%d", ls, rs, dest_d);  /* DIV stores quot+rem; rem in D+1 */
                                fxe(c, "MOV   D%d D%d", dest_d + 1, dest_d); return;
            default:
                fx_comment(c, "TODO: int-context op %d", op);
                c->error_count++;
                return;
        }
    }
    fx_comment(c, "TODO: int-context node type %d", node->type);
    c->error_count++;
}

/* Emit one statement.  */
static void emit_stmt(FxCtx* c, AST* node) {
    if (!node) return;

    switch (node->type) {

    case N_BLOCK:
        for (size_t i = 0; i < node->data.block.count; i++)
            emit_stmt(c, node->data.block.statements[i]);
        return;

    case N_DECL: {
        DeclNode* d = &node->data.decl;
        if (!d->name) return;
        if (d->is_extern) return;

        /* Runtime types: cst_time_t → T<n>, cst_redge_t/cst_fedge_t → 2 M bits */
        if (d->type && d->pointer_level == 0) {
            if (strcmp(d->type, "cst_time_t") == 0) {
                if (!timer_inst_find(d->name)) timer_inst_alloc(d->name);
                return;
            }
            if (strcmp(d->type, "cst_redge_t") == 0) {
                if (!edge_inst_find(d->name)) edge_inst_alloc(d->name, 0);
                return;
            }
            if (strcmp(d->type, "cst_fedge_t") == 0) {
                if (!edge_inst_find(d->name)) edge_inst_alloc(d->name, 1);
                return;
            }
            if (strcmp(d->type, "cst_ctu_t") == 0) {
                if (!counter_inst_find(d->name)) counter_inst_alloc(d->name, 0);
                return;
            }
            if (strcmp(d->type, "cst_ctd_t") == 0) {
                if (!counter_inst_find(d->name)) counter_inst_alloc(d->name, 1);
                return;
            }
        }

        /* Struct instance: allocate a contiguous D-block sized to the
         * struct's member count. Don't double-allocate if already declared. */
        const FxStructDef* sdef = struct_def_find(d->type);
        if (sdef && d->pointer_level == 0) {
            if (!struct_inst_find(d->name)) {
                alloc_struct_inst(d->name, sdef->type_name, sdef->member_count);
            }
            return;
        }

        /* Plain variable: reserve a D register. Constant init assigns now. */
        int addr = alloc_d(d->name);
        if (d->init_value) {
            int k;
            if (try_const_int(d->init_value, &k)) fxe(c, "MOV   K%d D%d", k, addr);
            else                                  emit_expr_to_d(c, d->init_value, addr);
        }
        return;
    }

    case N_ASSIGN: {
        /* render_operand() returns a static buffer — copy LHS off before
         * any further calls or it gets clobbered when we render the RHS. */
        char lhs[16];
        snprintf(lhs, sizeof(lhs), "%s", render_operand(node->data.assign.var_name));
        AST* rhs = node->data.assign.value;

        /* Output to a Y pin or M bit → boolean OUT.
         * Fast paths: literal 0/1 → SET/RST; X/Y/M ident → direct LD/OUT. */
        if (lhs[0] == 'Y' || lhs[0] == 'M') {
            int kk;
            if (try_const_int(rhs, &kk)) {
                if (kk == 0) fxe(c, "RST   %s", lhs);
                else         { fxe(c, "LD    M8000"); fxe(c, "OUT   %s", lhs); }
                return;
            }
            if (rhs && rhs->type == N_IDENT) {
                char rop[16];
                snprintf(rop, sizeof(rop), "%s", render_operand(rhs->data.ident.name));
                if (rop[0] == 'X' || rop[0] == 'Y' || rop[0] == 'M') {
                    fxe(c, "LD    %s", rop);
                    fxe(c, "OUT   %s", lhs);
                    return;
                }
            }
            int b = g_next_m++;
            emit_bool_into(c, rhs, b);
            fxe(c, "LD    M%d", b);
            fxe(c, "OUT   %s", lhs);
            return;
        }
        /* Otherwise it's a D register — int-context */
        int k;
        if (try_const_int(rhs, &k)) {
            fxe(c, "MOV   K%d %s", k, lhs);
            return;
        }
        if (rhs && rhs->type == N_IDENT) {
            char rop[16];
            snprintf(rop, sizeof(rop), "%s", render_operand(rhs->data.ident.name));
            fxe(c, "MOV   %s %s", rop, lhs);
            return;
        }
        int dest_d = atoi(lhs + 1);
        emit_expr_to_d(c, rhs, dest_d);
        return;
    }

    case N_IF: {
        /* IF cond { then } ELSE { else }
         *   evaluate cond into a scratch M
         *   CJ P_else if cond is FALSE  →  use LDI M_cond / CJ P_else
         *   <then>
         *   CJ P_end
         * P_else:
         *   <else>
         * P_end:
         */
        int p_else = g_next_p++;
        int p_end  = g_next_p++;
        int cond_m = g_next_m++;
        emit_bool_into(c, node->data.if_stmt.condition, cond_m);
        fxe(c, "LDI   M%d", cond_m);          /* invert: jump if cond was false */
        fxe(c, "CJ    P%d", p_else);
        emit_stmt(c, node->data.if_stmt.then_block);
        if (node->data.if_stmt.else_block) {
            fxe(c, "CJ    P%d", p_end);
            fxe(c, "P%d:", p_else);
            emit_stmt(c, node->data.if_stmt.else_block);
            fxe(c, "P%d:", p_end);
        } else {
            fxe(c, "P%d:", p_else);
        }
        return;
    }

    case N_WHILE: {
        /* WHILE cond DO body END_WHILE
         *   P_top:
         *     <cond> -> M_c
         *     LDI M_c
         *     CJ P_end       ; exit if false
         *     <body>
         *     CJ P_top       ; loop
         *   P_end:
         */
        int p_top = g_next_p++;
        int p_end = g_next_p++;
        int cond_m = g_next_m++;
        fxe(c, "P%d:", p_top);
        emit_bool_into(c, node->data.while_stmt.condition, cond_m);
        fxe(c, "LDI   M%d", cond_m);
        fxe(c, "CJ    P%d", p_end);
        emit_stmt(c, node->data.while_stmt.body);
        fxe(c, "LD    M8000");
        fxe(c, "CJ    P%d", p_top);
        fxe(c, "P%d:", p_end);
        return;
    }

    case N_DO_WHILE: {
        /* REPEAT body UNTIL NOT cond — for FX, lower as do-while:
         *   P_top:
         *     <body>
         *     <cond> -> M_c
         *     LD M_c
         *     CJ P_top       ; loop while true
         */
        int p_top = g_next_p++;
        int cond_m = g_next_m++;
        fxe(c, "P%d:", p_top);
        emit_stmt(c, node->data.do_while_stmt.body);
        emit_bool_into(c, node->data.do_while_stmt.condition, cond_m);
        fxe(c, "LD    M%d", cond_m);
        fxe(c, "CJ    P%d", p_top);
        return;
    }

    case N_FOR: {
        /* C `for(init; cond; incr) body` — lower as init + while+incr.
         * FX has no native FOR/NEXT. */
        if (node->data.for_stmt.init)
            emit_stmt(c, node->data.for_stmt.init);
        int p_top = g_next_p++;
        int p_end = g_next_p++;
        int cond_m = g_next_m++;
        fxe(c, "P%d:", p_top);
        if (node->data.for_stmt.condition) {
            emit_bool_into(c, node->data.for_stmt.condition, cond_m);
            fxe(c, "LDI   M%d", cond_m);
            fxe(c, "CJ    P%d", p_end);
        }
        emit_stmt(c, node->data.for_stmt.body);
        if (node->data.for_stmt.increment)
            emit_stmt(c, node->data.for_stmt.increment);
        fxe(c, "LD    M8000");
        fxe(c, "CJ    P%d", p_top);
        fxe(c, "P%d:", p_end);
        return;
    }

    case N_BREAK:
        /* C break — for FX without enclosing-loop tracking, emit a comment.
         * A proper implementation would track the innermost loop's end label. */
        fx_comment(c, "TODO: break (loop-end label tracking not yet wired)");
        return;

    case N_RETURN: {
        /* Inside an inlined function body: write the return value to the
         * inline result D and jump to the end-of-inline label.
         * Outside: this is `return` from main() — no-op (END is emitted by caller). */
        if (g_inline_end_label >= 0 && g_inline_result_d >= 0) {
            AST* val = node->data.return_stmt.value;
            int k;
            if (val) {
                if (try_const_int(val, &k)) {
                    fxe(c, "MOV   K%d D%d", k, g_inline_result_d);
                } else if (val->type == N_IDENT) {
                    const char* op = render_operand(val->data.ident.name);
                    fxe(c, "MOV   %s D%d", op, g_inline_result_d);
                } else if (val->type == N_MEMBER_ACCESS) {
                    const char* m = render_member_access(val);
                    if (m) fxe(c, "MOV   %s D%d", m, g_inline_result_d);
                } else {
                    emit_expr_to_d(c, val, g_inline_result_d);
                }
            }
            fxe(c, "CJ    P%d", g_inline_end_label);
        }
        return;
    }

    case N_CALL: {
        /* Runtime shims: cst_timer_on / cst_redge_update / cst_fedge_update
         * compile to FX-native IL, NOT the IEC FB-instance call pattern. */
        const char* fname = node->data.call.name;
        if (fname) {
            /* Timer on (TON-style): OUT T<n> K<preset_ms_in_units>
             * FX1S T0..T31 are 100ms units, T32..T62 are 10ms units.
             * For simplicity we use 100ms units (T0..T31). */
            if (strcmp(fname, "cst_timer_on") == 0 && node->data.call.arg_count == 2) {
                AST* tgt = node->data.call.args[0];
                if (tgt && tgt->type == N_UNARY && tgt->data.unary.op == TOKEN_AMPERSAND &&
                    tgt->data.unary.operand && tgt->data.unary.operand->type == N_IDENT) {
                    FxTimerInst* ti = timer_inst_find(tgt->data.unary.operand->data.ident.name);
                    if (!ti) ti = timer_inst_alloc(tgt->data.unary.operand->data.ident.name);
                    AST* preset = node->data.call.args[1];
                    int kk;
                    /* FX timer presets are in 100ms units; ms / 100. */
                    if (try_const_int(preset, &kk)) {
                        int units = kk / 100;
                        if (units < 1) units = 1;
                        fxe(c, "LD    M8000");
                        fxe(c, "OUT   T%d K%d", ti->t_index, units);
                    } else {
                        char rs[16];
                        if (render_value_into(c, preset, rs, sizeof(rs))) {
                            fxe(c, "LD    M8000");
                            fxe(c, "OUT   T%d %s", ti->t_index, rs);
                        }
                    }
                    return;
                }
            }
            if (strcmp(fname, "cst_timer_off") == 0 && node->data.call.arg_count == 1) {
                /* OUT T<n> with conditional FALSE (LDI M8000) effectively resets. */
                AST* tgt = node->data.call.args[0];
                if (tgt && tgt->type == N_UNARY && tgt->data.unary.op == TOKEN_AMPERSAND &&
                    tgt->data.unary.operand && tgt->data.unary.operand->type == N_IDENT) {
                    FxTimerInst* ti = timer_inst_find(tgt->data.unary.operand->data.ident.name);
                    if (ti) {
                        fxe(c, "LDI   M8000");
                        fxe(c, "OUT   T%d K1", ti->t_index);
                    }
                    return;
                }
            }
            /* Up/Down counters: FX uses dedicated C0..C15 (16-bit) registers.
             * cst_ctu_count(&c, in, rst, pv) lowers to:
             *   LD rst / RST C<n>          (reset path)
             *   LD in / OUT C<n> K<pv>     (count path — increments on rising edge)
             */
            if ((strcmp(fname, "cst_ctu_count") == 0 ||
                 strcmp(fname, "cst_ctd_count") == 0) &&
                node->data.call.arg_count == 4) {
                int down = (fname[4] == 'd');
                AST* tgt = node->data.call.args[0];
                if (tgt && tgt->type == N_UNARY && tgt->data.unary.op == TOKEN_AMPERSAND &&
                    tgt->data.unary.operand && tgt->data.unary.operand->type == N_IDENT) {
                    FxCounterInst* ci = counter_inst_find(tgt->data.unary.operand->data.ident.name);
                    if (!ci) ci = counter_inst_alloc(tgt->data.unary.operand->data.ident.name, down);
                    char in_s[16], rst_s[16], pv_s[16];
                    AST* in_arg  = node->data.call.args[1];
                    AST* rst_arg = node->data.call.args[2];
                    AST* pv_arg  = node->data.call.args[3];
                    int in_ok  = render_value_into(c, in_arg, in_s, sizeof(in_s));
                    int rst_ok = render_value_into(c, rst_arg, rst_s, sizeof(rst_s));
                    int pv_ok  = render_value_into(c, pv_arg, pv_s, sizeof(pv_s));
                    if (in_ok && rst_ok && pv_ok) {
                        /* Reset comes first so a held-reset wins over count. */
                        fxe(c, "LD    %s", rst_s);
                        fxe(c, "RST   C%d", ci->c_index);
                        /* Then count, gated on input. FX OUT C<n> K<pv> auto-increments
                         * on rising edge of accumulator. */
                        fxe(c, "LD    %s", in_s);
                        fxe(c, "OUT   C%d %s", ci->c_index, pv_s);
                    } else {
                        fx_comment(c, "TODO: complex counter operands");
                        c->error_count++;
                    }
                    return;
                }
            }

            /* Edge updates: FX has LDP/LDF instructions that detect rising/falling
             * edges of an X bit directly. We emulate cst_redge by storing prev-state
             * in our prev-M and comparing each scan. */
            if ((strcmp(fname, "cst_redge_update") == 0 ||
                 strcmp(fname, "cst_fedge_update") == 0) &&
                node->data.call.arg_count == 2) {
                int falling = (fname[4] == 'f');
                AST* tgt = node->data.call.args[0];
                AST* sig = node->data.call.args[1];
                if (tgt && tgt->type == N_UNARY && tgt->data.unary.op == TOKEN_AMPERSAND &&
                    tgt->data.unary.operand && tgt->data.unary.operand->type == N_IDENT) {
                    FxEdgeInst* ei = edge_inst_find(tgt->data.unary.operand->data.ident.name);
                    if (!ei) ei = edge_inst_alloc(tgt->data.unary.operand->data.ident.name, falling);
                    char ss[16];
                    if (!render_value_into(c, sig, ss, sizeof(ss))) {
                        int t = g_next_d++;
                        emit_expr_to_d(c, sig, t);
                        snprintf(ss, sizeof(ss), "D%d", t);
                    }
                    /* fired_this_scan = (signal == 1) AND NOT prev_state  (rising)
                     *                 = (signal == 0) AND prev_state      (falling)
                     *  prev_state := signal */
                    if (falling) {
                        fxe(c, "LDI   %s", ss);
                        fxe(c, "AND   M%d", ei->m_index);
                    } else {
                        fxe(c, "LD    %s", ss);
                        fxe(c, "ANI   M%d", ei->m_index);
                    }
                    fxe(c, "OUT   M%d", ei->q_m_index);
                    /* Update prev-state for next scan */
                    fxe(c, "LD    %s", ss);
                    fxe(c, "OUT   M%d", ei->m_index);
                    return;
                }
            }
        }
        /* Otherwise: ordinary inline expansion. */
        emit_inline_call(c, node);
        return;
    }

    case N_OPERATOR: {
        Tokens op = node->data.op.op;
        if (op == TOKEN_ASSIGN) {
            AST* lhs = node->data.op.left;
            AST* rhs = node->data.op.right;
            /* Member-access LHS: `XV1.actuated = 1` */
            if (lhs && lhs->type == N_MEMBER_ACCESS) {
                const char* m = render_member_access(lhs);
                if (!m) { fx_comment(c, "TODO: unresolved member assign"); c->error_count++; return; }
                int kk;
                if (try_const_int(rhs, &kk)) {
                    fxe(c, "MOV   K%d %s", kk, m);
                    return;
                }
                if (rhs && rhs->type == N_IDENT) {
                    const char* rop = render_operand(rhs->data.ident.name);
                    fxe(c, "MOV   %s %s", rop, m);
                    return;
                }
                if (rhs && rhs->type == N_MEMBER_ACCESS) {
                    const char* rm = render_member_access(rhs);
                    if (rm) { fxe(c, "MOV   %s %s", rm, m); return; }
                }
                /* Fallback: lower into a scratch D, then move to member */
                int t = g_next_d++;
                emit_expr_to_d(c, rhs, t);
                fxe(c, "MOV   D%d %s", t, m);
                return;
            }
            /* Plain ident LHS — synthesize an N_ASSIGN to reuse that path */
            if (lhs && lhs->type == N_IDENT) {
                AST tmp;
                tmp.type = N_ASSIGN;
                tmp.data.assign.var_name = lhs->data.ident.name;
                tmp.data.assign.value    = rhs;
                emit_stmt(c, &tmp);
                return;
            }
        }
        fx_comment(c, "TODO: expression statement (op %d)", op);
        return;
    }

    default:
        fx_comment(c, "TODO: stmt node type %d", node->type);
        return;
    }
}

/* ==================== Function Call Inlining ====================
 *
 * FX1S has no stack and no native parameter-passing mechanism. We lower
 * C function calls by inlining: at each call site, we splice the callee's
 * body in with parameter substitutions active, plus a "return result"
 * D-register and a synthetic end-label for early-return semantics.
 *
 * Substitution policy:
 *   - struct pointer param  →  alias: param.name refers to caller's variable
 *                              (which was passed via &caller_var)
 *   - int param, constant arg →  constant-prop: param renders as `K<value>`
 *   - int param, complex arg →  pre-evaluate into a fresh D, alias param to it
 *
 * Returns the D-register address holding the call's return value, or -1 on
 * failure.
 */
static int emit_inline_call(FxCtx* c, AST* call_node)
{
    if (!call_node || call_node->type != N_CALL) return -1;
    const char* fname = call_node->data.call.name;

    AST* fn = find_function(fname);
    if (!fn || !fn->data.function.body) {
        fx_comment(c, "TODO: undefined function '%s'", fname ? fname : "?");
        c->error_count++;
        return -1;
    }
    FunctionNode* f = &fn->data.function;

    /* Save caller's inline context for nested inlines. */
    int saved_subst   = subst_save_count();
    int saved_result  = g_inline_result_d;
    int saved_endlbl  = g_inline_end_label;

    /* Allocate the result D and the end-of-inline label. */
    int result_d = g_next_d++;
    int end_lbl  = g_next_p++;
    g_inline_result_d  = result_d;
    g_inline_end_label = end_lbl;

    fx_comment(c, "inline call: %s", fname);

    /* Bind parameters. */
    size_t nparams = f->param_count;
    size_t nargs   = call_node->data.call.arg_count;
    if (nargs != nparams) {
        fx_comment(c, "WARN: arity mismatch %s (%zu params, %zu args)",
                   fname, nparams, nargs);
    }
    for (size_t i = 0; i < nparams && i < nargs; i++) {
        DeclNode* p = &f->params[i]->data.decl;
        AST* arg = call_node->data.call.args[i];
        if (!p->name || !p->name[0]) continue;

        /* Skip `void` placeholder param from `f(void)`. */
        if (p->pointer_level == 0 && p->type && strcmp(p->type, "void") == 0)
            continue;

        /* Struct-pointer param: arg must be `&ident` of a struct instance. */
        if (p->pointer_level >= 1 && p->type && strncmp(p->type, "struct ", 7) == 0) {
            if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND &&
                arg->data.unary.operand && arg->data.unary.operand->type == N_IDENT) {
                const char* alias = arg->data.unary.operand->data.ident.name;
                /* Re-resolve through any active alias chain (for nested inlines). */
                FxSubst* outer = subst_find(alias);
                if (outer && outer->kind == SUBST_ALIAS_VAR) alias = outer->alias_name;
                subst_push_alias(p->name, alias);
            } else {
                fx_comment(c, "TODO: struct ptr arg for '%s' isn't &ident", p->name);
                c->error_count++;
            }
            continue;
        }

        /* Int by value */
        int kv;
        if (try_const_int(arg, &kv)) {
            subst_push_const(p->name, kv);
        } else if (arg && arg->type == N_IDENT) {
            /* Bind to the underlying D / X / Y / M name via alias. */
            FxSubst* outer = subst_find(arg->data.ident.name);
            if (outer && outer->kind == SUBST_INT_CONST) {
                subst_push_const(p->name, outer->int_value);
            } else {
                /* Reuse caller's storage by aliasing identifier. */
                subst_push_alias(p->name, arg->data.ident.name);
            }
        } else {
            /* Complex arg expression — evaluate into a fresh D, alias param to it. */
            int d = g_next_d++;
            emit_expr_to_d(c, arg, d);
            subst_push_dreg(p->name, d);
        }
    }

    /* Default the result to 0 in case the function falls off without returning. */
    fxe(c, "MOV   K0 D%d", result_d);

    /* Splice in the body. */
    emit_stmt(c, f->body);

    /* End-of-inline label. */
    fxe(c, "P%d:", end_lbl);

    /* Restore caller's inline context. */
    subst_restore_count(saved_subst);
    g_inline_result_d  = saved_result;
    g_inline_end_label = saved_endlbl;

    return result_d;
}

/* ==================== Top-Level Driver ==================== */

static void mfx_emit_program(AST* root, const char* output_dir)
{
    if (!root || root->type != N_PROGRAM) return;

    g_program_root = root;
    io_parse_from_source(g_fx_source_hint);
    syms_reset();
    runtime_insts_clear();
    subst_clear();
    g_inline_result_d  = -1;
    g_inline_end_label = -1;
    register_struct_defs(root);

    char path[1024];
    snprintf(path, sizeof(path), "%s/program.gxil", output_dir);
    /* normalize separators */
    for (char* p = path; *p; p++) if (*p == '\\') *p = '/';

    FILE* fp = fopen(path, "w");
    if (!fp) {
        fprintf(stderr, "CST/FX: cannot create %s\n", path);
        return;
    }

    FxCtx c = { .fp = fp, .step_count = 0, .error_count = 0 };

    /* Header comment */
    fprintf(fp, "; ============================================================\n");
    fprintf(fp, "; CST -> Mitsubishi FX1S Instruction List\n");
    fprintf(fp, "; Paste into GX Developer:  File > Import > Instruction List\n");
    fprintf(fp, "; Or:  Project > Convert > Convert from List\n");
    fprintf(fp, "; ============================================================\n\n");

    /* I/O bindings as comments */
    if (g_io_count > 0) {
        fprintf(fp, "; ---- I/O bindings ----\n");
        for (size_t i = 0; i < g_io_count; i++) {
            fprintf(fp, ";   %c%-2d  =  %s\n", g_io[i].pin_kind, g_io[i].pin_num, g_io[i].name);
        }
        fputc('\n', fp);
    }

    /* Globals: pre-allocate registers for non-IO globals so they have stable
     * addresses across emit. Struct types get contiguous D-blocks (member
     * access uses base + offset); runtime types (cst_time_t / cst_redge_t /
     * cst_ctu_t) get their dedicated runtime instance; everything else
     * gets a single D register. */
    ProgramNode* prog = &root->data.program;
    fprintf(fp, "; ---- Global / persistent variables ----\n");
    for (size_t i = 0; i < prog->global_count; i++) {
        AST* g = prog->globals[i];
        if (!g || g->type != N_DECL) continue;
        if (io_lookup(g->data.decl.name)) continue;

        const char* tname = g->data.decl.type;

        /* Runtime types: matching alloc */
        if (tname && g->data.decl.pointer_level == 0) {
            if (strcmp(tname, "cst_time_t") == 0) {
                if (!timer_inst_find(g->data.decl.name)) timer_inst_alloc(g->data.decl.name);
                fprintf(fp, ";   %s  =  T%d\n", g->data.decl.name,
                        timer_inst_find(g->data.decl.name)->t_index);
                continue;
            }
            if (strcmp(tname, "cst_redge_t") == 0 || strcmp(tname, "cst_fedge_t") == 0) {
                int falling = (strcmp(tname, "cst_fedge_t") == 0);
                if (!edge_inst_find(g->data.decl.name)) edge_inst_alloc(g->data.decl.name, falling);
                FxEdgeInst* ei = edge_inst_find(g->data.decl.name);
                fprintf(fp, ";   %s  =  M%d/M%d (edge)\n", g->data.decl.name,
                        ei->m_index, ei->q_m_index);
                continue;
            }
            if (strcmp(tname, "cst_ctu_t") == 0 || strcmp(tname, "cst_ctd_t") == 0) {
                int down = (strcmp(tname, "cst_ctd_t") == 0);
                if (!counter_inst_find(g->data.decl.name)) counter_inst_alloc(g->data.decl.name, down);
                fprintf(fp, ";   %s  =  C%d\n", g->data.decl.name,
                        counter_inst_find(g->data.decl.name)->c_index);
                continue;
            }
        }

        /* User struct: allocate a contiguous D-block sized to the struct */
        const FxStructDef* sdef = struct_def_find(tname);
        if (sdef && g->data.decl.pointer_level == 0) {
            if (!struct_inst_find(g->data.decl.name))
                alloc_struct_inst(g->data.decl.name, sdef->type_name, sdef->member_count);
            FxStructInst* si = struct_inst_find(g->data.decl.name);
            fprintf(fp, ";   %s : %s  =  D%d..D%d\n", g->data.decl.name,
                    sdef->type_name, si->base_d, si->base_d + sdef->member_count - 1);
            continue;
        }

        /* Plain scalar */
        int addr = alloc_d(g->data.decl.name);
        fprintf(fp, ";   %s  =  D%d\n", g->data.decl.name, addr);
    }
    fputc('\n', fp);

    /* Find main(). Initial values for globals get emitted first as part of
     * scan startup — but FX doesn't have a "first scan only" semantics
     * built in. We use M8002 (initial-pulse special relay) to gate inits. */
    AST* main_fn = NULL;
    for (size_t i = 0; i < prog->func_count; i++) {
        AST* f = prog->functions[i];
        if (f && f->type == N_FUNCTION &&
            f->data.function.name && strcmp(f->data.function.name, "main") == 0) {
            main_fn = f;
            break;
        }
    }

    /* Emit global initial values gated by M8002 (pulses high on first scan). */
    int has_inits = 0;
    for (size_t i = 0; i < prog->global_count; i++) {
        AST* g = prog->globals[i];
        if (g && g->type == N_DECL && g->data.decl.init_value) { has_inits = 1; break; }
    }
    if (has_inits) {
        fprintf(fp, "; ---- First-scan initialization (gated by M8002) ----\n");
        fxe(&c, "LD    M8002");
        for (size_t i = 0; i < prog->global_count; i++) {
            AST* g = prog->globals[i];
            if (!g || g->type != N_DECL || !g->data.decl.init_value) continue;
            const char* lhs = render_operand(g->data.decl.name);
            /* X pins are read-only inputs — can't write a default value to them.
             * Y pins start at 0 by hardware default; explicit init is fine. */
            if (lhs[0] == 'X') continue;
            int k;
            if (try_const_int(g->data.decl.init_value, &k))
                fxe(&c, "MOV   K%d %s", k, lhs);
            else
                fx_comment(&c, "TODO: non-constant init for %s", g->data.decl.name);
        }
        fputc('\n', fp);
    }

    /* Emit main body — runs every scan. */
    fprintf(fp, "; ---- Main scan body ----\n");
    if (main_fn && main_fn->data.function.body) {
        emit_stmt(&c, main_fn->data.function.body);
    } else {
        fx_comment(&c, "no main() defined — emitting empty program");
    }

    fputc('\n', fp);
    fxe(&c, "END");

    /* Footer summary */
    fprintf(fp, "\n; ---- Program statistics ----\n");
    fprintf(fp, ";   %d steps emitted (FX1S limit: 2000)\n", c.step_count);
    fprintf(fp, ";   %d D registers in use (FX1S limit: 128)\n", g_next_d);
    fprintf(fp, ";   %d M bits in use   (FX1S limit: 384)\n", g_next_m);
    fprintf(fp, ";   %d codegen errors\n", c.error_count);

    fclose(fp);

    if (c.error_count > 0) {
        fprintf(stderr, "CST/FX: %d codegen issue(s) — check `; TODO:` comments in %s\n",
                c.error_count, path);
    }
    printf("CST/FX: program.gxil written (%d steps, %d D, %d M)\n",
           c.step_count, g_next_d, g_next_m);
}

/* ==================== Target Definition ==================== */

/* FX type mapping for GX Works 2 Structured Project on FX1S/FX1N.
 *
 * GX Works 2 Structured Project supports the standard IEC 61131-3 type
 * keywords. FX1S native word size is 16-bit, so `int` maps to INT (not
 * DINT like on Beckhoff/AB) — keeps register usage compact and matches
 * what the FX hardware actually does in one cycle.
 *
 * Runtime FB types (cst_time_t etc.) get recognized regardless of the
 * #include status because the runtime header isn't always pulled in
 * before codegen. */
static const char* mfx_map_type(const char* c_type, int pointer_level)
{
    static char buf[256];

    if (!c_type || strlen(c_type) == 0)
    {
        if (pointer_level > 0) return "POINTER TO WORD";
        return "INT";
    }

    if (pointer_level == 0)
    {
        if (strcmp(c_type, "cst_time_t")  == 0) return "TON";
        if (strcmp(c_type, "cst_tof_t")   == 0) return "TOF";
        if (strcmp(c_type, "cst_redge_t") == 0) return "R_TRIG";
        if (strcmp(c_type, "cst_fedge_t") == 0) return "F_TRIG";
        if (strcmp(c_type, "cst_ctu_t")   == 0) return "CTU";
        if (strcmp(c_type, "cst_ctd_t")   == 0) return "CTD";
    }

    const char* base = c_type;
    if (strncmp(c_type, "struct ", 7) == 0) base = c_type + 7;

    /* char* maps to STRING; FX1S Structured Project supports STRING up to
     * a configured max length. */
    if (strcmp(base, "char") == 0 && pointer_level == 1) return "STRING";

    const char* st_base = NULL;
    if      (strcmp(base, "bool") == 0 || strcmp(base, "_Bool") == 0) st_base = "BOOL";
    else if (strcmp(base, "char") == 0)                               st_base = "INT";    /* FX has no SINT */
    else if (strcmp(base, "short") == 0)                              st_base = "INT";
    else if (strcmp(base, "int") == 0)                                st_base = "INT";    /* FX1S native = 16-bit */
    else if (strcmp(base, "long") == 0)                               st_base = "DINT";   /* 32-bit */
    else if (strcmp(base, "float") == 0 || strcmp(base, "double") == 0) st_base = "REAL";
    else if (strcmp(base, "void") == 0)                               st_base = "VOID";
    else if (strcmp(base, "unsigned char") == 0)                      st_base = "WORD";
    else if (strcmp(base, "unsigned short") == 0 ||
             strcmp(base, "unsigned int") == 0   ||
             strcmp(base, "unsigned") == 0)                           st_base = "WORD";
    else if (strcmp(base, "unsigned long") == 0)                      st_base = "DWORD";
    else                                                              st_base = base;

    if (pointer_level > 0)
    {
        snprintf(buf, sizeof(buf), "%s", st_base);
        for (int i = 0; i < pointer_level; i++)
        {
            char tmp[256];
            snprintf(tmp, sizeof(tmp), "POINTER TO %s", buf);
            strcpy(buf, tmp);
        }
        return buf;
    }
    return st_base;
}

static int mfx_get_type_size(const char* c_type)
{
    if (!c_type) return 0;
    if (strcmp(c_type, "char") == 0 || strcmp(c_type, "unsigned char") == 0)   return 1;
    if (strcmp(c_type, "short") == 0 || strcmp(c_type, "unsigned short") == 0) return 2;
    if (strcmp(c_type, "int") == 0   || strcmp(c_type, "unsigned int") == 0)   return 2; /* FX1S = 16-bit int */
    if (strcmp(c_type, "long") == 0  || strcmp(c_type, "unsigned long") == 0)  return 4;
    if (strcmp(c_type, "float") == 0)                                          return 4;
    if (strcmp(c_type, "double") == 0)                                         return 8;
    return 0;
}

static int mfx_is_unsupported_call(const char* name)
{
    if (!name) return 0;
    if (strcmp(name, "printf") == 0 || strcmp(name, "fprintf") == 0 ||
        strcmp(name, "sprintf") == 0 || strcmp(name, "snprintf") == 0 ||
        strcmp(name, "scanf") == 0 || strcmp(name, "puts") == 0 ||
        strcmp(name, "putchar") == 0 || strcmp(name, "getchar") == 0) return 1;
    if (strcmp(name, "malloc") == 0 || strcmp(name, "calloc") == 0 ||
        strcmp(name, "realloc") == 0 || strcmp(name, "free") == 0) return 2;
    return 0;
}

static const char* mfx_unsupported_call_comment(int code)
{
    switch (code) {
        case 1: return "(* NO CONSOLE IN ST *)";
        case 2: return "(* HEAP NOT SUPPORTED ON FX1S *)";
        default: return "(* UNSUPPORTED CALL *)";
    }
}

/* ==================== ST Variable Resolver ==================== */

/* Called by the standard IEC ST writer (CST.c) for every variable reference.
 * Lazily parses CST_IO pragmas on first call so the io table is populated
 * even when the IL emit_program path is not used (i.e. ST output mode).
 * Returns "X0" / "Y1" / etc. if `name` is I/O-bound, or NULL otherwise. */
static const char* mfx_resolve_var(const char* name)
{
    /* Lazy init: parse I/O pragmas the first time we're asked.
     * g_io_parsed tracks whether we've already scanned this source so we
     * don't re-scan on every identifier reference. cst_fx_set_source() resets
     * the flag so a fresh compilation gets a fresh parse. */
    static int   g_io_parsed   = 0;
    static const char* g_last_hint = NULL;
    if (!g_io_parsed || g_last_hint != g_fx_source_hint) {
        if (g_fx_source_hint)
            io_parse_from_source(g_fx_source_hint);
        g_io_parsed  = 1;
        g_last_hint  = g_fx_source_hint;
    }
    return io_lookup(name);
}

const plc_target_t target_mitsubishi_fx = {
    .name                       = "mitsubishi_fx",
    .runtime = {
        /* GX Works 2 Structured Project on FX1S/FX1N exposes the standard
         * IEC 61131-3 function blocks — same names as Beckhoff but the
         * implementations live in Mitsubishi's libs.  We mirror Beckhoff's
         * runtime config because the user-facing identifiers are identical. */
        .timer_fb_type          = "TON",
        .timer_in_member        = "IN",
        .timer_pt_member        = "PT",
        .timer_done_member      = "Q",
        .timer_pt_is_time       = 1,            /* PT takes a TIME literal (T#5s) */
        .memcpy_fn              = "BMOV",       /* FX block-move */
        .memcpy_dst_first       = 0,            /* BMOV(src, dst, n) */
        .memset_fn              = "FMOV",       /* FX fill-move */
        .memset_dst_first       = 0,
        .log_supported          = 0,            /* No ADSLOGSTR equivalent on FX */
        .log_int_fn             = "",
        .log_str_fn             = "",
        .redge_fb_type          = "R_TRIG",
        .fedge_fb_type          = "F_TRIG",
        .edge_clk_member        = "CLK",
        .edge_q_member          = "Q",
        .tof_fb_type            = "TOF",
        .tof_in_member          = "IN",
        .tof_pt_member          = "PT",
        .tof_q_member           = "Q",
        .ctu_fb_type            = "CTU",
        .ctu_cu_member          = "CU",
        .ctu_reset_member       = "RESET",
        .ctu_pv_member          = "PV",
        .ctu_q_member           = "Q",
        .ctu_cv_member          = "CV",
        .ctd_fb_type            = "CTD",
        .ctd_cd_member          = "CD",
        .ctd_load_member        = "LOAD",
        .ctd_pv_member          = "PV",
        .ctd_q_member           = "Q",
        .ctd_cv_member          = "CV",
    },
    .map_type                   = mfx_map_type,
    .get_type_size              = mfx_get_type_size,
    .is_unsupported_call        = mfx_is_unsupported_call,
    .unsupported_call_comment   = mfx_unsupported_call_comment,
    .supports_pointers          = 0,    /* FX1S Structured doesn't support raw pointers */
    .supports_enums             = 1,
    .supports_unsigned          = 1,    /* WORD / DWORD */
    .supports_continue          = 1,
    .supports_line_comments     = 1,    /* GX Works 2 Structured ST accepts // */
    /* emit_program intentionally NULL — fall through to the standard IEC ST
     * writer, same path Beckhoff and Allen-Bradley use. The legacy
     * mfx_emit_program (IL output) is kept above for reference but is no
     * longer wired up; GX Works 2 Structured Project only takes ST anyway. */
    .resolve_var                = mfx_resolve_var,
};
