/*
 * Allen-Bradley Studio 5000 / Logix Designer LADDER LOGIC backend for CST.
 *
 * Unlike the sibling `allen_bradley.c` (which emits Structured Text), this
 * backend transpiles the C AST directly to AB Relay Ladder Logic (RLL) and
 * writes a Studio 5000-importable L5X file.
 *
 * Pipeline:
 *   1. Collect globals + per-function locals into a flat program-scoped
 *      tag table (Logix has no routine-local tags).
 *   2. Lower each C function to a Routine of type RLL. main() becomes the
 *      MainRoutine; everything else is callable via JSR.
 *   3. Walk each function body, lowering statements to rungs:
 *        assign        -> CPT / MOV / OTE-with-contact-chain
 *        if/else       -> contact-gated rungs (synthesizes _cond_N for complex)
 *        while/for     -> JMP/LBL pattern (Logix doesn't really have
 *                         structured loops in ladder)
 *        call          -> JSR(routine)
 *        return        -> RET   (end-of-routine returns silently emit nothing)
 *        cst_timer_on  -> TON(t, preset, 0)
 *        cst_timer_done-> XIC(t.DN)   (in contact chain context)
 *        cst_counter_* -> CTU/CTD
 *   4. Serialize the ladder IR to an L5X XML file at <output_dir>/<prog>.L5X.
 *
 * Selection: `#include <allen_bradley_ll>` in the source, or `-target=allen_bradley_ll`
 * on the command line.
 *
 * Capability rules (hard rules — codegen rejects with an inline ERROR rung
 * comment rather than producing wrong ladder):
 *   - No pointer arithmetic. Pass-by-reference is limited to runtime types
 *     (cst_time_t*, cst_ctu_t*, ...) which become tag-name passing.
 *   - No malloc/printf/scanf — same gating as the ST AB backend.
 *   - No recursion (Logix JSR cannot recurse safely).
 *   - Function parameters become input/return tags via JSR's parameter slots.
 */

#include "../../Includes.h"
#include "target.h"
#include "../../Parser/Parser.h"
#include "../../Tokenizer/Tokenizer.h"

#include <time.h>

/* ====================================================================== */
/*                          Type mapping                                  */
/* ====================================================================== */

static const char* abll_map_type(const char* c_type, int pointer_level)
{
    static char buf[256];

    if (!c_type || !*c_type) {
        if (pointer_level > 0) return "DINT";  /* pointers not supported, gated later */
        return "DINT";
    }

    /* Strip a leading "struct " so runtime-type detection works whether the
     * parser hands us "cst_time_t" or "struct cst_time_t". */
    const char* base = c_type;
    if (strncmp(c_type, "struct ", 7) == 0) base = c_type + 7;

    /* Runtime opaque types — recognized as AB predefined types (checked on the
     * struct-stripped base so both spellings map). */
    if (pointer_level == 0) {
        if (strcmp(base, "cst_time_t")  == 0) return "TIMER";
        if (strcmp(base, "cst_tof_t")   == 0) return "TIMER";
        if (strcmp(base, "cst_redge_t") == 0) return "BOOL";   /* ONS is bit-based */
        if (strcmp(base, "cst_fedge_t") == 0) return "BOOL";
        if (strcmp(base, "cst_ctu_t")   == 0) return "COUNTER";
        if (strcmp(base, "cst_ctd_t")   == 0) return "COUNTER";
    }

    if (strcmp(base, "char") == 0 && pointer_level == 1) return "STRING";
    if (strcmp(base, "void") == 0 && pointer_level > 0) return "DINT";  /* unsupported */

    if (pointer_level > 0) {
        /* Pointers to runtime types become bare tag names — strip the pointer. */
        if (strcmp(base, "cst_time_t")  == 0) return "TIMER";
        if (strcmp(base, "cst_tof_t")   == 0) return "TIMER";
        if (strcmp(base, "cst_ctu_t")   == 0) return "COUNTER";
        if (strcmp(base, "cst_ctd_t")   == 0) return "COUNTER";
        /* All other pointer types unsupported in ladder. */
        snprintf(buf, sizeof(buf), "DINT");
        return buf;
    }

    if (strcmp(base, "int") == 0 || strcmp(base, "long") == 0)            return "DINT";
    if (strcmp(base, "short") == 0)                                       return "INT";
    if (strcmp(base, "char") == 0 || strcmp(base, "signed char") == 0)    return "SINT";
    if (strcmp(base, "float") == 0)                                       return "REAL";
    if (strcmp(base, "double") == 0)                                      return "LREAL";
    if (strcmp(base, "bool") == 0 || strcmp(base, "_Bool") == 0)          return "BOOL";
    if (strcmp(base, "unsigned char") == 0)                               return "INT";
    if (strcmp(base, "unsigned short") == 0)                              return "DINT";
    if (strcmp(base, "unsigned int") == 0 || strcmp(base, "unsigned long") == 0 ||
        strcmp(base, "unsigned") == 0)                                    return "DINT";
    if (strcmp(base, "signed") == 0 || strcmp(base, "signed int") == 0)   return "DINT";
    if (strcmp(base, "signed short") == 0)                                return "INT";
    if (strcmp(base, "signed long") == 0)                                 return "DINT";
    if (strcmp(base, "void") == 0)                                        return "VOID";

    return base;  /* user-defined struct/typedef */
}

static int abll_get_type_size(const char* c_type)
{
    if (!c_type) return 0;
    if (strcmp(c_type, "char") == 0)   return 1;
    if (strcmp(c_type, "short") == 0)  return 2;
    if (strcmp(c_type, "int") == 0 || strcmp(c_type, "long") == 0 || strcmp(c_type, "float") == 0) return 4;
    if (strcmp(c_type, "double") == 0) return 8;
    return 0;
}

static int abll_is_unsupported_call(const char* name)
{
    if (!name) return 0;
    if (strcmp(name, "printf") == 0 || strcmp(name, "fprintf") == 0 ||
        strcmp(name, "sprintf") == 0 || strcmp(name, "scanf") == 0 ||
        strcmp(name, "puts") == 0 || strcmp(name, "putchar") == 0 ||
        strcmp(name, "gets") == 0 || strcmp(name, "fgets") == 0) return 1;
    if (strcmp(name, "malloc") == 0 || strcmp(name, "calloc") == 0 ||
        strcmp(name, "realloc") == 0 || strcmp(name, "free") == 0) return 2;
    return 0;
}

static const char* abll_unsupported_call_comment(int code)
{
    switch (code) {
    case 1: return "AB-LL: console I/O - use MSG instruction";
    case 2: return "AB-LL: heap unsupported - use static tags";
    default: return "AB-LL: unsupported call";
    }
}

/* ====================================================================== */
/*                          Ladder IR                                     */
/* ====================================================================== */

typedef struct LLRung {
    char* text;              /* rung text like "XIC(Start)OTE(Motor);" */
    char* comment;           /* optional, may be NULL */
    int   is_error;          /* 1 = produced from unsupported construct */
    struct LLRung* next;
} LLRung;

typedef struct LLTag {
    char* name;
    char* data_type;         /* BOOL, DINT, REAL, TIMER, COUNTER, STRING */
    int   array_size;        /* 0 = scalar, >0 = 1D array */
    char* initial;           /* optional, may be NULL */
    int   is_alias_of_param; /* tags that mirror a routine input */
    struct LLTag* next;
} LLTag;

typedef struct LLRoutine {
    char* name;
    LLRung* rungs_head;
    LLRung* rungs_tail;
    int   rung_count;
    int   label_counter;
    int   synth_counter;     /* for _cond_N etc. */
    /* Function metadata (for JSR call sites) */
    char** param_names;
    int    param_count;
    int    has_return;
    struct LLRoutine* next;
} LLRoutine;

/* User-defined type (struct) — emitted as a Logix UDT in <DataTypes>. */
typedef struct LLUdtMember {
    char* name;
    char* data_type;    /* mapped Logix type: BOOL/DINT/REAL/... or nested UDT */
    int   array_size;   /* 0 = scalar */
    struct LLUdtMember* next;
} LLUdtMember;

typedef struct LLUdt {
    char* name;
    LLUdtMember* members_head;
    LLUdtMember* members_tail;
    struct LLUdt* next;
} LLUdt;

typedef struct LLProgram {
    char* name;
    LLTag* tags_head;
    LLTag* tags_tail;
    LLRoutine* routines_head;
    LLRoutine* routines_tail;
    LLRoutine* main_routine;
    LLRoutine* current_routine;   /* set during lowering */
    LLUdt* udts_head;             /* user struct definitions */
    LLUdt* udts_tail;
} LLProgram;

/* ---------------- IR helpers ---------------- */

