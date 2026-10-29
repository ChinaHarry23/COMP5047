/*
 * edge_sim.c - run the real policy core against scripted scenarios.
 *
 * The "app" half mirrors src/exhibit/main.cpp: presence through the real
 * debouncer every 10 ms, a spectral frame every 200 ms through the real sep2,
 * the startup sequence with a modelled network, and one FSM step every 50 ms.
 * The "plant" half stands in for the hood: it turns the lamp's commanded
 * light, the room light and a torch into spectral frames, and a hand into
 * force-sensor readings. Invariants I1-I9 (plan 7.4) are checked on every
 * control step; scenarios add their own `expect` checks.
 *
 * The device clock follows the hardware: unknown at every power-up (no RTC),
 * set only by a server's answer (its Date header) or by SNTP while Wi-Fi is
 * up during a fetch, and kept across new nights. Scenarios cannot simply
 * declare it known.
 *
 * Usage: edge_sim [-v] scenario.scn ...   (run from sim/, fixtures are relative)
 * Scenario language: see sim/README section in firmware/README.md.
 */
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "budget_fsm.h"
#include "calib.h"
#include "dial.h"
#include "edge_config.h"
#include "forecast.h"
#include "plant_shapes.h"
#include "presence_filter.h"
#include "sep2.h"
#include "startup_seq.h"

#define TICK_MS          10
#define FIXTURE_PATH     "fixtures/openmeteo_aifs_20261005.json"
#define ERROR_FIXTURE    "fixtures/openmeteo_error_synthetic.json"
#define OVERCAST_FIXTURE "fixtures/openmeteo_overcast_synthetic.json"
#define FIXTURE_EPOCH    1791187301LL /* capture time of the fixture (HTTP Date) */
#define JOIN_MS          1000         /* a fetch's Wi-Fi join                         */
#define ANSWER_MS        1500         /* the server's answer, Date header included   */
#define LATE_ANSWER_MS   6000         /* `net late`: past the 5 s cap, inside SHOW_SKY */
#define FRAME_WINDOW_MS  180          /* integration window inside each 200 ms frame */
#define HIST_TICKS       64
#define MAX_REPORTS      12

/* ---- plant: the hood, the room, the network ---------------------------- */

typedef enum { NET_OK, NET_DOWN, NET_API_DOWN, NET_TRUNCATED, NET_ERROR, NET_LATE } net_mode_t;
typedef enum { CACHE_NONE, CACHE_FRESH, CACHE_STALE, CACHE_UNKNOWN_AGE } cache_mode_t;
typedef enum { ROOM_WHITE, ROOM_WARM, ROOM_FLUOR } room_shape_t;

typedef struct {
    bool            hand;            /* a hand on the force sensor          */
    float           lamp_lux_at_max; /* photopic lux at the bat, lamp at max */
    float           room_lux;
    room_shape_t    room_shape;
    float           wobble;          /* people moving: room light +/- this fraction */
    float           torch_lux;       /* extra cool-white light at the bat   */
    bool            sensor;          /* bat sensor connected                */
    net_mode_t      net;
    uint32_t        sntp_ms;         /* SNTP's first answer after Wi-Fi joins */
    cache_mode_t    cache;
    edge_selector_t selector;
    float           hist_light[HIST_TICKS], hist_room[HIST_TICKS], hist_torch[HIST_TICKS];
    int             hist_head;
    uint32_t        rng;
} plant_t;

/* ---- app: the same wiring as the exhibit firmware ----------------------- */

typedef struct {
    edge_calib_t     calib;
    edge_sep2_t      sep;
    edge_fsm_t       fsm;
    edge_presence_t  pres;
    edge_startup_t   su;
    bool             present;
    bool             have_cache;
    edge_sky_cache_t cache;     /* what NVS holds                      */
    edge_sky_cache_t recorded;  /* the captured response, for REC-*    */
    bool             fetch_pending;
    uint32_t         fetch_answer_ms;
    bool             clock_due;       /* this fetch will set the clock ...   */
    uint32_t         clock_due_ms;    /* ... at this time                    */
    bool             clock_known;     /* the device's clock (no RTC)         */
    bool             booted;
} app_t;

/* ---- invariant bookkeeping ---------------------------------------------- */

typedef struct {
    float    prev_level;
    double   prev_remaining;
    bool     remaining_reset; /* a new night or a budget hand-over this step */
    bool     absent;
    uint32_t absent_since;
    edge_state_t prev_state;
    uint32_t state_since;
    bool     last_frame_valid;
    bool     sensor_back;     /* a valid frame arrived after an outage   */
    uint32_t sensor_back_ms;
    /* I8: the FSM's response to a re-baseline, checked on the next step. */
    bool        rebase_armed;
    const char *rebase_what;
    float       rebase_budget;
    bool        rebase_budget_set;
    double      rebase_spent;
    double      rebase_event_fmm;
} watch_t;

