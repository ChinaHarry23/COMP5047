/*
 * test_sep2.c - two-endmember separation on synthetic mixtures.
 *
 * Frames are built exactly from known amplitudes (no noise), so the expected
 * answers are known in closed form.
 */
#include <string.h>

#include "calib.h"
#include "edge_config.h"
#include "plant_shapes.h"
#include "sep2.h"
#include "test_util.h"

typedef struct {
    edge_calib_t calib;
    edge_sep2_t  sep;
    uint32_t     t;
} harness_t;

#define GAIN_AT_MAX 4.0f /* lamp amplitude at full light, as the firmware assumes */

static void setup(harness_t *h)
{
    edge_calib_defaults(&h->calib);
    h->t = 1000;
    edge_sep2_init(&h->sep, &h->calib, GAIN_AT_MAX);
    edge_sep2_note_light(&h->sep, h->t, 0.0f);
}

/* One 200 ms frame: four control steps at lamp light `light`, then a frame
 * containing the given amplitudes of each spectrum. */
static const edge_light_t *frame(harness_t *h, float light, float lamp, float broad,
                                 float room, const float *room_shape, bool saturated)
{
    for (int k = 0; k < 4; k++) {
        h->t += 50;
        edge_sep2_note_light(&h->sep, h->t, light);
    }
    edge_spectral_frame_t f;
    memset(&f, 0, sizeof f);
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        f.ch[i] = lamp * h->calib.shape[EDGE_EM_LAMP][i] + broad * h->calib.shape[EDGE_EM_BROAD][i];
        if (room_shape != NULL) {
            f.ch[i] += room * room_shape[i];
        }
    }
    f.t_start_ms = h->t - 180;
    f.t_end_ms   = h->t;
    f.valid      = true;
    f.saturated  = saturated;
    return edge_sep2_update(&h->sep, &f);
}

static const edge_light_t *frames(harness_t *h, int n, float light, float lamp, float broad,
                                  float room, const float *room_shape)
{
    const edge_light_t *out = NULL;
    for (int i = 0; i < n; i++) {
        out = frame(h, light, lamp, broad, room, room_shape, false);
    }
    return out;
}

/* Broadband amplitude that carries `lux` photopic lux. */
static float broad_amp(const harness_t *h, float lux)
{
    return lux / h->calib.phot_per_amp[EDGE_EM_BROAD];
}

static void test_baseline_then_pure_lamp(void)
{
    harness_t h;
    setup(&h);
    const float *W = h.calib.shape[EDGE_EM_BROAD];
    const edge_light_t *o = frames(&h, EDGE_BASELINE_FRAMES - 1, 0.0f, 0.0f, 0.0f, 2.0f, W);
    CHECK(!o->baseline_ready);
    o = frames(&h, 1, 0.0f, 0.0f, 0.0f, 2.0f, W);
    CHECK(o->baseline_ready);

    o = frames(&h, 3, 1.0f, 4.0f, 0.0f, 2.0f, W);
    CHECK(o->lamp_measured);
    CHECK_NEAR(o->lamp_amp, 4.0, 1e-4);
    CHECK_NEAR(o->broad_delta, 0.0, 1e-4);
    CHECK(!o->event);
}

static void test_lamp_plus_torch(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    const edge_light_t *o = frames(&h, 2, 1.0f, 4.0f, 10.0f, 0.0f, NULL);
    CHECK_NEAR(o->lamp_amp, 4.0, 1e-3);
    CHECK_NEAR(o->broad_delta, 10.0, 1e-3);
}

/* A fluorescent room is neither endmember, but while it stays put the
 * baseline spectrum absorbs it exactly. */
static void test_fluorescent_room_absorbed(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 6.0f, k_shape_triphosphor);
    const edge_light_t *o = frames(&h, 3, 1.0f, 4.0f, 0.0f, 6.0f, k_shape_triphosphor);
    CHECK_NEAR(o->lamp_amp, 4.0, 1e-4);
    CHECK_NEAR(o->broad_delta, 0.0, 1e-4);
}

/* Independent least squares of `d` on (A, W), a >= 0 not active here. */
static void project(const harness_t *h, const float *d, double *a, double *w)
{
    const float *A = h->calib.shape[EDGE_EM_LAMP];
    const float *W = h->calib.shape[EDGE_EM_BROAD];
    double aa = 0, ww = 0, aw = 0, ad = 0, wd = 0;
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        aa += (double)A[i] * A[i];
        ww += (double)W[i] * W[i];
        aw += (double)A[i] * W[i];
        ad += (double)A[i] * d[i];
        wd += (double)W[i] * d[i];
    }
    const double det = aa * ww - aw * aw;
    *a = (ww * ad - aw * wd) / det;
    *w = (aa * wd - aw * ad) / det;
}

