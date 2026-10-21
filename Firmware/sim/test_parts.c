/* test_parts.c - presence filter, dial mapping and the startup sequence. */
#include <string.h>

#include "dial.h"
#include "edge_config.h"
#include "forecast.h"
#include "presence_filter.h"
#include "startup_seq.h"
#include "test_util.h"

static void test_presence_learning_and_hysteresis(void)
{
    edge_presence_t p;
    edge_presence_init(&p);
    for (int i = 0; i < 50; i++) {
        edge_presence_learn(&p, (i % 2) ? 40.0f : 60.0f); /* mean 50, SD 10 */
    }
    edge_presence_finish(&p, EDGE_FORCE_MIN_DELTA, EDGE_FORCE_LEARN_CEILING, EDGE_FORCE_DEFAULT_MEAN);
    CHECK(!p.learn_refused);
    const float margin = EDGE_FORCE_MIN_DELTA > 60.0f ? EDGE_FORCE_MIN_DELTA : 60.0f;
    CHECK_NEAR(p.press_threshold, 50.0 + margin, 1e-3);
    CHECK(p.release_threshold < p.press_threshold && p.release_threshold > 50.0f);

    /* Press: reported only after the debounce time. */
    uint32_t t = 0;
    CHECK(!edge_presence_update(&p, t, 1800.0f));
    t += EDGE_PRESENCE_DEBOUNCE_MS - 10;
    CHECK(!edge_presence_update(&p, t, 1800.0f));
    t += 10;
    CHECK(edge_presence_update(&p, t, 1800.0f));

    /* Sagging between the thresholds keeps the hand "on" (hysteresis). */
    const float between = (p.press_threshold + p.release_threshold) / 2.0f;
    for (int i = 0; i < 30; i++) {
        t += 10;
        CHECK(edge_presence_update(&p, t, between));
    }
    /* Chatter faster than the debounce never gets through. */
    for (int i = 0; i < 40; i++) {
        t += 40;
        CHECK(edge_presence_update(&p, t, (i % 2) ? 1800.0f : 0.0f));
    }
}

static void test_presence_refuses_hand_at_boot(void)
{
    edge_presence_t p;
    edge_presence_init(&p);
    for (int i = 0; i < 50; i++) {
        edge_presence_learn(&p, 2000.0f); /* someone was pressing during boot */
    }
    edge_presence_finish(&p, EDGE_FORCE_MIN_DELTA, EDGE_FORCE_LEARN_CEILING, EDGE_FORCE_DEFAULT_MEAN);
    CHECK(p.learn_refused);
    CHECK_NEAR(p.press_threshold, EDGE_FORCE_DEFAULT_MEAN + EDGE_FORCE_MIN_DELTA, 1e-3);
}

static void test_dial(void)
{
    CHECK_NEAR(edge_dial_budget_deg(EDGE_B_MAX_FMM), EDGE_DIAL_FULL_DEG, 1e-4);
    CHECK_NEAR(edge_dial_budget_deg(0.0), EDGE_DIAL_ZERO_DEG, 1e-4);
    CHECK_NEAR(edge_dial_budget_deg(2.0 * EDGE_B_MAX_FMM), EDGE_DIAL_FULL_DEG, 1e-4);
    CHECK_NEAR(edge_dial_budget_deg(-EDGE_DIAL_PAST_ZERO_FMM), EDGE_DIAL_PAST_ZERO_DEG, 1e-4);
    CHECK_NEAR(edge_dial_budget_deg(-10.0 * EDGE_DIAL_PAST_ZERO_FMM), EDGE_DIAL_PAST_ZERO_DEG, 1e-4);
    /* Past zero is beyond zero, on the far side from full. */
    CHECK((edge_dial_budget_deg(-1.0) - EDGE_DIAL_ZERO_DEG) * (EDGE_DIAL_FULL_DEG - EDGE_DIAL_ZERO_DEG) < 0.0f);
    /* An overcast night starts visibly lower than a clear one. */
    CHECK(edge_dial_budget_deg(edge_budget_for_cloud(1.0f)) < edge_dial_budget_deg(edge_budget_for_cloud(0.0f)) - 30.0f);
    CHECK_NEAR(edge_dial_sky_deg(0.0f), EDGE_DIAL_SKY_CLEAR_DEG, 1e-4);
    CHECK_NEAR(edge_dial_sky_deg(1.0f), EDGE_DIAL_SKY_OVERCAST_DEG, 1e-4);
}

static edge_sky_cache_t one_night(int64_t fetch_epoch, float cloud)
{
    edge_sky_cache_t c;
    memset(&c, 0, sizeof c);
    c.fetch_epoch = fetch_epoch;
    c.n = 1;
    c.night[0].sunset = 1000;
    c.night[0].midnight = 100000;
    c.night[0].cloud = cloud;
    return c;
}

