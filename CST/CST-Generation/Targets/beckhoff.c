/*
 * Beckhoff TwinCAT 3 backend for CST.
 *
 * Maps C types and stdlib calls to TwinCAT-flavored IEC 61131-3:
 *   int    -> DINT
 *   short  -> INT
 *   char   -> BYTE
 *   float  -> REAL
 *   double -> REAL          (TwinCAT supports LREAL too; preserving legacy behavior)
 *   bool   -> BOOL
 *   T*     -> POINTER TO T  (TwinCAT supports pointers)
 *   char*  -> STRING        (special case)
 *   void*  -> POINTER TO BYTE
 *
 * This file replicates the type/call mapping that previously lived inline
 * in CST.c, byte-for-byte, so existing TwinCAT goldens are preserved.
 */

#include "../../Includes.h"
#include "target.h"

static const char* bk_map_type(const char* c_type, int pointer_level)
{
    static char buf[512];

    if (!c_type || strlen(c_type) == 0)
    {
        if (pointer_level > 0) return "POINTER TO BYTE";
        return "DINT";
    }

    /* Runtime types — recognized regardless of #include status. */
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
    if (strncmp(c_type, "struct ", 7) == 0)
        base = c_type + 7;

    /* char* -> STRING */
    if (strcmp(base, "char") == 0 && pointer_level == 1)
        return "STRING";

    /* void* -> POINTER TO BYTE (recursive for **, ***) */
    if (strcmp(base, "void") == 0 && pointer_level > 0)
    {
        strcpy(buf, "POINTER TO BYTE");
        for (int i = 1; i < pointer_level; i++)
        {
            char tmp[512];
            snprintf(tmp, sizeof(tmp), "POINTER TO %s", buf);
            strcpy(buf, tmp);
        }
        return buf;
    }

    const char* st_base = NULL;

    if (strcmp(base, "int") == 0 || strcmp(base, "long") == 0)
        st_base = "DINT";
    else if (strcmp(base, "short") == 0)
        st_base = "INT";
    else if (strcmp(base, "char") == 0)
        st_base = "BYTE";
    else if (strcmp(base, "float") == 0 || strcmp(base, "double") == 0)
        st_base = "REAL";
    else if (strcmp(base, "bool") == 0 || strcmp(base, "_Bool") == 0)
        st_base = "BOOL";
    else if (strcmp(base, "void") == 0)
        st_base = "VOID";
    else if (strcmp(base, "unsigned int") == 0 || strcmp(base, "unsigned long") == 0 ||
             strcmp(base, "unsigned") == 0)
        st_base = "UDINT";
    else if (strcmp(base, "unsigned short") == 0)
        st_base = "UINT";
    else if (strcmp(base, "unsigned char") == 0)
        st_base = "BYTE";
    else if (strcmp(base, "signed char") == 0)
        st_base = "SINT";
    else if (strcmp(base, "signed int") == 0 || strcmp(base, "signed") == 0)
        st_base = "DINT";
    else if (strcmp(base, "signed short") == 0)
        st_base = "INT";
    else if (strcmp(base, "signed long") == 0)
        st_base = "DINT";
    else
        st_base = base;

    if (pointer_level > 0)
    {
        snprintf(buf, sizeof(buf), "%s", st_base);
        for (int i = 0; i < pointer_level; i++)
        {
            char tmp[512];
            snprintf(tmp, sizeof(tmp), "POINTER TO %s", buf);
            strcpy(buf, tmp);
        }
        return buf;
    }

    return st_base;
}

static int bk_get_type_size(const char* c_type)
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

static int bk_is_unsupported_call(const char* name)
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

static const char* bk_unsupported_call_comment(int code)
{
    switch (code)
    {
    case 1: return "(* NO CONSOLE IN ST *)";
    case 2: return "(* HEAP NOT SUPPORTED - USE ARENA *)";
    default: return "(* UNSUPPORTED CALL *)";
    }
}

const plc_target_t target_beckhoff = {
    .name                       = "beckhoff",
    .runtime = {
        .timer_fb_type          = "TON",
        .timer_in_member        = "IN",
        .timer_pt_member        = "PT",
        .timer_done_member      = "Q",
        .timer_pt_is_time       = 1,
        .memcpy_fn              = "MEMCPY",
        .memcpy_dst_first       = 1,    /* MEMCPY(dst, src, n) */
        .memset_fn              = "MEMSET",
        .memset_dst_first       = 1,
        .log_supported          = 1,
        .log_int_fn             = "ADSLOGSTR",
        .log_str_fn             = "ADSLOGSTR",
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
    .map_type                   = bk_map_type,
    .get_type_size              = bk_get_type_size,
    .is_unsupported_call        = bk_is_unsupported_call,
    .unsupported_call_comment   = bk_unsupported_call_comment,
    .supports_pointers          = 1,
    .supports_enums             = 1,
    .supports_unsigned          = 1,
    .supports_continue          = 1,   /* CODESYS extension; standard IEC has none */
    .supports_line_comments     = 1,   /* TwinCAT 3 accepts // */
};
