/*
 * CST_Gen.c � C to IEC 61131-3 Structured Text Code Generator
 *
 * Two-pass codegen:
 *   Pass 1 (.data segment): TYPE definitions, VAR CONSTANT, VAR declarations
 *   Pass 2 (.code segment): All executable logic / statements
 *
 * Walks the SubsetC AST and emits valid Structured Text for TwinCAT 3 / Allen Bradley.
 */

#include "../CST.h"
#include "../Targets/target.h"

/* ====================== Active Target ====================== */

/* Early forward decls used by helpers further down. */
static void collect_local_decls(AST* node, AST*** decls, size_t* count, size_t* cap);

const plc_target_t* g_target = &target_beckhoff;

void plc_target_set(const plc_target_t* t)
{
    g_target = t ? t : &target_beckhoff;
}

const plc_target_t* plc_target_lookup(const char* name)
{
    if (!name) return NULL;
    if (strcmp(name, "beckhoff") == 0)      return &target_beckhoff;
    if (strcmp(name, "allen_bradley") == 0) return &target_allen_bradley;
    if (strcmp(name, "allen_bradley_ll") == 0) return &target_allen_bradley_ll;
    if (strcmp(name, "ladder") == 0)        return &target_allen_bradley_ll;
    if (strcmp(name, "mitsubishi_fx") == 0) return &target_mitsubishi_fx;
    return NULL;
}

/* ====================== Function Signature Table ====================== */

/* Per-function parameter info, populated once per `cst_generate` call so
 * call sites can ask "is arg i passed-by-reference (struct ptr) or by value?"
 * That decides whether `&x` strips to bare `x` (VAR_IN_OUT) or becomes ADR(x). */
typedef struct {
    char* name;
    size_t param_count;
    int*   param_pointer_levels;
    char** param_types;     /* heap-owned */
    char** param_names;     /* heap-owned; empty string if anonymous */
} FuncSig;

static FuncSig* g_func_table = NULL;
static size_t   g_func_count = 0;

static void func_table_free(void)
{
    for (size_t i = 0; i < g_func_count; i++) {
        free(g_func_table[i].name);
        free(g_func_table[i].param_pointer_levels);
        for (size_t j = 0; j < g_func_table[i].param_count; j++) {
            free(g_func_table[i].param_types[j]);
            free(g_func_table[i].param_names[j]);
        }
        free(g_func_table[i].param_types);
        free(g_func_table[i].param_names);
    }
    free(g_func_table);
    g_func_table = NULL;
    g_func_count = 0;
}

static void func_table_build(AST* root)
{
    func_table_free();
    if (!root || root->type != N_PROGRAM) return;

    ProgramNode* prog = &root->data.program;
    g_func_table = (FuncSig*)calloc(prog->func_count, sizeof(FuncSig));
    g_func_count = 0;

    for (size_t i = 0; i < prog->func_count; i++) {
        AST* f = prog->functions[i];
        if (!f || f->type != N_FUNCTION) continue;
        FunctionNode* fn = &f->data.function;
        FuncSig* sig = &g_func_table[g_func_count++];
        sig->name = _strdup(fn->name);
        sig->param_count = fn->param_count;
        sig->param_pointer_levels = (int*)calloc(fn->param_count, sizeof(int));
        sig->param_types = (char**)calloc(fn->param_count, sizeof(char*));
        sig->param_names = (char**)calloc(fn->param_count, sizeof(char*));
        for (size_t k = 0; k < fn->param_count; k++) {
            DeclNode* d = &fn->params[k]->data.decl;
            sig->param_pointer_levels[k] = d->pointer_level;
            sig->param_types[k] = _strdup(d->type ? d->type : "");
            sig->param_names[k] = _strdup(d->name ? d->name : "");
        }
    }
}

/* Returns the FuncSig for `name`, or NULL if unknown. */
static FuncSig* func_table_lookup(const char* name)
{
    if (!name) return NULL;
    for (size_t i = 0; i < g_func_count; i++)
        if (strcmp(g_func_table[i].name, name) == 0) return &g_func_table[i];
    return NULL;
}

/* ====================== BOOL-typed Identifier Set ====================== *
 *
 * Tracks which C identifiers are bool-typed (declared `bool`/`_Bool` or
 * struct members of bool type). Lets expr_produces_bool recognize bare
 * idents as bool-producing, and lets N_ASSIGN suppress the BOOL_TO_DINT
 * wrap when both LHS and RHS are BOOL.
 *
 * Built once per cst_generate from the AST. */

#define MAX_BOOL_NAMES 256
static char* g_bool_names[MAX_BOOL_NAMES];
static size_t g_bool_count = 0;

/* Encoded as either:
 *   "name"             -> top-level ident (param or global) is bool
 *   "Type.member"      -> struct member is bool
 */
static int is_c_type_bool(const char* type) {
    if (!type) return 0;
    return strcmp(type, "bool") == 0 || strcmp(type, "_Bool") == 0;
}

static void bool_names_add(const char* key) {
    if (!key) return;
    if (g_bool_count >= MAX_BOOL_NAMES) return;
    for (size_t i = 0; i < g_bool_count; i++)
        if (strcmp(g_bool_names[i], key) == 0) return;
    g_bool_names[g_bool_count++] = _strdup(key);
}

static int bool_names_has(const char* key) {
    if (!key) return 0;
    for (size_t i = 0; i < g_bool_count; i++)
        if (strcmp(g_bool_names[i], key) == 0) return 1;
    return 0;
}

static void bool_names_clear(void) {
    for (size_t i = 0; i < g_bool_count; i++) free(g_bool_names[i]);
    g_bool_count = 0;
}

/* Scan the program: every `bool x;` decl becomes "x"; every struct field
 * `bool fault;` becomes "Tag.fault". */
static void bool_names_walk(AST* node) {
    if (!node) return;
    switch (node->type) {
        case N_PROGRAM:
            for (size_t i = 0; i < node->data.program.global_count; i++)
                bool_names_walk(node->data.program.globals[i]);
            for (size_t i = 0; i < node->data.program.func_count; i++)
                bool_names_walk(node->data.program.functions[i]);
            break;
        case N_FUNCTION:
            for (size_t i = 0; i < node->data.function.param_count; i++)
                bool_names_walk(node->data.function.params[i]);
            bool_names_walk(node->data.function.body);
            break;
        case N_BLOCK:
            for (size_t i = 0; i < node->data.block.count; i++)
                bool_names_walk(node->data.block.statements[i]);
            break;
        case N_DECL:
            if (node->data.decl.name && is_c_type_bool(node->data.decl.type) &&
                node->data.decl.pointer_level == 0)
                bool_names_add(node->data.decl.name);
            break;
        case N_STRUCT_DECL: {
            const char* tag = node->data.struct_decl.name;
            if (!tag) break;
            for (size_t i = 0; i < node->data.struct_decl.member_count; i++) {
                AST* m = node->data.struct_decl.members[i];
                if (!m || m->type != N_DECL) continue;
                if (!m->data.decl.name) continue;
                if (is_c_type_bool(m->data.decl.type) && m->data.decl.pointer_level == 0) {
                    char key[256];
                    snprintf(key, sizeof(key), "%s.%s", tag, m->data.decl.name);
                    bool_names_add(key);
                }
            }
            break;
        }
        case N_IF:
            bool_names_walk(node->data.if_stmt.then_block);
            bool_names_walk(node->data.if_stmt.else_block);
            break;
        case N_WHILE:
            bool_names_walk(node->data.while_stmt.body); break;
        case N_DO_WHILE:
            bool_names_walk(node->data.do_while_stmt.body); break;
        case N_FOR:
            bool_names_walk(node->data.for_stmt.init);
            bool_names_walk(node->data.for_stmt.body); break;
        case N_SWITCH:
            for (size_t i = 0; i < node->data.switch_stmt.case_count; i++)
                bool_names_walk(node->data.switch_stmt.case_bodies[i]);
            bool_names_walk(node->data.switch_stmt.default_body); break;
        default: break;
    }
}

/* Looks up the C type of a member access by walking the FuncSig table for
 * the current function's params, then globals, to find the object's type,
 * then checking the struct member set. Approximate but works for the common
 * cases: ident.member and ident->member where ident is a param or global. */
static int member_access_is_bool(AST* node, const char* current_function)
{
    if (!node || node->type != N_MEMBER_ACCESS) return 0;
    AST* obj = node->data.member_access.object;
    if (!obj || obj->type != N_IDENT) return 0;
    const char* obj_name = obj->data.ident.name;

    /* Try current function's params first. */
    for (size_t i = 0; i < g_func_count; i++) {
        if (current_function && strcmp(g_func_table[i].name, current_function) == 0) {
            for (size_t k = 0; k < g_func_table[i].param_count; k++) {
                if (strcmp(g_func_table[i].param_names[k], obj_name) != 0) continue;
                const char* t = g_func_table[i].param_types[k];
                if (!t) continue;
                /* Strip "struct " prefix */
                if (strncmp(t, "struct ", 7) == 0) t += 7;
                char key[256];
                snprintf(key, sizeof(key), "%s.%s", t, node->data.member_access.member);
                return bool_names_has(key);
            }
        }
    }
    return 0;
}

/* ====================== Semantic Pass ====================== */

/* Pre-codegen check: walk the AST and report problems we can catch without a
 * full type system. Currently:
 *   - call to an undefined function (typo, missing forward decl)
 *   - arity mismatch on a call to a known function
 *
 * Builtins gated by g_target->is_unsupported_call (printf, malloc, ...) are
 * NOT reported - they're handled later as comments.
 *
 * Reports go to stderr; the run still produces output so the user can see
 * what got emitted before the error. */
static int g_sema_errors = 0;

static int is_runtime_call(const char* name);  /* fwd decl, defined later */

static int is_known_runtime(const char* name)
{
    /* Calls we deliberately allow without a definition: target-recognized
     * unsupported stdlib (printf etc. — gated elsewhere) plus runtime shims. */
    if (g_target && g_target->is_unsupported_call && g_target->is_unsupported_call(name))
        return 1;
    if (is_runtime_call(name)) return 1;
    return 0;
}