/* Room LEDs and torches are nearly collinear, so they share one endmember.
 * A warm-white room that changes while the lamp is lit is not quite that
 * endmember: part of the change leaks into the lamp estimate. The test pins
 * the leak to the closed-form projection and shows the two guards: no leak
 * when the change is broadband-shaped, none at all with the lamp off. */
static void test_near_collinear_room_change(void)
{
    harness_t h;
    setup(&h);
    const float room0 = 5.0f, room1 = 3.5f; /* lights dimmed by 30% mid-visit */
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, room0, k_shape_warm_white);
    frames(&h, 3, 1.0f, 4.0f, 0.0f, room0, k_shape_warm_white);
    const edge_light_t *o = frames(&h, 1, 1.0f, 4.0f, 0.0f, room1, k_shape_warm_white);

    float d[EDGE_SPEC_NCH];
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        d[i] = (room1 - room0) * k_shape_warm_white[i];
    }
    double leak_a, leak_w;
    project(&h, d, &leak_a, &leak_w);
    CHECK_NEAR(o->lamp_amp, 4.0 + leak_a, 1e-3);
    CHECK_NEAR(o->broad_delta, leak_w, 1e-3);
    const double room_lux_change = (room1 - room0) * WARM_WHITE_LUX_PER_AMP;
    printf("  info: warm-white room change while lit leaks %.2f lamp-lux per room-lux\n",
           leak_a * h.calib.phot_per_amp[EDGE_EM_LAMP] / room_lux_change);

    /* Same size of change but broadband-shaped: no leak. */
    harness_t g;
    setup(&g);
    const float *W = g.calib.shape[EDGE_EM_BROAD];
    frames(&g, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, room0, W);
    o = frames(&g, 2, 1.0f, 4.0f, 0.0f, room1, W);
    CHECK_NEAR(o->lamp_amp, 4.0, 1e-4);

    /* Lamp off: an amber-ish change is the room's, never the lamp's. */
    harness_t z;
    setup(&z);
    frames(&z, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, room0, k_shape_warm_white);
    o = frames(&z, 1, 0.0f, 0.0f, 0.0f, room1, k_shape_warm_white);
    CHECK(o->lamp_measured);
    CHECK_NEAR(o->lamp_amp, 0.0, 1e-9);
}

/* Frames taken while the lamp is fading are not unmixed: the lamp is
 * estimated from the transfer measured on steady frames. */
static void test_steady_gate(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    /* Measure a transfer of 3.0 (not the assumed 4.0) at full light. */
    frames(&h, 40, 1.0f, 3.0f, 0.0f, 0.0f, NULL);
    CHECK_NEAR(h.sep.lamp_gain, 3.0, 1e-3);

    /* Fade down: light changes within every frame. */
    float light = 1.0f;
    for (int k = 0; k < 4; k++) {
        light -= 0.05f;
        h.t += 50;
        edge_sep2_note_light(&h.sep, h.t, light);
    }
    edge_spectral_frame_t f;
    memset(&f, 0, sizeof f);
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        f.ch[i] = 99.0f * h.calib.shape[EDGE_EM_LAMP][i]; /* nonsense the gate must ignore */
    }
    f.t_start_ms = h.t - 180;
    f.t_end_ms   = h.t;
    f.valid      = true;
    const edge_light_t *o = edge_sep2_update(&h.sep, &f);
    CHECK(!o->lamp_measured);
    CHECK(o->lamp_amp > 2.4f && o->lamp_amp < 3.0f); /* 3.0 x mean light over the window */
}

static void test_saturated_frame_not_unmixed(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    frames(&h, 3, 1.0f, 4.0f, 0.0f, 0.0f, NULL);
    const edge_light_t *o = frame(&h, 1.0f, 50.0f, 0.0f, 0.0f, NULL, true);
    CHECK(!o->lamp_measured);
    CHECK_NEAR(o->lamp_amp, 4.0, 1e-3);
}

