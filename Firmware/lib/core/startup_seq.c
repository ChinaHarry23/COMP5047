#include "startup_seq.h"

#include <string.h>

#include "edge_config.h"

static void enter(edge_startup_t *s, edge_su_phase_t phase, uint32_t now_ms)
{
    s->phase    = phase;
    s->phase_ms = now_ms;
}

void edge_startup_begin(edge_startup_t *s, uint32_t now_ms, edge_selector_t sel, bool sweep)
{
    memset(s, 0, sizeof *s);
    s->selector = sel;
    s->begin_ms = now_ms;
    enter(s, sweep ? EDGE_SU_SWEEP : EDGE_SU_WAIT_FETCH, now_ms);
}

bool edge_startup_wants_fetch(const edge_startup_t *s)
{
    return s->selector == EDGE_SEL_LIVE;
}

void edge_startup_fetch_result(edge_startup_t *s, const edge_sky_cache_t *live)
{
    if (s->phase == EDGE_SU_DONE || s->fetch_answered) {
        return;
    }
    s->fetch_answered = true;
    s->fetch_ok       = live != NULL;
    if (live != NULL) {
        s->live = *live;
    }
}

static edge_sky_t resolve(const edge_startup_t *s, const edge_startup_env_t *env)
{
    switch (s->selector) {
    case EDGE_SEL_REC_CLEAR:    return edge_sky_recorded(env->rec_clear);
    case EDGE_SEL_REC_OVERCAST: return edge_sky_recorded(env->rec_overcast);
    case EDGE_SEL_LIVE:
    default:
        if (s->fetch_ok) {
            const edge_sky_t live = edge_sky_live(&s->live, env->now_epoch);
            if (live.source == EDGE_SKY_LIVE) {
                return live;
            }
        }
        return edge_sky_cached(env->cache, env->now_epoch);
    }
}

/* An answer that missed the cap but arrived before the hand-over: tighten-only,
 * as after the hand-over, so a cloudier live forecast is never dropped. */
static void weigh_late_answer(edge_startup_t *s, const edge_startup_env_t *env)
{
    s->fetch_weighed = true;
    if (!s->fetch_ok || s->selector != EDGE_SEL_LIVE) {
        return;
    }
    const edge_sky_t late = edge_sky_live(&s->live, env->now_epoch);
    if (late.source == EDGE_SKY_LIVE && late.budget_fmm < s->sky.budget_fmm) {
        s->sky = late;
    }
}

bool edge_startup_step(edge_startup_t *s, uint32_t now_ms, const edge_startup_env_t *env)
{
    switch (s->phase) {
    case EDGE_SU_SWEEP:
        if (now_ms - s->phase_ms >= EDGE_NEEDLE_SWEEP_MS) {
            enter(s, EDGE_SU_WAIT_FETCH, now_ms);
        }
        return false;
    case EDGE_SU_WAIT_FETCH: {
        const bool waiting = edge_startup_wants_fetch(s) && !s->fetch_answered &&
                             now_ms - s->begin_ms < EDGE_FETCH_CAP_MS;
        if (!waiting) {
            s->sky           = resolve(s, env);
            s->fetch_weighed = s->fetch_answered;
            enter(s, EDGE_SU_SHOW_SKY, now_ms);
        }
        return false;
    }
    case EDGE_SU_SHOW_SKY:
        if (s->fetch_answered && !s->fetch_weighed) {
            weigh_late_answer(s, env);
        }
        if (now_ms - s->phase_ms >= EDGE_SKY_SHOW_MS) {
            enter(s, EDGE_SU_DONE, now_ms);
            return true;
        }
        return false;
    case EDGE_SU_DONE:
    default:
        return false;
    }
}

const char *edge_selector_name(edge_selector_t sel)
{
    switch (sel) {
    case EDGE_SEL_REC_CLEAR:    return "REC-CLEAR";
    case EDGE_SEL_REC_OVERCAST: return "REC-OVERCAST";
    case EDGE_SEL_LIVE:
    default:                    return "LIVE";
    }
}