static plant_t  P;
static app_t    A;
static watch_t  W;
static uint32_t g_t;          /* simulated milliseconds */
static int      g_fail;       /* failures in the current scenario */
static int      g_reports;
static int      g_expects;
static bool     g_verbose;
static const char *g_file;
static int      g_line;
static char     g_body[8192];      /* the captured response            */
static size_t   g_body_len;
static char     g_error_body[512];  /* Open-Meteo's error object        */
static size_t   g_error_len;
static char     g_overcast_body[8192]; /* captured response, every sample 100% */
static size_t   g_overcast_len;
static double   g_remembered[32];
static char     g_remembered_name[32][16];
static int      g_n_remembered;

static void fail(const char *fmt, ...)
{
    g_fail++;
    if (g_reports++ >= MAX_REPORTS) {
        return;
    }
    va_list ap;
    va_start(ap, fmt);
    printf("    t=%7.2fs %s:%d: ", g_t / 1000.0, g_file, g_line);
    vprintf(fmt, ap);
    printf("\n");
    va_end(ap);
}

/* ---- plant model -------------------------------------------------------- */

static double gaussian(void)
{
    /* Box-Muller on a fixed LCG: every run is identical. */
    P.rng = P.rng * 1664525u + 1013904223u;
    const double u1 = ((P.rng >> 8) + 1.0) / 16777217.0;
    P.rng = P.rng * 1664525u + 1013904223u;
    const double u2 = (P.rng >> 8) / 16777216.0;
    return sqrt(-2.0 * log(u1)) * cos(6.283185307179586 * u2);
}

static float force_reading(void)
{
    return (float)(P.hand ? 1800.0 + 30.0 * gaussian() : 30.0 + 5.0 * gaussian());
}

static void plant_record_tick(float light)
{
    const float wob = 1.0f + P.wobble * sinf(6.2831853f * (float)g_t / 3000.0f);
    P.hist_head = (P.hist_head + 1) % HIST_TICKS;
    P.hist_light[P.hist_head] = light;
    P.hist_room[P.hist_head]  = P.room_lux * wob;
    P.hist_torch[P.hist_head] = P.torch_lux;
}

static edge_spectral_frame_t plant_frame(void)
{
    edge_spectral_frame_t f;
    memset(&f, 0, sizeof f);
    f.t_end_ms   = g_t;
    f.t_start_ms = g_t - FRAME_WINDOW_MS;
    if (!P.sensor) {
        return f; /* valid = false: the driver reports no sensor */
    }
    const int n = FRAME_WINDOW_MS / TICK_MS;
    float light = 0, room = 0, torch = 0;
    for (int k = 0; k < n; k++) {
        const int i = (P.hist_head - k + HIST_TICKS) % HIST_TICKS;
        light += P.hist_light[i];
        room  += P.hist_room[i];
        torch += P.hist_torch[i];
    }
    light /= (float)n;
    room  /= (float)n;
    torch /= (float)n;

    const edge_calib_t *c = &A.calib;
    const float *room_shape = c->shape[EDGE_EM_BROAD];
    float room_lux_per_amp = c->phot_per_amp[EDGE_EM_BROAD];
    if (P.room_shape == ROOM_WARM) {
        room_shape = k_shape_warm_white;
        room_lux_per_amp = WARM_WHITE_LUX_PER_AMP * c->phot_per_amp[EDGE_EM_LAMP];
    } else if (P.room_shape == ROOM_FLUOR) {
        room_shape = k_shape_triphosphor;
        room_lux_per_amp = TRIPHOSPHOR_LUX_PER_AMP * c->phot_per_amp[EDGE_EM_LAMP];
    }
    const float lamp_amp  = P.lamp_lux_at_max * light / c->phot_per_amp[EDGE_EM_LAMP];
    const float room_amp  = room / room_lux_per_amp;
    const float torch_amp = torch / c->phot_per_amp[EDGE_EM_BROAD];
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        const float y = lamp_amp * c->shape[EDGE_EM_LAMP][i] + room_amp * room_shape[i] +
                        torch_amp * c->shape[EDGE_EM_BROAD][i];
        f.ch[i] = y + (float)((0.003 * fabs(y) + 0.0005) * gaussian());
    }
    f.valid = true;
    return f;
}

/* ---- network model ------------------------------------------------------ */

static int64_t now_epoch(void)
{
    return A.clock_known ? FIXTURE_EPOCH + (int64_t)(g_t / 1000u) : 0;
}