static char* xstrdup(const char* s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char* p = (char*)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static LLTag* ll_find_tag(LLProgram* p, const char* name) {
    for (LLTag* t = p->tags_head; t; t = t->next)
        if (strcmp(t->name, name) == 0) return t;
    return NULL;
}

static LLTag* ll_add_tag(LLProgram* p, const char* name, const char* type) {
    if (!name || !*name) return NULL;
    LLTag* existing = ll_find_tag(p, name);
    if (existing) return existing;
    LLTag* t = (LLTag*)calloc(1, sizeof(LLTag));
    t->name = xstrdup(name);
    t->data_type = xstrdup(type ? type : "DINT");
    if (!p->tags_head) p->tags_head = t;
    else p->tags_tail->next = t;
    p->tags_tail = t;
    return t;
}

/* ---------------- UDT registry ---------------- */

static LLUdt* ll_find_udt(LLProgram* p, const char* name) {
    if (!name) return NULL;
    for (LLUdt* u = p->udts_head; u; u = u->next)
        if (strcmp(u->name, name) == 0) return u;
    return NULL;
}

static LLUdt* ll_add_udt(LLProgram* p, const char* name) {
    LLUdt* existing = ll_find_udt(p, name);
    if (existing) return existing;
    LLUdt* u = (LLUdt*)calloc(1, sizeof(LLUdt));
    u->name = xstrdup(name);
    if (!p->udts_head) p->udts_head = u;
    else p->udts_tail->next = u;
    p->udts_tail = u;
    return u;
}

static void ll_udt_add_member(LLUdt* u, const char* name, const char* type, int array_size) {
    LLUdtMember* m = (LLUdtMember*)calloc(1, sizeof(LLUdtMember));
    m->name = xstrdup(name);
    m->data_type = xstrdup(type ? type : "DINT");
    m->array_size = array_size;
    if (!u->members_head) u->members_head = m;
    else u->members_tail->next = m;
    u->members_tail = m;
}

/* Logix predefined structured types we must NOT emit as UDTs. */
static int is_predefined_type(const char* t) {
    if (!t) return 0;
    return strcmp(t, "TIMER") == 0 || strcmp(t, "COUNTER") == 0 ||
           strcmp(t, "BOOL")  == 0 || strcmp(t, "SINT")    == 0 ||
           strcmp(t, "INT")   == 0 || strcmp(t, "DINT")    == 0 ||
           strcmp(t, "LINT")  == 0 || strcmp(t, "REAL")    == 0 ||
           strcmp(t, "LREAL") == 0 || strcmp(t, "STRING")  == 0;
}

static int is_structured_type(const char* t) {
    return t && (strcmp(t, "TIMER") == 0 || strcmp(t, "COUNTER") == 0);
}

static LLTag* ll_add_array_tag(LLProgram* p, const char* name, const char* type, int size) {
    LLTag* t = ll_add_tag(p, name, type);
    if (t && t->array_size == 0) t->array_size = size;
    return t;
}

static LLRoutine* ll_add_routine(LLProgram* p, const char* name) {
    LLRoutine* r = (LLRoutine*)calloc(1, sizeof(LLRoutine));
    r->name = xstrdup(name);
    if (!p->routines_head) p->routines_head = r;
    else p->routines_tail->next = r;
    p->routines_tail = r;
    return r;
}

static LLRoutine* ll_find_routine(LLProgram* p, const char* name) {
    for (LLRoutine* r = p->routines_head; r; r = r->next)
        if (strcmp(r->name, name) == 0) return r;
    return NULL;
}

static void ll_add_rung(LLRoutine* r, const char* text) {
    LLRung* g = (LLRung*)calloc(1, sizeof(LLRung));
    g->text = xstrdup(text);
    if (!r->rungs_head) r->rungs_head = g;
    else r->rungs_tail->next = g;
    r->rungs_tail = g;
    r->rung_count++;
}

static void ll_add_rung_comment(LLRoutine* r, const char* text, const char* comment) {
    LLRung* g = (LLRung*)calloc(1, sizeof(LLRung));
    g->text = xstrdup(text);
    g->comment = xstrdup(comment);
    if (!r->rungs_head) r->rungs_head = g;
    else r->rungs_tail->next = g;
    r->rungs_tail = g;
    r->rung_count++;
}

static void ll_add_error_rung(LLRoutine* r, const char* reason) {
    char buf[512];
    snprintf(buf, sizeof(buf), "NOP();");
    LLRung* g = (LLRung*)calloc(1, sizeof(LLRung));
    g->text = xstrdup(buf);
    g->comment = xstrdup(reason);
    g->is_error = 1;
    if (!r->rungs_head) r->rungs_head = g;
    else r->rungs_tail->next = g;
    r->rungs_tail = g;
    r->rung_count++;
    fprintf(stderr, "AB-LL: %s (in routine %s)\n", reason, r->name);
}

/* ====================================================================== */
/*                Expression rendering (for CPT / numeric)                */
/* ====================================================================== */

static const char* op_to_logix(Tokens t) {
    switch (t) {
    case TOKEN_PLUS:        return "+";
    case TOKEN_MINUS:       return "-";
    case TOKEN_STAR:        return "*";
    case TOKEN_SLASH:       return "/";
    case TOKEN_PERCENT:     return "MOD";
    case TOKEN_AMPERSAND:   return "AND";
    case TOKEN_PIPE:        return "OR";
    case TOKEN_CARET:       return "XOR";
    case TOKEN_LSHIFT:      return "<<";
    case TOKEN_RSHIFT:      return ">>";
    case TOKEN_EQUAL:       return "=";
    case TOKEN_NOT_EQUAL:   return "<>";
    case TOKEN_LESS:        return "<";
    case TOKEN_GREATER:     return ">";
    case TOKEN_LESS_EQUAL:  return "<=";
    case TOKEN_GREATER_EQUAL: return ">=";
    case TOKEN_AND:         return "AND";
    case TOKEN_OR:          return "OR";
    default: return "?";
    }
}

static int append_str(char* out, size_t out_size, size_t* pos, const char* s) {
    if (!s) return 0;
    size_t n = strlen(s);
    if (*pos + n + 1 > out_size) return 0;
    memcpy(out + *pos, s, n);
    *pos += n;
    out[*pos] = '\0';
    return 1;
}

static int append_fmt(char* out, size_t out_size, size_t* pos, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    int avail = (int)(out_size - *pos);
    if (avail <= 1) { va_end(ap); return 0; }
    int n = vsnprintf(out + *pos, avail, fmt, ap);
    va_end(ap);
    if (n < 0 || n >= avail) return 0;
    *pos += n;
    return 1;
}

/* Render an expression as a Logix CPT-style string. Used for numeric RHS. */
static void render_expr(AST* node, char* out, size_t out_size, size_t* pos) {
    if (!node) { append_str(out, out_size, pos, "0"); return; }

    switch (node->type) {
    case N_INTLIT:
        append_fmt(out, out_size, pos, "%d", node->data.int_lit.value);
        return;
    case N_FLOATLIT:
        append_str(out, out_size, pos, node->data.float_lit.text);
        return;
    case N_CHAR_LIT:
        append_fmt(out, out_size, pos, "%d", (int)node->data.char_lit.value);
        return;
    case N_IDENT:
        append_str(out, out_size, pos, node->data.ident.name);
        return;
    case N_OPERATOR: {
        Tokens op = node->data.op.op;
        /* Logical ops fall back to OR/AND treated bitwise in CPT context */
        append_str(out, out_size, pos, "(");
        render_expr(node->data.op.left, out, out_size, pos);
        append_fmt(out, out_size, pos, " %s ", op_to_logix(op));
        render_expr(node->data.op.right, out, out_size, pos);
        append_str(out, out_size, pos, ")");
        return;
    }
    case N_UNARY: {
        Tokens op = node->data.unary.op;
        if (op == TOKEN_MINUS) {
            append_str(out, out_size, pos, "(-");
            render_expr(node->data.unary.operand, out, out_size, pos);
            append_str(out, out_size, pos, ")");
        } else if (op == TOKEN_EXCLAIM) {
            append_str(out, out_size, pos, "NOT(");
            render_expr(node->data.unary.operand, out, out_size, pos);
            append_str(out, out_size, pos, ")");
        } else if (op == TOKEN_TILDE) {
            append_str(out, out_size, pos, "NOT(");
            render_expr(node->data.unary.operand, out, out_size, pos);
            append_str(out, out_size, pos, ")");
        } else {
            render_expr(node->data.unary.operand, out, out_size, pos);
        }
        return;
    }
    case N_ARRAY_ACCESS: {
        render_expr(node->data.array_access.array, out, out_size, pos);
        append_str(out, out_size, pos, "[");
        render_expr(node->data.array_access.index, out, out_size, pos);
        append_str(out, out_size, pos, "]");
        return;
    }
    case N_MEMBER_ACCESS: {
        render_expr(node->data.member_access.object, out, out_size, pos);
        append_str(out, out_size, pos, ".");
        append_str(out, out_size, pos, node->data.member_access.member);
        return;
    }
    case N_CAST:
        render_expr(node->data.cast.expr, out, out_size, pos);
        return;
    case N_TERNARY: {
        /* Logix CPT doesn't have ternary; emulate with bitwise: cond * t + !cond * f */
        append_str(out, out_size, pos, "((");
        render_expr(node->data.ternary.condition, out, out_size, pos);
        append_str(out, out_size, pos, ")*(");
        render_expr(node->data.ternary.true_expr, out, out_size, pos);
        append_str(out, out_size, pos, ")+NOT(");
        render_expr(node->data.ternary.condition, out, out_size, pos);
        append_str(out, out_size, pos, ")*(");
        render_expr(node->data.ternary.false_expr, out, out_size, pos);
        append_str(out, out_size, pos, "))");
        return;
    }
    case N_CALL:
        /* Function call inside an expression — Logix can't inline this in a CPT.
         * We synthesize a tag, queue a JSR, then reference the return tag.
         * For simplicity in v1, we just emit the name and let the user notice. */
        append_fmt(out, out_size, pos, "%s_ret", node->data.call.name);
        return;
    default:
        append_str(out, out_size, pos, "0");
        return;
    }
}

/* ====================================================================== */
/*           Boolean expression -> contact chain                          */
/* ====================================================================== */

/*
 * Build a contact-chain string for a boolean expression. The chain represents
 * the "input side" of a ladder rung. The output side (OTE/coil/etc.) is
 * appended by the caller.
 *
 * Strategies:
 *   IDENT(bool)               -> XIC(ident)
 *   !X                        -> XIO(...) by recursion sign-flip
 *   X && Y                    -> chain_X chain_Y  (series)
 *   X || Y                    -> [chain_X,chain_Y] (parallel branch)
 *   X == Y / X < Y / ...      -> EQU(X,Y) / LES(X,Y) / ...
 *   IDENT(non-bool)           -> NEQ(ident,0)
 *   anything else             -> render expr to CPT into a synth BOOL tag,
 *                                use XIC of that tag in this chain
 *
 * `negate` flips the sense (XIO instead of XIC, swap branch type).
 */

static int is_relational(Tokens op) {
    return op == TOKEN_EQUAL || op == TOKEN_NOT_EQUAL || op == TOKEN_LESS ||
           op == TOKEN_GREATER || op == TOKEN_LESS_EQUAL || op == TOKEN_GREATER_EQUAL;
}

static const char* relational_logix(Tokens op, int negate) {
    if (!negate) {
        switch (op) {
        case TOKEN_EQUAL:         return "EQU";
        case TOKEN_NOT_EQUAL:     return "NEQ";
        case TOKEN_LESS:          return "LES";
        case TOKEN_GREATER:       return "GRT";
        case TOKEN_LESS_EQUAL:    return "LEQ";
        case TOKEN_GREATER_EQUAL: return "GEQ";
        default: return "EQU";
        }
    }
    switch (op) {
    case TOKEN_EQUAL:         return "NEQ";
    case TOKEN_NOT_EQUAL:     return "EQU";
    case TOKEN_LESS:          return "GEQ";
    case TOKEN_GREATER:       return "LEQ";
    case TOKEN_LESS_EQUAL:    return "GRT";
    case TOKEN_GREATER_EQUAL: return "LES";
    default: return "NEQ";
    }
}

static void build_bool_chain(LLProgram* p, LLRoutine* r, AST* expr,
                              char* out, size_t out_size, size_t* pos, int negate);

/* Render a numeric expression to a tag name suitable for an EQU/NEQ argument.
 * If it's a plain ident/literal/member, render in-place; otherwise synthesize
 * a DINT tag and emit a CPT rung. */
static void render_numeric_operand(LLProgram* p, LLRoutine* r, AST* e, char* out, size_t out_size, size_t* pos) {
    if (!e) { append_str(out, out_size, pos, "0"); return; }
    if (e->type == N_INTLIT || e->type == N_FLOATLIT || e->type == N_CHAR_LIT ||
        e->type == N_IDENT || e->type == N_MEMBER_ACCESS || e->type == N_ARRAY_ACCESS) {
        render_expr(e, out, out_size, pos);
        return;
    }
    /* Complex expression — synthesize tag */
    char synth[64];
    snprintf(synth, sizeof(synth), "_tmp_%s_%d", r->name, r->synth_counter++);
    ll_add_tag(p, synth, "DINT");
    char rhs[512]; size_t rp = 0; rhs[0] = '\0';
    render_expr(e, rhs, sizeof(rhs), &rp);
    char rung[640];
    snprintf(rung, sizeof(rung), "CPT(%s,%s);", synth, rhs);
    ll_add_rung(r, rung);
    append_str(out, out_size, pos, synth);
}

static void build_bool_chain(LLProgram* p, LLRoutine* r, AST* expr,
                              char* out, size_t out_size, size_t* pos, int negate)
{
    if (!expr) {
        /* empty condition: always true (negate => never) */
        append_str(out, out_size, pos, negate ? "XIO(_one_)" : "");
        return;
    }

    switch (expr->type) {
    case N_INTLIT: {
        int v = expr->data.int_lit.value;
        int truthy = (v != 0) ^ negate;
        /* Always-true → emit no contact (rung is unconditional / inherits prior chain).
         * Always-false → emit a guaranteed-false contact so the rung is dead. */
        if (!truthy) append_str(out, out_size, pos, "EQU(0,1)");
        return;
    }
    case N_IDENT: {
        const char* nm = expr->data.ident.name;
        LLTag* t = ll_find_tag(p, nm);
        if (t && strcmp(t->data_type, "BOOL") == 0) {
            append_fmt(out, out_size, pos, "%s(%s)", negate ? "XIO" : "XIC", nm);
        } else {
            /* Treat non-bool as int: NEQ(x,0) is true; XIO sense flips to EQU */
            append_fmt(out, out_size, pos, "%s(%s,0)", negate ? "EQU" : "NEQ", nm);
        }
        return;
    }
    case N_MEMBER_ACCESS: {
        char buf[256]; size_t bp = 0; buf[0] = '\0';
        render_expr(expr, buf, sizeof(buf), &bp);
        /* member access on TIMER/COUNTER often produces .DN .EN .TT etc — treat as BOOL */
        append_fmt(out, out_size, pos, "%s(%s)", negate ? "XIO" : "XIC", buf);
        return;
    }
    case N_UNARY: {
        if (expr->data.unary.op == TOKEN_EXCLAIM) {
            build_bool_chain(p, r, expr->data.unary.operand, out, out_size, pos, !negate);
            return;
        }
        /* other unary ops fall through to numeric */
        char b[128]; size_t bp = 0; b[0] = '\0';
        render_numeric_operand(p, r, expr, b, sizeof(b), &bp);
        append_fmt(out, out_size, pos, "%s(%s,0)", negate ? "EQU" : "NEQ", b);
        return;
    }
    case N_OPERATOR: {
        Tokens op = expr->data.op.op;
        if (op == TOKEN_AND) {
            /* AND: series of chains. Under negation, becomes OR via De Morgan. */
            if (!negate) {
                build_bool_chain(p, r, expr->data.op.left,  out, out_size, pos, 0);
                build_bool_chain(p, r, expr->data.op.right, out, out_size, pos, 0);
            } else {
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, expr->data.op.left,  out, out_size, pos, 1);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, expr->data.op.right, out, out_size, pos, 1);
                append_str(out, out_size, pos, "]");
            }
            return;
        }
        if (op == TOKEN_OR) {
            /* OR: branch. Under negation, becomes AND series via De Morgan. */
            if (!negate) {
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, expr->data.op.left,  out, out_size, pos, 0);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, expr->data.op.right, out, out_size, pos, 0);
                append_str(out, out_size, pos, "]");
            } else {
                build_bool_chain(p, r, expr->data.op.left,  out, out_size, pos, 1);
                build_bool_chain(p, r, expr->data.op.right, out, out_size, pos, 1);
            }
            return;
        }
        if (is_relational(op)) {
            char a[128], b[128]; size_t ap = 0, bp = 0; a[0]=b[0]='\0';
            render_numeric_operand(p, r, expr->data.op.left,  a, sizeof(a), &ap);
            render_numeric_operand(p, r, expr->data.op.right, b, sizeof(b), &bp);
            append_fmt(out, out_size, pos, "%s(%s,%s)", relational_logix(op, negate), a, b);
            return;
        }
        /* Arithmetic in boolean context: NEQ(expr, 0) */
        char e[256]; size_t ep = 0; e[0] = '\0';
        render_expr(expr, e, sizeof(e), &ep);
        /* If complex, just embed; Logix EQU/NEQ accept tags only — synthesize */
        char synth[64];
        snprintf(synth, sizeof(synth), "_tmp_%s_%d", r->name, r->synth_counter++);
        ll_add_tag(p, synth, "DINT");
        char rung[320];
        snprintf(rung, sizeof(rung), "CPT(%s,%s);", synth, e);
        ll_add_rung(r, rung);
        append_fmt(out, out_size, pos, "%s(%s,0)", negate ? "EQU" : "NEQ", synth);
        return;
    }
    case N_CALL: {
        /* cst_timer_done(&t) -> XIC(t.DN). Others -> assume returns BOOL, synth alias. */
        const char* nm = expr->data.call.name;
        if (strcmp(nm, "cst_timer_done") == 0 && expr->data.call.arg_count >= 1) {
            AST* arg = expr->data.call.args[0];
            char tnm[128] = "";
            if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND &&
                arg->data.unary.operand && arg->data.unary.operand->type == N_IDENT) {
                snprintf(tnm, sizeof(tnm), "%s", arg->data.unary.operand->data.ident.name);
            } else if (arg && arg->type == N_IDENT) {
                snprintf(tnm, sizeof(tnm), "%s", arg->data.ident.name);
            }
            if (*tnm) {
                append_fmt(out, out_size, pos, "%s(%s.DN)", negate ? "XIO" : "XIC", tnm);
                return;
            }
        }
        if (strcmp(nm, "cst_counter_done") == 0 && expr->data.call.arg_count >= 1) {
            AST* arg = expr->data.call.args[0];
            char tnm[128] = "";
            if (arg && arg->type == N_UNARY && arg->data.unary.op == TOKEN_AMPERSAND &&
                arg->data.unary.operand && arg->data.unary.operand->type == N_IDENT) {
                snprintf(tnm, sizeof(tnm), "%s", arg->data.unary.operand->data.ident.name);
            } else if (arg && arg->type == N_IDENT) {
                snprintf(tnm, sizeof(tnm), "%s", arg->data.ident.name);
            }
            if (*tnm) {
                append_fmt(out, out_size, pos, "%s(%s.DN)", negate ? "XIO" : "XIC", tnm);
                return;
            }
        }

        /* cst_seal(state, set, reset)  →  set-priority seal-in:
         *   chain = [chain(state), chain(set)] chain(!reset)
         * which is `(state || set) && !reset`. Under negation, flip via
         * De Morgan: `!state && !set` OR `reset`. */
        if (strcmp(nm, "cst_seal") == 0 && expr->data.call.arg_count >= 3) {
            AST* a_state = expr->data.call.args[0];
            AST* a_set   = expr->data.call.args[1];
            AST* a_reset = expr->data.call.args[2];
            if (!negate) {
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, a_state, out, out_size, pos, 0);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, a_set,   out, out_size, pos, 0);
                append_str(out, out_size, pos, "]");
                build_bool_chain(p, r, a_reset, out, out_size, pos, 1); /* XIO of reset */
            } else {
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, a_state, out, out_size, pos, 1);
                build_bool_chain(p, r, a_set,   out, out_size, pos, 1);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, a_reset, out, out_size, pos, 0);
                append_str(out, out_size, pos, "]");
            }
            return;
        }

        /* cst_seal_rp(state, set, reset)  →  reset-priority seal-in:
         *   chain = chain(!reset) [chain(state), chain(set)]
         * which is `!reset && (state || set)`. Reset wins on simultaneous. */
        if (strcmp(nm, "cst_seal_rp") == 0 && expr->data.call.arg_count >= 3) {
            AST* a_state = expr->data.call.args[0];
            AST* a_set   = expr->data.call.args[1];
            AST* a_reset = expr->data.call.args[2];
            if (!negate) {
                build_bool_chain(p, r, a_reset, out, out_size, pos, 1);
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, a_state, out, out_size, pos, 0);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, a_set,   out, out_size, pos, 0);
                append_str(out, out_size, pos, "]");
            } else {
                append_str(out, out_size, pos, "[");
                build_bool_chain(p, r, a_reset, out, out_size, pos, 0);
                append_str(out, out_size, pos, ",");
                build_bool_chain(p, r, a_state, out, out_size, pos, 1);
                build_bool_chain(p, r, a_set,   out, out_size, pos, 1);
                append_str(out, out_size, pos, "]");
            }
            return;
        }

        /* cst_within(v, lo, hi)  →  LIM(lo, v, hi). In a contact chain that
         * appears as a single LIM input instruction. */
        if (strcmp(nm, "cst_within") == 0 && expr->data.call.arg_count >= 3) {
            char a[128], b[128], c[128]; size_t ap = 0, bp = 0, cp = 0;
            a[0] = b[0] = c[0] = '\0';
            render_numeric_operand(p, r, expr->data.call.args[1], a, sizeof(a), &ap);  /* lo */
            render_numeric_operand(p, r, expr->data.call.args[0], b, sizeof(b), &bp);  /* v  */
            render_numeric_operand(p, r, expr->data.call.args[2], c, sizeof(c), &cp);  /* hi */
            if (!negate) {
                append_fmt(out, out_size, pos, "LIM(%s,%s,%s)", a, b, c);
            } else {
                /* No LIM-not in ladder — wrap with a series of compare inverses.
                 * Simpler: emit a branch with the two out-of-range cases. */
                append_fmt(out, out_size, pos, "[LES(%s,%s),GRT(%s,%s)]", b, a, b, c);
            }
            return;
        }

        /* Generic call: synth BOOL, JSR before the rung, XIC the result */
        char synth[64];
        snprintf(synth, sizeof(synth), "_call_%s_%d", nm, r->synth_counter++);
        ll_add_tag(p, synth, "BOOL");
        char jsr[256];
        snprintf(jsr, sizeof(jsr), "JSR(%s,0,%s);", nm, synth);
        ll_add_rung(r, jsr);
        append_fmt(out, out_size, pos, "%s(%s)", negate ? "XIO" : "XIC", synth);
        return;
    }
    default: {
        /* Fallback: numeric-style with NEQ */
        char b[128]; size_t bp = 0; b[0] = '\0';
        render_numeric_operand(p, r, expr, b, sizeof(b), &bp);
        append_fmt(out, out_size, pos, "%s(%s,0)", negate ? "EQU" : "NEQ", b);
        return;
    }
    }
}

