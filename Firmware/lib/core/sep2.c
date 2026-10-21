#include "sep2.h"

#include <math.h>
#include <string.h>

#include "edge_config.h"

/* Signed difference of two wrapping millisecond times. */
static int32_t ms_diff(uint32_t later, uint32_t earlier)
{
    return (int32_t)(later - earlier);
}

static float dot(const float *x, const float *y)
{
    float acc = 0.0f;
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        acc += x[i] * y[i];
    }
    return acc;
}

/* Time-weighted mean, minimum and maximum of the commanded light over
 * [ts, te]. Each history entry holds its value until the next entry; the
 * oldest entry is assumed to have held since before the window. */
static void light_window(const edge_sep2_t *s, uint32_t ts, uint32_t te,
                         float *mean, float *lo, float *hi)
{
    *mean = *lo = *hi = 0.0f;
    if (s->hist_count == 0) {
        return;
    }
    if (ms_diff(te, ts) <= 0) {
        te = ts + 1;
    }
    double acc = 0.0;
    float mn = INFINITY;
    float mx = -INFINITY;
    uint32_t seg_end = te;
    for (int k = 0; k < s->hist_count; k++) {
        const int i = (s->hist_head - k + EDGE_SEP2_HISTORY) % EDGE_SEP2_HISTORY;
        const uint32_t seg_start = s->hist_t[i];
        const bool covers_start = ms_diff(seg_start, ts) <= 0 || k == s->hist_count - 1;
        const uint32_t a = covers_start ? ts : seg_start;
        const uint32_t b = ms_diff(seg_end, te) < 0 ? seg_end : te;
        if (ms_diff(b, a) > 0) {
            const float v = s->hist_light[i];
            acc += (double)v * (double)ms_diff(b, a);
            mn = fminf(mn, v);
            mx = fmaxf(mx, v);
        }
        if (covers_start) {
            break;
        }
        seg_end = seg_start;
    }
    if (mx < mn) { /* no entry overlapped (all newer than the window) */
        return;
    }
    *mean = (float)(acc / (double)ms_diff(te, ts));
    *lo = mn;
    *hi = mx;
}

static void start_capture(edge_sep2_t *s)
{
    memset(s->capture_sum, 0, sizeof s->capture_sum);
    s->capture_n      = 0;
    s->baseline_ready = false;
    s->f6             = EDGE_F6_QUIET;
    s->offset         = false;
    s->below_low      = false;
    s->rebaselines++;
    s->out.baseline_ready = false;
    s->out.event          = false;
    s->out.broad_delta    = 0.0f;
    s->out.room_epoch     = s->rebaselines;
}

void edge_sep2_init(edge_sep2_t *s, const edge_calib_t *calib, float initial_lamp_gain)
{
    memset(s, 0, sizeof *s);
    s->calib     = calib;
    s->lamp_gain = initial_lamp_gain;
    s->out.lamp_gain = initial_lamp_gain;
    start_capture(s);
    s->rebaselines    = 0; /* the first capture is not a re-baseline */
    s->out.room_epoch = 0;
}

void edge_sep2_note_light(edge_sep2_t *s, uint32_t now_ms, float light)
{
    if (s->hist_count > 0 && s->hist_t[s->hist_head] == now_ms) {
        s->hist_light[s->hist_head] = light;
        return;
    }
    s->hist_head = (s->hist_head + 1) % EDGE_SEP2_HISTORY;
    s->hist_t[s->hist_head]     = now_ms;
    s->hist_light[s->hist_head] = light;
    if (s->hist_count < EDGE_SEP2_HISTORY) {
        s->hist_count++;
    }
}

void edge_sep2_rebaseline(edge_sep2_t *s)
{
    start_capture(s);
}

const edge_light_t *edge_sep2_light(const edge_sep2_t *s)
{
    return &s->out;
}

/* Least squares for (a, w) with a >= 0. Returns false when the endmembers are
 * too alike for the solve to mean anything. */
static bool solve_two(const edge_calib_t *c, const float *d, float *a, float *w)
{
    const float *A = c->shape[EDGE_EM_LAMP];
    const float *W = c->shape[EDGE_EM_BROAD];
    const float aa = dot(A, A), ww = dot(W, W), aw = dot(A, W);
    const float det = aa * ww - aw * aw;
    if (det < EDGE_COLLINEAR_LIMIT * aa * ww) {
        return false;
    }
    const float ad = dot(A, d), wd = dot(W, d);
    *a = (ww * ad - aw * wd) / det;
    *w = (aa * wd - aw * ad) / det;
    if (*a < 0.0f) { /* the lamp cannot emit negative light */
        *a = 0.0f;
        *w = wd / ww;
    }
    return true;
}

/* Broadband change once a known lamp amplitude is taken out. */
static float project_broad(const edge_calib_t *c, const float *d, float a)
{
    const float *A = c->shape[EDGE_EM_LAMP];
    const float *W = c->shape[EDGE_EM_BROAD];
    float r[EDGE_SPEC_NCH];
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        r[i] = d[i] - a * A[i];
    }
    return dot(W, r) / dot(W, W);
}

/* F6 event detector and the scene-change rule, on the broadband change in
 * photopic lux. Returns true if a scene change asked for a re-baseline. */