static size_t load(const char *path, char *buf, size_t cap)
{
    FILE *fp = fopen(path, "rb");
    if (fp == NULL) {
        fprintf(stderr, "edge_sim: cannot open %s (run from sim/)\n", path);
        exit(2);
    }
    const size_t n = fread(buf, 1, cap - 1, fp);
    fclose(fp);
    buf[n] = '\0';
    return n;
}

static uint32_t min_u32(uint32_t a, uint32_t b)
{
    return a < b ? a : b;
}

static uint32_t max_u32(uint32_t a, uint32_t b)
{
    return a > b ? a : b;
}

/* When the exhibit's fetch task (src/exhibit/net.cpp) would post its result,
 * and whether and when it sets the clock on the way. It gives up at the same
 * deadline as the startup cap. */
static void start_fetch(void)
{
    const uint32_t deadline = g_t + EDGE_FETCH_CAP_MS;
    const uint32_t sntp_at  = g_t + JOIN_MS + P.sntp_ms;
    A.fetch_pending = true;
    A.clock_due     = true;
    switch (P.net) {
    case NET_DOWN: /* the hotspot never answers: the join times out, no clock */
        A.fetch_answer_ms = deadline;
        A.clock_due       = false;
        break;
    case NET_API_DOWN: /* joined; the request fails, then the task waits for SNTP until the cap */
        A.clock_due       = sntp_at <= deadline;
        A.clock_due_ms    = sntp_at;
        A.fetch_answer_ms = A.clock_due ? max_u32(g_t + ANSWER_MS, sntp_at) : deadline;
        break;
    case NET_OK:
    case NET_TRUNCATED:
    case NET_ERROR: /* any answer carries a Date header */
        A.fetch_answer_ms = g_t + ANSWER_MS;
        A.clock_due_ms    = min_u32(A.fetch_answer_ms, sntp_at);
        break;
    case NET_LATE:
        A.fetch_answer_ms = g_t + LATE_ANSWER_MS;
        A.clock_due_ms    = min_u32(A.fetch_answer_ms, sntp_at);
        break;
    }
}

static void check_sky(const edge_sky_t *s, bool live_ok);

static bool parse_body(const char *body, size_t len, edge_sky_cache_t *out)
{
    return edge_sky_cache_from_body(body, len, now_epoch(), out) == EDGE_FC_OK && out->n > 0;
}

/* What the fetch task hands back, then what on_fetch() in
 * src/exhibit/main.cpp does with it. */
static void deliver_fetch(void)
{
    A.fetch_pending = false;
    edge_sky_cache_t live;
    bool ok = false;
    switch (P.net) {
    case NET_OK:        ok = parse_body(g_body, g_body_len, &live); break;
    case NET_ERROR:     ok = parse_body(g_error_body, g_error_len, &live); break;
    case NET_LATE:      ok = parse_body(g_overcast_body, g_overcast_len, &live); break;
    case NET_TRUNCATED:
        ok = parse_body(g_body, g_body_len * 6 / 10, &live);
        if (ok) {
            fail("a truncated body was accepted");
        }
        break;
    case NET_DOWN:
    case NET_API_DOWN:
        break;
    }
    if (ok && edge_sky_cache_replaces(&live, A.have_cache ? &A.cache : NULL)) {
        A.cache      = live; /* NVS and RAM follow the same rule */
        A.have_cache = true;
    }
    if (A.su.phase != EDGE_SU_DONE) {
        edge_startup_fetch_result(&A.su, ok ? &live : NULL);
    } else if (ok) {
        /* After the hand-over a newer forecast can only tighten tonight's budget. */
        const edge_sky_t s = edge_sky_live(&live, now_epoch());
        check_sky(&s, true);
        if (s.source == EDGE_SKY_LIVE) {
            edge_fsm_tighten_budget(&A.fsm, s.budget_fmm, s.cloud);
        }
    }
}

/* ---- app ---------------------------------------------------------------- */

static void begin_startup(bool sweep)
{
    edge_startup_begin(&A.su, g_t, P.selector, sweep);
    if (edge_startup_wants_fetch(&A.su)) {
        start_fetch();
    }
}

