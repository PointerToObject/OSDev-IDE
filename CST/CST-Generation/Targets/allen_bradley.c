/*
 * Allen-Bradley Studio 5000 / Logix Designer backend for CST.
 *
 * STUB. Type mapping and capability flags reflect Logix conventions; the
 * real project-file emission (L5X XML, AOI structure) lands in Phase 6.
 *
 * Differences from Beckhoff that this stub already enforces:
 *   - No BYTE primitive   -> char becomes SINT
 *   - No unsigned types   -> all unsigned variants collapse to signed widths
 *   - No POINTER TO       -> pointer-typed values map to a stub name; the
 *                            codegen will reject non-VAR_IN_OUT pointer use
 *                            once capability checks are wired in (Phase 2)
 *   - LREAL for double    -> distinct from REAL
 *   - char* still maps to STRING (Logix STRING is structurally different
 *                                 but lowering happens in Phase 6)
 */

#include "../../Includes.h"
#include "target.h"

static const char* ab_map_type(const char* c_type, int pointer_level)
{
    static char buf[512];

    if (!c_type || strlen(c_type) == 0)
    {
        if (pointer_level > 0) return "(* AB: pointers unsupported *) DINT";
        return "DINT";
    }

    /* Runtime types — recognized regardless of #include status. */
    if (pointer_level == 0)
    {
        if (strcmp(c_type, "cst_time_t")  == 0) return "TONR";
        if (strcmp(c_type, "cst_tof_t")   == 0) return "TOF";
        if (strcmp(c_type, "cst_redge_t") == 0) return "ONS";
        if (strcmp(c_type, "cst_fedge_t") == 0) return "OSF";
        if (strcmp(c_type, "cst_ctu_t")   == 0) return "CTU";
        if (strcmp(c_type, "cst_ctd_t")   == 0) return "CTD";
    }

    const char* base = c_type;
    if (strncmp(c_type, "struct ", 7) == 0)
        base = c_type + 7;

    if (strcmp(base, "char") == 0 && pointer_level == 1)
        return "STRING";

    if (strcmp(base, "void") == 0 && pointer_level > 0)
    {
        /* Logix has no pointers; emit a placeholder and let Phase 6 reject. */
        strcpy(buf, "(* AB: void* unsupported *) DINT");
        return buf;
    }

    const char* st_base = NULL;

    if (strcmp(base, "int") == 0 || strcmp(base, "long") == 0)
        st_base = "DINT";
    else if (strcmp(base, "short") == 0)
        st_base = "INT";
    else if (strcmp(base, "char") == 0)
        st_base = "SINT";                 /* AB has no BYTE */
    else if (strcmp(base, "float") == 0)
        st_base = "REAL";
    else if (strcmp(base, "double") == 0)
        st_base = "LREAL";                /* AB distinguishes 32 vs 64 */
    else if (strcmp(base, "bool") == 0 || strcmp(base, "_Bool") == 0)
        st_base = "BOOL";
    else if (strcmp(base, "void") == 0)
        st_base = "VOID";
    /* AB has no native unsigned types — fold into next-larger signed where safe. */
    else if (strcmp(base, "unsigned char") == 0)
        st_base = "INT";                  /* widen to keep 0..255 representable */
    else if (strcmp(base, "unsigned short") == 0)
        st_base = "DINT";                 /* widen to keep 0..65535 representable */
    else if (strcmp(base, "unsigned int") == 0 || strcmp(base, "unsigned long") == 0 ||
             strcmp(base, "unsigned") == 0)
        st_base = "DINT";                 /* sign loss; Phase 2 should warn */
    else if (strcmp(base, "signed char") == 0)
        st_base = "SINT";
    else if (strcmp(base, "signed int") == 0 || strcmp(base, "signed") == 0)
        st_base = "DINT";
    else if (strcmp(base, "signed short") == 0)
        st_base = "INT";
    else if (strcmp(base, "signed long") == 0)
        st_base = "DINT";
    else
        st_base = base;                   /* user-defined struct/typedef */

    if (pointer_level > 0)
    {
        /* Logix has no pointer types. Pass-by-reference is via VAR_IN_OUT
         * (stripped one level by the function emitter); any leftover pointer
         * here is unsupported. Emit a marker; Phase 2 capability checks will
         * convert this into a hard error. */
        snprintf(buf, sizeof(buf), "(* AB: POINTER TO %s unsupported *) %s", st_base, st_base);
        return buf;
    }

    return st_base;
}

