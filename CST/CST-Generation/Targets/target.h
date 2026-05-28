#pragma once
#ifndef PLC_TARGET_H
#define PLC_TARGET_H

#include <stddef.h>

/*
 * Backend abstraction for CST.
 *
 * Each PLC vendor (Beckhoff TwinCAT, Allen-Bradley Studio 5000, ...) provides
 * one plc_target_t instance describing its dialect and capabilities. The
 * codegen in CST.c calls through g_target so adding a new vendor is just
 * filling in a new struct, not touching codegen.
 *
 * Selection precedence (highest first):
 *   1. CLI flag:        -target=beckhoff | -target=allen_bradley
 *   2. Source pragma:   #include <beckhoff> | #include <allen_bradley>
 *   3. Default:         beckhoff
 */

/* Runtime shim configuration. Each backend fills in the strings below; codegen
 * reads them when emitting calls to `cst_*` functions and declarations of
 * `cst_*` types. Lets us write portable PLC code in C and produce the right
 * vendor-specific ST without per-call backend logic.
 *
 * If `<feature>_supported` is 0 the codegen emits a clear "(* AB: not yet *)"
 * comment instead of broken code. */
typedef struct plc_runtime_t
{
    /* Timers — modeled as the IEC TON family. User declares `cst_time_t t;`
     * which lowers to an FB instance, then calls cst_timer_on(&t, ms) and
     * reads cst_timer_done(&t). */
    const char* timer_fb_type;       /* "TON"   / "TONR" */
    const char* timer_in_member;     /* "IN"    / "TimerEnable" */
    const char* timer_pt_member;     /* "PT"    / "PRE" */
    const char* timer_done_member;   /* "Q"     / "DN" */
    int         timer_pt_is_time;    /* 1 = TIME literal (T#5s); 0 = ms DINT */

    /* Memory ops */
    const char* memcpy_fn;           /* "MEMCPY" / "COP" */
    int         memcpy_dst_first;    /* TwinCAT (dst,src,n) = 1; AB COP(src,dst,len) = 0 */
    const char* memset_fn;           /* "MEMSET" / "FILL" */
    int         memset_dst_first;

    /* Logging */
    int         log_supported;       /* 0 means emit a comment */
    const char* log_int_fn;          /* "ADSLOGSTR" / "" */
    const char* log_str_fn;          /* "ADSLOGSTR" / "" */

    /* Edge detectors */
    const char* redge_fb_type;       /* "R_TRIG" / "" */
    const char* fedge_fb_type;       /* "F_TRIG" / "" */
    const char* edge_clk_member;     /* "CLK"  on TwinCAT  */
    const char* edge_q_member;       /* "Q"    on TwinCAT  */

    /* Off-delay timer (TOF) */
    const char* tof_fb_type;         /* "TOF" */
    const char* tof_in_member;       /* "IN" */
    const char* tof_pt_member;       /* "PT" */
    const char* tof_q_member;        /* "Q"  */

    /* Up-counter (CTU) */
    const char* ctu_fb_type;         /* "CTU" / "CTU" */
    const char* ctu_cu_member;       /* "CU"  / "CU"  */
    const char* ctu_reset_member;    /* "RESET" / "RES" */
    const char* ctu_pv_member;       /* "PV" / "PRE" */
    const char* ctu_q_member;        /* "Q"  / "DN"  */
    const char* ctu_cv_member;       /* "CV" / "ACC" */

    /* Down-counter (CTD) */
    const char* ctd_fb_type;         /* "CTD" / "CTD" */
    const char* ctd_cd_member;       /* "CD" */
    const char* ctd_load_member;     /* "LOAD" / "LD" */
    const char* ctd_pv_member;
    const char* ctd_q_member;
    const char* ctd_cv_member;
} plc_runtime_t;

typedef struct plc_target_t
{
    const char* name;                       /* "beckhoff" / "allen_bradley" */
    plc_runtime_t runtime;                  /* Shim configuration (Phase 4) */

    /* ---- Type mapping ---- */
    /* Map a C type + pointer level to the vendor's ST type spelling.
     * Implementations may return a pointer to a static buffer; callers
     * must consume the result before the next call. */
    const char* (*map_type)(const char* c_type, int pointer_level);

    /* Byte size of a C primitive type, or 0 if unknown. */
    int         (*get_type_size)(const char* c_type);

    /* ---- C stdlib gating ---- */
    /* Returns 0 for supported calls; non-zero for unsupported calls.
     * The non-zero code is passed to unsupported_call_comment(). */
    int         (*is_unsupported_call)(const char* name);
    const char* (*unsupported_call_comment)(int code);

    /* ---- Capability flags ---- */
    /* Currently informational; future codegen passes will branch on these. */
    int supports_pointers;
    int supports_enums;
    int supports_unsigned;
    int supports_continue;
    int supports_line_comments;

    /* ---- Whole-program emission override ---- */
    /* If non-NULL, called instead of the built-in IEC ST file writer. The
     * target owns everything: file structure, format, naming. Used by
     * targets that don't speak ST (e.g. Mitsubishi FX → Instruction List). */
    void (*emit_program)(struct AST* root, const char* output_dir);

    /* If non-NULL, called for each variable name during ST emission.
     * Returns the hardware device address string (e.g. "X0", "Y1", "M5")
     * if the variable is bound to a physical device, or NULL to use the
     * variable name as-is.  Used by Mitsubishi FX to substitute CST_IO
     * pragma bindings (X0->StartBtn → emit "X0" in place of "StartBtn").
     * Beckhoff / Allen-Bradley leave this NULL. */
    const char* (*resolve_var)(const char* name);
} plc_target_t;

extern const plc_target_t target_beckhoff;
extern const plc_target_t target_allen_bradley;
extern const plc_target_t target_allen_bradley_ll;
extern const plc_target_t target_mitsubishi_fx;

/* Forward: the AST root. The whole-program emitter takes this. */
struct AST;

/* Optional whole-program emission hook. When non-NULL, codegen calls this
 * INSTEAD of the built-in IEC ST writer (which writes flat .st files for
 * Beckhoff / AB to be wrapped into vendor XML by the IDE). Targets like
 * Mitsubishi FX, which speak Instruction List and have a single-program
 * monolithic output, set this. */

/* Active target. Defaults to &target_beckhoff. */
extern const plc_target_t* g_target;

/* Override the active target. Pass NULL to reset to default. */
void                       plc_target_set(const plc_target_t* t);

/* Look up a target by name string. Returns NULL if name is unknown. */
const plc_target_t*        plc_target_lookup(const char* name);

#endif /* PLC_TARGET_H */