static void app_boot(void)
{
    memset(&A, 0, sizeof A);
    edge_calib_defaults(&A.calib);
    edge_sep2_init(&A.sep, &A.calib, EDGE_LAMP_PHOT_LUX_AT_MAX / A.calib.phot_per_amp[EDGE_EM_LAMP]);
    edge_fsm_init(&A.fsm, &A.calib, g_t);

    edge_presence_init(&A.pres);
    for (int i = 0; i < EDGE_PRESENCE_LEARN_MS / TICK_MS; i++) {
        edge_presence_learn(&A.pres, force_reading());
    }
    edge_presence_finish(&A.pres, EDGE_FORCE_MIN_DELTA, EDGE_FORCE_LEARN_CEILING, EDGE_FORCE_DEFAULT_MEAN);

    if (edge_sky_cache_from_body(g_body, g_body_len, FIXTURE_EPOCH, &A.recorded) != EDGE_FC_OK) {
        fprintf(stderr, "edge_sim: fixture does not parse\n");
        exit(2);
    }
    if (P.cache != CACHE_NONE) {
        A.cache = A.recorded;
        A.cache.fetch_epoch = P.cache == CACHE_FRESH ? FIXTURE_EPOCH - 2 * 3600
                            : P.cache == CACHE_STALE ? FIXTURE_EPOCH - 30 * 3600
                                                     : 0;
        A.have_cache = true;
    }
    memset(&W, 0, sizeof W);
    W.remaining_reset = true;
    W.prev_state = A.fsm.state;
    W.state_since = g_t;
    W.last_frame_valid = true;
    A.booted = true;
    begin_startup(true);
}

/* I8 looks at the FSM's response to a re-baseline, on the step after it:
 * B unchanged, nothing charged for the event the re-baseline absorbed, and
 * spending grown by no more than the lamp's own light over that step. */
static void arm_rebaseline_check(const char *what)
{
    W.rebase_armed      = true;
    W.rebase_what       = what;
    W.rebase_budget     = A.fsm.budget_fmm;
    W.rebase_budget_set = A.fsm.budget_set;
    W.rebase_spent      = A.fsm.tally.spent_fmm;
    W.rebase_event_fmm  = A.fsm.tally.event_fmm;
}

static void check_rebaseline_response(void)
{
    const edge_fsm_t *f = &A.fsm;
    const double lamp_step = edge_dose_fmm(f->rate.lamp_scot_lux, EDGE_STEP_MS);
    if (f->budget_fmm != W.rebase_budget || f->budget_set != W.rebase_budget_set) {
        fail("I8: %s changed B (%.3f -> %.3f)", W.rebase_what, (double)W.rebase_budget, (double)f->budget_fmm);
    }
    if (f->tally.event_fmm != W.rebase_event_fmm) {
        fail("I8: %s charged %.3f FMM of event light", W.rebase_what, f->tally.event_fmm - W.rebase_event_fmm);
    }
    if (f->tally.spent_fmm - W.rebase_spent > lamp_step + 1e-9) {
        fail("I8: %s spent %.4f FMM in one step (lamp alone: %.4f)", W.rebase_what,
             f->tally.spent_fmm - W.rebase_spent, lamp_step);
    }
    W.rebase_armed = false;
}

static void press(const char *button)
{
    if (strcmp(button, "rebase") == 0) {
        edge_sep2_rebaseline(&A.sep);
        arm_rebaseline_check("the re-baseline button");
    } else if (strcmp(button, "night") == 0) {
        edge_fsm_new_night(&A.fsm, g_t);
        edge_sep2_rebaseline(&A.sep);
        begin_startup(false);
        W.remaining_reset = true;
    } else {
        fail("unknown button '%s'", button);
    }
}

static void check_sky(const edge_sky_t *s, bool live_ok)
{
    if (!(s->cloud >= 0.0f && s->cloud <= 1.0f)) {
        fail("I6: sky factor %.3f outside [0, 1]", (double)s->cloud);
    }
    if (s->source == EDGE_SKY_PROTECTIVE && s->cloud != 1.0f) {
        fail("I6: protective sky with C = %.3f", (double)s->cloud);
    }
    if (s->source == EDGE_SKY_CACHED && (s->age_s < 0 || s->age_s > EDGE_FORECAST_STALE_S)) {
        fail("I6: cache of age %lld s used", (long long)s->age_s);
    }
    /* Independent oracle for the cache rule: known age, at most 24 h. */
    const int64_t now = now_epoch();
    const bool cache_unusable = !A.have_cache || A.cache.fetch_epoch == 0 || now == 0 ||
                                now - A.cache.fetch_epoch > EDGE_FORECAST_STALE_S;
    if (P.selector == EDGE_SEL_LIVE && !live_ok && cache_unusable && s->cloud != 1.0f) {
        fail("I6: stale, missing or unknown-age data gave C = %.3f", (double)s->cloud);
    }
}