static void sema_check_node(AST* node)
{
    if (!node) return;

    switch (node->type) {
    case N_CALL: {
        const char* nm = node->data.call.name;
        if (!is_known_runtime(nm)) {
            FuncSig* sig = func_table_lookup(nm);
            if (!sig) {
                fprintf(stderr, "CST error: call to undefined function '%s'\n", nm);
                g_sema_errors++;
            } else if (sig->param_count != node->data.call.arg_count) {
                /* Skip arity check if signature has a single `void` param
                 * (C `f(void)` means zero args). */
                int is_void_only = (sig->param_count == 1 &&
                                    sig->param_pointer_levels[0] == 0 &&
                                    sig->param_types[0] &&
                                    strcmp(sig->param_types[0], "void") == 0);
                if (!is_void_only || node->data.call.arg_count != 0) {
                    fprintf(stderr,
                        "CST error: '%s' expects %zu argument(s), got %zu\n",
                        nm, sig->param_count, node->data.call.arg_count);
                    g_sema_errors++;
                }
            }
        }
        for (size_t i = 0; i < node->data.call.arg_count; i++)
            sema_check_node(node->data.call.args[i]);
        break;
    }
    case N_OPERATOR:    sema_check_node(node->data.op.left); sema_check_node(node->data.op.right); break;
    case N_UNARY:       sema_check_node(node->data.unary.operand); break;
    case N_ASSIGN:      sema_check_node(node->data.assign.value); break;
    case N_DECL:        sema_check_node(node->data.decl.init_value); break;
    case N_RETURN:      sema_check_node(node->data.return_stmt.value); break;
    case N_BLOCK:
        for (size_t i = 0; i < node->data.block.count; i++)
            sema_check_node(node->data.block.statements[i]);
        break;
    case N_IF:
        sema_check_node(node->data.if_stmt.condition);
        sema_check_node(node->data.if_stmt.then_block);
        sema_check_node(node->data.if_stmt.else_block);
        break;
    case N_WHILE:
        sema_check_node(node->data.while_stmt.condition);
        sema_check_node(node->data.while_stmt.body);
        break;
    case N_DO_WHILE:
        sema_check_node(node->data.do_while_stmt.condition);
        sema_check_node(node->data.do_while_stmt.body);
        break;
    case N_FOR:
        sema_check_node(node->data.for_stmt.init);
        sema_check_node(node->data.for_stmt.condition);
        sema_check_node(node->data.for_stmt.increment);
        sema_check_node(node->data.for_stmt.body);
        break;
    case N_SWITCH:
        sema_check_node(node->data.switch_stmt.expression);
        for (size_t i = 0; i < node->data.switch_stmt.case_count; i++)
            sema_check_node(node->data.switch_stmt.case_bodies[i]);
        sema_check_node(node->data.switch_stmt.default_body);
        break;
    case N_ARRAY_ACCESS:
        sema_check_node(node->data.array_access.array);
        sema_check_node(node->data.array_access.index);
        break;
    case N_MEMBER_ACCESS: sema_check_node(node->data.member_access.object); break;
    case N_CAST:          sema_check_node(node->data.cast.expr); break;
    case N_SIZEOF:        sema_check_node(node->data.sizeof_expr.expr); break;
    case N_TERNARY:
        sema_check_node(node->data.ternary.condition);
        sema_check_node(node->data.ternary.true_expr);
        sema_check_node(node->data.ternary.false_expr);
        break;
    default: break;
    }
}

/* Check for duplicate local variable names within one function (ST has no
 * block scope - all VARs share one namespace). Warning, not fatal. */
static void sema_check_local_shadowing(AST* func_node)
{
    if (!func_node || func_node->type != N_FUNCTION) return;
    AST**  decls = NULL;
    size_t cnt = 0, cap = 0;
    collect_local_decls(func_node->data.function.body, &decls, &cnt, &cap);
    for (size_t i = 0; i < cnt; i++) {
        const char* a = decls[i]->data.decl.name;
        if (!a || !*a) continue;
        for (size_t j = i + 1; j < cnt; j++) {
            const char* b = decls[j]->data.decl.name;
            if (b && strcmp(a, b) == 0) {
                fprintf(stderr,
                    "CST warning: function '%s' has duplicate local '%s' "
                    "(C block scope flattens to one ST VAR; rename one)\n",
                    func_node->data.function.name, a);
                break;
            }
        }
    }
    free(decls);
}

static void sema_run(AST* root)
{
    g_sema_errors = 0;
    if (!root || root->type != N_PROGRAM) return;
    ProgramNode* prog = &root->data.program;
    for (size_t i = 0; i < prog->func_count; i++) {
        AST* f = prog->functions[i];
        if (!f || f->type != N_FUNCTION) continue;
        sema_check_local_shadowing(f);
        sema_check_node(f->data.function.body);
    }
}

/* Returns 1 if `ident_name` matches a current-function struct-pointer
 * parameter that was emitted as VAR_IN_OUT (i.e. already dereferenced).
 * Used to decide between `.` (VAR_IN_OUT) and `^.` (real pointer). */
static int ident_is_struct_ref_param(const char* fn_name, const char* ident_name)
{
    if (!fn_name || !ident_name) return 0;
    for (size_t i = 0; i < g_func_count; i++) {
        if (strcmp(g_func_table[i].name, fn_name) != 0) continue;
        for (size_t k = 0; k < g_func_table[i].param_count; k++) {
            if (g_func_table[i].param_pointer_levels[k] <= 0) continue;
            const char* t = g_func_table[i].param_types[k];
            if (!t || strncmp(t, "struct ", 7) != 0) continue;
            if (strcmp(g_func_table[i].param_names[k], ident_name) == 0) return 1;
        }
        return 0;
    }
    return 0;
}

/* Returns 1 if callee's param `idx` is a struct pointer (i.e. emitted as
 * VAR_IN_OUT, so caller should pass the variable directly without ADR). */
static int param_is_struct_ref(const char* callee, size_t idx)
{
    if (!callee) return 0;
    for (size_t i = 0; i < g_func_count; i++) {
        if (strcmp(g_func_table[i].name, callee) != 0) continue;
        if (idx >= g_func_table[i].param_count) return 0;
        const char* t = g_func_table[i].param_types[idx];
        int lvl = g_func_table[i].param_pointer_levels[idx];
        if (lvl > 0 && t && strncmp(t, "struct ", 7) == 0) return 1;
        return 0;
    }
    return 0;
}

 /* ====================== Output Context ====================== */

typedef struct
{
    FILE* fp;
    int indent_level;
    char current_function[256];  /* For return translation: funcName := val; RETURN; */
    int  current_is_program;     /* 1 if emitting PROGRAM (entry point); 0 for FUNCTION */
} CSTContext;

static void ctx_init(CSTContext* ctx, FILE* fp)
{
    ctx->fp = fp;
    ctx->indent_level = 0;
    ctx->current_function[0] = '\0';
    ctx->current_is_program = 0;
}

static void emit(CSTContext* ctx, const char* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    vfprintf(ctx->fp, fmt, args);
    va_end(args);
}

static void emit_indent(CSTContext* ctx)
{
    for (int i = 0; i < ctx->indent_level; i++)
        fprintf(ctx->fp, "    ");
}

static void emit_line(CSTContext* ctx, const char* fmt, ...)
{
    emit_indent(ctx);
    va_list args;
    va_start(args, fmt);
    vfprintf(ctx->fp, fmt, args);
    va_end(args);
    fprintf(ctx->fp, "\n");
}

static void emit_newline(CSTContext* ctx)
{
    fprintf(ctx->fp, "\n");
}

/* ====================== Type Mapping & Call Gating ====================== */

/* Type mapping, type sizing, and unsupported-call detection live in the
 * active target (Targets/<vendor>.c). Codegen reaches them via g_target. */

/* ====================== Forward Declarations ====================== */

static void emit_expression(CSTContext* ctx, AST* node);
static void emit_statement(CSTContext* ctx, AST* node);
static void emit_block_statements(CSTContext* ctx, AST* node);
static void collect_local_decls(AST* node, AST*** decls, size_t* count, size_t* cap);
static int  try_emit_st_for(CSTContext* ctx, AST* node);
static void emit_bool_expression(CSTContext* ctx, AST* node);

/* ====================== Expression Emission ====================== */

/* Emit a function call argument with context-aware `&` handling.
 *
 *   `&x` passed where callee param is `struct T*` (VAR_IN_OUT)  ->  `x`
 *   `&x` passed anywhere else (e.g. callee takes int*)          ->  `ADR(x)`
 *   non-`&` argument                                            ->  emit as-is
 */
static void emit_call_arg(CSTContext* ctx, const char* callee, size_t param_index, AST* arg)
{
    if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND)
    {
        if (param_is_struct_ref(callee, param_index)) {
            emit_expression(ctx, arg->data.unary.operand);
        } else {
            emit_expression(ctx, arg);   /* emits ADR(...) via existing N_UNARY handling */
        }
    }
    else
    {
        emit_expression(ctx, arg);
    }
}

/* Emit an operator token as its ST equivalent */
static void emit_operator(CSTContext* ctx, Tokens op)
{
    switch (op)
    {
    case TOKEN_PLUS:          emit(ctx, " + ");   break;
    case TOKEN_MINUS:         emit(ctx, " - ");   break;
    case TOKEN_STAR:          emit(ctx, " * ");   break;
    case TOKEN_SLASH:         emit(ctx, " / ");   break;
    case TOKEN_PERCENT:       emit(ctx, " MOD "); break;
    case TOKEN_EQUAL:         emit(ctx, " = ");   break;
    case TOKEN_NOT_EQUAL:     emit(ctx, " <> ");  break;
    case TOKEN_LESS:          emit(ctx, " < ");   break;
    case TOKEN_GREATER:       emit(ctx, " > ");   break;
    case TOKEN_LESS_EQUAL:    emit(ctx, " <= ");  break;
    case TOKEN_GREATER_EQUAL: emit(ctx, " >= ");  break;
    case TOKEN_AND:           emit(ctx, " AND "); break;
    case TOKEN_OR:            emit(ctx, " OR ");  break;
    case TOKEN_AMPERSAND:     emit(ctx, " AND "); break; /* Bitwise AND in ST */
    case TOKEN_PIPE:          emit(ctx, " OR ");  break; /* Bitwise OR in ST */
    case TOKEN_CARET:         emit(ctx, " XOR "); break;
    case TOKEN_LSHIFT:        emit(ctx, " SHL "); break;
    case TOKEN_RSHIFT:        emit(ctx, " SHR "); break;
    default:
        emit(ctx, " (* CST ERROR: unknown op %d *) ", op);
        break;
    }
}

