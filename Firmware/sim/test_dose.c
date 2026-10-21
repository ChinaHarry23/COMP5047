/* test_dose.c - photopic / V'-proxy values, FMM integration, attribution. */
#include <string.h>

#include "calib.h"
#include "dose.h"
#include "edge_config.h"
#include "test_util.h"

static edge_light_t lamp_only(float amp)
{
    edge_light_t l;
    memset(&l, 0, sizeof l);
    l.valid = true;
    l.baseline_ready = true;
    l.lamp_amp = amp;
    l.lamp_measured = true;
    l.lamp_gain = amp;
    return l;
}

static void test_unit_definition(void)
{
    /* One full moon (0.2 lux x S/P 1.91) for one exhibit second = one night
     * minute = 1 FMM. */
    CHECK_NEAR(edge_dose_fmm((float)EDGE_FMM_SCOT_LUX, 1000), 1.0, 1e-6);
    CHECK_NEAR(edge_dose_fmm(1.0f, 0), 0.0, 1e-12);

    edge_dose_tally_t t;
    memset(&t, 0, sizeof t);
    edge_dose_rate_t r = { 0 };
    r.lamp_phot_lux = EDGE_FMM_PHOT_LUX; /* photopic full moon, for 1 s */
    edge_dose_charge(&t, &r, 1000);
    CHECK_NEAR(t.phot_fmm, 1.0, 1e-6);
}

static void test_per_endmember_factors(void)
{
    edge_calib_t c;
    edge_calib_defaults(&c);
    edge_light_t l = lamp_only(EDGE_LAMP_PHOT_LUX_AT_MAX / c.phot_per_amp[EDGE_EM_LAMP]);
    edge_dose_rate_t r = edge_dose_rate(&c, &l, true, 1.0f);
    CHECK_NEAR(r.lamp_phot_lux, EDGE_LAMP_PHOT_LUX_AT_MAX, 1e-5);
    CHECK_NEAR(r.lamp_scot_lux, EDGE_LAMP_PHOT_LUX_AT_MAX * EDGE_SP_AMBER, 1e-5);
    CHECK_NEAR(r.event_scot_lux, 0.0, 1e-12);

    /* Equal photopic light from the broadband endmember costs S/P-ratio more. */
    edge_light_t e = lamp_only(0.0f);
    e.event = true;
    e.broad_delta = EDGE_LAMP_PHOT_LUX_AT_MAX / c.phot_per_amp[EDGE_EM_BROAD];
    r = edge_dose_rate(&c, &e, true, 0.0f);
    CHECK_NEAR(r.event_phot_lux, EDGE_LAMP_PHOT_LUX_AT_MAX, 1e-4);
    CHECK_NEAR(r.event_scot_lux / (EDGE_LAMP_PHOT_LUX_AT_MAX * EDGE_SP_AMBER),
               EDGE_SP_BROAD / EDGE_SP_AMBER, 1e-3);
}

/* B_max is sized so that holding the lamp at maximum for EDGE_LAMP_HOLD_S
 * spends exactly a clear-night budget. */
static void test_b_max_sizing(void)
{
    edge_calib_t c;
    edge_calib_defaults(&c);
    edge_light_t l = lamp_only(EDGE_LAMP_PHOT_LUX_AT_MAX / c.phot_per_amp[EDGE_EM_LAMP]);
    edge_dose_tally_t t;
    memset(&t, 0, sizeof t);
    const int steps = (int)(EDGE_LAMP_HOLD_S * 1000.0f) / EDGE_STEP_MS;
    for (int i = 0; i < steps; i++) {
        const edge_dose_rate_t r = edge_dose_rate(&c, &l, true, 1.0f);
        edge_dose_charge(&t, &r, EDGE_STEP_MS);
    }
    CHECK_NEAR(t.spent_fmm, EDGE_B_MAX_FMM, 1e-3);
    printf("  info: B_max = %.1f FMM; lamp at max costs %.2f FMM per exhibit second\n",
           (double)EDGE_B_MAX_FMM, t.spent_fmm / EDGE_LAMP_HOLD_S);
}