static void check_invariants(void)
{
    const edge_fsm_t *f = &A.fsm;
    const float lvl = f->level;
    const float floor = EDGE_LEVEL_FLOOR;
    const bool present = A.present;

    if (f->state != W.prev_state) {
        W.prev_state = f->state;
        W.state_since = g_t;
    }
    const uint32_t in_state = g_t - W.state_since;

    if (present && W.prev_level >= floor && lvl < floor) { /* I1 */
        fail("I1: lamp crossed below the floor (%.3f -> %.3f) with someone present",
             (double)W.prev_level, (double)lvl);
    }
    if (!present) { /* I2 */
        if (!W.absent) {
            W.absent = true;
            W.absent_since = g_t;
        }
        if (g_t - W.absent_since > EDGE_FADE_DOWN_MS + EDGE_STEP_MS && lvl > 0.0f) {
            fail("I2: lamp at %.3f on an empty path %.1f s after the hand left", (double)lvl,
                 (g_t - W.absent_since) / 1000.0);
        }
    } else {
        W.absent = false;
    }
    const double rem = edge_fsm_remaining(f); /* I3 */
    if (!W.remaining_reset && rem > W.prev_remaining + 1e-9) {
        fail("I3: budget increased within a night (%.4f -> %.4f)", W.prev_remaining, rem);
    }
    W.prev_remaining = rem;
    W.remaining_reset = false;
    if ((f->state == EDGE_ST_YIELD || f->state == EDGE_ST_FAULT) && /* I4, I5 */
        in_state >= EDGE_DIM_TO_FLOOR_MS + EDGE_STEP_MS && lvl > floor + 1e-6f) {
        fail("I%d: lamp %.3f above the floor in %s", f->state == EDGE_ST_YIELD ? 4 : 5,
             (double)lvl, edge_fsm_state_name(f->state));
    }
    if (W.sensor_back && g_t >= W.sensor_back_ms + EDGE_FAULT_CLEAR_MS + 2 * EDGE_STEP_MS) {
        if (f->state == EDGE_ST_FAULT) { /* I5 */
            fail("I5: FAULT still set %.2f s after the sensor returned",
                 (g_t - W.sensor_back_ms) / 1000.0);
        }
        W.sensor_back = false;
    }
    /* I7, per state: up never faster than the 1.5 s fade; down never faster
     * than the 5 s fade, except YIELD and FAULT, which dim to the floor at
     * EDGE_DIM_TO_FLOOR_MS. The state after the step chose the step's ramp. */
    const bool  fast_dim = f->state == EDGE_ST_YIELD || f->state == EDGE_ST_FAULT;
    const float up_max   = (float)EDGE_STEP_MS / EDGE_FADE_UP_MS;
    const float down_max = (float)EDGE_STEP_MS / (fast_dim ? EDGE_DIM_TO_FLOOR_MS : EDGE_FADE_DOWN_MS);
    if (lvl - W.prev_level > up_max + 1e-5f || W.prev_level - lvl > down_max + 1e-5f) {
        fail("I7: %s step %.3f -> %.3f faster than its ramp", edge_fsm_state_name(f->state),
             (double)W.prev_level, (double)lvl);
    }
    if (f->state == EDGE_ST_YIELD && /* I9 */
        in_state > EDGE_SCENE_CHANGE_MS + EDGE_FRAME_PERIOD_MS + EDGE_STEP_MS) {
        fail("I9: YIELD has lasted %.1f s without a re-baseline", in_state / 1000.0);
    }
    W.prev_level = lvl;
}

static void trace(void)
{
    const edge_fsm_t *f = &A.fsm;
    const edge_light_t *l = edge_sep2_light(&A.sep);
    printf("      %7.2fs %-9s hand=%d pres=%d lvl=%.3f lamp=%.2flx dw=%+.2f ev=%d rem=%7.2f/%5.1f needle=%5.1f\n",
           g_t / 1000.0, edge_fsm_state_name(f->state), P.hand, A.present, (double)f->level,
           (double)(l->lamp_amp * A.calib.phot_per_amp[EDGE_EM_LAMP]),
           (double)(l->broad_delta * A.calib.phot_per_amp[EDGE_EM_BROAD]), l->event,
           edge_fsm_remaining(f), (double)f->budget_fmm,
           (double)edge_dial_target_deg(&A.su, f, g_t));
}

/* One 10 ms tick of the exhibit's main loop. */
static void tick(void)
{
    g_t += TICK_MS;
    A.present = edge_presence_update(&A.pres, g_t, force_reading());

    if (A.fetch_pending && A.clock_due && g_t >= A.clock_due_ms) {
        A.clock_known = true; /* SNTP, or the answer's Date header */
    }
    if (A.fetch_pending && g_t >= A.fetch_answer_ms) {
        deliver_fetch();
    }
    const edge_startup_env_t env = {
        A.have_cache ? &A.cache : NULL, now_epoch(), { &A.recorded, 1 }, { &A.recorded, 0 },
    };
    if (edge_startup_step(&A.su, g_t, &env)) {
        check_sky(&A.su.sky, A.su.fetch_ok);
        edge_fsm_set_budget(&A.fsm, A.su.sky.budget_fmm, A.su.sky.cloud);
        W.remaining_reset = true;
    }

    if (g_t % EDGE_FRAME_PERIOD_MS == 0) {
        const edge_spectral_frame_t fr = plant_frame();
        if (fr.valid && !W.last_frame_valid) {
            W.sensor_back = true;
            W.sensor_back_ms = g_t;
        }
        W.last_frame_valid = fr.valid;
        const unsigned scenes = A.sep.scene_changes;
        edge_sep2_update(&A.sep, &fr);
        if (A.sep.scene_changes != scenes) {
            arm_rebaseline_check("a scene-change re-baseline");
        }
    }

    if (g_t % EDGE_STEP_MS == 0) {
        const edge_fsm_input_t in = { g_t, A.present, *edge_sep2_light(&A.sep) };
        edge_fsm_step(&A.fsm, &in);
        edge_sep2_note_light(&A.sep, g_t, edge_fsm_light(&A.fsm));
        check_invariants();
        if (W.rebase_armed) {
            check_rebaseline_response();
        }
    }
    plant_record_tick(edge_fsm_light(&A.fsm));

    if (g_verbose && g_t % 1000 == 0) {
        trace();
    }
}