static bool run_f6(edge_sep2_t *s, uint32_t t, float w_lux)
{
    switch (s->f6) {
    case EDGE_F6_QUIET:
        if (w_lux > EDGE_F6_HIGH_LUX && ms_diff(t, s->last_low_ms) <= EDGE_F6_ONSET_MS) {
            s->f6          = EDGE_F6_PENDING;
            s->f6_since_ms = t;
        }
        break;
    case EDGE_F6_PENDING:
        if (w_lux <= EDGE_F6_HIGH_LUX) {
            s->f6 = EDGE_F6_QUIET; /* too brief: a passer-by, not another light */
        } else if (ms_diff(t, s->f6_since_ms) >= EDGE_F6_HOLD_MS) {
            s->f6          = EDGE_F6_ACTIVE;
            s->f6_since_ms = t;
            s->below_low   = false;
            s->events++;
        }
        break;
    case EDGE_F6_ACTIVE:
        if (w_lux >= EDGE_F6_LOW_LUX) {
            s->below_low = false;
        } else if (!s->below_low) {
            s->below_low          = true;
            s->below_low_since_ms = t;
        } else if (ms_diff(t, s->below_low_since_ms) >= EDGE_F6_END_MS) {
            s->f6 = EDGE_F6_QUIET;
        }
        if (s->f6 == EDGE_F6_ACTIVE && ms_diff(t, s->f6_since_ms) >= EDGE_SCENE_CHANGE_MS) {
            return true; /* the "event" is the new room */
        }
        break;
    }
    if (w_lux < EDGE_F6_LOW_LUX) {
        s->last_low_ms = t;
    }

    /* Any other lasting offset (slow drift, lights off) is also a new scene. */
    if (s->f6 != EDGE_F6_ACTIVE && fabsf(w_lux) > EDGE_F6_HIGH_LUX) {
        if (!s->offset) {
            s->offset          = true;
            s->offset_since_ms = t;
        } else if (ms_diff(t, s->offset_since_ms) >= EDGE_SCENE_CHANGE_MS) {
            return true;
        }
    } else {
        s->offset = false;
    }
    return false;
}

const edge_light_t *edge_sep2_update(edge_sep2_t *s, const edge_spectral_frame_t *f)
{
    if (!f->valid) {
        s->out.valid = false; /* everything else keeps its last value (edge_light.h) */
        return &s->out;
    }
    const edge_calib_t *c = s->calib;
    const uint32_t t = f->t_end_ms;

    /* A sensor coming back after a gap may be a different (spare) bat. */
    if (s->have_valid && ms_diff(t, s->last_valid_ms) > EDGE_SENSOR_TIMEOUT_MS) {
        start_capture(s);
    }
    const uint32_t frame_dt = s->have_valid ? (uint32_t)ms_diff(t, s->last_valid_ms) : 0u;
    s->have_valid    = true;
    s->last_valid_ms = t;

    float l_mean, l_min, l_max;
    light_window(s, f->t_start_ms, f->t_end_ms, &l_mean, &l_min, &l_max);
    const bool steady = (l_max - l_min) <= EDGE_STEADY_FRACTION * l_max;
    const bool usable = steady && !f->saturated;
    const bool lamp_off = l_max <= 0.0f;
    const float estimate = s->lamp_gain * l_mean;

    s->out.valid = true;
    s->out.t_ms  = t;

    if (!s->baseline_ready) {
        if (usable) {
            const float *A = c->shape[EDGE_EM_LAMP];
            for (int i = 0; i < EDGE_SPEC_NCH; i++) {
                s->capture_sum[i] += f->ch[i] - estimate * A[i];
            }
            if (++s->capture_n >= EDGE_BASELINE_FRAMES) {
                for (int i = 0; i < EDGE_SPEC_NCH; i++) {
                    s->base[i] = s->capture_sum[i] / (float)s->capture_n;
                }
                s->baseline_ready = true;
                s->last_low_ms    = t;
            }
        }
        s->out.baseline_ready = s->baseline_ready;
        s->out.lamp_amp       = estimate;
        s->out.lamp_measured  = false;
        s->out.broad_delta    = 0.0f;
        s->out.event          = false;
        return &s->out;
    }

    float d[EDGE_SPEC_NCH];
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        d[i] = f->ch[i] - s->base[i];
    }

    float a = estimate;
    float w;
    bool measured = false;
    if (lamp_off) {
        a = 0.0f; /* the lamp emits nothing at zero duty; amber-like change is the room's */
        w = project_broad(c, d, 0.0f);
        measured = true;
    } else if (usable && solve_two(c, d, &a, &w)) {
        measured = true;
    } else {
        a = estimate;
        w = project_broad(c, d, a);
    }

    const bool rebase = run_f6(s, t, w * c->phot_per_amp[EDGE_EM_BROAD]);

    const bool quiet = s->f6 == EDGE_F6_QUIET && !s->offset;
    if (measured && !lamp_off && quiet && l_mean >= EDGE_MIN_LIT_LIGHT) {
        s->lamp_gain += EDGE_LAMP_GAIN_ALPHA * (a / l_mean - s->lamp_gain);
    }
    if (usable && lamp_off && quiet && frame_dt > 0u) {
        const float alpha = fminf(1.0f, (float)frame_dt / (EDGE_BASELINE_TAU_S * 1000.0f));
        for (int i = 0; i < EDGE_SPEC_NCH; i++) {
            s->base[i] += alpha * (f->ch[i] - s->base[i]);
        }
    }

    s->out.lamp_amp      = a;
    s->out.lamp_measured = measured;
    s->out.lamp_gain     = s->lamp_gain;
    s->out.broad_delta   = w;
    s->out.event         = s->f6 == EDGE_F6_ACTIVE;

    if (rebase) {
        s->scene_changes++;
        start_capture(s);
    }
    return &s->out;
}