static void emit_expression(CSTContext* ctx, AST* node)
{
    if (!node)
    {
        emit(ctx, "(* NULL EXPR *)");
        return;
    }

    switch (node->type)
    {
    case N_INTLIT:
        emit(ctx, "%d", node->data.int_lit.value);
        break;

    case N_FLOATLIT:
        /* Emit literal text verbatim — IEC ST accepts the same fractional /
         * scientific notation as C (`1.5`, `0.001`, `1.5E-3`). */
        emit(ctx, "%s", node->data.float_lit.text);
        break;

    case N_STRING_LIT:
        emit(ctx, "'%s'", node->data.string_lit.value);
        break;

    case N_CHAR_LIT:
    {
        /* Char literals become integer byte values in ST */
        int val = (unsigned char)node->data.char_lit.value;
        emit(ctx, "%d", val);
        break;
    }

    case N_IDENT:
    {
        /* For targets that bind variables to hardware devices (e.g. Mitsubishi
         * FX CST_IO pragmas), substitute the device address directly so GX
         * Works 2 sees "X0" / "Y1" rather than an undeclared label name. */
        const char* dev = g_target->resolve_var
                        ? g_target->resolve_var(node->data.ident.name)
                        : NULL;
        emit(ctx, "%s", dev ? dev : node->data.ident.name);
        break;
    }

    case N_OPERATOR:
    {
        /* Compound assignment operators used as expressions on non-ident LHS */
        Tokens op = node->data.op.op;
        if (op == TOKEN_ASSIGN)
        {
            /* Non-ident assignment expression (rare, e.g. *ptr = val) */
            emit_expression(ctx, node->data.op.left);
            emit(ctx, " := ");
            emit_expression(ctx, node->data.op.right);
        }
        else if (op == TOKEN_PLUS_ASSIGN || op == TOKEN_MINUS_ASSIGN ||
            op == TOKEN_STAR_ASSIGN || op == TOKEN_SLASH_ASSIGN)
        {
            /* These on non-ident LHS:  lhs := lhs OP rhs */
            emit_expression(ctx, node->data.op.left);
            emit(ctx, " := ");
            emit_expression(ctx, node->data.op.left);
            switch (op)
            {
            case TOKEN_PLUS_ASSIGN:  emit(ctx, " + "); break;
            case TOKEN_MINUS_ASSIGN: emit(ctx, " - "); break;
            case TOKEN_STAR_ASSIGN:  emit(ctx, " * "); break;
            case TOKEN_SLASH_ASSIGN: emit(ctx, " / "); break;
            default: break;
            }
            emit_expression(ctx, node->data.op.right);
        }
        else if (op == TOKEN_AND || op == TOKEN_OR)
        {
            /* Logical && / || — both operands must be BOOL. ST `AND`/`OR` on
             * integer operands is bitwise; on BOOL operands is logical. We force
             * BOOL operands so semantics match the C source. */
            emit(ctx, "(");
            emit_bool_expression(ctx, node->data.op.left);
            emit_operator(ctx, op);
            emit_bool_expression(ctx, node->data.op.right);
            emit(ctx, ")");
        }
        else
        {
            /* Standard binary operator */
            emit(ctx, "(");
            emit_expression(ctx, node->data.op.left);
            emit_operator(ctx, op);
            emit_expression(ctx, node->data.op.right);
            emit(ctx, ")");
        }
        break;
    }

    case N_UNARY:
    {
        Tokens op = node->data.unary.op;
        switch (op)
        {
        case TOKEN_MINUS:
            emit(ctx, "(-");
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, ")");
            break;

        case TOKEN_PLUS:
            emit_expression(ctx, node->data.unary.operand);
            break;

        case TOKEN_EXCLAIM:
            /* Logical NOT — coerce operand to BOOL. `NOT 5` is bitwise (-6 in DINT);
             * we want `NOT (5 <> 0)` which is `NOT TRUE` = `FALSE`. */
            emit(ctx, "NOT ");
            emit_bool_expression(ctx, node->data.unary.operand);
            break;

        case TOKEN_TILDE:
            /* Bitwise NOT — IEC `NOT` on integer is bitwise complement (correct). */
            emit(ctx, "NOT ");
            emit_expression(ctx, node->data.unary.operand);
            break;

        case TOKEN_AMPERSAND:
            /* Address-of: &var ? ADR(var) */
            emit(ctx, "ADR(");
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, ")");
            break;

        case TOKEN_STAR:
            /* Dereference: *ptr ? ptr^ */
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, "^");
            break;

        case TOKEN_PLUS_PLUS:
            /* Post-increment as expression: just emit the operand
             * (the statement emitter handles the actual increment) */
            emit_expression(ctx, node->data.unary.operand);
            break;

        case TOKEN_MINUS_MINUS:
            emit_expression(ctx, node->data.unary.operand);
            break;

        default:
            emit(ctx, "(* CST ERROR: unsupported unary op %d *)", op);
            emit_expression(ctx, node->data.unary.operand);
            break;
        }
        break;
    }

    case N_ASSIGN:
    {
        /* Assignment as expression:  name := value */
        {
            const char* lhs_dev = (g_target->resolve_var && node->data.assign.var_name)
                                ? g_target->resolve_var(node->data.assign.var_name)
                                : NULL;
            emit(ctx, "%s := ", lhs_dev ? lhs_dev : node->data.assign.var_name);
        }
        emit_expression(ctx, node->data.assign.value);
        break;
    }

    case N_CALL:
    {
        const char* nm = node->data.call.name;
        if (is_runtime_call(nm) &&
            emit_runtime_call(ctx, nm, node->data.call.args, node->data.call.arg_count, 0))
            break;

        int unsup = g_target->is_unsupported_call(nm);
        if (unsup)
        {
            emit(ctx, "%s", g_target->unsupported_call_comment(unsup));
        }
        else
        {
            emit(ctx, "%s(", nm);
            for (size_t i = 0; i < node->data.call.arg_count; i++)
            {
                if (i > 0) emit(ctx, ", ");
                emit_call_arg(ctx, nm, i, node->data.call.args[i]);
            }
            emit(ctx, ")");
        }
        break;
    }

    case N_FUNC_PTR_CALL:
    {
        /* Function pointer call � emit as callee^(args) or just callee(args) */
        emit_expression(ctx, node->data.func_ptr_call.callee);
        emit(ctx, "(");
        for (size_t i = 0; i < node->data.func_ptr_call.arg_count; i++)
        {
            if (i > 0) emit(ctx, ", ");
            emit_expression(ctx, node->data.func_ptr_call.args[i]);
        }
        emit(ctx, ")");
        break;
    }

    case N_ARRAY_ACCESS:
        emit_expression(ctx, node->data.array_access.array);
        emit(ctx, "[");
        emit_expression(ctx, node->data.array_access.index);
        emit(ctx, "]");
        break;

    case N_MEMBER_ACCESS:
    {
        emit_expression(ctx, node->data.member_access.object);
        /* `.` is correct when the LHS is already a struct value:
         *   - non-arrow access (`s.field`)
         *   - arrow access where the LHS is a current-function VAR_IN_OUT param
         * Otherwise the LHS is a real pointer and IEC requires `^.`. */
        int use_dot = 1;
        if (node->data.member_access.is_arrow) {
            AST* obj = node->data.member_access.object;
            int is_inout = 0;
            if (obj && obj->type == N_IDENT) {
                is_inout = ident_is_struct_ref_param(ctx->current_function,
                                                     obj->data.ident.name);
            }
            use_dot = is_inout;
        }
        emit(ctx, use_dot ? "." : "^.");
        emit(ctx, "%s", node->data.member_access.member);
        break;
    }

    case N_CAST:
    {
        /* C cast ? ST type conversion function or just emit the expr.
         * ST has type conversion functions like DINT_TO_REAL, etc.
         * For simplicity, emit as a comment with the type + the expression. */
        const char* st_type = g_target->map_type(node->data.cast.type, 0);

        /* If it's a pointer cast, just emit the expression (same memory, different view) */
        if (strstr(st_type, "POINTER") != NULL)
        {
            emit_expression(ctx, node->data.cast.expr);
        }
        else
        {
            /* Emit as ST type conversion: TYPE_TO_TYPE(expr) is platform-specific.
             * Most IEC environments accept direct assignment with implicit conversion.
             * Emit with an inline comment noting the cast. */
            emit(ctx, "(* CAST TO %s *) ", st_type);
            emit_expression(ctx, node->data.cast.expr);
        }
        break;
    }

    case N_SIZEOF:
    {
        /* sizeof(type) ? literal byte size if known, else SIZEOF(type) */
        if (node->data.sizeof_expr.expr && node->data.sizeof_expr.expr->type == N_IDENT)
        {
            const char* type_name = node->data.sizeof_expr.expr->data.ident.name;
            int sz = g_target->get_type_size(type_name);
            if (sz > 0)
                emit(ctx, "%d", sz);
            else
                emit(ctx, "SIZEOF(%s)", g_target->map_type(type_name, 0));
        }
        else
        {
            emit(ctx, "SIZEOF(");
            emit_expression(ctx, node->data.sizeof_expr.expr);
            emit(ctx, ")");
        }
        break;
    }

    case N_TERNARY:
    {
        /* C ternary ? ST SEL(condition, false_val, true_val)
         * Note: SEL's second arg is the FALSE branch, third is TRUE. */
        emit(ctx, "SEL(");
        emit_bool_expression(ctx, node->data.ternary.condition);
        emit(ctx, ", ");
        emit_expression(ctx, node->data.ternary.false_expr);
        emit(ctx, ", ");
        emit_expression(ctx, node->data.ternary.true_expr);
        emit(ctx, ")");
        break;
    }

    default:
        emit(ctx, "(* CST ERROR: unsupported expr node type %d *)", node->type);
        break;
    }
}

/* ====================== Industrial Runtime Shims (Phase 4) ====================== */

/* Names recognized by the codegen as builtins. Users get IDE-level type info
 * by `#include`ing cst_runtime.h (a declarative header); the compiler itself
 * only needs the names here and the per-target config in g_target->runtime. */

/* If `arg` is `&<expr>`, return <expr>. Else NULL.
 * Used for runtime calls like cst_timer_on(&t.handle, ...) where the timer
 * target may be a bare ident, a member access, or any other addressable lvalue.
 * The caller emits the target via emit_expression so all the existing
 * member-access / VAR_IN_OUT plumbing applies. */
static AST* extract_addr_target(AST* arg)
{
    if (!arg || arg->type != N_UNARY || arg->data.unary.op != TOKEN_AMPERSAND) return NULL;
    return arg->data.unary.operand;
}

/* Returns 1 if `name` is a CST runtime builtin (handled specially). */
static int is_runtime_call(const char* name)
{
    if (!name) return 0;
    return strcmp(name, "cst_timer_on")  == 0 ||
           strcmp(name, "cst_timer_off") == 0 ||
           strcmp(name, "cst_timer_done")== 0 ||
           strcmp(name, "cst_timer_elapsed") == 0 ||
           strcmp(name, "cst_memcpy")    == 0 ||
           strcmp(name, "cst_memset")    == 0 ||
           strcmp(name, "cst_log_int")   == 0 ||
           strcmp(name, "cst_log_str")   == 0 ||
           strcmp(name, "cst_abs")       == 0 ||
           strcmp(name, "cst_min")       == 0 ||
           strcmp(name, "cst_max")       == 0 ||
           strcmp(name, "cst_clamp")     == 0 ||
           strcmp(name, "cst_redge_update") == 0 ||
           strcmp(name, "cst_redge_fired") == 0 ||
           strcmp(name, "cst_fedge_update") == 0 ||
           strcmp(name, "cst_fedge_fired") == 0 ||
           strcmp(name, "cst_tof_start")    == 0 ||
           strcmp(name, "cst_tof_active")   == 0 ||
           strcmp(name, "cst_ctu_count")    == 0 ||
           strcmp(name, "cst_ctu_done")     == 0 ||
           strcmp(name, "cst_ctu_value")    == 0 ||
           strcmp(name, "cst_ctd_count")    == 0 ||
           strcmp(name, "cst_ctd_done")     == 0 ||
           strcmp(name, "cst_ctd_value")    == 0 ||
           strcmp(name, "cst_sqrt")         == 0 ||
           strcmp(name, "cst_pow")          == 0 ||
           strcmp(name, "cst_sin")          == 0 ||
           strcmp(name, "cst_cos")          == 0 ||
           strcmp(name, "cst_tan")          == 0 ||
           strcmp(name, "cst_ln")           == 0 ||
           strcmp(name, "cst_log10")        == 0 ||
           strcmp(name, "cst_exp")          == 0 ||
           strcmp(name, "cst_floor")        == 0 ||
           strcmp(name, "cst_ceil")         == 0;
}