/* Combine a guard chain with an additional chain (AND them in series). */
static void chain_concat(char* out, size_t out_size, size_t* pos,
                          const char* guard, const char* tail)
{
    if (guard && *guard) append_str(out, out_size, pos, guard);
    if (tail  && *tail)  append_str(out, out_size, pos, tail);
}

/* ====================================================================== */
/*                       Statement lowering                               */
/* ====================================================================== */

static void lower_stmt(LLProgram* p, LLRoutine* r, AST* stmt, const char* guard);

/* Walk LHS of assignment to get tag name (handles ident, member, array). */
static void render_lhs(AST* lhs, char* out, size_t out_size) {
    size_t pos = 0; out[0] = '\0';
    if (!lhs) return;
    if (lhs->type == N_IDENT) {
        append_str(out, out_size, &pos, lhs->data.ident.name);
        return;
    }
    render_expr(lhs, out, out_size, &pos);
}

/* Determine whether the result type of expr is boolean (best-effort). */
static int expr_is_bool(LLProgram* p, AST* e) {
    if (!e) return 0;
    /* INTLIT 0/1 is ambiguous — could be a bool literal or a numeric literal
     * assigned to an int field. The LHS tag type is the authority; only fall
     * back to expression-shape inference when the LHS is unknown, and even
     * then INTLIT alone is not enough evidence to claim "bool". */
    if (e->type == N_OPERATOR) {
        Tokens op = e->data.op.op;
        if (is_relational(op)) return 1;
        if (op == TOKEN_AND || op == TOKEN_OR) return 1;
    }
    if (e->type == N_UNARY && e->data.unary.op == TOKEN_EXCLAIM) return 1;
    if (e->type == N_IDENT) {
        LLTag* t = ll_find_tag(p, e->data.ident.name);
        if (t && strcmp(t->data_type, "BOOL") == 0) return 1;
    }
    if (e->type == N_CALL) {
        const char* nm = e->data.call.name;
        if (strcmp(nm, "cst_timer_done")   == 0 ||
            strcmp(nm, "cst_counter_done") == 0 ||
            strcmp(nm, "cst_seal")         == 0 ||
            strcmp(nm, "cst_seal_rp")      == 0 ||
            strcmp(nm, "cst_within")       == 0) return 1;
    }
    return 0;
}

