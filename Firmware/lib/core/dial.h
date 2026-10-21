/*
 * dial.h - where the needle points (servo degrees, see edge_config.h).
 *
 * Outer arc: budget remaining on an absolute FMM scale whose full end is
 * B_max, so an overcast night visibly starts lower. Below zero the needle
 * moves into the past-zero sector and keeps counting the floor's dose.
 * Inner arc: tonight's cloud fraction C, shown for a few seconds at startup.
 */
#ifndef EDGE_DIAL_H
#define EDGE_DIAL_H

#include <stdint.h>

#include "budget_fsm.h"
#include "startup_seq.h"

#ifdef __cplusplus
extern "C" {
#endif

float edge_dial_budget_deg(double remaining_fmm);
float edge_dial_sky_deg(float cloud);

/* Self-test sweep: zero -> full -> zero over EDGE_NEEDLE_SWEEP_MS. */
float edge_dial_sweep_deg(uint32_t elapsed_ms);

/* What the needle shows now: the startup sequence (sweep, a neutral park
 * while the forecast is fetched, the sky factor), then the budget, or the
 * FAULT tick while the bat sensor is missing. */
float edge_dial_target_deg(const edge_startup_t *su, const edge_fsm_t *fsm, uint32_t now_ms);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_DIAL_H */