/* Emit a TIME literal or runtime expression for a preset milliseconds value.
 * `as_time` controls TwinCAT-style T#5000ms vs AB-style raw DINT. */
static void emit_preset_value(CSTContext* ctx, AST* preset, int as_time)
{
    if (!as_time) {
        emit_expression(ctx, preset);
        return;
    }
    if (preset && preset->type == N_INTLIT) {
        emit(ctx, "T#%dms", preset->data.int_lit.value);
    } else {
        /* C `int` is signed -> DINT in our codegen, so use DINT_TO_TIME to
         * avoid TwinCAT's "implicit DINT->UDINT, possible sign change" warning. */
        emit(ctx, "DINT_TO_TIME(");
        emit_expression(ctx, preset);
        emit(ctx, ")");
    }
}

/* Emit a runtime call. Returns 1 on success. as_statement=1 means we own the
 * line (indent + trailing ";\n"); 0 means we're inside an expression. */
static int emit_runtime_call(CSTContext* ctx, const char* name, AST** args, size_t n, int as_statement)
{
    const plc_runtime_t* rt = &g_target->runtime;

    /* ---- Timers ---- */

    if (strcmp(name, "cst_timer_on") == 0 && n == 2) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, "(%s := TRUE, %s := ", rt->timer_in_member, rt->timer_pt_member);
        emit_preset_value(ctx, args[1], rt->timer_pt_is_time);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    if (strcmp(name, "cst_timer_off") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, "(%s := FALSE)", rt->timer_in_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    if (strcmp(name, "cst_timer_done") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->timer_done_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- Memory ops ---- */

    if (strcmp(name, "cst_memcpy") == 0 && n == 3) {
        if (as_statement) emit_indent(ctx);
        emit(ctx, "%s(", rt->memcpy_fn);
        if (rt->memcpy_dst_first) {
            emit_expression(ctx, args[0]); emit(ctx, ", ");
            emit_expression(ctx, args[1]); emit(ctx, ", ");
        } else {
            emit_expression(ctx, args[1]); emit(ctx, ", ");
            emit_expression(ctx, args[0]); emit(ctx, ", ");
        }
        emit_expression(ctx, args[2]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    if (strcmp(name, "cst_memset") == 0 && n == 3) {
        if (as_statement) emit_indent(ctx);
        emit(ctx, "%s(", rt->memset_fn);
        if (rt->memset_dst_first) {
            emit_expression(ctx, args[0]); emit(ctx, ", ");
            emit_expression(ctx, args[1]); emit(ctx, ", ");
        } else {
            emit_expression(ctx, args[1]); emit(ctx, ", ");
            emit_expression(ctx, args[0]); emit(ctx, ", ");
        }
        emit_expression(ctx, args[2]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- Edge detectors (R_TRIG / F_TRIG) ----
     * Pattern: `cst_redge_update(&e, signal);` then `if (cst_redge_fired(&e)) ...` */

    if (strcmp(name, "cst_redge_update") == 0 && n == 2) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt || !rt->redge_fb_type) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        if (rt->edge_clk_member && *rt->edge_clk_member)
            emit(ctx, "(%s := ", rt->edge_clk_member);
        else
            emit(ctx, "(");
        emit_expression(ctx, args[1]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_redge_fired") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        if (rt->edge_q_member && *rt->edge_q_member)
            emit(ctx, ".%s", rt->edge_q_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_fedge_update") == 0 && n == 2) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt || !rt->fedge_fb_type) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        if (rt->edge_clk_member && *rt->edge_clk_member)
            emit(ctx, "(%s := ", rt->edge_clk_member);
        else
            emit(ctx, "(");
        emit_expression(ctx, args[1]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_fedge_fired") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        if (rt->edge_q_member && *rt->edge_q_member)
            emit(ctx, ".%s", rt->edge_q_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- TOF (off-delay timer) ----
     * Pattern: `cst_tof_start(&tof, signal, ms);` then `if (cst_tof_active(&tof)) ...`
     * TOF.Q is TRUE while signal is true OR while signal-just-went-false-and-timer-running. */
    if (strcmp(name, "cst_tof_start") == 0 && n == 3) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt || !rt->tof_fb_type) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, "(%s := ", rt->tof_in_member);
        emit_bool_expression(ctx, args[1]);
        emit(ctx, ", %s := ", rt->tof_pt_member);
        emit_preset_value(ctx, args[2], rt->timer_pt_is_time);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_tof_active") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->tof_q_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- Counters (CTU / CTD) ----
     * count(&c, input, reset, preset) drives the FB; done()/value() read outputs. */

    if (strcmp(name, "cst_ctu_count") == 0 && n == 4) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt || !rt->ctu_fb_type) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, "(%s := ", rt->ctu_cu_member);
        emit_bool_expression(ctx, args[1]);
        emit(ctx, ", %s := ", rt->ctu_reset_member);
        emit_bool_expression(ctx, args[2]);
        emit(ctx, ", %s := ", rt->ctu_pv_member);
        emit_expression(ctx, args[3]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_ctu_done") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->ctu_q_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_ctu_value") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->ctu_cv_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_ctd_count") == 0 && n == 4) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt || !rt->ctd_fb_type) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, "(%s := ", rt->ctd_cd_member);
        emit_bool_expression(ctx, args[1]);
        emit(ctx, ", %s := ", rt->ctd_load_member);
        emit_bool_expression(ctx, args[2]);
        emit(ctx, ", %s := ", rt->ctd_pv_member);
        emit_expression(ctx, args[3]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_ctd_done") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->ctd_q_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_ctd_value") == 0 && n == 1) {
        AST* tgt = extract_addr_target(args[0]);
        if (!tgt) return 0;
        if (as_statement) emit_indent(ctx);
        emit_expression(ctx, tgt);
        emit(ctx, ".%s", rt->ctd_cv_member);
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- Extended math (IEC std names — work on TwinCAT and AB) ----
     * SQRT/LN/LOG/EXP/SIN/COS/TAN take REAL; FLOOR/CEIL via TRUNC + adjust;
     * POW = EXPT(base, exp). */
    {
        const struct { const char* c; const char* st; int arity; } m[] = {
            { "cst_sqrt",  "SQRT",  1 },
            { "cst_pow",   "EXPT",  2 },
            { "cst_sin",   "SIN",   1 },
            { "cst_cos",   "COS",   1 },
            { "cst_tan",   "TAN",   1 },
            { "cst_ln",    "LN",    1 },
            { "cst_log10", "LOG",   1 },
            { "cst_exp",   "EXP",   1 },
            { "cst_floor", "TRUNC", 1 },
            { "cst_ceil",  "TRUNC", 1 },   /* approximation; proper ceil = -TRUNC(-x) */
            { NULL, NULL, 0 }
        };
        for (size_t i = 0; m[i].c; i++) {
            if (strcmp(name, m[i].c) == 0 && (int)n == m[i].arity) {
                if (as_statement) emit_indent(ctx);
                emit(ctx, "%s(", m[i].st);
                for (size_t k = 0; k < n; k++) {
                    if (k > 0) emit(ctx, ", ");
                    emit_expression(ctx, args[k]);
                }
                emit(ctx, ")");
                if (as_statement) emit(ctx, ";\n");
                return 1;
            }
        }
    }

    /* ---- Math / utility (target-portable; IEC std functions) ---- */

    if (strcmp(name, "cst_abs") == 0 && n == 1) {
        if (as_statement) emit_indent(ctx);
        emit(ctx, "ABS(");
        emit_expression(ctx, args[0]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_min") == 0 && n == 2) {
        if (as_statement) emit_indent(ctx);
        emit(ctx, "MIN(");
        emit_expression(ctx, args[0]); emit(ctx, ", ");
        emit_expression(ctx, args[1]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_max") == 0 && n == 2) {
        if (as_statement) emit_indent(ctx);
        emit(ctx, "MAX(");
        emit_expression(ctx, args[0]); emit(ctx, ", ");
        emit_expression(ctx, args[1]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }
    if (strcmp(name, "cst_clamp") == 0 && n == 3) {
        /* IEC LIMIT(MN, IN, MX) — order: lower, value, upper.
         * cst_clamp(value, lo, hi) -> LIMIT(lo, value, hi). */
        if (as_statement) emit_indent(ctx);
        emit(ctx, "LIMIT(");
        emit_expression(ctx, args[1]); emit(ctx, ", ");
        emit_expression(ctx, args[0]); emit(ctx, ", ");
        emit_expression(ctx, args[2]);
        emit(ctx, ")");
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    /* ---- Logging ---- */

    if (strcmp(name, "cst_log_str") == 0 && n == 1) {
        if (as_statement) emit_indent(ctx);
        if (!rt->log_supported || !rt->log_str_fn || !*rt->log_str_fn) {
            emit(ctx, "(* CST_LOG: ");
            emit_expression(ctx, args[0]);
            emit(ctx, " - target has no logging shim, use MSG instruction *)");
        } else {
            /* TwinCAT: ADSLOGSTR(ADSLOG_MSGTYPE_HINT OR ADSLOG_MSGTYPE_LOG, '%s', msg) */
            emit(ctx, "%s(ADSLOG_MSGTYPE_HINT OR ADSLOG_MSGTYPE_LOG, '%%s', ", rt->log_str_fn);
            emit_expression(ctx, args[0]);
            emit(ctx, ")");
        }
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    if (strcmp(name, "cst_log_int") == 0 && n == 1) {
        if (as_statement) emit_indent(ctx);
        if (!rt->log_supported || !rt->log_int_fn || !*rt->log_int_fn) {
            emit(ctx, "(* CST_LOG: int=");
            emit_expression(ctx, args[0]);
            emit(ctx, " - target has no logging shim, use MSG instruction *)");
        } else {
            /* TwinCAT: ADSLOGSTR with %d format */
            emit(ctx, "%s(ADSLOG_MSGTYPE_HINT OR ADSLOG_MSGTYPE_LOG, '%%d', ", rt->log_int_fn);
            emit_expression(ctx, args[0]);
            emit(ctx, ")");
        }
        if (as_statement) emit(ctx, ";\n");
        return 1;
    }

    return 0;
}

/* ====================== BOOL coercion ====================== */

/* IEC 61131-3 IF/WHILE/etc. require BOOL operands. C lets `if (x)` mean
 * `if (x != 0)` for any int. Wrap non-bool-producing expressions with `<> 0`. */
static int expr_produces_bool(AST* node)
{
    if (!node) return 0;
    if (node->type == N_OPERATOR) {
        switch (node->data.op.op) {
            case TOKEN_EQUAL:
            case TOKEN_NOT_EQUAL:
            case TOKEN_LESS:
            case TOKEN_GREATER:
            case TOKEN_LESS_EQUAL:
            case TOKEN_GREATER_EQUAL:
            case TOKEN_AND:
            case TOKEN_OR:
                return 1;
            default:
                return 0;
        }
    }
    if (node->type == N_UNARY && node->data.unary.op == TOKEN_EXCLAIM)
        return 1;
    /* Runtime calls that return BOOL — wrapping with `<> 0` would be a
     * type error since their result is already BOOL. */
    if (node->type == N_CALL && node->data.call.name) {
        const char* nm = node->data.call.name;
        if (strcmp(nm, "cst_timer_done")  == 0) return 1;
        if (strcmp(nm, "cst_redge_fired") == 0) return 1;
        if (strcmp(nm, "cst_fedge_fired") == 0) return 1;
        if (strcmp(nm, "cst_tof_active")  == 0) return 1;
        if (strcmp(nm, "cst_ctu_done")    == 0) return 1;
        if (strcmp(nm, "cst_ctd_done")    == 0) return 1;
    }
    /* Bare ident declared as `bool` */
    if (node->type == N_IDENT && bool_names_has(node->data.ident.name))
        return 1;
    /* Hardware device addresses resolved via target (e.g. Mitsubishi FX X/Y
     * coils).  X inputs and Y outputs are always 1-bit BOOL devices — no
     * `<> 0` wrap needed or valid. */
    if (node->type == N_IDENT && node->data.ident.name && g_target->resolve_var) {
        const char* dev = g_target->resolve_var(node->data.ident.name);
        if (dev && (dev[0] == 'X' || dev[0] == 'Y' || dev[0] == 'M'))
            return 1;
    }
    return 0;
}

static void emit_bool_expression(CSTContext* ctx, AST* node)
{
    /* Already-bool expressions emit as-is; non-bool get wrapped in `(... <> 0)`.
     * Parens are required because IEC `<>` binds tighter than NOT/AND/OR — without
     * them `NOT x <> 0` parses as `(NOT x) <> 0`. */
    if (expr_produces_bool(node)) {
        emit_expression(ctx, node);
    } else {
        emit(ctx, "(");
        emit_expression(ctx, node);
        emit(ctx, " <> 0)");
    }
}

/* ====================== Compile-time constant detection ====================== */

/* Returns 1 if `node` is a literal that ST will accept as a VAR-block init.
 * IEC permits initializers that are constant expressions; we conservatively
 * approve simple literals plus +/-literal and the BOOL literals 0/1 (handled
 * via N_INTLIT). Anything that requires evaluation (calls, idents, operators)
 * stays as a runtime assignment. */
static int is_constant_init(AST* node)
{
    if (!node) return 0;
    switch (node->type) {
        case N_INTLIT:
        case N_FLOATLIT:
        case N_CHAR_LIT:
        case N_STRING_LIT:
            return 1;
        case N_UNARY:
            if (node->data.unary.op == TOKEN_MINUS || node->data.unary.op == TOKEN_PLUS)
                return is_constant_init(node->data.unary.operand);
            return 0;
        default:
            return 0;
    }
}

/* ====================== FOR loop pattern recognition ====================== */

/* Try to emit a C `for` loop as a real IEC `FOR i := start TO end BY step DO`.
 * Recognized shape:
 *   init: `v = <expr>`
 *   cond: `v <op> <bound>`              op in { <, <=, >, >= }
 *   incr: `v = v + <intlit>` / `v = v - <intlit>` / `v++` / `v--`
 * Returns 1 on success, 0 to fall back to WHILE-form.
 *
 * Closed-interval rewrite:
 *   `i < n`   ascending  ->  TO (n - 1)
 *   `i <= n`  ascending  ->  TO n
 *   `i > n`   descending ->  TO (n + 1) BY -step
 *   `i >= n`  descending ->  TO n       BY -step
 */
static int try_emit_st_for(CSTContext* ctx, AST* node)
{
    AST* init = node->data.for_stmt.init;
    AST* cond = node->data.for_stmt.condition;
    AST* incr = node->data.for_stmt.increment;
    if (!init || !cond || !incr) return 0;

    if (init->type != N_ASSIGN) return 0;
    const char* var = init->data.assign.var_name;
    AST* start = init->data.assign.value;

    if (cond->type != N_OPERATOR) return 0;
    if (cond->data.op.left == NULL || cond->data.op.left->type != N_IDENT) return 0;
    if (strcmp(cond->data.op.left->data.ident.name, var) != 0) return 0;
    Tokens relop = cond->data.op.op;
    AST* bound = cond->data.op.right;

    int step = 0;
    int ascending = 0;

    if (incr->type == N_UNARY &&
        (incr->data.unary.op == TOKEN_PLUS_PLUS || incr->data.unary.op == TOKEN_MINUS_MINUS) &&
        incr->data.unary.operand && incr->data.unary.operand->type == N_IDENT &&
        strcmp(incr->data.unary.operand->data.ident.name, var) == 0)
    {
        step = 1;
        ascending = (incr->data.unary.op == TOKEN_PLUS_PLUS);
    }
    else if (incr->type == N_ASSIGN &&
             strcmp(incr->data.assign.var_name, var) == 0 &&
             incr->data.assign.value && incr->data.assign.value->type == N_OPERATOR)
    {
        AST* rhs = incr->data.assign.value;
        if (rhs->data.op.left && rhs->data.op.left->type == N_IDENT &&
            strcmp(rhs->data.op.left->data.ident.name, var) == 0 &&
            rhs->data.op.right && rhs->data.op.right->type == N_INTLIT)
        {
            int amt = rhs->data.op.right->data.int_lit.value;
            if (amt <= 0) return 0;
            if (rhs->data.op.op == TOKEN_PLUS)        { step = amt; ascending = 1; }
            else if (rhs->data.op.op == TOKEN_MINUS)  { step = amt; ascending = 0; }
            else return 0;
        }
        else return 0;
    }
    else return 0;

    int incl;
    int dir_match;
    switch (relop) {
        case TOKEN_LESS:          incl = 0; dir_match =  ascending; break;
        case TOKEN_LESS_EQUAL:    incl = 1; dir_match =  ascending; break;
        case TOKEN_GREATER:       incl = 0; dir_match = !ascending; break;
        case TOKEN_GREATER_EQUAL: incl = 1; dir_match = !ascending; break;
        default: return 0;
    }
    if (!dir_match) return 0;

    emit_indent(ctx);
    emit(ctx, "FOR %s := ", var);
    emit_expression(ctx, start);
    emit(ctx, " TO ");
    if (incl) {
        emit_expression(ctx, bound);
    } else {
        if (bound && bound->type == N_INTLIT) {
            emit(ctx, "%d", bound->data.int_lit.value + (ascending ? -1 : 1));
        } else {
            emit(ctx, "(");
            emit_expression(ctx, bound);
            emit(ctx, ascending ? " - 1)" : " + 1)");
        }
    }
    if (!ascending)
        emit(ctx, " BY -%d", step);
    else if (step != 1)
        emit(ctx, " BY %d", step);
    emit(ctx, " DO\n");

    ctx->indent_level++;
    emit_block_statements(ctx, node->data.for_stmt.body);
    ctx->indent_level--;
    emit_indent(ctx);
    emit(ctx, "END_FOR;\n");
    return 1;
}

/* ====================== Statement Emission ====================== */

static void emit_statement(CSTContext* ctx, AST* node)
{
    if (!node) return;

    switch (node->type)
    {
    case N_DECL:
        /* Declarations are emitted in pass 1 (VAR block).
         * Compile-time-constant inits also go in the VAR block (handled by
         * emit_var_decl_line). Runtime-expression inits emit as assignments
         * here, in pass 2 — the VAR block leaves the variable uninitialized. */
        if (node->data.decl.init_value && !node->data.decl.is_extern &&
            !node->data.decl.is_const &&
            !is_constant_init(node->data.decl.init_value))
        {
            emit_indent(ctx);
            emit(ctx, "%s := ", node->data.decl.name);
            emit_expression(ctx, node->data.decl.init_value);
            emit(ctx, ";\n");
        }
        break;

    case N_ASSIGN: {
        emit_indent(ctx);
        {
            const char* lhs_dev = (g_target->resolve_var && node->data.assign.var_name)
                                ? g_target->resolve_var(node->data.assign.var_name)
                                : NULL;
            emit(ctx, "%s := ", lhs_dev ? lhs_dev : node->data.assign.var_name);
        }
        /* If RHS is BOOL and LHS is NOT a known BOOL, wrap with BOOL_TO_DINT:
         * TwinCAT/AB strict-type modes reject `DINT := BOOL` otherwise.
         * If LHS is `bool`-declared, no wrap — types match. */
        int rhs_bool = expr_produces_bool(node->data.assign.value);
        int lhs_bool = bool_names_has(node->data.assign.var_name);
        if (rhs_bool && !lhs_bool) {
            emit(ctx, "BOOL_TO_DINT(");
            emit_expression(ctx, node->data.assign.value);
            emit(ctx, ")");
        } else {
            emit_expression(ctx, node->data.assign.value);
        }
        emit(ctx, ";\n");
        break;
    }

    case N_OPERATOR:
    {
        /* Compound assignment on non-ident LHS emitted as operator node */
        Tokens op = node->data.op.op;
        if (op == TOKEN_ASSIGN || op == TOKEN_PLUS_ASSIGN || op == TOKEN_MINUS_ASSIGN ||
            op == TOKEN_STAR_ASSIGN || op == TOKEN_SLASH_ASSIGN)
        {
            emit_indent(ctx);
            emit_expression(ctx, node);
            emit(ctx, ";\n");
        }
        else
        {
            /* Expression statement � binary op with side effects? */
            emit_indent(ctx);
            emit_expression(ctx, node);
            emit(ctx, ";\n");
        }
        break;
    }

    case N_UNARY:
    {
        Tokens op = node->data.unary.op;
        if (op == TOKEN_PLUS_PLUS)
        {
            /* i++ ? i := i + 1; */
            emit_indent(ctx);
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, " := ");
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, " + 1;\n");
        }
        else if (op == TOKEN_MINUS_MINUS)
        {
            /* i-- ? i := i - 1; */
            emit_indent(ctx);
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, " := ");
            emit_expression(ctx, node->data.unary.operand);
            emit(ctx, " - 1;\n");
        }
        else
        {
            /* Other unary as expression statement */
            emit_indent(ctx);
            emit_expression(ctx, node);
            emit(ctx, ";\n");
        }
        break;
    }

    case N_CALL:
    {
        const char* nm = node->data.call.name;
        if (is_runtime_call(nm) &&
            emit_runtime_call(ctx, nm, node->data.call.args, node->data.call.arg_count, 1))
            break;

        int unsup = g_target->is_unsupported_call(nm);
        if (unsup)
        {
            emit_indent(ctx);
            emit(ctx, "%s\n", g_target->unsupported_call_comment(unsup));
        }
        else
        {
            emit_indent(ctx);
            emit(ctx, "%s(", nm);
            for (size_t i = 0; i < node->data.call.arg_count; i++)
            {
                if (i > 0) emit(ctx, ", ");
                emit_call_arg(ctx, nm, i, node->data.call.args[i]);
            }
            emit(ctx, ");\n");
        }
        break;
    }

    case N_FUNC_PTR_CALL:
    {
        emit_indent(ctx);
        emit_expression(ctx, node);
        emit(ctx, ";\n");
        break;
    }

    case N_IF:
    {
        emit_indent(ctx);
        emit(ctx, "IF ");
        emit_bool_expression(ctx, node->data.if_stmt.condition);
        emit(ctx, " THEN\n");
        ctx->indent_level++;
        emit_block_statements(ctx, node->data.if_stmt.then_block);
        ctx->indent_level--;

        if (node->data.if_stmt.else_block)
        {
            /* Check for else-if chain */
            if (node->data.if_stmt.else_block->type == N_IF)
            {
                emit_indent(ctx);
                emit(ctx, "ELS");
                /* Recurse � emit_if_chain will handle the ELSIF */
                AST* elseif = node->data.if_stmt.else_block;
                emit(ctx, "IF ");
                emit_bool_expression(ctx, elseif->data.if_stmt.condition);
                emit(ctx, " THEN\n");
                ctx->indent_level++;
                emit_block_statements(ctx, elseif->data.if_stmt.then_block);
                ctx->indent_level--;
                if (elseif->data.if_stmt.else_block)
                {
                    if (elseif->data.if_stmt.else_block->type == N_IF)
                    {
                        /* Deep else-if: emit as separate statement inside ELSE for safety */
                        emit_indent(ctx);
                        emit(ctx, "ELSE\n");
                        ctx->indent_level++;
                        emit_statement(ctx, elseif->data.if_stmt.else_block);
                        ctx->indent_level--;
                    }
                    else
                    {
                        emit_indent(ctx);
                        emit(ctx, "ELSE\n");
                        ctx->indent_level++;
                        emit_block_statements(ctx, elseif->data.if_stmt.else_block);
                        ctx->indent_level--;
                    }
                }
            }
            else
            {
                emit_indent(ctx);
                emit(ctx, "ELSE\n");
                ctx->indent_level++;
                emit_block_statements(ctx, node->data.if_stmt.else_block);
                ctx->indent_level--;
            }
        }

        emit_indent(ctx);
        emit(ctx, "END_IF;\n");
        break;
    }

    case N_WHILE:
    {
        emit_indent(ctx);
        emit(ctx, "WHILE ");
        emit_bool_expression(ctx, node->data.while_stmt.condition);
        emit(ctx, " DO\n");
        ctx->indent_level++;
        emit_block_statements(ctx, node->data.while_stmt.body);
        ctx->indent_level--;
        emit_indent(ctx);
        emit(ctx, "END_WHILE;\n");
        break;
    }

    case N_DO_WHILE:
    {
        /* do { body } while (cond); ? REPEAT body UNTIL NOT cond END_REPEAT; */
        emit_indent(ctx);
        emit(ctx, "REPEAT\n");
        ctx->indent_level++;
        emit_block_statements(ctx, node->data.do_while_stmt.body);
        ctx->indent_level--;
        emit_indent(ctx);
        emit(ctx, "UNTIL NOT (");
        emit_bool_expression(ctx, node->data.do_while_stmt.condition);
        emit(ctx, ")\n");
        emit_indent(ctx);
        emit(ctx, "END_REPEAT;\n");
        break;
    }

    case N_FOR:
    {
        /* Try to recognize the canonical counted-loop shape and emit a real
         * IEC `FOR i := start TO end BY step DO`. Anything that doesn't match
         * falls back to init+WHILE+incr+END_WHILE. */
        if (try_emit_st_for(ctx, node)) break;

         /* Emit init */
        if (node->data.for_stmt.init)
            emit_statement(ctx, node->data.for_stmt.init);

        emit_indent(ctx);
        emit(ctx, "WHILE ");
        if (node->data.for_stmt.condition)
            emit_expression(ctx, node->data.for_stmt.condition);
        else
            emit(ctx, "TRUE");
        emit(ctx, " DO\n");

        ctx->indent_level++;
        emit_block_statements(ctx, node->data.for_stmt.body);

        /* Emit increment */
        if (node->data.for_stmt.increment)
            emit_statement(ctx, node->data.for_stmt.increment);

        ctx->indent_level--;
        emit_indent(ctx);
        emit(ctx, "END_WHILE;\n");
        break;
    }

    case N_SWITCH:
    {
        /* switch ? CASE expr OF
         * Fallthrough between cases (no `break`/`return`) has no IEC equivalent —
         * reject at codegen time rather than silently producing wrong code. */
        emit_indent(ctx);
        emit(ctx, "CASE ");
        emit_expression(ctx, node->data.switch_stmt.expression);
        emit(ctx, " OF\n");

        ctx->indent_level++;
        for (size_t i = 0; i < node->data.switch_stmt.case_count; i++)
        {
            emit_indent(ctx);
            emit_expression(ctx, node->data.switch_stmt.case_values[i]);
            emit(ctx, ":\n");

            ctx->indent_level++;
            /* Emit case body, skipping break statements (ST has no fallthrough) */
            AST* body = node->data.switch_stmt.case_bodies[i];
            int has_terminator = 0;
            if (body && body->type == N_BLOCK)
            {
                for (size_t j = 0; j < body->data.block.count; j++)
                {
                    AST* stmt = body->data.block.statements[j];
                    if (stmt->type == N_BREAK) { has_terminator = 1; continue; }
                    if (stmt->type == N_RETURN) has_terminator = 1;
                    emit_statement(ctx, stmt);
                }
            }
            else if (body)
            {
                emit_statement(ctx, body);
                if (body->type == N_BREAK || body->type == N_RETURN)
                    has_terminator = 1;
            }
            if (!has_terminator)
            {
                emit_indent(ctx);
                emit(ctx, "(* CST ERROR: switch fallthrough is not supported in IEC 61131-3; add explicit `break` *)\n");
                fprintf(stderr, "CST error: switch fallthrough in case (no terminating break/return)\n");
            }
            ctx->indent_level--;
        }

        if (node->data.switch_stmt.default_body)
        {
            emit_indent(ctx);
            emit(ctx, "ELSE\n");
            ctx->indent_level++;
            AST* defbody = node->data.switch_stmt.default_body;
            if (defbody->type == N_BLOCK)
            {
                for (size_t j = 0; j < defbody->data.block.count; j++)
                {
                    AST* stmt = defbody->data.block.statements[j];
                    if (stmt->type == N_BREAK) continue;
                    emit_statement(ctx, stmt);
                }
            }
            else
            {
                emit_statement(ctx, defbody);
            }
            ctx->indent_level--;
        }

        ctx->indent_level--;
        emit_indent(ctx);
        emit(ctx, "END_CASE;\n");
        break;
    }

    case N_RETURN:
    {
        /* For FUNCTION:  return x;  ->  funcName := x; RETURN;
         * For PROGRAM:   return x;  ->  (nothing)  — PROGRAMs have no return value;
         *                                             `return 0;` at end of main() is dropped. */
        if (ctx->current_is_program)
            break;  /* suppress entirely inside PROGRAM */

        if (node->data.return_stmt.value)
        {
            if (ctx->current_function[0] != '\0' &&
                strcmp(ctx->current_function, "void") != 0)
            {
                emit_indent(ctx);
                emit(ctx, "%s := ", ctx->current_function);
                emit_expression(ctx, node->data.return_stmt.value);
                emit(ctx, ";\n");
            }
        }
        emit_indent(ctx);
        emit(ctx, "RETURN;\n");
        break;
    }

    case N_BLOCK:
        emit_block_statements(ctx, node);
        break;

    case N_BREAK:
        emit_indent(ctx);
        emit(ctx, "EXIT;\n");
        break;

    case N_CONTINUE:
        /* ST has no CONTINUE. Emit a comment. Some PLCs support CONTINUE as extension. */
        emit_indent(ctx);
        emit(ctx, "(* CONTINUE - not standard IEC 61131-3, check PLC support *)\n");
        emit_indent(ctx);
        emit(ctx, "CONTINUE;\n");
        break;

    case N_ASM:
        emit_indent(ctx);
        emit(ctx, "(* INLINE ASSEMBLY NOT SUPPORTED IN ST *)\n");
        emit_indent(ctx);
        emit(ctx, "(* %s *)\n", node->data.asm_stmt.assembly_code);
        break;

    case N_CAST:
        /* Cast as statement � emit the inner expression */
        emit_indent(ctx);
        emit_expression(ctx, node);
        emit(ctx, ";\n");
        break;

    case N_IDENT:
        /* Bare identifier as statement � unusual but legal */
        emit_indent(ctx);
        emit(ctx, "%s;\n", node->data.ident.name);
        break;

    case N_INTLIT:
        /* Bare literal as statement */
        emit_indent(ctx);
        emit(ctx, "%d;\n", node->data.int_lit.value);
        break;

    default:
        emit_indent(ctx);
        emit(ctx, "(* CST ERROR: unsupported statement node type %d *)\n", node->type);
        break;
    }
}

/* Emit statements from a block node or a single statement */
static void emit_block_statements(CSTContext* ctx, AST* node)
{
    if (!node) return;

    if (node->type == N_BLOCK)
    {
        for (size_t i = 0; i < node->data.block.count; i++)
            emit_statement(ctx, node->data.block.statements[i]);
    }
    else
    {
        /* Single statement (e.g. if without braces) */
        emit_statement(ctx, node);
    }
}

/* ====================== Declaration Collection (Pass 1 Helpers) ====================== */

/* Recursively collect all N_DECL nodes from a block/statement tree */
static void collect_local_decls(AST* node, AST*** decls, size_t* count, size_t* cap)
{
    if (!node) return;

    switch (node->type)
    {
    case N_DECL:
        if (*count >= *cap)
        {
            *cap = (*cap == 0) ? 16 : (*cap) * 2;
            *decls = (AST**)realloc(*decls, (*cap) * sizeof(AST*));
        }
        (*decls)[(*count)++] = node;
        break;

    case N_BLOCK:
        for (size_t i = 0; i < node->data.block.count; i++)
            collect_local_decls(node->data.block.statements[i], decls, count, cap);
        break;

    case N_IF:
        collect_local_decls(node->data.if_stmt.then_block, decls, count, cap);
        collect_local_decls(node->data.if_stmt.else_block, decls, count, cap);
        break;

    case N_WHILE:
        collect_local_decls(node->data.while_stmt.body, decls, count, cap);
        break;

    case N_DO_WHILE:
        collect_local_decls(node->data.do_while_stmt.body, decls, count, cap);
        break;

    case N_FOR:
        collect_local_decls(node->data.for_stmt.init, decls, count, cap);
        collect_local_decls(node->data.for_stmt.body, decls, count, cap);
        break;

    case N_SWITCH:
        for (size_t i = 0; i < node->data.switch_stmt.case_count; i++)
            collect_local_decls(node->data.switch_stmt.case_bodies[i], decls, count, cap);
        collect_local_decls(node->data.switch_stmt.default_body, decls, count, cap);
        break;

    default:
        break;
    }
}

/* Emit a single VAR declaration line */
static void emit_var_decl_line(CSTContext* ctx, AST* decl)
{
    if (!decl || decl->type != N_DECL) return;

    DeclNode* d = &decl->data.decl;

    /* Skip unnamed or empty-named declarations — nothing useful to emit. */
    if (!d->name || d->name[0] == '\0') return;

    /* Skip variables bound to hardware device addresses (e.g. Mitsubishi FX
     * CST_IO pragma). They are emitted as "X0"/"Y1" in expressions directly,
     * so no VAR declaration is needed or valid in GX Works 2. */
    if (g_target->resolve_var && g_target->resolve_var(d->name))
        return;

    /* Skip extern declarations� they're forward references */
    if (d->is_extern) return;

    emit_indent(ctx);

    /* Array declaration (single or multi-dim).
     *   C   `int m[3][4];`   -> ST `m : ARRAY[0..2, 0..3] OF DINT;` */
    if (d->array_dim_count > 0 || d->array_size)
    {
        size_t ndim = d->array_dim_count > 0 ? d->array_dim_count : 1;
        emit(ctx, "%s : ARRAY[", d->name);
        for (size_t k = 0; k < ndim; k++)
        {
            AST* dk = (d->array_dim_count > 0) ? d->array_dims[k] : d->array_size;
            if (k > 0) emit(ctx, ", ");
            emit(ctx, "0..");
            if (dk && dk->type == N_INTLIT)
                emit(ctx, "%d", dk->data.int_lit.value - 1);
            else if (dk)
            {
                emit(ctx, "(");
                emit_expression(ctx, dk);
                emit(ctx, " - 1)");
            }
            else
                emit(ctx, "0");   /* unsized [] - degenerate; emit minimal valid */
        }
        emit(ctx, "] OF %s", g_target->map_type(d->type, d->pointer_level));
    }
    /* Function pointer declaration */
    else if (d->is_function_pointer)
    {
        /* In ST, function pointers don't exist natively.
         * Emit as a POINTER TO BYTE with a comment. */
        emit(ctx, "%s : POINTER TO BYTE (* function pointer *)", d->name);
    }
    /* Regular declaration */
    else
    {
        emit(ctx, "%s : %s", d->name, g_target->map_type(d->type, d->pointer_level));
    }

    /* Initial value: any compile-time constant goes inline in the VAR
     * declaration so the variable starts at the right value at PLC scan zero.
     * Runtime expressions (calls, computed exprs) stay as body assignments. */
    if (d->init_value && (d->is_const || is_constant_init(d->init_value)))
    {
        emit(ctx, " := ");
        emit_expression(ctx, d->init_value);
    }

    emit(ctx, ";\n");
}

/* ====================== Function Emission ====================== */

/* Check if first param is a struct pointer (OOP pattern ? FUNCTION_BLOCK) */
static int is_function_block_pattern(AST* func)
{
    if (func->data.function.param_count == 0) return 0;
    AST* first = func->data.function.params[0];
    if (first->type != N_DECL) return 0;
    DeclNode* d = &first->data.decl;
    if (d->pointer_level >= 1 && strncmp(d->type, "struct ", 7) == 0)
        return 1;
    return 0;
}

/* Check if this is the program entry point (like _start / jmp main) */
static int is_entry_point(AST* func)
{
    const char* name = func->data.function.name;
    if (strcmp(name, "main") == 0 || strcmp(name, "kernel_main") == 0 ||
        strcmp(name, "PLC_PRG") == 0)
        return 1;
    return 0;
}

static void emit_function(CSTContext* ctx, AST* func)
{
    if (!func || func->type != N_FUNCTION) return;

    FunctionNode* f = &func->data.function;

    /* Skip forward declarations (no body) */
    if (!f->body) return;

    /* Skip extern functions */
    if (f->is_extern) return;

    int is_entry = is_entry_point(func);
    const char* ret_st = g_target->map_type(f->return_type, 0);

    /* Save current function name for RETURN translation */
    strncpy(ctx->current_function, f->name, sizeof(ctx->current_function) - 1);
    ctx->current_function[sizeof(ctx->current_function) - 1] = '\0';
    ctx->current_is_program = is_entry;

    /* Emit PROGRAM or FUNCTION header.
     * Entry point (main) -> PROGRAM MAIN (what PlcTask calls).
     * Everything else     -> FUNCTION with VAR_IN_OUT for struct ptr params. */
    if (is_entry)
    {
        emit_line(ctx, "PROGRAM MAIN");
    }
    else
    {
        if (strcmp(ret_st, "VOID") == 0)
            emit_line(ctx, "FUNCTION %s", f->name);
        else
            emit_line(ctx, "FUNCTION %s : %s", f->name, ret_st);
    }

    /* ---- VAR_INPUT / VAR_IN_OUT for parameters ---- */
    if (f->param_count > 0)
    {
        /* Pointer params (except char* = STRING) become VAR_IN_OUT (pass by ref).
         * Value params become VAR_INPUT. Simple as that.
         * `void` value params from `f(void)` are dropped — they signal "no params" in C. */
        int has_input = 0, has_inout = 0;

        for (size_t i = 0; i < f->param_count; i++)
        {
            DeclNode* d = &f->params[i]->data.decl;
            if (d->pointer_level == 0 && d->type && strcmp(d->type, "void") == 0)
                continue;  /* `f(void)` - no real params */
            if (!d->name || d->name[0] == '\0')
                continue;  /* unnamed param — drop it */
            if (d->pointer_level > 0 && strcmp(d->type, "char") != 0)
                has_inout = 1;
            else
                has_input = 1;
        }

        /* Emit VAR_IN_OUT block if needed */
        if (has_inout)
        {
            emit_line(ctx, "VAR_IN_OUT");
            ctx->indent_level++;

            for (size_t i = 0; i < f->param_count; i++)
            {
                DeclNode* d = &f->params[i]->data.decl;
                if (!d->name || d->name[0] == '\0') continue;
                if (d->pointer_level == 0 && d->type && strcmp(d->type, "void") == 0) continue;
                if (d->pointer_level > 0 && strcmp(d->type, "char") != 0)
                {
                    /* Single pointer -> pass by reference (strip pointer).
                     * Multi-pointer -> keep remaining pointer levels. */
                    const char* param_type;
                    if (d->pointer_level > 1)
                        param_type = g_target->map_type(d->type, d->pointer_level - 1);
                    else
                        param_type = g_target->map_type(d->type, 0);

                    /* Strip "struct " prefix for clean ST type names */
                    if (strncmp(param_type, "struct ", 7) == 0)
                        param_type += 7;

                    emit_indent(ctx);
                    emit(ctx, "%s : %s;\n",
                        (d->name && strlen(d->name) > 0) ? d->name : "param",
                        param_type);
                }
            }

            ctx->indent_level--;
            emit_line(ctx, "END_VAR");
        }

        /* Emit VAR_INPUT block if needed */
        if (has_input)
        {
            emit_line(ctx, "VAR_INPUT");
            ctx->indent_level++;

            for (size_t i = 0; i < f->param_count; i++)
            {
                DeclNode* d = &f->params[i]->data.decl;
                if (d->pointer_level == 0 && d->type && strcmp(d->type, "void") == 0)
                    continue;
                if (!d->name || d->name[0] == '\0')
                    continue;  /* unnamed param — drop it */
                if (d->pointer_level == 0 || strcmp(d->type, "char") == 0)
                {
                    emit_var_decl_line(ctx, f->params[i]);
                }
            }

            ctx->indent_level--;
            emit_line(ctx, "END_VAR");
        }
    }

    /* ---- VAR block for local variables (pass 1 of function body) ---- */
    AST** local_decls = NULL;
    size_t local_count = 0, local_cap = 0;
    collect_local_decls(f->body, &local_decls, &local_count, &local_cap);

    if (local_count > 0)
    {
        /* C `static` locals require persistent state across calls. IEC
         * FUNCTIONs are stateless — the only construct with persistent state
         * is FUNCTION_BLOCK. Until full FB lowering lands, refuse to silently
         * miscompile this. */
        for (size_t i = 0; i < local_count; i++)
        {
            if (local_decls[i]->data.decl.is_static)
            {
                fprintf(stderr,
                    "CST error: function '%s' uses `static` local '%s' which "
                    "requires FUNCTION_BLOCK lowering (not yet implemented).\n",
                    f->name,
                    local_decls[i]->data.decl.name ? local_decls[i]->data.decl.name : "?");
                emit_indent(ctx);
                emit(ctx, "(* CST ERROR: static local '%s' needs FB lowering *)\n",
                    local_decls[i]->data.decl.name ? local_decls[i]->data.decl.name : "?");
            }
        }

        /* Separate const locals from regular locals */
        int has_const = 0, has_regular = 0;
        for (size_t i = 0; i < local_count; i++)
        {
            if (local_decls[i]->data.decl.is_const)
                has_const = 1;
            else
                has_regular = 1;
        }

        if (has_const)
        {
            emit_line(ctx, "VAR CONSTANT");
            ctx->indent_level++;
            for (size_t i = 0; i < local_count; i++)
            {
                if (local_decls[i]->data.decl.is_const)
                    emit_var_decl_line(ctx, local_decls[i]);
            }
            ctx->indent_level--;
            emit_line(ctx, "END_VAR");
        }

        if (has_regular)
        {
            emit_line(ctx, "VAR");
            ctx->indent_level++;
            for (size_t i = 0; i < local_count; i++)
            {
                if (!local_decls[i]->data.decl.is_const)
                    emit_var_decl_line(ctx, local_decls[i]);
            }
            ctx->indent_level--;
            emit_line(ctx, "END_VAR");
        }
    }

    if (local_decls) free(local_decls);

    /* ---- Function body (pass 2: logic only) ---- */
    ctx->indent_level++;
    emit_block_statements(ctx, f->body);
    ctx->indent_level--;

    /* Close function */
    if (is_entry)
        emit_line(ctx, "END_PROGRAM");
    else
        emit_line(ctx, "END_FUNCTION");

    emit_newline(ctx);
    ctx->current_function[0] = '\0';
    ctx->current_is_program = 0;
}

/* ====================== Struct / Enum / Typedef Emission ====================== */

static void emit_struct_decl(CSTContext* ctx, AST* node)
{
    if (!node || node->type != N_STRUCT_DECL) return;
    StructDeclNode* s = &node->data.struct_decl;

    if (!s->name) return;  /* Anonymous struct without typedef � skip */

    emit_line(ctx, "TYPE %s :", s->name);
    emit_line(ctx, "STRUCT");
    ctx->indent_level++;

    for (size_t i = 0; i < s->member_count; i++)
    {
        AST* member = s->members[i];
        if (member && member->type == N_DECL)
            emit_var_decl_line(ctx, member);
    }

    ctx->indent_level--;
    emit_line(ctx, "END_STRUCT");
    emit_line(ctx, "END_TYPE");
    emit_newline(ctx);
}

static void emit_enum_decl(CSTContext* ctx, AST* node)
{
    if (!node || node->type != N_ENUM_DECL) return;
    EnumDeclNode* e = &node->data.enum_decl;

    /* ST enums: TYPE EnumName : (VAL1, VAL2, VAL3 := N); END_TYPE */
    if (e->name)
        emit_indent(ctx);

    emit(ctx, "TYPE %s : (\n", e->name ? e->name : "AnonymousEnum");
    ctx->indent_level++;

    for (size_t i = 0; i < e->value_count; i++)
    {
        AST* val = e->values[i];
        emit_indent(ctx);

        if (val->type == N_IDENT)
        {
            emit(ctx, "%s", val->data.ident.name);
        }
        else if (val->type == N_ASSIGN)
        {
            emit(ctx, "%s := ", val->data.assign.var_name);
            emit_expression(ctx, val->data.assign.value);
        }

        if (i < e->value_count - 1)
            emit(ctx, ",");
        emit(ctx, "\n");
    }

    ctx->indent_level--;
    emit_line(ctx, ");");
    emit_line(ctx, "END_TYPE");
    emit_newline(ctx);
}

static void emit_typedef_decl(CSTContext* ctx, AST* node)
{
    if (!node || node->type != N_TYPEDEF) return;

    /* Typedefs that alias structs are already emitted as part of struct emission.
     * For primitive typedefs, emit as a TYPE alias. */
    TypedefNode* td = &node->data.typedef_decl;

    /* If old_name starts with "struct ", the struct TYPE block handles it */
    if (strncmp(td->old_name, "struct ", 7) == 0)
        return;

    /* Primitive typedef: TYPE NewName : OldSTType; END_TYPE
     * TwinCAT requires multi-line format. */
    emit_line(ctx, "TYPE %s :", td->new_name);
    ctx->indent_level++;
    emit_line(ctx, "%s;", g_target->map_type(td->old_name, 0));
    ctx->indent_level--;
    emit_line(ctx, "END_TYPE");
    emit_newline(ctx);
}

/* ====================== Top-Level Program Emission ====================== */

/* ====================== Helper: Build file path ====================== */

static void build_path(char* dest, size_t dest_size, const char* dir, const char* filename)
{
    snprintf(dest, dest_size, "%s/%s", dir, filename);
    /* Normalize backslashes to forward slashes */
    for (char* p = dest; *p; p++)
        if (*p == '\\') *p = '/';
}

static FILE* open_st_file(const char* dir, const char* filename, const char* header_comment)
{
    char path[1024];
    build_path(path, sizeof(path), dir, filename);

    FILE* fp = fopen(path, "w");
    if (!fp)
    {
        fprintf(stderr, "CST Error: cannot create '%s'\n", path);
        return NULL;
    }

    /* Write file header */
    fprintf(fp, "(* ============================================= *)\n");
    fprintf(fp, "(* CST Generated - %s\n", header_comment);
    fprintf(fp, "(* ============================================= *)\n\n");

    return fp;
}

/* ====================== Top-Level: Multi-File Output ====================== */

void cst_generate(AST* root, const char* output_dir)
{
    if (!root || root->type != N_PROGRAM)
    {
        fprintf(stderr, "CST Error: root node is not N_PROGRAM\n");
        return;
    }

    /* Pre-pass: build function signature table for context-aware call emission. */
    func_table_build(root);

    /* Pre-pass: gather bool-typed identifiers so codegen knows which idents
     * already produce BOOL (skip `<> 0` wrap, skip BOOL_TO_DINT). */
    bool_names_clear();
    bool_names_walk(root);

    /* Pre-pass: semantic checks (call arity, undefined funcs, local shadowing). */
    sema_run(root);

    /* Custom whole-program emission hook (e.g. Mitsubishi FX → Instruction List). */
    if (g_target->emit_program) {
        g_target->emit_program(root, output_dir);
        func_table_free();
        return;
    }

    ProgramNode* prog = &root->data.program;

    /* ================================================================
     *  FILE 1: DUT.st - All TYPE definitions (structs, enums, typedefs)
     *  This maps to TwinCAT's DUT folder.
     * ================================================================ */
    {
        int has_types = 0;
        for (size_t i = 0; i < prog->global_count; i++)
        {
            AST* g = prog->globals[i];
            if (!g) continue;
            if (g->type == N_STRUCT_DECL || g->type == N_TYPEDEF || g->type == N_ENUM_DECL)
            {
                has_types = 1; break;
            }
        }

        if (has_types)
        {
            FILE* fp = open_st_file(output_dir, "DUT.st", "Data Unit Types (DUT)              *)");
            if (fp)
            {
                CSTContext ctx;
                ctx_init(&ctx, fp);

                /* Structs */
                for (size_t i = 0; i < prog->global_count; i++)
                {
                    AST* g = prog->globals[i];
                    if (g && g->type == N_STRUCT_DECL)
                        emit_struct_decl(&ctx, g);
                }

                /* Primitive typedefs */
                for (size_t i = 0; i < prog->global_count; i++)
                {
                    AST* g = prog->globals[i];
                    if (g && g->type == N_TYPEDEF)
                        emit_typedef_decl(&ctx, g);
                }

                /* Enums */
                for (size_t i = 0; i < prog->global_count; i++)
                {
                    AST* g = prog->globals[i];
                    if (g && g->type == N_ENUM_DECL)
                        emit_enum_decl(&ctx, g);
                }

                fclose(fp);
                printf("CST: DUT.st written\n");
            }
        }
    }

    /* ================================================================
     *  FILE 2: GVL.st - Global Variable List
     *  This maps to TwinCAT's GVL folder.
     * ================================================================ */
    {
        int has_const = 0, has_var = 0;
        for (size_t i = 0; i < prog->global_count; i++)
        {
            AST* g = prog->globals[i];
            if (!g || g->type != N_DECL) continue;
            if (g->data.decl.is_extern) continue;
            /* Skip hardware-bound variables (Mitsubishi FX CST_IO) */
            if (g->data.decl.name && g_target->resolve_var &&
                g_target->resolve_var(g->data.decl.name)) continue;
            if (g->data.decl.is_const) has_const = 1;
            else has_var = 1;
        }

        if (has_const || has_var)
        {
            FILE* fp = open_st_file(output_dir, "GVL.st", "Global Variable List (GVL)          *)");
            if (fp)
            {
                CSTContext ctx;
                ctx_init(&ctx, fp);

                if (has_const)
                {
                    emit_line(&ctx, "VAR_GLOBAL CONSTANT");
                    ctx.indent_level++;
                    for (size_t i = 0; i < prog->global_count; i++)
                    {
                        AST* g = prog->globals[i];
                        if (!g || g->type != N_DECL) continue;
                        if (g->data.decl.is_extern) continue;
                        if (g->data.decl.is_const)
                            emit_var_decl_line(&ctx, g);
                    }
                    ctx.indent_level--;
                    emit_line(&ctx, "END_VAR");
                    emit_newline(&ctx);
                }

                if (has_var)
                {
                    emit_line(&ctx, "VAR_GLOBAL");
                    ctx.indent_level++;
                    for (size_t i = 0; i < prog->global_count; i++)
                    {
                        AST* g = prog->globals[i];
                        if (!g || g->type != N_DECL) continue;
                        if (g->data.decl.is_extern) continue;
                        if (!g->data.decl.is_const)
                            emit_var_decl_line(&ctx, g);
                    }
                    ctx.indent_level--;
                    emit_line(&ctx, "END_VAR");
                }

                fclose(fp);
                printf("CST: GVL.st written\n");
            }
        }
    }

    /* ================================================================
     *  FILES 3..N: Individual FUNCTION and FUNCTION_BLOCK POUs
     *  Each gets its own .st file, matching TwinCAT's POU structure.
     * ================================================================ */
    for (size_t i = 0; i < prog->func_count; i++)
    {
        AST* f = prog->functions[i];
        if (!f || f->type != N_FUNCTION) continue;
        if (!f->data.function.body) continue;
        if (f->data.function.is_extern) continue;

        /* Skip entry point - it gets its own file below */
        if (is_entry_point(f)) continue;

        /* Build filename: {funcname}.st */
        char fname[512];
        snprintf(fname, sizeof(fname), "%s.st", f->data.function.name);

        const char* kind = "FUNCTION";
        char header[256];
        snprintf(header, sizeof(header), "%s %s *)                          ", kind, f->data.function.name);
        header[44] = '*';
        header[45] = ')';
        header[46] = '\0';

        FILE* fp = open_st_file(output_dir, fname, header);
        if (fp)
        {
            CSTContext ctx;
            ctx_init(&ctx, fp);
            emit_function(&ctx, f);
            fclose(fp);
            printf("CST: %s written\n", fname);
        }
    }

    /* ================================================================
     *  LAST FILE: MAIN.st - PROGRAM MAIN (entry point)
     *  Like _start in your bootloader - this is what PlcTask calls.
     * ================================================================ */
    {
        AST* entry_func = NULL;
        for (size_t i = 0; i < prog->func_count; i++)
        {
            AST* f = prog->functions[i];
            if (f && f->type == N_FUNCTION && is_entry_point(f))
            {
                entry_func = f;
                break;
            }
        }

        if (entry_func)
        {
            FILE* fp = open_st_file(output_dir, "MAIN.st", "PROGRAM MAIN (Entry Point)          *)");
            if (fp)
            {
                CSTContext ctx;
                ctx_init(&ctx, fp);

                /* If there are global inits, emit them at top of MAIN before user code */
                /* (TwinCAT initializes GVL vars to 0 by default, but explicit inits go here) */

                emit_function(&ctx, entry_func);
                fclose(fp);
                printf("CST: MAIN.st written\n");
            }
        }
        else
        {
            printf("CST: Warning - no main() entry point found\n");
        }
    }

    printf("CST: All files written to '%s'\n", output_dir);
    func_table_free();
}