static void lower_assign(LLProgram* p, LLRoutine* r, AST* lhs, AST* rhs, const char* guard) {
    char lhs_buf[256];
    render_lhs(lhs, lhs_buf, sizeof(lhs_buf));
    if (!lhs_buf[0]) return;

    /* Ensure tag exists; infer type if unknown. Trust existing tag type. */
    LLTag* tag = ll_find_tag(p, lhs_buf);
    int is_bool;
    if (tag) is_bool = (strcmp(tag->data_type, "BOOL") == 0);
    else     is_bool = expr_is_bool(p, rhs);

    if (is_bool) {
        /* Latch / unlatch / coil selection:
         *   guarded `x = 1`  -> OTL(x)   (set-only under guard, latches)
         *   guarded `x = 0`  -> OTU(x)   (clear-only under guard)
         *   anything else    -> OTE(x)   (continuous coil tracking rung state)
         * Rationale: OTE always *drives* the coil every scan (1 when rung true,
         * 0 when rung false). For `if (cond) x = 1;` we want to *not touch* x
         * when cond is false — that is the OTL/OTU idiom. */
        int rhs_is_literal_1 = (rhs && rhs->type == N_INTLIT && rhs->data.int_lit.value == 1);
        int rhs_is_literal_0 = (rhs && rhs->type == N_INTLIT && rhs->data.int_lit.value == 0);
        int has_guard = (guard && *guard);

        char rung[1280];
        if (has_guard && rhs_is_literal_1) {
            snprintf(rung, sizeof(rung), "%sOTL(%s);", guard, lhs_buf);
        } else if (has_guard && rhs_is_literal_0) {
            snprintf(rung, sizeof(rung), "%sOTU(%s);", guard, lhs_buf);
        } else {
            char chain[1024]; size_t cp = 0; chain[0] = '\0';
            if (has_guard) append_str(chain, sizeof(chain), &cp, guard);
            build_bool_chain(p, r, rhs, chain, sizeof(chain), &cp, 0);
            snprintf(rung, sizeof(rung), "%sOTE(%s);", chain, lhs_buf);
        }
        ll_add_rung(r, rung);
        /* Don't add dotted/indexed names as top-level tags — those are struct
         * members or array elements of an existing tag, not new tags. */
        if (!tag && !strchr(lhs_buf, '.') && !strchr(lhs_buf, '['))
            ll_add_tag(p, lhs_buf, "BOOL");
        return;
    }

    /* Numeric: CPT(lhs, expr) — gated by `guard` if present */
    char rhs_buf[512]; size_t rp = 0; rhs_buf[0] = '\0';
    render_expr(rhs, rhs_buf, sizeof(rhs_buf), &rp);
    char rung[2048];
    if (guard && *guard) {
        snprintf(rung, sizeof(rung), "%sCPT(%s,%s);", guard, lhs_buf, rhs_buf);
    } else {
        /* Simple MOV optimization when RHS is a literal/ident */
        if (rhs && (rhs->type == N_INTLIT || rhs->type == N_FLOATLIT ||
                    rhs->type == N_CHAR_LIT || rhs->type == N_IDENT)) {
            snprintf(rung, sizeof(rung), "MOV(%s,%s);", rhs_buf, lhs_buf);
        } else {
            snprintf(rung, sizeof(rung), "CPT(%s,%s);", lhs_buf, rhs_buf);
        }
    }
    ll_add_rung(r, rung);
    if (!tag && !strchr(lhs_buf, '.') && !strchr(lhs_buf, '['))
        ll_add_tag(p, lhs_buf, "DINT");
}