static void advance(double seconds)
{
    const uint32_t end = g_t + (uint32_t)lround(seconds * 1000.0);
    while (g_t < end) {
        tick();
    }
}

/* ---- scenario language -------------------------------------------------- */

typedef enum { V_NUM, V_STATE, V_SOURCE, V_WHY } vkind_t;

static bool lookup_var(const char *name, double *out, vkind_t *kind)
{
    const edge_fsm_t *f = &A.fsm;
    const edge_light_t *l = edge_sep2_light(&A.sep);
    *kind = V_NUM;
    if (strcmp(name, "state") == 0)              { *out = f->state; *kind = V_STATE; }
    else if (strcmp(name, "source") == 0)        { *out = A.su.sky.source; *kind = V_SOURCE; }
    else if (strcmp(name, "why") == 0)           { *out = A.su.sky.why; *kind = V_WHY; }
    else if (strcmp(name, "level") == 0)         *out = f->level;
    else if (strcmp(name, "present") == 0)       *out = A.present;
    else if (strcmp(name, "remaining") == 0)     *out = edge_fsm_remaining(f);
    else if (strcmp(name, "spent") == 0)         *out = f->tally.spent_fmm;
    else if (strcmp(name, "event_fmm") == 0)     *out = f->tally.event_fmm;
    else if (strcmp(name, "lamp_fmm") == 0)      *out = f->tally.lamp_fmm;
    else if (strcmp(name, "pending") == 0)       *out = f->event_pending.spent_fmm;
    else if (strcmp(name, "clock") == 0)         *out = A.clock_known;
    else if (strcmp(name, "budget") == 0)        *out = f->budget_fmm;
    else if (strcmp(name, "budget_set") == 0)    *out = f->budget_set;
    else if (strcmp(name, "sky_c") == 0)         *out = f->sky_cloud;
    else if (strcmp(name, "event") == 0)         *out = l->event;
    else if (strcmp(name, "lamp_lux") == 0)      *out = l->lamp_amp * A.calib.phot_per_amp[EDGE_EM_LAMP];
    else if (strcmp(name, "events") == 0)        *out = A.sep.events;
    else if (strcmp(name, "scene_changes") == 0) *out = A.sep.scene_changes;
    else if (strcmp(name, "needle") == 0)        *out = edge_dial_target_deg(&A.su, f, g_t);
    else return false;
    return true;
}

static bool parse_value(const char *tok, vkind_t kind, double *out)
{
    if (tok[0] == '@') {
        for (int i = 0; i < g_n_remembered; i++) {
            if (strcmp(g_remembered_name[i], tok + 1) == 0) {
                *out = g_remembered[i];
                return true;
            }
        }
        return false;
    }
    if (kind == V_STATE) {
        for (int s = 0; s < EDGE_ST_COUNT; s++) {
            if (strcmp(tok, edge_fsm_state_name((edge_state_t)s)) == 0) {
                *out = s;
                return true;
            }
        }
        return false;
    }
    if (kind == V_SOURCE) {
        for (int s = EDGE_SKY_LIVE; s <= EDGE_SKY_PROTECTIVE; s++) {
            if (strcmp(tok, edge_sky_source_name((edge_sky_source_t)s)) == 0) {
                *out = s;
                return true;
            }
        }
        return false;
    }
    if (kind == V_WHY) {
        for (int w = EDGE_SKY_WHY_OK; w <= EDGE_SKY_WHY_NO_TONIGHT; w++) {
            if (strcmp(tok, edge_sky_why_name((edge_sky_why_t)w)) == 0) {
                *out = w;
                return true;
            }
        }
        return false;
    }
    if (strcmp(tok, "floor") == 0) { *out = EDGE_LEVEL_FLOOR; return true; }
    if (strcmp(tok, "max") == 0)   { *out = EDGE_LEVEL_MAX; return true; }
    if (strcmp(tok, "bmax") == 0)  { *out = EDGE_B_MAX_FMM; return true; }
    char *end;
    *out = strtod(tok, &end);
    return *end == '\0';
}

