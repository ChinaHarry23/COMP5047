/*
 * calib.h - the two endmembers and their photopic / V'-proxy factors.
 *
 * sep2 explains each frame as "lamp" (narrowband amber) plus "broadband"
 * (torch, room LEDs). For each endmember this holds:
 *   shape         its spectral signature on the 12 channels, normalised to
 *                 sum 1, so an amplitude is the total basic counts it adds;
 *   phot_per_amp  photopic lux at the bat per unit amplitude;
 *   sp_ratio      scotopic (V') / photopic (V) of its spectrum.
 * The V'-proxy factor is phot_per_amp x sp_ratio (edge_calib_scot_per_amp).
 *
 * Every default here is a PLACEHOLDER until E0/E4: the shapes come from an
 * analytic model (sim/tools/gen_endmembers.py), the lamp's lux-per-amplitude
 * is set on the bench with the `cal` serial command, and the S/P ratios are
 * the plan's literature values.
 */
#ifndef EDGE_CALIB_H
#define EDGE_CALIB_H

#include "spectral.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    EDGE_EM_LAMP = 0, /* narrowband amber star */
    EDGE_EM_BROAD,    /* broadband white light */
    EDGE_EM_COUNT
} edge_endmember_t;

typedef struct {
    float shape[EDGE_EM_COUNT][EDGE_SPEC_NCH];
    float phot_per_amp[EDGE_EM_COUNT];
    float sp_ratio[EDGE_EM_COUNT];
} edge_calib_t;

/* Fill in the placeholder calibration. */
void edge_calib_defaults(edge_calib_t *c);

/* Set the lamp's photopic lux per unit amplitude, measured with `cal`. The
 * broadband factor is rescaled by the same ratio: the snoot, diffuser and
 * chip gain attenuate both endmembers alike, so one bench number fixes both
 * until E0 measures the torch separately. Non-positive values are ignored. */
void edge_calib_set_lamp_scale(edge_calib_t *c, float lux_per_amp);

static inline float edge_calib_scot_per_amp(const edge_calib_t *c, edge_endmember_t e)
{
    return c->phot_per_amp[e] * c->sp_ratio[e];
}

#ifdef __cplusplus
}
#endif

#endif /* EDGE_CALIB_H */
