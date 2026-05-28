/*
 * cst_devices.h — Industrial device abstractions for CST.
 *
 * High-level types for the building blocks of any PLC program:
 *   button_t  — debounced / edge-detected push button
 *   valve_t   — solenoid valve with optional position feedback
 *   pump_t    — pump with run command and speed reference
 *   light_t   — indicator light (on/off; blink via separate timer)
 *
 * The functions are all `static inline` so the CST compiler folds them into
 * the call site — clean ST on TwinCAT/AB, clean Instruction List on FX.
 *
 * I/O wiring pattern (PLC scan model: read inputs → run logic → write outputs):
 *
 *     #include <beckhoff>          // or <mitsubishi>, <allen_bradley>
 *     #include "cst_devices.h"
 *
 *     // Map physical pins to plain int "raw" variables via CST_IO pragmas.
 *     // CST_IO: X0 -> StartBtnRaw
 *     // CST_IO: X1 -> StopBtnRaw
 *     // CST_IO: Y0 -> XV1Cmd
 *     // CST_IO: Y4 -> P1RunCmd
 *     // CST_IO: Y6 -> RunLightCmd
 *
 *     int StartBtnRaw = 0;
 *     int StopBtnRaw  = 0;
 *     int XV1Cmd      = 0;
 *     int P1RunCmd    = 0;
 *     int RunLightCmd = 0;
 *
 *     // Declare logical devices.
 *     button_t StartBtn;
 *     button_t StopBtn;
 *     valve_t  XV1;
 *     pump_t   P1;
 *     light_t  RunLight;
 *
 *     int main(void) {
 *         // 1. Read raw inputs into device state
 *         button_poll(&StartBtn, StartBtnRaw);
 *         button_poll(&StopBtn,  StopBtnRaw);
 *
 *         // 2. Logic in terms of devices, not pins
 *         if (button_pressed(&StartBtn)) {
 *             valve_open(&XV1);
 *             pump_run(&P1, 45);
 *             light_on(&RunLight);
 *         }
 *         if (button_pressed(&StopBtn)) {
 *             valve_close(&XV1);
 *             pump_stop(&P1);
 *             light_off(&RunLight);
 *         }
 *
 *         // 3. Drive raw outputs from device state
 *         XV1Cmd      = valve_apply(&XV1);
 *         P1RunCmd    = pump_apply(&P1);
 *         RunLightCmd = light_apply(&RunLight);
 *         return 0;
 *     }
 */
#ifndef CST_DEVICES_H
#define CST_DEVICES_H

/* ============================================================
 *  Push button — debounced (one-scan press / release detection)
 * ============================================================ */
typedef struct {
    int raw;          /* last scan's physical input */
    int pressed;      /* TRUE for exactly one scan after rising edge */
    int released;     /* TRUE for exactly one scan after falling edge */
    int held;         /* TRUE while button is currently down */
} button_t;

static inline void button_poll(button_t* b, int raw) {
    if (raw == 1 && b->raw == 0) { b->pressed = 1;  } else { b->pressed = 0;  }
    if (raw == 0 && b->raw == 1) { b->released = 1; } else { b->released = 0; }
    if (raw == 1) { b->held = 1; } else { b->held = 0; }
    b->raw = raw;
}

static inline int button_pressed (button_t* b) { return b->pressed;  }
static inline int button_released(button_t* b) { return b->released; }
static inline int button_held    (button_t* b) { return b->held;     }

/* ============================================================
 *  Valve — solenoid / pneumatic, on/off command + optional feedback
 * ============================================================ */
typedef struct {
    int cmd;          /* commanded state we drive to the output */
    int feedback;     /* optional position feedback (read from input) */
    int fault;        /* TRUE when feedback disagrees with cmd */
} valve_t;

static inline void valve_open  (valve_t* v) { v->cmd = 1; }
static inline void valve_close (valve_t* v) { v->cmd = 0; }
static inline void valve_set   (valve_t* v, int state) { v->cmd = state; }
static inline int  valve_state (valve_t* v) { return v->cmd; }

/* Returns the value to drive to the physical output pin. */
static inline int  valve_apply (valve_t* v) { return v->cmd; }

/* Optional: call with the limit-switch feedback to detect mismatches. */
static inline void valve_check_feedback(valve_t* v, int fb) {
    v->feedback = fb;
    if (v->cmd == fb) { v->fault = 0; } else { v->fault = 1; }
}

/* ============================================================
 *  Pump — run command + speed reference
 * ============================================================ */
typedef struct {
    int run_cmd;      /* 0 = stopped, 1 = running */
    int speed_ref;    /* commanded speed (engineering units of your choice) */
    int speed_actual; /* optional VFD feedback */
} pump_t;

static inline void pump_run(pump_t* p, int speed) {
    p->run_cmd = 1;
    p->speed_ref = speed;
}

static inline void pump_stop(pump_t* p) {
    p->run_cmd = 0;
    p->speed_ref = 0;
}

static inline void pump_set_speed(pump_t* p, int speed) {
    p->speed_ref = speed;
}

static inline int  pump_apply     (pump_t* p) { return p->run_cmd;   }
static inline int  pump_speed_ref (pump_t* p) { return p->speed_ref; }
static inline int  pump_running   (pump_t* p) { return p->run_cmd;   }

/* ============================================================
 *  Indicator light — on/off
 *  (For blinking, declare a cst_time_t in your code and toggle .on yourself.)
 * ============================================================ */
typedef struct {
    int on;           /* commanded state */
} light_t;

static inline void light_on    (light_t* l) { l->on = 1; }
static inline void light_off   (light_t* l) { l->on = 0; }
static inline void light_set   (light_t* l, int state) { l->on = state; }
static inline int  light_apply (light_t* l) { return l->on; }

#endif /* CST_DEVICES_H */
