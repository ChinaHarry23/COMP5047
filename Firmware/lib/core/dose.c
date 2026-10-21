#include "dose.h"

#include "edge_config.h"

edge_dose_rate_t edge_dose_rate(const edge_calib_t *c, const edge_light_t *light,
                                bool sensor_ok, float lamp_light)
{
    edge_dose_rate_t r = { 0 };

    float lamp_amp = sensor_ok && light->valid ? light->lamp_amp
                                               : light->lamp_gain * lamp_light;
    if (lamp_amp < 0.0f) {
        lamp_amp = 0.0f;
    }
    r.lamp_phot_lux = lamp_amp * c->phot_per_amp[EDGE_EM_LAMP];
    r.lamp_scot_lux = lamp_amp * edge_calib_scot_per_amp(c, EDGE_EM_LAMP);

    if (sensor_ok && light->valid && light->event && light->broad_delta > 0.0f) {
        r.event_phot_lux = light->broad_delta * c->phot_per_amp[EDGE_EM_BROAD];
        r.event_scot_lux = light->broad_delta * edge_calib_scot_per_amp(c, EDGE_EM_BROAD);
    }
    return r;
}

static double night_minutes(uint32_t dt_ms)
{
    return (double)dt_ms / 1000.0 * EDGE_TIME_COMPRESSION / 60.0;
}

double edge_dose_fmm(float scot_lux, uint32_t dt_ms)
{
    return (double)scot_lux * night_minutes(dt_ms) / EDGE_FMM_SCOT_LUX;
}

void edge_dose_charge(edge_dose_tally_t *t, const edge_dose_rate_t *r, uint32_t dt_ms)
{
    const double lamp  = edge_dose_fmm(r->lamp_scot_lux, dt_ms);
    const double event = edge_dose_fmm(r->event_scot_lux, dt_ms);
    t->lamp_fmm  += lamp;
    t->event_fmm += event;
    t->spent_fmm += lamp + event;
    /* Against the photopic full moon, so it compares with a lux meter. */
    t->phot_fmm += (double)(r->lamp_phot_lux + r->event_phot_lux) * night_minutes(dt_ms)
                   / EDGE_FMM_PHOT_LUX;
}

void edge_dose_add(edge_dose_tally_t *t, const edge_dose_tally_t *more)
{
    t->spent_fmm += more->spent_fmm;
    t->lamp_fmm  += more->lamp_fmm;
    t->event_fmm += more->event_fmm;
    t->phot_fmm  += more->phot_fmm;
}

double edge_dose_past_zero(const edge_dose_tally_t *t, double budget_fmm)
{
    const double over = t->spent_fmm - budget_fmm;
    return over > 0.0 ? over : 0.0;
}