static void test_startup_live_in_time(void)
{
    edge_sky_cache_t cache = one_night(900, 0.5f);
    edge_startup_env_t env = { &cache, 1000, { NULL, 0 }, { NULL, 0 } };
    edge_startup_t s;
    edge_startup_begin(&s, 0, EDGE_SEL_LIVE, true);
    CHECK(edge_startup_wants_fetch(&s));
    uint32_t t = 0;
    for (; t < EDGE_NEEDLE_SWEEP_MS; t += 50) {
        CHECK(!edge_startup_step(&s, t, &env));
    }
    edge_sky_cache_t live = one_night(1000, 0.1f);
    edge_startup_fetch_result(&s, &live);
    CHECK(!edge_startup_step(&s, t, &env));
    CHECK(s.phase == EDGE_SU_WAIT_FETCH);
    t += 50;
    CHECK(!edge_startup_step(&s, t, &env));
    CHECK(s.phase == EDGE_SU_SHOW_SKY);
    CHECK(s.sky.source == EDGE_SKY_LIVE);
    CHECK_NEAR(s.sky.cloud, 0.1, 1e-6);
    int handed = 0;
    for (uint32_t k = 0; k < EDGE_SKY_SHOW_MS + 500; k += 50) {
        handed += edge_startup_step(&s, t + 50 + k, &env) ? 1 : 0;
    }
    CHECK(handed == 1);
}

static void test_startup_cap_falls_back_to_cache(void)
{
    edge_sky_cache_t cache = one_night(900, 0.5f);
    edge_startup_env_t env = { &cache, 1000, { NULL, 0 }, { NULL, 0 } };
    edge_startup_t s;
    edge_startup_begin(&s, 0, EDGE_SEL_LIVE, false);
    uint32_t t = 0;
    for (; t < EDGE_FETCH_CAP_MS; t += 50) {
        edge_startup_step(&s, t, &env);
        CHECK(s.phase == EDGE_SU_WAIT_FETCH);
    }
    edge_startup_step(&s, t, &env);
    CHECK(s.phase == EDGE_SU_SHOW_SKY);
    CHECK(s.sky.source == EDGE_SKY_CACHED);
    edge_sky_cache_t late = one_night(1000, 0.0f);
    edge_startup_fetch_result(&s, &late); /* after the cap and clearer: tighten-only keeps the cache */
    edge_startup_step(&s, t + 50, &env);
    CHECK(s.sky.source == EDGE_SKY_CACHED);
}

/* A cloudier answer after the cap but before the hand-over tightens what is
 * handed over; after the hand-over the sequence no longer takes answers. */
static void test_startup_late_cloudier_answer_tightens(void)
{
    edge_sky_cache_t cache = one_night(900, 0.0f);
    edge_startup_env_t env = { &cache, 1000, { NULL, 0 }, { NULL, 0 } };
    edge_startup_t s;
    edge_startup_begin(&s, 0, EDGE_SEL_LIVE, false);
    uint32_t t = 0;
    for (; s.phase == EDGE_SU_WAIT_FETCH; t += 50) {
        edge_startup_step(&s, t, &env);
    }
    CHECK(s.phase == EDGE_SU_SHOW_SKY && s.sky.source == EDGE_SKY_CACHED);
    CHECK_NEAR(s.sky.budget_fmm, EDGE_B_MAX_FMM, 1e-3);

    edge_sky_cache_t late = one_night(1000, 0.9f);
    edge_startup_fetch_result(&s, &late);
    int handed = 0;
    for (uint32_t k = 0; k <= EDGE_SKY_SHOW_MS; k += 50) {
        handed += edge_startup_step(&s, t + k, &env) ? 1 : 0;
    }
    CHECK(handed == 1);
    CHECK(s.sky.source == EDGE_SKY_LIVE);
    CHECK_NEAR(s.sky.cloud, 0.9, 1e-6);
    CHECK_NEAR(s.sky.budget_fmm, edge_budget_for_cloud(0.9f), 1e-4);

    edge_sky_cache_t later = one_night(1000, 1.0f);
    edge_startup_fetch_result(&s, &later); /* DONE: the FSM's tighten handles it */
    edge_startup_step(&s, t + EDGE_SKY_SHOW_MS + 50, &env);
    CHECK(s.phase == EDGE_SU_DONE);
    CHECK_NEAR(s.sky.cloud, 0.9, 1e-6);
    CHECK_NEAR(s.live.night[0].cloud, 0.9, 1e-6);
}

static void test_startup_recorded_ignores_network(void)
{
    edge_sky_cache_t rec = one_night(0, 0.9f);
    edge_startup_env_t env = { NULL, 0, { &rec, 0 }, { &rec, 0 } };
    edge_startup_t s;
    edge_startup_begin(&s, 0, EDGE_SEL_REC_OVERCAST, false);
    CHECK(!edge_startup_wants_fetch(&s));
    edge_startup_step(&s, 0, &env);
    CHECK(s.sky.source == EDGE_SKY_RECORDED);
    CHECK_NEAR(s.sky.cloud, 0.9, 1e-6);
}

int main(void)
{
    test_presence_learning_and_hysteresis();
    test_presence_refuses_hand_at_boot();
    test_dial();
    test_startup_live_in_time();
    test_startup_cap_falls_back_to_cache();
    test_startup_late_cloudier_answer_tightens();
    test_startup_recorded_ignores_network();
    return test_summary("test_parts");
}
