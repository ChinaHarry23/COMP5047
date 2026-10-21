/*
 * sep2.h - two-endmember light separation at the bat, plus F6 event detection.
 *
 * Each spectral frame y is explained as
 *
 *     y = baseline + a * shape_lamp + w * shape_broad + residual
 *
 *  - baseline is the room's own spectrum, learned with the lamp's estimated
 *    contribution removed. Because it is a whole spectrum, room light of any
 *    shape (LED, fluorescent lines) is absorbed exactly while it stays put.
 *  - a >= 0 is the lamp component. It is solved by least squares only on
 *    steady frames (the lamp changed < 5% during the read). On other frames
 *    it is estimated as lamp_gain x commanded light, where lamp_gain is the
 *    transfer measured on the last steady frames. With the lamp off, a = 0.
 *  - w is the broadband change relative to the baseline. It is signed: room
 *    lights going off is a negative w, not a negative lamp.
 *
 * The baseline adapts slowly while the lamp is off and nothing is happening,
 * and is frozen while lit and during events.
 *
 * F6 detector on w (in photopic lux): a fast rise above HIGH (from below LOW
 * within EDGE_F6_ONSET_MS) that holds for EDGE_F6_HOLD_MS starts an event;
 * EDGE_F6_END_MS below LOW ends it. Any offset larger than HIGH that lasts
 * EDGE_SCENE_CHANGE_MS (an event, a slow drift, or lights going off) is a
 * scene change: the baseline is re-learned and the event ends.
 *
 * None of this touches the budget: sep2 has no access to it. Every re-learn
 * (button, scene change, sensor return) bumps out.room_epoch, which is how
 * the FSM tells an event that ended from one that turned out to be the room.
 */
#ifndef EDGE_SEP2_H
#define EDGE_SEP2_H

#include <stdbool.h>
#include <stdint.h>

#include "calib.h"
#include "edge_light.h"
#include "spectral.h"

#ifdef __cplusplus
extern "C" {
#endif

#define EDGE_SEP2_HISTORY 64 /* 3.2 s of commanded light at one entry per step */

typedef enum { EDGE_F6_QUIET, EDGE_F6_PENDING, EDGE_F6_ACTIVE } edge_f6_phase_t;

typedef struct {
    const edge_calib_t *calib;

    /* Commanded lamp light, for the steady-frame gate. */
    uint32_t hist_t[EDGE_SEP2_HISTORY];
    float    hist_light[EDGE_SEP2_HISTORY];
    int      hist_head;
    int      hist_count;

    /* The room's spectrum, and the capture that (re)learns it. */
    float base[EDGE_SPEC_NCH];
    float capture_sum[EDGE_SPEC_NCH];
    int   capture_n;
    bool  baseline_ready;

    float    lamp_gain;     /* lamp amplitude per unit commanded light */
    bool     have_valid;
    uint32_t last_valid_ms;

    /* F6 detector and scene-change rule. */
    edge_f6_phase_t f6;
    uint32_t f6_since_ms;   /* pending start, or event start            */
    bool     below_low;
    uint32_t below_low_since_ms;
    uint32_t last_low_ms;   /* last frame with the change below LOW      */
    bool     offset;
    uint32_t offset_since_ms;

    unsigned rebaselines;   /* every re-learn, whatever its cause        */
    unsigned scene_changes; /* re-learns caused by the 30 s rule         */
    unsigned events;        /* F6 events started                         */

    edge_light_t out;
} edge_sep2_t;

/* initial_lamp_gain: expected lamp amplitude at full light, used until the
 * first steady lit frame measures it. Starts by learning the baseline. */
void edge_sep2_init(edge_sep2_t *s, const edge_calib_t *calib, float initial_lamp_gain);

/* Record the lamp light just commanded (call once per control step). */
void edge_sep2_note_light(edge_sep2_t *s, uint32_t now_ms, float light);

/* Process one frame. Returns the updated light estimate. */
const edge_light_t *edge_sep2_update(edge_sep2_t *s, const edge_spectral_frame_t *f);

/* Re-learn the room (button, new night). Clears any event immediately; the
 * FSM sees the new room_epoch and does not charge that event. */
void edge_sep2_rebaseline(edge_sep2_t *s);

/* The latest estimate. */
const edge_light_t *edge_sep2_light(const edge_sep2_t *s);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_SEP2_H */
