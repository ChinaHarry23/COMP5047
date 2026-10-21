/* test_kit.cpp - FW0's light adapter and the shared needle/touch code on the
 * host, wired to the real core in the same order as src/kit/main.cpp's loop.
 * fake_arduino/Arduino.h stands in for the ADC, the touch pad and the LEDC.
 *
 * The bench numbers below (room level, what the LED adds, ADC noise) are
 * assumptions for the test, not measurements. */
#include <string.h>

#include "budget_fsm.h"
#include "calib.h"
#include "dial.h"
#include "edge_config.h"
#include "kit_light.h"
#include "needle_servo.h"
#include "startup_seq.h"
#include "test_util.h"
#include "touch_presence.h"

namespace {

/* ---- the bench: what the phototransistor and the foil read -------------- */

struct Bench {
    float    dark      = 600.0f; // room level at the phototransistor, counts
    float    delta     = 300.0f; // what the LED adds at full light (a plausible `cal`)
    float    noise_sd  = 8.0f;   // ADC noise per sample, counts
    bool     connected = true;   // phototransistor plugged in (10k pull-down otherwise)
    float    light     = 0.0f;   // LED light commanded right now
    bool     touching  = false;  // a hand on the foil
    uint32_t rng       = 12345u;
};
Bench g_bench;

double gaussian()
{
    /* Box-Muller on a fixed LCG: every run is identical. */
    g_bench.rng = g_bench.rng * 1664525u + 1013904223u;
    const double u1 = ((g_bench.rng >> 8) + 1.0) / 16777217.0;
    g_bench.rng = g_bench.rng * 1664525u + 1013904223u;
    const double u2 = (g_bench.rng >> 8) / 16777216.0;
    return sqrt(-2.0 * log(u1)) * cos(6.283185307179586 * u2);
}

uint16_t bench_adc(uint8_t)
{
    if (!g_bench.connected) {
        return 0;
    }
    const double v = g_bench.dark + g_bench.delta * g_bench.light + g_bench.noise_sd * gaussian();
    return static_cast<uint16_t>(v < 0.0 ? 0.0 : (v > 4095.0 ? 4095.0 : v));
}

uint32_t bench_touch(uint8_t)
{
    return static_cast<uint32_t>((g_bench.touching ? 60.0 : 100.0) + gaussian());
}

/* ---- the kit, wired as in src/kit/main.cpp ------------------------------- */

struct Kit {
    edge_calib_t   calib;
    edge_fsm_t     fsm;
    edge_startup_t su;
    KitLight       light;
    NeedleServo    needle;
    TouchPresence  touch;
    bool           use_touch = false;
    bool           hand      = false; // the button, when not using touch
    bool           present   = false;
    uint32_t       t = 0, last_step = 0, last_presence = 0;

    void boot(bool touch_input)
    {
        use_touch = touch_input;
        edge_calib_defaults(&calib);
        light.begin(39, g_bench.delta);
        calib.phot_per_amp[EDGE_EM_LAMP] = EDGE_LAMP_PHOT_LUX_AT_MAX / g_bench.delta;
        needle.begin(26, 2);
        if (use_touch) {
            touch.begin(4);
        }
        edge_fsm_init(&fsm, &calib, t);
        edge_startup_begin(&su, t, EDGE_SEL_LIVE, true);
        edge_startup_fetch_result(&su, nullptr); /* FW0: no network */
    }

    void tick()
    {
        t++;
        if (t - last_presence >= 10) {
            last_presence = t;
            present = use_touch ? touch.update(t, needle.quiet(t)) : hand;
        }
        light.sample(t);
        const edge_startup_env_t env = { nullptr, 0, { nullptr, 0 }, { nullptr, 0 } };
        if (edge_startup_step(&su, t, &env)) {
            edge_fsm_set_budget(&fsm, su.sky.budget_fmm, su.sky.cloud);
        }
        if (t - last_step >= EDGE_STEP_MS) {
            last_step = t;
            edge_fsm_input_t in;
            in.now_ms  = t;
            in.present = present;
            in.light   = light.update(t, edge_fsm_light(&fsm));
            edge_fsm_step(&fsm, &in);
            g_bench.light = edge_fsm_light(&fsm);
        }
        needle.move_to(edge_dial_target_deg(&su, &fsm, t), t);
        needle.service(t);
    }

    void run(uint32_t ms)
    {
        for (uint32_t i = 0; i < ms; i++) {
            tick();
        }
    }