static bool compare(double a, const char *op, double b)
{
    const double eps = 1e-4 * (1.0 + fabs(b));
    if (strcmp(op, "==") == 0) return fabs(a - b) <= eps;
    if (strcmp(op, "!=") == 0) return fabs(a - b) > eps;
    if (strcmp(op, "<") == 0)  return a < b;
    if (strcmp(op, "<=") == 0) return a <= b + eps;
    if (strcmp(op, ">") == 0)  return a > b;
    if (strcmp(op, ">=") == 0) return a >= b - eps;
    return false;
}

/* Evaluate "<var> <op> <value>"; sets *ok false on a syntax error. */
static bool condition(char **tok, int n, bool *ok, double *got)
{
    double want;
    vkind_t kind;
    *ok = n == 3 && lookup_var(tok[0], got, &kind) && parse_value(tok[2], kind, &want);
    if (!*ok) {
        return false;
    }
    return compare(*got, tok[1], want);
}

static void describe(char **tok, double got)
{
    double v;
    vkind_t kind;
    lookup_var(tok[0], &v, &kind);
    if (kind == V_STATE) {
        fail("expected %s %s %s, got %s", tok[0], tok[1], tok[2], edge_fsm_state_name((edge_state_t)got));
    } else if (kind == V_SOURCE) {
        fail("expected %s %s %s, got %s", tok[0], tok[1], tok[2], edge_sky_source_name((edge_sky_source_t)got));
    } else if (kind == V_WHY) {
        fail("expected %s %s %s, got %s", tok[0], tok[1], tok[2], edge_sky_why_name((edge_sky_why_t)got));
    } else {
        fail("expected %s %s %s, got %.4f", tok[0], tok[1], tok[2], got);
    }
}

static bool parse_bool(const char *s)
{
    return strcmp(s, "1") == 0 || strcmp(s, "on") == 0;
}

static bool run_command(char **tok, int n)
{
    const char *cmd = tok[0];
    bool ok = true;
    if (strcmp(cmd, "boot") == 0) {
        app_boot();
    } else if (strcmp(cmd, "hand") == 0 && n == 2) {
        P.hand = parse_bool(tok[1]);
    } else if (strcmp(cmd, "chatter") == 0 && n == 3) {
        const uint32_t end = g_t + (uint32_t)lround(atof(tok[1]) * 1000.0);
        const uint32_t half = (uint32_t)atoi(tok[2]) / 2u;
        uint32_t next = g_t + half;
        while (g_t < end) {
            if (g_t >= next) {
                P.hand = !P.hand;
                next += half;
            }
            tick();
        }
    } else if (strcmp(cmd, "lamp_lux") == 0 && n == 2) {
        P.lamp_lux_at_max = (float)atof(tok[1]);
    } else if (strcmp(cmd, "room") == 0 && (n == 2 || n == 3)) {
        P.room_lux = (float)atof(tok[1]);
        if (n == 3) {
            P.room_shape = strcmp(tok[2], "warm") == 0 ? ROOM_WARM
                         : strcmp(tok[2], "fluor") == 0 ? ROOM_FLUOR : ROOM_WHITE;
        }
    } else if (strcmp(cmd, "wobble") == 0 && n == 2) {
        P.wobble = (float)atof(tok[1]);
    } else if (strcmp(cmd, "torch") == 0 && n == 2) {
        P.torch_lux = (float)atof(tok[1]);
    } else if (strcmp(cmd, "sensor") == 0 && n == 2) {
        P.sensor = parse_bool(tok[1]);
    } else if (strcmp(cmd, "net") == 0 && n == 2) {
        static const char *const modes[] = { "ok", "down", "api_down", "truncated", "error", "late" };
        ok = false;
        for (int m = 0; m < (int)(sizeof modes / sizeof modes[0]); m++) {
            if (strcmp(tok[1], modes[m]) == 0) {
                P.net = (net_mode_t)m;
                ok = true;
            }
        }
    } else if (strcmp(cmd, "sntp") == 0 && n == 2) {
        P.sntp_ms = (uint32_t)lround(atof(tok[1]) * 1000.0);
    } else if (strcmp(cmd, "cache") == 0 && n == 2) {
        P.cache = strcmp(tok[1], "fresh") == 0 ? CACHE_FRESH : strcmp(tok[1], "stale") == 0 ? CACHE_STALE
                : strcmp(tok[1], "unknown_age") == 0 ? CACHE_UNKNOWN_AGE : CACHE_NONE;
    } else if (strcmp(cmd, "selector") == 0 && n == 2) {
        P.selector = strcmp(tok[1], "clear") == 0 ? EDGE_SEL_REC_CLEAR
                   : strcmp(tok[1], "overcast") == 0 ? EDGE_SEL_REC_OVERCAST : EDGE_SEL_LIVE;
    } else if (strcmp(cmd, "press") == 0 && n == 2) {
        press(tok[1]);
    } else if (strcmp(cmd, "wait") == 0 && n == 2) {
        advance(atof(tok[1]));
    } else if (strcmp(cmd, "remember") == 0 && n == 2) {
        int slot = 0;
        while (slot < g_n_remembered && strcmp(g_remembered_name[slot], tok[1]) != 0) {
            slot++;
        }
        vkind_t kind;
        ok = slot < 32 && lookup_var(tok[1], &g_remembered[slot], &kind);
        if (ok && slot == g_n_remembered) {
            snprintf(g_remembered_name[slot], sizeof g_remembered_name[0], "%s", tok[1]);
            g_n_remembered++;
        }
    } else if (strcmp(cmd, "expect") == 0) {
        double got;
        g_expects++;
        if (!condition(tok + 1, n - 1, &ok, &got) && ok) {
            describe(tok + 1, got);
        }
    } else if (strcmp(cmd, "within") == 0 && n == 5) {
        /* within <seconds> <var> <op> <value>: step until true, or fail */
        const uint32_t end = g_t + (uint32_t)lround(atof(tok[1]) * 1000.0);
        double got = 0;
        bool held = condition(tok + 2, 3, &ok, &got);
        g_expects++;
        while (ok && !held && g_t < end) {
            tick();
            held = condition(tok + 2, 3, &ok, &got);
        }
        if (ok && !held) {
            describe(tok + 2, got);
        }
    } else {
        ok = false;
    }
    return ok;
}

