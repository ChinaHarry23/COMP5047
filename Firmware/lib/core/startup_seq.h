/*
 * startup_seq.h - the startup sequence that sizes tonight's budget.
 *
 *   SWEEP       needle self-test (boot only); a live fetch already runs
 *   WAIT_FETCH  until the fetch answers or EDGE_FETCH_CAP_MS has passed
 *               since begin; then the sky is resolved (live, cache, recorded,
 *               or protective)
 *   SHOW_SKY    the needle shows C on the inner arc for EDGE_SKY_SHOW_MS; a
 *               live answer that arrives now (after the cap) may only
 *               tighten the sky that is shown and handed over
 *   DONE        edge_startup_step() returns true once: hand the budget to
 *               the FSM with edge_fsm_set_budget(). From here on, a later
 *               answer goes to edge_fsm_tighten_budget() instead.
 *
 * The selector is read by the caller at begin (boot or new night) and never
 * again, so turning it mid-night changes nothing until the next new night.
 * Pure logic: the app does the fetching, the needle, and the clock.
 */
#ifndef EDGE_STARTUP_SEQ_H
#define EDGE_STARTUP_SEQ_H

#include <stdbool.h>
#include <stdint.h>

#include "forecast.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum { EDGE_SEL_LIVE, EDGE_SEL_REC_CLEAR, EDGE_SEL_REC_OVERCAST } edge_selector_t;

typedef enum { EDGE_SU_SWEEP, EDGE_SU_WAIT_FETCH, EDGE_SU_SHOW_SKY, EDGE_SU_DONE } edge_su_phase_t;

/* What the sequence may draw on when it resolves the sky. */
typedef struct {
    const edge_sky_cache_t *cache;   /* NVS cache, NULL if none         */
    int64_t now_epoch;               /* UTC seconds, 0 if clock unknown */
    edge_recorded_night_t rec_clear;
    edge_recorded_night_t rec_overcast;
} edge_startup_env_t;

typedef struct {
    edge_su_phase_t  phase;
    edge_selector_t  selector;
    uint32_t         begin_ms;
    uint32_t         phase_ms;
    bool             fetch_answered;
    bool             fetch_ok;
    bool             fetch_weighed;  /* the answer has been used or rejected */
    edge_sky_cache_t live;
    edge_sky_t       sky;            /* valid from SHOW_SKY on */
} edge_startup_t;

void edge_startup_begin(edge_startup_t *s, uint32_t now_ms, edge_selector_t sel, bool sweep);

/* True when this startup should start a live fetch (selector on LIVE). */
bool edge_startup_wants_fetch(const edge_startup_t *s);

/* Report the fetch outcome; live is NULL when it failed. Accepted until DONE:
 * an answer after the cap can only tighten (SHOW_SKY above). Once the phase
 * is DONE it is ignored here, and the caller tightens the FSM's budget. */
void edge_startup_fetch_result(edge_startup_t *s, const edge_sky_cache_t *live);

/* Advance. Returns true exactly once, when the budget should be handed over. */
bool edge_startup_step(edge_startup_t *s, uint32_t now_ms, const edge_startup_env_t *env);

const char *edge_selector_name(edge_selector_t sel);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_STARTUP_SEQ_H */
