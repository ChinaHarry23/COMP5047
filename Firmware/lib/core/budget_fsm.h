/*
 * budget_fsm.h - the six-state lamp policy and tonight's budget (plan 7.3).
 *
 *   STARTUP    lamp 0           -> DARK_IDLE when the budget is set and the
 *                                  room's light has been learned
 *   DARK_IDLE  lamp 0           -> LIT on presence; YIELD on an F6 event
 *   LIT        floor + (max - floor) x remaining / B, in perceptual level
 *                               -> FADING when the hand leaves; YIELD on F6
 *   YIELD      floor if present, else 0 (another light is there: the lamp
 *              adds nothing above the floor) -> LIT / FADING / DARK_IDLE
 *              when the event ends
 *   FADING     0 over the fade  -> LIT on presence; DARK_IDLE at 0
 *   FAULT      floor if present, else 0; entered when no valid light reading
 *              arrives for EDGE_SENSOR_TIMEOUT_MS, left on its own once the
 *              sensor has been back for EDGE_FAULT_CLEAR_MS
 *
 * Rules that hold in every state:
 *  - edge_fsm_step() is the only thing that moves the lamp or spends budget,
 *    and the app calls it from exactly one place every EDGE_STEP_MS;
 *  - all lamp changes are ramps at fixed full-scale rates (EDGE_FADE_UP_MS up,
 *    EDGE_FADE_DOWN_MS down, EDGE_DIM_TO_FLOOR_MS for YIELD/FAULT), so a fade
 *    from level L takes L x that time: the plan's 1.5 s / 5 s are fades from
 *    maximum (TP3 must be timed from maximum);
 *  - the level is perceptual: emitted light is level^EDGE_LAMP_GAMMA, so F4's
 *    interpolation between floor and maximum is linear in perceived
 *    brightness, not in lux (half the budget gives 0.49 of maximum light with
 *    the placeholder floor, not the lux-linear 0.59);
 *  - while someone is present the lamp never crosses downward through the
 *    floor, whatever the state (a new night mid-visit included);
 *  - the remaining budget only shrinks between new nights: a re-baseline
 *    never touches it, a forecast refresh can only tighten B.
 *
 * F6 events: plan F6 charges an event's light, and its 30 s rule says an
 * "event" that lasts longer was the room changing. So an event's dose waits
 * aside (event_pending) and is committed when the event ends; if it ends
 * because the light layer re-learned the room (scene change or re-baseline
 * button: room_epoch changed, edge_light.h), it is dropped. The needle and
 * the lamp's pull-back follow committed spending only, so they move when a
 * torch is taken away, not while it is held. (TP15 "budget unchanged" read as
 * "remaining unchanged"; the team rules on that reading.)
 *
 * "Night" is the startup baseline and the operator's new-night button, never
 * the wall clock.
 */
#ifndef EDGE_BUDGET_FSM_H
#define EDGE_BUDGET_FSM_H

#include <stdbool.h>
#include <stdint.h>

#include "calib.h"
#include "dose.h"
#include "edge_light.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    EDGE_ST_STARTUP = 0,
    EDGE_ST_DARK_IDLE,
    EDGE_ST_LIT,
    EDGE_ST_YIELD,
    EDGE_ST_FADING,
    EDGE_ST_FAULT,
    EDGE_ST_COUNT
} edge_state_t;

typedef struct {
    uint32_t     now_ms;
    bool         present; /* debounced hand on the mark */
    edge_light_t light;   /* latest light estimate      */
} edge_fsm_input_t;

typedef struct {
    const edge_calib_t *calib;

    edge_state_t state;
    uint32_t     state_since_ms;
    uint32_t     last_step_ms;
    bool         stepped;
    float        level;            /* perceptual lamp level 0..1 */

    bool              budget_set;  /* tonight's budget has been handed over */
    float             budget_fmm;  /* B                                     */
    float             sky_cloud;   /* the C that sized B                    */
    edge_dose_tally_t tally;       /* tonight's committed spending          */
    edge_dose_rate_t  rate;        /* light charged during the last step    */

    bool              in_event;        /* an F6 event is running             */
    unsigned          event_epoch;     /* the light layer's room_epoch at its start */
    edge_dose_tally_t event_pending;   /* its dose so far, not yet committed */
    unsigned          events_settled;  /* finished events (for the log)      */
    double            last_event_fmm;  /* the latest finished event's dose   */
    bool              last_event_charged; /* ... committed (true) or dropped as the room */

    bool         sensor_ok;
    uint32_t     last_valid_ms;    /* last valid light reading (init time until the first) */
    uint32_t     sensor_ok_since_ms;
} edge_fsm_t;

void edge_fsm_init(edge_fsm_t *f, const edge_calib_t *calib, uint32_t now_ms);

/* The single control step. */
void edge_fsm_step(edge_fsm_t *f, const edge_fsm_input_t *in);

/* Operator action: start a new night (re-runs STARTUP, resets spending). */
void edge_fsm_new_night(edge_fsm_t *f, uint32_t now_ms);

/* Hand over tonight's budget (> 0); accepted once per night. */
bool edge_fsm_set_budget(edge_fsm_t *f, float budget_fmm, float cloud);

/* A later forecast may only make tonight's budget smaller. */
bool edge_fsm_tighten_budget(edge_fsm_t *f, float budget_fmm, float cloud);

/* Linear light for the lamp driver and the light layer: level^gamma. */
float edge_fsm_light(const edge_fsm_t *f);

/* B - committed spending; negative once past zero. */
double edge_fsm_remaining(const edge_fsm_t *f);

const char *edge_fsm_state_name(edge_state_t s);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_BUDGET_FSM_H */
