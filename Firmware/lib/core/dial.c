#include "dial.h"

#include "edge_config.h"

static float lerp(float a, float b, float t)
{
    return a + (b - a) * t;
}

static float clamp01(float t)
{
    return t < 0.0f ? 0.0f : (t > 1.0f ? 1.0f : t);
}

float edge_dial_budget_deg(double remaining_fmm)
{
    if (remaining_fmm >= 0.0) {
        const float t = clamp01((float)(remaining_fmm / EDGE_B_MAX_FMM));
        return lerp(EDGE_DIAL_ZERO_DEG, EDGE_DIAL_FULL_DEG, t);
    }
    const float over = clamp01((float)(-remaining_fmm / EDGE_DIAL_PAST_ZERO_FMM));
    return lerp(EDGE_DIAL_ZERO_DEG, EDGE_DIAL_PAST_ZERO_DEG, over);
}

float edge_dial_sky_deg(float cloud)
{
    return lerp(EDGE_DIAL_SKY_CLEAR_DEG, EDGE_DIAL_SKY_OVERCAST_DEG, clamp01(cloud));
}

float edge_dial_sweep_deg(uint32_t elapsed_ms)
{
    const float half = (float)EDGE_NEEDLE_SWEEP_MS / 2.0f;
    const float t = (float)elapsed_ms;
    const float up = t <= half ? t / half : (2.0f * half - t) / half;
    return lerp(EDGE_DIAL_ZERO_DEG, EDGE_DIAL_FULL_DEG, clamp01(up));
}

float edge_dial_target_deg(const edge_startup_t *su, const edge_fsm_t *fsm, uint32_t now_ms)
{
    switch (su->phase) {
    case EDGE_SU_SWEEP:      return edge_dial_sweep_deg(now_ms - su->phase_ms);
    case EDGE_SU_WAIT_FETCH: return EDGE_DIAL_ZERO_DEG;
    case EDGE_SU_SHOW_SKY:   return edge_dial_sky_deg(su->sky.cloud);
    case EDGE_SU_DONE:
    default:
        return fsm->state == EDGE_ST_FAULT ? EDGE_DIAL_FAULT_DEG
                                           : edge_dial_budget_deg(edge_fsm_remaining(fsm));
    }
}