static void test_event_detector(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES + 2, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    const float torch = broad_amp(&h, 5.0f);

    /* A single bright frame (a passer-by's phone screen) is not an event. */
    const edge_light_t *o = frames(&h, 1, 0.0f, 0.0f, torch, 0.0f, NULL);
    CHECK(!o->event);
    o = frames(&h, 3, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    CHECK(!o->event);

    /* A held torch: pending for the hold time, then an event. */
    o = frames(&h, 2, 0.0f, 0.0f, torch, 0.0f, NULL); /* 0 ms and 200 ms above */
    CHECK(!o->event);
    o = frames(&h, 1, 0.0f, 0.0f, torch, 0.0f, NULL); /* 400 ms above */
    CHECK(o->event);
    CHECK(h.sep.events == 1);

    /* The baseline does not learn the torch while the event runs. */
    float base[EDGE_SPEC_NCH];
    memcpy(base, h.sep.base, sizeof base);
    frames(&h, 10, 0.0f, 0.0f, torch, 0.0f, NULL);
    CHECK(memcmp(base, h.sep.base, sizeof base) == 0);

    /* Torch off: the event ends after 1 s below LOW, not before. */
    o = frames(&h, 5, 0.0f, 0.0f, 0.0f, 0.0f, NULL); /* 0..800 ms below */
    CHECK(o->event);
    o = frames(&h, 1, 0.0f, 0.0f, 0.0f, 0.0f, NULL); /* 1000 ms below */
    CHECK(!o->event);
}

static void test_slow_rise_is_not_an_event(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    /* Room light creeping up 5 lux over 20 s: no fast onset, no event. */
    const edge_light_t *o = NULL;
    for (int i = 1; i <= 100; i++) {
        o = frames(&h, 1, 0.0f, 0.0f, broad_amp(&h, 5.0f * (float)i / 100.0f), 0.0f, NULL);
        CHECK(!o->event);
    }
    CHECK(h.sep.events == 0);
}

/* An "event" that lasts 30 s is the new room: it is re-learned. */
static void test_scene_change(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    const float lights = broad_amp(&h, 20.0f);
    const edge_light_t *o = frames(&h, 3, 0.0f, 0.0f, lights, 0.0f, NULL);
    CHECK(o->event);
    const int frames_to_30s = EDGE_SCENE_CHANGE_MS / 200;
    o = frames(&h, frames_to_30s - 1, 0.0f, 0.0f, lights, 0.0f, NULL);
    CHECK(o->event);
    CHECK(h.sep.scene_changes == 0);
    o = frames(&h, 1, 0.0f, 0.0f, lights, 0.0f, NULL);
    CHECK(!o->event);
    CHECK(h.sep.scene_changes == 1);
    CHECK(!o->baseline_ready);
    o = frames(&h, EDGE_BASELINE_FRAMES + 3, 0.0f, 0.0f, lights, 0.0f, NULL);
    CHECK(o->baseline_ready);
    CHECK(!o->event);
    CHECK_NEAR(o->broad_delta, 0.0, 1e-4);
}

/* Lights switched off for good is a scene change too (no event, though). */
static void test_lights_off_scene_change(void)
{
    harness_t h;
    setup(&h);
    const float room = broad_amp(&h, 20.0f);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, room, h.calib.shape[EDGE_EM_BROAD]);
    frames(&h, 3, 1.0f, 4.0f, 0.0f, room, h.calib.shape[EDGE_EM_BROAD]);
    const edge_light_t *o = frames(&h, EDGE_SCENE_CHANGE_MS / 200 - 1, 1.0f, 4.0f, 0.0f, 0.0f, NULL);
    CHECK(!o->event);
    CHECK(o->broad_delta < -10.0f);
    CHECK(h.sep.scene_changes == 0);
    frames(&h, 2, 1.0f, 4.0f, 0.0f, 0.0f, NULL);
    CHECK(h.sep.scene_changes == 1);
}

static void test_rebaseline_clears_event(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    const edge_light_t *o = frames(&h, 3, 0.0f, 0.0f, broad_amp(&h, 8.0f), 0.0f, NULL);
    CHECK(o->event);
    edge_sep2_rebaseline(&h.sep);
    CHECK(!edge_sep2_light(&h.sep)->event);
    CHECK(h.sep.rebaselines == 1);
}

static void test_sensor_return_relearns(void)
{
    harness_t h;
    setup(&h);
    frames(&h, EDGE_BASELINE_FRAMES, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    CHECK(edge_sep2_light(&h.sep)->baseline_ready);

    edge_spectral_frame_t lost;
    memset(&lost, 0, sizeof lost);
    for (int i = 0; i < 8; i++) { /* 1.6 s without a valid frame */
        h.t += 200;
        edge_sep2_note_light(&h.sep, h.t, 0.0f);
        lost.t_end_ms = h.t;
        CHECK(!edge_sep2_update(&h.sep, &lost)->valid);
    }
    const edge_light_t *o = frames(&h, 1, 0.0f, 0.0f, 0.0f, 0.0f, NULL);
    CHECK(o->valid);
    CHECK(!o->baseline_ready);
    CHECK(h.sep.rebaselines == 1);
}

int main(void)
{
    test_baseline_then_pure_lamp();
    test_lamp_plus_torch();
    test_fluorescent_room_absorbed();
    test_near_collinear_room_change();
    test_steady_gate();
    test_saturated_frame_not_unmixed();
    test_event_detector();
    test_slow_rise_is_not_an_event();
    test_scene_change();
    test_lights_off_scene_change();
    test_rebaseline_clears_event();
    test_sensor_return_relearns();
    return test_summary("test_sep2");
}