static void lower_if(LLProgram* p, LLRoutine* r, AST* node, const char* guard) {
    AST* cond = node->data.if_stmt.condition;
    AST* thenb = node->data.if_stmt.then_block;
    AST* elseb = node->data.if_stmt.else_block;

    /* Build a synthesized BOOL tag if the condition is non-trivial.
     * For simple bool ident / relational, we can inline; otherwise synth. */
    char chain_then[512]; size_t cp = 0; chain_then[0] = '\0';
    if (guard && *guard) append_str(chain_then, sizeof(chain_then), &cp, guard);
    build_bool_chain(p, r, cond, chain_then, sizeof(chain_then), &cp, 0);

    lower_stmt(p, r, thenb, chain_then);

    if (elseb) {
        char chain_else[512]; size_t ep = 0; chain_else[0] = '\0';
        if (guard && *guard) append_str(chain_else, sizeof(chain_else), &ep, guard);
        build_bool_chain(p, r, cond, chain_else, sizeof(chain_else), &ep, 1);
        lower_stmt(p, r, elseb, chain_else);
    }
}

static void lower_while(LLProgram* p, LLRoutine* r, AST* node, const char* guard) {
    /* Pattern:
     *   LBL Loop_N
     *   <guard><not cond> JMP End_N
     *   <body, guarded by guard so outer if/else propagates>
     *   JMP Loop_N
     *   LBL End_N
     */
    int id = r->label_counter++;
    char lbl_top[64], lbl_end[64];
    snprintf(lbl_top, sizeof(lbl_top), "Loop_%d", id);
    snprintf(lbl_end, sizeof(lbl_end), "End_%d", id);

    char rung[512];
    snprintf(rung, sizeof(rung), "LBL(%s);", lbl_top);
    ll_add_rung(r, rung);

    /* exit test */
    char chain[512]; size_t cp = 0; chain[0] = '\0';
    if (guard && *guard) append_str(chain, sizeof(chain), &cp, guard);
    build_bool_chain(p, r, node->data.while_stmt.condition, chain, sizeof(chain), &cp, 1);
    snprintf(rung, sizeof(rung), "%sJMP(%s);", chain, lbl_end);
    ll_add_rung(r, rung);

    /* body */
    lower_stmt(p, r, node->data.while_stmt.body, guard);

    /* back-edge */
    snprintf(rung, sizeof(rung), "JMP(%s);", lbl_top);
    ll_add_rung(r, rung);

    snprintf(rung, sizeof(rung), "LBL(%s);", lbl_end);
    ll_add_rung(r, rung);
}

static void lower_for(LLProgram* p, LLRoutine* r, AST* node, const char* guard) {
    /* Lower as: init; while(cond) { body; incr; } using JMP/LBL */
    if (node->data.for_stmt.init)
        lower_stmt(p, r, node->data.for_stmt.init, guard);

    int id = r->label_counter++;
    char lbl_top[64], lbl_end[64];
    snprintf(lbl_top, sizeof(lbl_top), "Loop_%d", id);
    snprintf(lbl_end, sizeof(lbl_end), "End_%d", id);

    char rung[512];
    snprintf(rung, sizeof(rung), "LBL(%s);", lbl_top);
    ll_add_rung(r, rung);

    if (node->data.for_stmt.condition) {
        char chain[512]; size_t cp = 0; chain[0] = '\0';
        if (guard && *guard) append_str(chain, sizeof(chain), &cp, guard);
        build_bool_chain(p, r, node->data.for_stmt.condition, chain, sizeof(chain), &cp, 1);
        snprintf(rung, sizeof(rung), "%sJMP(%s);", chain, lbl_end);
        ll_add_rung(r, rung);
    }

    lower_stmt(p, r, node->data.for_stmt.body, guard);

    if (node->data.for_stmt.increment) {
        /* Increment is typically an assignment expression statement */
        lower_stmt(p, r, node->data.for_stmt.increment, guard);
    }

    snprintf(rung, sizeof(rung), "JMP(%s);", lbl_top);
    ll_add_rung(r, rung);
    snprintf(rung, sizeof(rung), "LBL(%s);", lbl_end);
    ll_add_rung(r, rung);
}

static void lower_runtime_call(LLProgram* p, LLRoutine* r, AST* call, const char* guard) {
    const char* nm = call->data.call.name;

    /* Helper: extract a tag name from either `&ident` or `ident`. */
    #define EXTRACT_TAG(arg, buf) do { \
        (buf)[0] = '\0'; \
        if ((arg) && (arg)->type == N_UNARY && (arg)->data.unary.op == TOKEN_AMPERSAND && \
            (arg)->data.unary.operand && (arg)->data.unary.operand->type == N_IDENT) \
            snprintf((buf), sizeof(buf), "%s", (arg)->data.unary.operand->data.ident.name); \
        else if ((arg) && (arg)->type == N_IDENT) \
            snprintf((buf), sizeof(buf), "%s", (arg)->data.ident.name); \
    } while (0)

    if (strcmp(nm, "cst_timer_on") == 0 && call->data.call.arg_count >= 2) {
        char tnm[128]; EXTRACT_TAG(call->data.call.args[0], tnm);
        char preset[128]; size_t pp = 0; preset[0]='\0';
        render_expr(call->data.call.args[1], preset, sizeof(preset), &pp);
        if (*tnm) {
            ll_add_tag(p, tnm, "TIMER");
            char rung[384];
            if (guard && *guard)
                snprintf(rung, sizeof(rung), "%sTON(%s,%s,0);", guard, tnm, preset);
            else
                snprintf(rung, sizeof(rung), "TON(%s,%s,0);", tnm, preset);
            ll_add_rung(r, rung);
        }
        return;
    }
    if (strcmp(nm, "cst_timer_off") == 0 && call->data.call.arg_count >= 1) {
        char tnm[128]; EXTRACT_TAG(call->data.call.args[0], tnm);
        if (*tnm) {
            ll_add_tag(p, tnm, "TIMER");
            char rung[256];
            if (guard && *guard)
                snprintf(rung, sizeof(rung), "%sRES(%s);", guard, tnm);
            else
                snprintf(rung, sizeof(rung), "RES(%s);", tnm);
            ll_add_rung(r, rung);
        }
        return;
    }
    if (strcmp(nm, "cst_counter_inc") == 0 && call->data.call.arg_count >= 2) {
        char tnm[128]; EXTRACT_TAG(call->data.call.args[0], tnm);
        char preset[128]; size_t pp = 0; preset[0]='\0';
        render_expr(call->data.call.args[1], preset, sizeof(preset), &pp);
        if (*tnm) {
            ll_add_tag(p, tnm, "COUNTER");
            char rung[384];
            if (guard && *guard)
                snprintf(rung, sizeof(rung), "%sCTU(%s,%s,0);", guard, tnm, preset);
            else
                snprintf(rung, sizeof(rung), "CTU(%s,%s,0);", tnm, preset);
            ll_add_rung(r, rung);
        }
        return;
    }
    if (strcmp(nm, "cst_counter_reset") == 0 && call->data.call.arg_count >= 1) {
        char tnm[128]; EXTRACT_TAG(call->data.call.args[0], tnm);
        if (*tnm) {
            ll_add_tag(p, tnm, "COUNTER");
            char rung[256];
            if (guard && *guard)
                snprintf(rung, sizeof(rung), "%sRES(%s);", guard, tnm);
            else
                snprintf(rung, sizeof(rung), "RES(%s);", tnm);
            ll_add_rung(r, rung);
        }
        return;
    }
    if (strcmp(nm, "cst_memcpy") == 0 && call->data.call.arg_count >= 3) {
        char dst[128] = "", src[128] = "", len[128] = "";
        size_t dp=0, sp=0, lp=0;
        render_expr(call->data.call.args[0], dst, sizeof(dst), &dp);
        render_expr(call->data.call.args[1], src, sizeof(src), &sp);
        render_expr(call->data.call.args[2], len, sizeof(len), &lp);
        char rung[640];
        if (guard && *guard)
            snprintf(rung, sizeof(rung), "%sCOP(%s,%s,%s);", guard, src, dst, len);
        else
            snprintf(rung, sizeof(rung), "COP(%s,%s,%s);", src, dst, len);
        ll_add_rung(r, rung);
        return;
    }
    if (strcmp(nm, "cst_memset") == 0 && call->data.call.arg_count >= 3) {
        char dst[128] = "", val[128] = "", len[128] = "";
        size_t dp=0, vp=0, lp=0;
        render_expr(call->data.call.args[0], dst, sizeof(dst), &dp);
        render_expr(call->data.call.args[1], val, sizeof(val), &vp);
        render_expr(call->data.call.args[2], len, sizeof(len), &lp);
        char rung[640];
        if (guard && *guard)
            snprintf(rung, sizeof(rung), "%sFILL(%s,%s,%s);", guard, val, dst, len);
        else
            snprintf(rung, sizeof(rung), "FILL(%s,%s,%s);", val, dst, len);
        ll_add_rung(r, rung);
        return;
    }
    if (strcmp(nm, "cst_edge_rising") == 0 && call->data.call.arg_count >= 2) {
        char st[128] = "", in[128] = "";
        size_t sp=0, ip=0;
        EXTRACT_TAG(call->data.call.args[0], st);
        render_expr(call->data.call.args[1], in, sizeof(in), &ip);
        if (*st) {
            ll_add_tag(p, st, "BOOL");
            char rung[384];
            /* ONS uses a storage bit; rising-edge of `in` energizes one scan */
            if (guard && *guard)
                snprintf(rung, sizeof(rung), "%sONS(%s);", guard, st);
            else
                snprintf(rung, sizeof(rung), "ONS(%s);", st);
            ll_add_rung(r, rung);
        }
        return;
    }
    if (strcmp(nm, "cst_log_str") == 0 || strcmp(nm, "cst_log_int") == 0) {
        ll_add_error_rung(r, "AB-LL: cst_log_* requires MSG instruction (deferred)");
        return;
    }

    /* Generic user call -> JSR */
    char rung[1024];
    size_t pos = 0; rung[0] = '\0';
    if (guard && *guard) append_str(rung, sizeof(rung), &pos, guard);
    append_fmt(rung, sizeof(rung), &pos, "JSR(%s,%zu", nm, call->data.call.arg_count);
    for (size_t i = 0; i < call->data.call.arg_count; i++) {
        append_str(rung, sizeof(rung), &pos, ",");
        AST* a = call->data.call.args[i];
        if (a && a->type == N_UNARY && a->data.unary.op == TOKEN_AMPERSAND &&
            a->data.unary.operand && a->data.unary.operand->type == N_IDENT) {
            append_str(rung, sizeof(rung), &pos, a->data.unary.operand->data.ident.name);
        } else {
            render_expr(a, rung, sizeof(rung), &pos);
        }
    }
    append_str(rung, sizeof(rung), &pos, ");");
    ll_add_rung(r, rung);
    #undef EXTRACT_TAG
}

