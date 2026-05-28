/*
 * cst_runtime.h — CST industrial PLC runtime shims.
 *
 * Include this in your CST source files to get IDE / IntelliSense support
 * for the builtin runtime types and functions. The CST compiler itself
 * recognizes these names as builtins and emits target-specific Structured
 * Text (TwinCAT TON / AB TONR / MEMCPY vs COP / etc.) automatically.
 *
 * Example:
 *
 *     #include "cst_runtime.h"
 *
 *     cst_time_t  door_close;
 *
 *     void update(int sensor) {
 *         cst_timer_on(&door_close, 5000);     // 5 second TON
 *         if (cst_timer_done(&door_close)) {
 *             cst_log_str("door close timeout");
 *         }
 *     }
 */
#ifndef CST_RUNTIME_H
#define CST_RUNTIME_H

/* Opaque timer handle. Lowered to TON (TwinCAT) or TONR (AB) FB instance. */
typedef struct {
    int  _opaque[8];   /* placeholder - real layout is vendor FB internals */
} cst_time_t;

/* Timer control. Preset is in milliseconds regardless of vendor. */
extern void cst_timer_on   (cst_time_t* t, int preset_ms);
extern void cst_timer_off  (cst_time_t* t);
extern int  cst_timer_done (cst_time_t* t);
extern int  cst_timer_elapsed(cst_time_t* t);   /* reserved; not yet emitted */

/* Memory ops. dst/src are byte addresses; n is byte count.
 * Lowers to MEMCPY/MEMSET (TwinCAT) or COP/FILL (AB; arg order differs but
 * the codegen handles the swap). */
extern void cst_memcpy(void* dst, const void* src, int n);
extern void cst_memset(void* dst, int byte_value, int n);

/* Diagnostic logging. On TwinCAT lowers to ADSLOGSTR; on AB emits a
 * placeholder comment until MSG-instruction lowering lands. */
extern void cst_log_str(const char* msg);
extern void cst_log_int(int value);

/* Math / utility — lower to IEC standard ABS/MIN/MAX/LIMIT.
 * Portable across vendors. */
extern int cst_abs   (int x);
extern int cst_min   (int a, int b);
extern int cst_max   (int a, int b);
extern int cst_clamp (int value, int lo, int hi);

/* Off-delay timer (TOF). Q is TRUE while `signal` is high OR for `preset_ms`
 * after `signal` falls. Use for "stay on for N ms after the button releases". */
typedef struct { int _opaque[8]; } cst_tof_t;
extern void cst_tof_start (cst_tof_t* t, int signal, int preset_ms);
extern int  cst_tof_active(cst_tof_t* t);

/* Rising-edge detector. `fired` is TRUE for exactly one scan after `signal`
 * transitions from low to high. Call `update` every scan with the current
 * signal level. Lowers to R_TRIG (TwinCAT) / ONS (AB) / LDP (FX). */
typedef struct { int _opaque[4]; } cst_redge_t;
extern void cst_redge_update(cst_redge_t* e, int signal);
extern int  cst_redge_fired (cst_redge_t* e);

/* Falling-edge detector. Same as above but for high-to-low transitions.
 * Lowers to F_TRIG (TwinCAT) / OSF (AB) / LDF (FX). */
typedef struct { int _opaque[4]; } cst_fedge_t;
extern void cst_fedge_update(cst_fedge_t* e, int signal);
extern int  cst_fedge_fired (cst_fedge_t* e);

/* Up-counter (CTU). Increments on rising edge of `input`, resets on `reset`,
 * latches `done` when count >= preset.  TwinCAT CTU / AB CTU / FX C0..C15. */
typedef struct { int _opaque[8]; } cst_ctu_t;
extern void cst_ctu_count(cst_ctu_t* c, int input, int reset, int preset);
extern int  cst_ctu_done (cst_ctu_t* c);
extern int  cst_ctu_value(cst_ctu_t* c);

/* Down-counter (CTD). Decrements on rising edge of `input`, loads preset on
 * `load`, latches `done` when count <= 0. */
typedef struct { int _opaque[8]; } cst_ctd_t;
extern void cst_ctd_count(cst_ctd_t* c, int input, int load, int preset);
extern int  cst_ctd_done (cst_ctd_t* c);
extern int  cst_ctd_value(cst_ctd_t* c);

/* ---------------- Industrial logic helpers ---------------- *
 *
 * cst_seal — classic set-reset latch (seal-in). One line replaces five lines
 * of fragile if/else bullshit:
 *
 *     pouring = cst_seal(pouring, beer_on, beer_off);
 *     valve_in = pouring; valve_out = pouring; pump = pouring;
 *
 * Lowers to the textbook seal-in rung:
 *
 *     [XIC(pouring), XIC(beer_on)] XIO(beer_off) OTE(pouring);
 *
 * Logical equivalent: `(state || set) && !reset` — SET-priority, meaning if
 * `set` and `reset` are both true the same scan, `state` ends up TRUE. That
 * matches what an operator expects from a START / STOP pair where START is
 * the "intent" button.
 *
 * For E-stops or safety circuits where you want STOP to ALWAYS win the tie,
 * use cst_seal_rp (reset-priority). Lowers to:
 *
 *     XIO(reset) [XIC(state), XIC(set)] OTE(state);
 *
 * which is `!reset && (state || set)` — reset is checked first. Always use
 * this for E-stop circuits.
 */
extern int cst_seal    (int state, int set, int reset);
extern int cst_seal_rp (int state, int set, int reset);

/* cst_within — range check. Lowers to LIM(lo, value, hi) on AB / a IN_RANGE
 * helper on TwinCAT. True iff lo <= value <= hi. */
extern int cst_within  (int value, int lo, int hi);

/* Extended math — IEC standard names; portable across vendors.
 * SQRT/LN/LOG/EXP/SIN/COS/TAN take REAL on TwinCAT/AB.
 * FLOOR/CEIL approximate via TRUNC.
 * POW = base ^ exp via IEC EXPT. */
extern int   cst_sqrt  (int x);
extern int   cst_pow   (int base, int exp);
extern int   cst_floor (int x);
extern int   cst_ceil  (int x);

#endif /* CST_RUNTIME_H */
