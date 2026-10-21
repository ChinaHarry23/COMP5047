/*
 * dose.h - what the bat receives, in photopic lux and in the V'-proxy, and
 * how that becomes full-moon-minutes (FMM) of budget.
 *
 * Charged light (plan F3):
 *  - the lamp component, always. Without a valid reading the lamp is still
 *    shining, so it is charged as lamp_gain x commanded light, the light
 *    layer's last measured transfer (or its expected one if the sensor never
 *    answered): losing the sensor never makes light free;
 *  - plus the broadband excess (the positive part of the change over the
 *    baseline) while an F6 event is in progress and readings are valid. The
 *    0.4 s confirmation hold before an event starts is not charged, and the
 *    FSM holds an event's charge aside until the event ends (budget_fsm.h).
 *
 * 1 FMM = EDGE_FMM_SCOT_LUX of V'-proxy light for one night minute, and one
 * exhibit second is EDGE_TIME_COMPRESSION / 60 night minutes.
 */
#ifndef EDGE_DOSE_H
#define EDGE_DOSE_H

#include <stdbool.h>
#include <stdint.h>

#include "calib.h"
#include "edge_light.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    float lamp_phot_lux;   /* lamp component, photopic                  */
    float lamp_scot_lux;   /* lamp component, V'-proxy                  */
    float event_phot_lux;  /* charged broadband excess, photopic        */
    float event_scot_lux;  /* charged broadband excess, V'-proxy        */
} edge_dose_rate_t;

/* Tonight's account, in FMM. Spending past the budget is still counted: the
 * floor keeps costing the bat after the budget reaches zero. */
typedef struct {
    double spent_fmm;      /* everything charged since the night began (V'-proxy) */
    double lamp_fmm;       /* ... of which the lamp component                      */
    double event_fmm;      /* ... of which F6 events (attributed in the log)       */
    double phot_fmm;       /* photopic full-moon-minutes, for the log only         */
} edge_dose_tally_t;

/* Light being charged right now. `light` is the latest estimate; when it is
 * invalid or sensor_ok is false, only the lamp estimate (lamp_gain x
 * lamp_light) counts. */
edge_dose_rate_t edge_dose_rate(const edge_calib_t *c, const edge_light_t *light,
                                bool sensor_ok, float lamp_light);

/* V'-proxy lux for dt_ms exhibit milliseconds, in full-moon-minutes. */
double edge_dose_fmm(float scot_lux, uint32_t dt_ms);

/* Add dt_ms of the given rate to the tally. */
void edge_dose_charge(edge_dose_tally_t *t, const edge_dose_rate_t *r, uint32_t dt_ms);

/* Add one tally to another (an F6 event's held-aside dose, when it ends). */
void edge_dose_add(edge_dose_tally_t *t, const edge_dose_tally_t *more);

/* How far past zero tonight's spending has gone (0 while budget remains). */
double edge_dose_past_zero(const edge_dose_tally_t *t, double budget_fmm);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_DOSE_H */