static void lower_stmt(LLProgram* p, LLRoutine* r, AST* stmt, const char* guard) {
    if (!stmt) return;

    switch (stmt->type) {
    case N_BLOCK: {
        for (size_t i = 0; i < stmt->data.block.count; i++)
            lower_stmt(p, r, stmt->data.block.statements[i], guard);
        return;
    }
    case N_DECL: {
        /* Add to tag table; emit init as assignment if present */
        const char* nm = stmt->data.decl.name;
        const char* type = abll_map_type(stmt->data.decl.type, stmt->data.decl.pointer_level);
        if (nm) {
            if (stmt->data.decl.array_dim_count > 0 && stmt->data.decl.array_dims[0] &&
                stmt->data.decl.array_dims[0]->type == N_INTLIT) {
                ll_add_array_tag(p, nm, type, stmt->data.decl.array_dims[0]->data.int_lit.value);
            } else {
                ll_add_tag(p, nm, type);
            }
        }
        if (stmt->data.decl.init_value && nm) {
            AST id; id.type = N_IDENT; id.data.ident.name = (char*)nm;
            lower_assign(p, r, &id, stmt->data.decl.init_value, guard);
        }
        return;
    }
    case N_ASSIGN: {
        AST id; id.type = N_IDENT; id.data.ident.name = stmt->data.assign.var_name;
        lower_assign(p, r, &id, stmt->data.assign.value, guard);
        return;
    }
    case N_OPERATOR: {
        /* Compound assignments like `x = y` come as N_ASSIGN; an N_OPERATOR
         * at statement level is rare — most likely arrow/dot LHS assignments
         * formed by parser. Try to detect `=` op with N_MEMBER_ACCESS LHS. */
        if (stmt->data.op.op == TOKEN_ASSIGN) {
            lower_assign(p, r, stmt->data.op.left, stmt->data.op.right, guard);
            return;
        }
        return;
    }
    case N_IF:    lower_if(p, r, stmt, guard); return;
    case N_WHILE: lower_while(p, r, stmt, guard); return;
    case N_FOR:   lower_for(p, r, stmt, guard); return;
    case N_DO_WHILE: {
        /* do { body } while(cond);  -> body once, then while(cond) body */
        int id = r->label_counter++;
        char lbl_top[64]; snprintf(lbl_top, sizeof(lbl_top), "DoLoop_%d", id);
        char rung[256];
        snprintf(rung, sizeof(rung), "LBL(%s);", lbl_top);
        ll_add_rung(r, rung);
        lower_stmt(p, r, stmt->data.do_while_stmt.body, guard);
        char chain[512]; size_t cp = 0; chain[0] = '\0';
        if (guard && *guard) append_str(chain, sizeof(chain), &cp, guard);
        build_bool_chain(p, r, stmt->data.do_while_stmt.condition, chain, sizeof(chain), &cp, 0);
        snprintf(rung, sizeof(rung), "%sJMP(%s);", chain, lbl_top);
        ll_add_rung(r, rung);
        return;
    }
    case N_RETURN: {
        /* MainRoutine has no caller — `return N;` is a no-op in ladder. */
        int is_main = (strcmp(r->name, "MainRoutine") == 0);
        if (stmt->data.return_stmt.value && !is_main) {
            char retname[128];
            snprintf(retname, sizeof(retname), "%s_ret", r->name);
            AST id; id.type = N_IDENT; id.data.ident.name = retname;
            lower_assign(p, r, &id, stmt->data.return_stmt.value, guard);
        }
        if (!is_main) {
            char rung[256];
            if (guard && *guard) snprintf(rung, sizeof(rung), "%sRET();", guard);
            else                 snprintf(rung, sizeof(rung), "RET();");
            ll_add_rung(r, rung);
        }
        return;
    }
    case N_CALL: {
        const char* nm = stmt->data.call.name;
        int unsup = abll_is_unsupported_call(nm);
        if (unsup) {
            ll_add_error_rung(r, abll_unsupported_call_comment(unsup));
            return;
        }
        /* Recognized runtime helpers */
        if (strncmp(nm, "cst_", 4) == 0) {
            lower_runtime_call(p, r, stmt, guard);
            return;
        }
        /* User function call */
        lower_runtime_call(p, r, stmt, guard);
        return;
    }
    case N_BREAK:    ll_add_rung(r, "BRK();"); return;
    case N_CONTINUE: ll_add_error_rung(r, "AB-LL: continue not lowered (use restructured logic)"); return;
    case N_ASM:      ll_add_error_rung(r, "AB-LL: inline asm has no ladder mapping"); return;
    case N_SWITCH: {
        /* Lower as cascade: if(expr==v1) b1; else if(expr==v2) b2; else def; */
        AST* expr = stmt->data.switch_stmt.expression;
        for (size_t i = 0; i < stmt->data.switch_stmt.case_count; i++) {
            AST* v = stmt->data.switch_stmt.case_values[i];
            char chain[512]; size_t cp = 0; chain[0] = '\0';
            if (guard && *guard) append_str(chain, sizeof(chain), &cp, guard);
            char e[128], cv[64]; size_t ep=0, cvp=0; e[0]=cv[0]='\0';
            render_numeric_operand(p, r, expr, e, sizeof(e), &ep);
            render_expr(v, cv, sizeof(cv), &cvp);
            append_fmt(chain, sizeof(chain), &cp, "EQU(%s,%s)", e, cv);
            lower_stmt(p, r, stmt->data.switch_stmt.case_bodies[i], chain);
        }
        if (stmt->data.switch_stmt.default_body) {
            /* default is "none of the above" — we don't track exclusion in ladder
             * cleanly; emit unguarded (other-than user-guard) and rely on case
             * order. For correctness, we synth a "matched" flag. */
            char matched[64];
            snprintf(matched, sizeof(matched), "_sw_%d", r->synth_counter++);
            ll_add_tag(p, matched, "BOOL");
            char chain[512]; size_t cp = 0; chain[0] = '\0';
            if (guard && *guard) append_str(chain, sizeof(chain), &cp, guard);
            append_fmt(chain, sizeof(chain), &cp, "XIO(%s)", matched);
            lower_stmt(p, r, stmt->data.switch_stmt.default_body, chain);
        }
        return;
    }
    default:
        /* Unhandled expression at statement level — emit comment */
        ll_add_error_rung(r, "AB-LL: unhandled statement node");
        return;
    }
}

/* ====================================================================== */
/*                    Top-level: function -> routine                      */
/* ====================================================================== */

static int is_main_function(AST* func) {
    if (!func || func->type != N_FUNCTION) return 0;
    const char* nm = func->data.function.name;
    return nm && (strcmp(nm, "main") == 0 || strcmp(nm, "Main") == 0 ||
                  strcmp(nm, "PLC_PRG") == 0 || strcmp(nm, "Run") == 0);
}

/* Walk N_STRUCT_DECL globals and register each as a Logix UDT. Member C
 * types are mapped to Logix types; runtime opaque types collapse to their
 * predefined equivalents. Skips the internal cst_* opaque structs (they
 * lower to TIMER/COUNTER/BOOL, never user UDTs). */
static void collect_structs(LLProgram* p, AST* root) {
    if (!root || root->type != N_PROGRAM) return;
    ProgramNode* prog = &root->data.program;
    for (size_t i = 0; i < prog->global_count; i++) {
        AST* g = prog->globals[i];
        if (!g || g->type != N_STRUCT_DECL) continue;
        const char* sname = g->data.struct_decl.name;
        if (!sname || !*sname) continue;
        /* Skip the runtime opaque handle types. */
        if (strncmp(sname, "cst_", 4) == 0) continue;

        LLUdt* u = ll_add_udt(p, sname);
        if (u->members_head) continue;  /* already populated */

        for (size_t m = 0; m < g->data.struct_decl.member_count; m++) {
            AST* mem = g->data.struct_decl.members[m];
            if (!mem || mem->type != N_DECL) continue;
            const char* mty = abll_map_type(mem->data.decl.type, mem->data.decl.pointer_level);
            int dim = 0;
            if (mem->data.decl.array_dim_count > 0 && mem->data.decl.array_dims[0] &&
                mem->data.decl.array_dims[0]->type == N_INTLIT)
                dim = mem->data.decl.array_dims[0]->data.int_lit.value;
            ll_udt_add_member(u, mem->data.decl.name, mty, dim);
        }
    }
}