static int ab_get_type_size(const char* c_type)
{
    if (!c_type) return 0;
    if (strcmp(c_type, "char") == 0 || strcmp(c_type, "unsigned char") == 0 ||
        strcmp(c_type, "signed char") == 0)
        return 1;
    if (strcmp(c_type, "short") == 0 || strcmp(c_type, "unsigned short") == 0)
        return 2;
    if (strcmp(c_type, "int") == 0 || strcmp(c_type, "unsigned int") == 0 ||
        strcmp(c_type, "long") == 0 || strcmp(c_type, "unsigned long") == 0 ||
        strcmp(c_type, "float") == 0)
        return 4;
    if (strcmp(c_type, "double") == 0)
        return 8;
    return 0;
}

static int ab_is_unsupported_call(const char* name)
{
    if (!name) return 0;
    if (strcmp(name, "printf") == 0 || strcmp(name, "fprintf") == 0 ||
        strcmp(name, "sprintf") == 0 || strcmp(name, "snprintf") == 0 ||
        strcmp(name, "scanf") == 0 || strcmp(name, "puts") == 0 ||
        strcmp(name, "putchar") == 0 || strcmp(name, "getchar") == 0 ||
        strcmp(name, "gets") == 0 || strcmp(name, "fgets") == 0)
        return 1;
    if (strcmp(name, "malloc") == 0 || strcmp(name, "calloc") == 0 ||
        strcmp(name, "realloc") == 0 || strcmp(name, "free") == 0)
        return 2;
    return 0;
}

static const char* ab_unsupported_call_comment(int code)
{
    switch (code)
    {
    case 1: return "(* AB: console I/O - use MSG instruction *)";
    case 2: return "(* AB: heap unsupported - use static tags *)";
    default: return "(* AB: unsupported call *)";
    }
}

const plc_target_t target_allen_bradley = {
    .name                       = "allen_bradley",
    .runtime = {
        .timer_fb_type          = "TONR",
        .timer_in_member        = "TimerEnable",
        .timer_pt_member        = "PRE",
        .timer_done_member      = "DN",
        .timer_pt_is_time       = 0,    /* AB takes preset as DINT milliseconds */
        .memcpy_fn              = "COP",
        .memcpy_dst_first       = 0,    /* COP(src, dst, len) */
        .memset_fn              = "FILL",
        .memset_dst_first       = 0,    /* FILL(src, dst, len) */
        .log_supported          = 0,    /* AB needs MSG instruction; deferred */
        .log_int_fn             = "",
        .log_str_fn             = "",
        .redge_fb_type          = "ONS",      /* AB one-shot; FBD-flavored on Logix */
        .fedge_fb_type          = "OSF",      /* one-shot falling */
        .edge_clk_member        = "InputBit", /* Logix ONS uses InputBit */
        .edge_q_member          = "OutputBit",
        .tof_fb_type            = "TOF",      /* AB TOF instruction exists */
        .tof_in_member          = "TimerEnable",
        .tof_pt_member          = "PRE",
        .tof_q_member           = "DN",
        .ctu_fb_type            = "CTU",
        .ctu_cu_member          = "CUEnable",
        .ctu_reset_member       = "Reset",
        .ctu_pv_member          = "PRE",
        .ctu_q_member           = "DN",
        .ctu_cv_member          = "ACC",
        .ctd_fb_type            = "CTD",
        .ctd_cd_member          = "CDEnable",
        .ctd_load_member        = "Load",
        .ctd_pv_member          = "PRE",
        .ctd_q_member           = "DN",
        .ctd_cv_member          = "ACC",
    },
    .map_type                   = ab_map_type,
    .get_type_size              = ab_get_type_size,
    .is_unsupported_call        = ab_is_unsupported_call,
    .unsupported_call_comment   = ab_unsupported_call_comment,
    .supports_pointers          = 0,   /* No POINTER TO; VAR_IN_OUT only */
    .supports_enums             = 0,   /* Logix has no enum type */
    .supports_unsigned          = 0,   /* No native unsigned */
    .supports_continue          = 0,   /* Strict IEC */
    .supports_line_comments     = 0,   /* Older releases reject // */
};