static void reset_plant(void)
{
    memset(&P, 0, sizeof P);
    P.lamp_lux_at_max = EDGE_LAMP_PHOT_LUX_AT_MAX;
    P.sensor = true;
    P.net = NET_OK;
    P.sntp_ms = 2500; /* lwIP delays SNTP's first request by a random 0-5 s */
    P.cache = CACHE_NONE;
    P.selector = EDGE_SEL_LIVE;
    P.rng = 12345u;
}

static int run_file(const char *path)
{
    FILE *fp = fopen(path, "r");
    if (fp == NULL) {
        printf("FAIL %s: cannot open\n", path);
        return 1;
    }
    reset_plant();
    memset(&A, 0, sizeof A);
    g_t = 0;
    g_fail = g_reports = g_expects = g_n_remembered = 0;
    g_file = path;
    g_line = 0;

    char line[256];
    char title[200] = "";
    while (fgets(line, sizeof line, fp) != NULL) {
        g_line++;
        char *hash = strchr(line, '#');
        if (hash != NULL) {
            *hash = '\0';
        }
        if (strncmp(line, "scenario ", 9) == 0) {
            snprintf(title, sizeof title, "%s", line + 9);
            title[strcspn(title, "\r\n")] = '\0';
            if (g_verbose) {
                printf("  %s\n", title);
            }
            continue;
        }
        char *tok[8];
        int n = 0;
        for (char *p = strtok(line, " \t\r\n"); p != NULL && n < 8; p = strtok(NULL, " \t\r\n")) {
            tok[n++] = p;
        }
        if (n == 0) {
            continue;
        }
        if (!A.booted && strcmp(tok[0], "wait") == 0) {
            fail("'wait' before 'boot'");
            continue;
        }
        if (!run_command(tok, n)) {
            fail("cannot understand '%s'", tok[0]);
        }
    }
    fclose(fp);
    const char *slash = strrchr(path, '/');
    printf("%s %-40s %3d expects %6.1f s  %s\n", g_fail == 0 ? "PASS" : "FAIL",
           slash ? slash + 1 : path, g_expects, g_t / 1000.0, title);
    return g_fail == 0 ? 0 : 1;
}

int main(int argc, char **argv)
{
    g_body_len     = load(FIXTURE_PATH, g_body, sizeof g_body);
    g_error_len    = load(ERROR_FIXTURE, g_error_body, sizeof g_error_body);
    g_overcast_len = load(OVERCAST_FIXTURE, g_overcast_body, sizeof g_overcast_body);
    int failed = 0, total = 0;
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "-v") == 0) {
            g_verbose = true;
            continue;
        }
        failed += run_file(argv[i]);
        total++;
    }
    printf("edge_sim: %d scenario(s), %d failed -> %s\n", total, failed, failed == 0 ? "PASS" : "FAIL");
    return (failed == 0 && total > 0) ? 0 : 1;
}