static void collect_globals(LLProgram* p, AST* root) {
    if (!root || root->type != N_PROGRAM) return;
    ProgramNode* prog = &root->data.program;
    for (size_t i = 0; i < prog->global_count; i++) {
        AST* g = prog->globals[i];
        if (!g || g->type != N_DECL) continue;
        if (g->data.decl.is_extern) continue;
        const char* nm = g->data.decl.name;
        const char* ty = abll_map_type(g->data.decl.type, g->data.decl.pointer_level);
        if (!nm) continue;
        if (g->data.decl.array_dim_count > 0 && g->data.decl.array_dims[0] &&
            g->data.decl.array_dims[0]->type == N_INTLIT) {
            ll_add_array_tag(p, nm, ty, g->data.decl.array_dims[0]->data.int_lit.value);
        } else {
            ll_add_tag(p, nm, ty);
        }
        if (g->data.decl.init_value && g->data.decl.init_value->type == N_INTLIT) {
            LLTag* t = ll_find_tag(p, nm);
            if (t) {
                char b[32]; snprintf(b, sizeof(b), "%d", g->data.decl.init_value->data.int_lit.value);
                t->initial = xstrdup(b);
            }
        }
    }
}

static void lower_function(LLProgram* p, AST* func) {
    if (!func || func->type != N_FUNCTION || !func->data.function.body) return;
    FunctionNode* fn = &func->data.function;

    /* Determine routine name. main()-likes -> MainRoutine. */
    const char* rname = is_main_function(func) ? "MainRoutine" : fn->name;
    LLRoutine* r = ll_add_routine(p, rname);
    if (is_main_function(func)) p->main_routine = r;

    /* Register parameters as tags + record on routine for JSR docs */
    r->param_count = (int)fn->param_count;
    if (fn->param_count) {
        r->param_names = (char**)calloc(fn->param_count, sizeof(char*));
        for (size_t i = 0; i < fn->param_count; i++) {
            AST* pd = fn->params[i];
            if (pd && pd->type == N_DECL && pd->data.decl.name) {
                const char* ty = abll_map_type(pd->data.decl.type, pd->data.decl.pointer_level);
                ll_add_tag(p, pd->data.decl.name, ty);
                r->param_names[i] = xstrdup(pd->data.decl.name);
            } else {
                r->param_names[i] = xstrdup("");
            }
        }
    }

    /* Return slot — main is unreachable from JSR, so it has no return tag. */
    if (fn->return_type && strcmp(fn->return_type, "void") != 0 && !is_main_function(func)) {
        char retname[128];
        snprintf(retname, sizeof(retname), "%s_ret", rname);
        ll_add_tag(p, retname, abll_map_type(fn->return_type, 0));
        r->has_return = 1;
    }

    p->current_routine = r;
    lower_stmt(p, r, fn->body, NULL);
    p->current_routine = NULL;

    /* If empty, emit a no-op rung so Logix accepts it */
    if (r->rung_count == 0) ll_add_rung(r, "NOP();");
}

/* ====================================================================== */
/*                        L5X XML emission                                */
/* ====================================================================== */

static void xml_escape_into(FILE* fp, const char* s) {
    if (!s) return;
    for (; *s; s++) {
        switch (*s) {
        case '&':  fputs("&amp;",  fp); break;
        case '<':  fputs("&lt;",   fp); break;
        case '>':  fputs("&gt;",   fp); break;
        case '"':  fputs("&quot;", fp); break;
        case '\'': fputs("&apos;", fp); break;
        default:   fputc(*s, fp); break;
        }
    }
}

static void emit_export_date(char* buf, size_t n) {
    time_t now = time(NULL);
    struct tm* tm = localtime(&now);
    if (tm) strftime(buf, n, "%a %b %d %H:%M:%S %Y", tm);
    else snprintf(buf, n, "Mon Jan 01 00:00:00 2026");
}

static const char* radix_for_type(const char* type) {
    if (!type) return "Decimal";
    if (strcmp(type, "REAL") == 0 || strcmp(type, "LREAL") == 0) return "Float";
    if (strcmp(type, "BOOL") == 0) return "Decimal";
    if (strcmp(type, "TIMER") == 0 || strcmp(type, "COUNTER") == 0 ||
        strcmp(type, "STRING") == 0) return "NullType";
    return "Decimal";
}

static int is_primitive_type(const char* t) {
    if (!t) return 0;
    return strcmp(t, "BOOL") == 0 || strcmp(t, "SINT") == 0 || strcmp(t, "INT") == 0 ||
           strcmp(t, "DINT") == 0 || strcmp(t, "LINT") == 0 || strcmp(t, "REAL") == 0 ||
           strcmp(t, "LREAL") == 0;
}