    /* Run until the state is reached; false after max_ms. */
    bool run_until(edge_state_t st, uint32_t max_ms)
    {
        for (uint32_t i = 0; i < max_ms && fsm.state != st; i++) {
            tick();
        }
        return fsm.state == st;
    }
};

void reset_bench()
{
    g_bench = Bench();
    fake::analog_read = bench_adc;
    fake::touch_read  = bench_touch;
}

/* Nobody there for a minute: ADC noise must not be charged as lamp light. */
void test_idle_spends_nothing()
{
    reset_bench();
    Kit k;
    k.boot(false);
    CHECK(k.run_until(EDGE_ST_DARK_IDLE, 8000));
    k.run(1000);
    const float needle = k.needle.position();
    k.run(60000);
    CHECK(k.fsm.state == EDGE_ST_DARK_IDLE);
    CHECK(k.fsm.tally.spent_fmm == 0.0);
    CHECK(k.needle.position() == needle);
}

/* A hand on: the LED is seen, measured and charged. */
void test_lit_is_measured_and_charged()
{
    reset_bench();
    Kit k;
    k.boot(false);
    CHECK(k.run_until(EDGE_ST_DARK_IDLE, 8000));
    k.hand = true;
    k.run(2500);
    CHECK(k.fsm.state == EDGE_ST_LIT);
    CHECK(k.fsm.sensor_ok);
    /* lamp_amp = delta x light, so lux = declared lux-at-max x light */
    CHECK_NEAR(k.fsm.rate.lamp_phot_lux, EDGE_LAMP_PHOT_LUX_AT_MAX * edge_fsm_light(&k.fsm), 0.1);
    CHECK(k.fsm.tally.spent_fmm > 0.0);
}

/* Phototransistor pulled out while lit: FAULT, the floor still charged,
 * and recovery once it can see the LED again. */
void test_unplugged_while_lit()
{
    reset_bench();
    Kit k;
    k.boot(false);
    CHECK(k.run_until(EDGE_ST_DARK_IDLE, 8000));
    k.hand = true;
    k.run(3000);
    g_bench.connected = false;
    CHECK(k.run_until(EDGE_ST_FAULT, 1500));
    k.run(1000);
    const double spent = k.fsm.tally.spent_fmm;
    k.run(2000);
    CHECK_NEAR(k.fsm.level, EDGE_LEVEL_FLOOR, 1e-6);
    CHECK(k.fsm.tally.spent_fmm > spent); /* losing the sensor never makes light free */
    g_bench.connected = true;
    CHECK(k.run_until(EDGE_ST_LIT, 1500));
}

/* A room bright enough to clip the ADC hides the LED: FAULT, even with
 * nobody there, until the reading comes back down. */
void test_saturated_room()
{
    reset_bench();
    Kit k;
    k.boot(false);
    CHECK(k.run_until(EDGE_ST_DARK_IDLE, 8000));
    g_bench.dark = 4095.0f;
    CHECK(k.run_until(EDGE_ST_FAULT, 1500));
    CHECK(k.fsm.level == 0.0f);
    g_bench.dark = 600.0f;
    CHECK(k.run_until(EDGE_ST_DARK_IDLE, 1500));
    CHECK(k.fsm.tally.spent_fmm == 0.0);
}

/* Touch presence while a draining budget moves the needle: the hand leaving
 * must be seen within F1's 150 ms whatever the needle is doing. Hold lengths
 * are swept so the release falls at every phase of the needle's moves. */
void test_touch_release_while_needle_drains()
{
    uint32_t worst = 0;
    int near_move = 0, trials = 0;
    for (uint32_t hold = 1500; hold <= 6000; hold += 23) {
        reset_bench();
        Kit k;
        k.boot(true);
        if (!k.run_until(EDGE_ST_DARK_IDLE, 8000)) {
            CHECK(false);
            return;
        }
        k.run(500);
        g_bench.touching = true;
        k.run(hold);
        CHECK(k.present && k.fsm.state == EDGE_ST_LIT);
        g_bench.touching = false;
        const uint32_t off = k.t;
        bool moved_nearby = !k.needle.quiet(k.t); /* a move just before, or during, the release */
        while (k.present && k.t - off < 1000) {
            k.tick();
            moved_nearby = moved_nearby || !k.needle.quiet(k.t);
        }
        const uint32_t latency = k.t - off;
        worst = latency > worst ? latency : worst;
        near_move += moved_nearby ? 1 : 0;
        trials++;
    }
    printf("  info: touch release over %d holds: worst %u ms; %d released next to a needle move\n",
           trials, worst, near_move);
    CHECK(worst <= 150);
    CHECK(near_move > trials / 4); /* the needle really was moving */
}

} // namespace

int main()
{
    test_idle_spends_nothing();
    test_lit_is_measured_and_charged();
    test_unplugged_while_lit();
    test_saturated_room();
    test_touch_release_while_needle_drains();
    return test_summary("test_kit");
}