static void test_event_charging_rules(void)
{
    edge_calib_t c;
    edge_calib_defaults(&c);
    edge_light_t l = lamp_only(1.0f);

    l.broad_delta = 5.0f; /* brighter than baseline but no event: not charged */
    CHECK_NEAR(edge_dose_rate(&c, &l, true, 0.5f).event_scot_lux, 0.0, 1e-12);

    l.event = true;
    CHECK(edge_dose_rate(&c, &l, true, 0.5f).event_scot_lux > 0.0f);

    l.broad_delta = -5.0f; /* darker than baseline: never a credit */
    CHECK_NEAR(edge_dose_rate(&c, &l, true, 0.5f).event_scot_lux, 0.0, 1e-12);
}

/* With the sensor gone the lamp still costs: the light layer's transfer x light. */
static void test_sensor_missing_still_charges_lamp(void)
{
    edge_calib_t c;
    edge_calib_defaults(&c);
    edge_light_t last = lamp_only(3.0f);
    last.lamp_gain = 3.0f; /* amplitude at full light */
    last.event = true;
    last.broad_delta = 10.0f;
    edge_dose_rate_t r = edge_dose_rate(&c, &last, false, 0.25f);
    CHECK_NEAR(r.lamp_phot_lux, 3.0f * 0.25f * c.phot_per_amp[EDGE_EM_LAMP], 1e-5);
    CHECK_NEAR(r.event_scot_lux, 0.0, 1e-12); /* no events without a sensor */

    /* A dropped frame before the timeout: the same estimate, no event light. */
    last.valid = false;
    r = edge_dose_rate(&c, &last, true, 0.25f);
    CHECK_NEAR(r.lamp_phot_lux, 3.0f * 0.25f * c.phot_per_amp[EDGE_EM_LAMP], 1e-5);
    CHECK_NEAR(r.event_scot_lux, 0.0, 1e-12);
}

/* An F6 event's held-aside tally is added whole when it is committed. */
static void test_tally_add(void)
{
    edge_dose_tally_t t = { 1.0, 0.75, 0.25, 2.0 };
    const edge_dose_tally_t event = { 3.0, 0.0, 3.0, 1.5 };
    edge_dose_add(&t, &event);
    CHECK_NEAR(t.spent_fmm, 4.0, 1e-12);
    CHECK_NEAR(t.lamp_fmm, 0.75, 1e-12);
    CHECK_NEAR(t.event_fmm, 3.25, 1e-12);
    CHECK_NEAR(t.phot_fmm, 3.5, 1e-12);
}

static void test_tally_and_past_zero(void)
{
    edge_dose_tally_t t;
    memset(&t, 0, sizeof t);
    edge_dose_rate_t r = { 0 };
    r.lamp_scot_lux  = (float)EDGE_FMM_SCOT_LUX;       /* 1 FMM/s */
    r.event_scot_lux = (float)(2.0 * EDGE_FMM_SCOT_LUX); /* 2 FMM/s */
    for (int i = 0; i < 10; i++) {
        edge_dose_charge(&t, &r, 100);
    }
    CHECK_NEAR(t.lamp_fmm, 1.0, 1e-5);
    CHECK_NEAR(t.event_fmm, 2.0, 1e-5);
    CHECK_NEAR(t.spent_fmm, 3.0, 1e-5);
    CHECK_NEAR(edge_dose_past_zero(&t, 5.0), 0.0, 1e-12);
    CHECK_NEAR(edge_dose_past_zero(&t, 2.5), 0.5, 1e-5);
}

int main(void)
{
    test_unit_definition();
    test_per_endmember_factors();
    test_b_max_sizing();
    test_event_charging_rules();
    test_sensor_missing_still_charges_lamp();
    test_tally_add();
    test_tally_and_past_zero();
    return test_summary("test_dose");
}