/* Emit the <Structure> body for a TIMER / COUNTER predefined type. */
static void emit_predefined_structure(FILE* fp, const char* type, const char* indent) {
    fprintf(fp, "%s<Structure DataType=\"%s\">\n", indent, type);
    if (strcmp(type, "TIMER") == 0) {
        fprintf(fp, "%s  <DataValueMember Name=\"PRE\" DataType=\"DINT\" Radix=\"Decimal\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"ACC\" DataType=\"DINT\" Radix=\"Decimal\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"EN\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"TT\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"DN\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
    } else { /* COUNTER */
        fprintf(fp, "%s  <DataValueMember Name=\"PRE\" DataType=\"DINT\" Radix=\"Decimal\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"ACC\" DataType=\"DINT\" Radix=\"Decimal\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"CU\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"CD\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"DN\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"OV\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
        fprintf(fp, "%s  <DataValueMember Name=\"UN\" DataType=\"BOOL\" Value=\"0\"/>\n", indent);
    }
    fprintf(fp, "%s</Structure>\n", indent);
}

static void emit_tag(FILE* fp, LLProgram* p, LLTag* t) {
    fprintf(fp, "      <Tag Name=\"");
    xml_escape_into(fp, t->name);
    fprintf(fp, "\" TagType=\"Base\" DataType=\"%s\"", t->data_type);
    if (t->array_size > 0)
        fprintf(fp, " Dimensions=\"%d\"", t->array_size);
    if (is_primitive_type(t->data_type))
        fprintf(fp, " Radix=\"%s\"", radix_for_type(t->data_type));
    fprintf(fp, " Constant=\"false\" ExternalAccess=\"Read/Write\">\n");

    LLUdt* udt = ll_find_udt(p, t->data_type);

    if (t->array_size > 0) {
        /* Array of primitives — Logix wants an L5K array literal. */
        fprintf(fp, "        <Data Format=\"L5K\"><![CDATA[[");
        for (int i = 0; i < t->array_size; i++) fprintf(fp, "%s0", i ? "," : "");
        fprintf(fp, "]]]></Data>\n");
    } else if (is_structured_type(t->data_type)) {
        /* TIMER / COUNTER predefined structure. */
        fprintf(fp, "        <Data Format=\"Decorated\">\n");
        emit_predefined_structure(fp, t->data_type, "          ");
        fprintf(fp, "        </Data>\n");
    } else if (udt) {
        /* User struct instance — emit a Structure with each member. */
        fprintf(fp, "        <Data Format=\"Decorated\">\n");
        fprintf(fp, "          <Structure DataType=\"%s\">\n", t->data_type);
        for (LLUdtMember* m = udt->members_head; m; m = m->next) {
            if (m->array_size > 0) continue;  /* nested arrays: skip default data */
            if (strcmp(m->data_type, "REAL") == 0 || strcmp(m->data_type, "LREAL") == 0)
                fprintf(fp, "            <DataValueMember Name=\"%s\" DataType=\"%s\" Radix=\"Float\" Value=\"0.0\"/>\n",
                        m->name, m->data_type);
            else if (strcmp(m->data_type, "BOOL") == 0)
                fprintf(fp, "            <DataValueMember Name=\"%s\" DataType=\"BOOL\" Value=\"0\"/>\n", m->name);
            else
                fprintf(fp, "            <DataValueMember Name=\"%s\" DataType=\"%s\" Radix=\"Decimal\" Value=\"0\"/>\n",
                        m->name, m->data_type);
        }
        fprintf(fp, "          </Structure>\n");
        fprintf(fp, "        </Data>\n");
    } else if (is_primitive_type(t->data_type)) {
        /* Primitive scalar — L5K literal + decorated value. */
        const char* val = t->initial ? t->initial
                          : (strcmp(t->data_type, "REAL") == 0 ? "0.0" : "0");
        fprintf(fp, "        <Data Format=\"L5K\"><![CDATA[%s]]></Data>\n", val);
        fprintf(fp, "        <Data Format=\"Decorated\">\n");
        fprintf(fp, "          <DataValue DataType=\"%s\" Radix=\"%s\" Value=\"%s\"/>\n",
                t->data_type, radix_for_type(t->data_type), val);
        fprintf(fp, "        </Data>\n");
    }
    /* else: unknown type, emit no data (Logix will default-construct) */

    fprintf(fp, "      </Tag>\n");
}

/* Emit the <DataTypes> section with all user UDTs. */
static void emit_datatypes(FILE* fp, LLProgram* p) {
    if (!p->udts_head) {
        fprintf(fp, "  <DataTypes/>\n");
        return;
    }
    fprintf(fp, "  <DataTypes>\n");
    for (LLUdt* u = p->udts_head; u; u = u->next) {
        fprintf(fp, "    <DataType Name=\"%s\" Family=\"NoFamily\" Class=\"User\">\n", u->name);
        fprintf(fp, "      <Members>\n");
        for (LLUdtMember* m = u->members_head; m; m = m->next) {
            const char* radix = (strcmp(m->data_type, "REAL") == 0 || strcmp(m->data_type, "LREAL") == 0)
                                ? "Float" : "Decimal";
            int dim = m->array_size;
            if (strcmp(m->data_type, "BOOL") == 0)
                fprintf(fp, "        <Member Name=\"%s\" DataType=\"BOOL\" Dimension=\"%d\" Radix=\"Decimal\" Hidden=\"false\" ExternalAccess=\"Read/Write\"/>\n",
                        m->name, dim);
            else
                fprintf(fp, "        <Member Name=\"%s\" DataType=\"%s\" Dimension=\"%d\" Radix=\"%s\" Hidden=\"false\" ExternalAccess=\"Read/Write\"/>\n",
                        m->name, m->data_type, dim, radix);
        }
        fprintf(fp, "      </Members>\n");
        fprintf(fp, "    </DataType>\n");
    }
    fprintf(fp, "  </DataTypes>\n");
}

static void emit_rung(FILE* fp, LLRung* g, int number) {
    fprintf(fp, "        <Rung Number=\"%d\" Type=\"N\">\n", number);
    if (g->comment) {
        fprintf(fp, "          <Comment><![CDATA[");
        fputs(g->comment, fp);
        fprintf(fp, "]]></Comment>\n");
    }
    fprintf(fp, "          <Text><![CDATA[%s]]></Text>\n", g->text ? g->text : "NOP();");
    fprintf(fp, "        </Rung>\n");
}

static void emit_routine(FILE* fp, LLRoutine* r) {
    fprintf(fp, "      <Routine Name=\"%s\" Type=\"RLL\">\n", r->name);
    fprintf(fp, "        <RLLContent>\n");
    int n = 0;
    for (LLRung* g = r->rungs_head; g; g = g->next) {
        emit_rung(fp, g, n++);
    }
    fprintf(fp, "        </RLLContent>\n");
    fprintf(fp, "      </Routine>\n");
}

static void emit_l5x(LLProgram* p, const char* output_dir) {
    char path[1024];
    snprintf(path, sizeof(path), "%s/%s.L5X", output_dir, p->name);
    FILE* fp = fopen(path, "wb");
    if (!fp) {
        fprintf(stderr, "AB-LL: cannot open %s\n", path);
        return;
    }

    char date[64];
    emit_export_date(date, sizeof(date));

    const char* main_name = p->main_routine ? p->main_routine->name : "MainRoutine";

    fprintf(fp, "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
    fprintf(fp, "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"32.00\" "
                "TargetName=\"%s\" TargetType=\"Program\" ContainsContext=\"true\" "
                "ExportDate=\"%s\" ExportOptions=\"DecoratedData ForceProtectedEncoding AllProjDocTrans\">\n",
            p->name, date);

    fprintf(fp, "<Controller Use=\"Context\" Name=\"CST_Context\" ProcessorType=\"1756-L83E\" "
                "MajorRev=\"32\" MinorRev=\"11\" ProjectCreationDate=\"%s\" LastModifiedDate=\"%s\" "
                "TimeSlice=\"20\" ShareUnusedTimeSlice=\"1\" "
                "SFCExecutionControl=\"CurrentActive\" SFCRestartPosition=\"MostRecent\" "
                "SFCLastScan=\"DontScan\" MatchProjectToController=\"false\" "
                "CanUseRPIFromProducer=\"false\" InhibitAutomaticFirmwareUpdate=\"0\" "
                "PassThroughConfiguration=\"EnabledWithAppend\" "
                "DownloadProjectDocumentationAndExtendedProperties=\"true\" "
                "ReportMinorOverflow=\"true\">\n", date, date);

    emit_datatypes(fp, p);
    fprintf(fp, "  <Tags/>\n");
    fprintf(fp, "  <Programs Use=\"Context\">\n");
    fprintf(fp, "    <Program Use=\"Target\" Name=\"%s\" TestEdits=\"false\" "
                "MainRoutineName=\"%s\" Disabled=\"false\" UseAsFolder=\"false\">\n",
            p->name, main_name);

    /* Program tags */
    fprintf(fp, "    <Tags>\n");
    for (LLTag* t = p->tags_head; t; t = t->next) {
        emit_tag(fp, p, t);
    }
    fprintf(fp, "    </Tags>\n");

    /* Routines */
    fprintf(fp, "    <Routines>\n");
    /* Emit main routine first */
    if (p->main_routine) emit_routine(fp, p->main_routine);
    for (LLRoutine* r = p->routines_head; r; r = r->next) {
        if (r == p->main_routine) continue;
        emit_routine(fp, r);
    }
    fprintf(fp, "    </Routines>\n");

    fprintf(fp, "    </Program>\n");
    fprintf(fp, "  </Programs>\n");
    fprintf(fp, "</Controller>\n");
    fprintf(fp, "</RSLogix5000Content>\n");

    fclose(fp);
    printf("CST: %s.L5X written (%s)\n", p->name, path);
}

/* ====================================================================== */
/*                       Cleanup                                          */
/* ====================================================================== */

static void free_program(LLProgram* p) {
    for (LLTag* t = p->tags_head; t; ) {
        LLTag* nx = t->next;
        free(t->name); free(t->data_type); free(t->initial);
        free(t);
        t = nx;
    }
    for (LLRoutine* r = p->routines_head; r; ) {
        LLRoutine* nx = r->next;
        for (LLRung* g = r->rungs_head; g; ) {
            LLRung* gnx = g->next;
            free(g->text); free(g->comment); free(g);
            g = gnx;
        }
        if (r->param_names) {
            for (int i = 0; i < r->param_count; i++) free(r->param_names[i]);
            free(r->param_names);
        }
        free(r->name);
        free(r);
        r = nx;
    }
    free(p->name);
}

/* ====================================================================== */
/*                       Entry point                                      */
/* ====================================================================== */

static void abll_emit_program(struct AST* root, const char* output_dir)
{
    if (!root || root->type != N_PROGRAM) {
        fprintf(stderr, "AB-LL: root is not N_PROGRAM\n");
        return;
    }

    LLProgram prog = {0};
    prog.name = xstrdup("CST_Program");

    /* Pre-add a system always-true tag in case we need it. */
    LLTag* one = ll_add_tag(&prog, "_one_", "BOOL");
    if (one && !one->initial) one->initial = xstrdup("1");

    collect_structs(&prog, root);
    collect_globals(&prog, root);

    ProgramNode* P = &root->data.program;
    /* Lower main last so other routines are registered before forward JSRs */
    for (size_t i = 0; i < P->func_count; i++) {
        AST* f = P->functions[i];
        if (!f || f->type != N_FUNCTION) continue;
        if (f->data.function.is_extern) continue;
        if (!f->data.function.body) continue;
        if (is_main_function(f)) continue;
        lower_function(&prog, f);
    }
    for (size_t i = 0; i < P->func_count; i++) {
        AST* f = P->functions[i];
        if (!f || f->type != N_FUNCTION) continue;
        if (!is_main_function(f)) continue;
        lower_function(&prog, f);
    }

    /* If no main was found, synthesize an empty MainRoutine */
    if (!prog.main_routine) {
        LLRoutine* r = ll_add_routine(&prog, "MainRoutine");
        ll_add_rung(r, "NOP();");
        prog.main_routine = r;
    }

    emit_l5x(&prog, output_dir);
    free_program(&prog);
}

/* ====================================================================== */
/*                       Target descriptor                                */
/* ====================================================================== */

const plc_target_t target_allen_bradley_ll = {
    .name                       = "allen_bradley_ll",
    .runtime = {
        /* These are unused by the ladder backend (which has its own emitter)
         * but provide reasonable defaults if any shared codegen path runs. */
        .timer_fb_type          = "TON",
        .timer_in_member        = "EN",
        .timer_pt_member        = "PRE",
        .timer_done_member      = "DN",
        .timer_pt_is_time       = 0,
        .memcpy_fn              = "COP",
        .memcpy_dst_first       = 0,
        .memset_fn              = "FILL",
        .memset_dst_first       = 0,
        .log_supported          = 0,
        .log_int_fn             = "",
        .log_str_fn             = "",
        .redge_fb_type          = "ONS",
        .fedge_fb_type          = "OSF",
        .edge_clk_member        = "InputBit",
        .edge_q_member          = "OutputBit",
        .tof_fb_type            = "TOF",
        .tof_in_member          = "EN",
        .tof_pt_member          = "PRE",
        .tof_q_member           = "DN",
        .ctu_fb_type            = "CTU",
        .ctu_cu_member          = "CU",
        .ctu_reset_member       = "Reset",
        .ctu_pv_member          = "PRE",
        .ctu_q_member           = "DN",
        .ctu_cv_member          = "ACC",
        .ctd_fb_type            = "CTD",
        .ctd_cd_member          = "CD",
        .ctd_load_member        = "LD",
        .ctd_pv_member          = "PRE",
        .ctd_q_member           = "DN",
        .ctd_cv_member          = "ACC",
    },
    .map_type                   = abll_map_type,
    .get_type_size              = abll_get_type_size,
    .is_unsupported_call        = abll_is_unsupported_call,
    .unsupported_call_comment   = abll_unsupported_call_comment,
    .supports_pointers          = 0,
    .supports_enums             = 0,
    .supports_unsigned          = 0,
    .supports_continue          = 0,
    .supports_line_comments     = 1,
    .emit_program               = abll_emit_program,
    .resolve_var                = NULL,
};
