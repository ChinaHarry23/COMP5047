#include "budget_fsm.h"

#include <math.h>
#include <string.h>

#include "edge_config.h"
#include "forecast.h"

static const char *const k_state_names[EDGE_ST_COUNT] = {
    "STARTUP", "DARK_IDLE", "LIT", "YIELD", "FADING", "FAULT",
};

const char *edge_fsm_state_name(edge_state_t s)
{
    return (unsigned)s < EDGE_ST_COUNT ? k_state_names[s] : "?";
}

static int32_t ms_diff(uint32_t later, uint32_t earlier)
{
    return (int32_t)(later - earlier);
}

static void enter(edge_fsm_t *f, edge_state_t next, uint32_t now_ms)
{
    if (next != f->state) {
        f->state          = next;
        f->state_since_ms = now_ms;
    }
}

void edge_fsm_init(edge_fsm_t *f, const edge_calib_t *calib, uint32_t now_ms)
{
    memset(f, 0, sizeof *f);
    f->calib          = calib;
    f->state          = EDGE_ST_STARTUP;
    f->state_since_ms = now_ms;
    f->budget_fmm     = edge_budget_for_cloud(1.0f); /* protective until tonight's is set */
    f->sky_cloud      = 1.0f;
    f->sensor_ok      = true; /* the sensor gets EDGE_SENSOR_TIMEOUT_MS to answer at boot */
    f->last_valid_ms  = now_ms;
}

float edge_fsm_light(const edge_fsm_t *f)
{
    return f->level > 0.0f ? powf(f->level, EDGE_LAMP_GAMMA) : 0.0f;
}

double edge_fsm_remaining(const edge_fsm_t *f)
{
    return (double)f->budget_fmm - f->tally.spent_fmm;
}

void edge_fsm_new_night(edge_fsm_t *f, uint32_t now_ms)
{
    f->budget_set = false;
    memset(&f->tally, 0, sizeof f->tally);
    f->in_event = false; /* an event running now belongs to the old night */
    memset(&f->event_pending, 0, sizeof f->event_pending);
    if (f->state != EDGE_ST_FAULT) { /* FAULT resumes into STARTUP by itself */
        enter(f, EDGE_ST_STARTUP, now_ms);
    }
}

bool edge_fsm_set_budget(edge_fsm_t *f, float budget_fmm, float cloud)
{
    if (f->budget_set || !(budget_fmm > 0.0f)) {
        return false;
    }
    f->budget_set = true;
    f->budget_fmm = budget_fmm;
    f->sky_cloud  = cloud;
    return true;
}

bool edge_fsm_tighten_budget(edge_fsm_t *f, float budget_fmm, float cloud)
{
    if (!f->budget_set || !(budget_fmm > 0.0f) || !(budget_fmm < f->budget_fmm)) {
        return false;
    }
    f->budget_fmm = budget_fmm;
    f->sky_cloud  = cloud;
    return true;
}

/* Where to go when nothing in particular is forcing a state. */
static edge_state_t settle(const edge_fsm_t *f, bool present, bool event)
{
    if (event) {
        return EDGE_ST_YIELD;
    }
    if (present) {
        return EDGE_ST_LIT;
    }
    return f->level > 0.0f ? EDGE_ST_FADING : EDGE_ST_DARK_IDLE;
}

/* Move toward target at full-scale rates (constant perceived speed). */
static float ramp(float level, float target, uint32_t dt_ms, uint32_t up_ms, uint32_t down_ms)
{
    if (level < target) {
        level += (float)dt_ms / (float)up_ms;
        return level > target ? target : level;
    }
    if (level > target) {
        level -= (float)dt_ms / (float)down_ms;
        return level < target ? target : level;
    }
    return level;
}

static void update_sensor_health(edge_fsm_t *f, const edge_light_t *lt, uint32_t now)
{
    if (lt->valid && ms_diff(lt->t_ms, f->last_valid_ms) >= 0) {
        f->last_valid_ms = lt->t_ms;
    }
    const bool ok = ms_diff(now, f->last_valid_ms) <= EDGE_SENSOR_TIMEOUT_MS;
    if (ok && !f->sensor_ok) {
        f->sensor_ok_since_ms = now;
    }
    f->sensor_ok = ok;
}

/* Event bookkeeping (see budget_fsm.h): an event's dose is held aside and
 * committed when the event ends, or dropped when the light layer re-learned
 * the room instead. Committing only ever lowers what remains (I3). */
static void settle_event(edge_fsm_t *f, bool event, unsigned room_epoch)
{
    if (event && !f->in_event) {
        f->in_event    = true;
        f->event_epoch = room_epoch;
        memset(&f->event_pending, 0, sizeof f->event_pending);
    } else if (!event && f->in_event) {
        f->in_event           = false;
        f->last_event_fmm     = f->event_pending.spent_fmm;
        f->last_event_charged = room_epoch == f->event_epoch;
        if (f->last_event_charged) {
            edge_dose_add(&f->tally, &f->event_pending);
        }
        memset(&f->event_pending, 0, sizeof f->event_pending);
        f->events_settled++;
    }
}

/* The lamp's light goes straight onto tonight's tally; an event's light
 * waits in event_pending (it is only non-zero while an event runs). */
static void charge(edge_fsm_t *f, uint32_t dt)
{
    edge_dose_rate_t lamp  = f->rate;
    edge_dose_rate_t other = f->rate;
    lamp.event_phot_lux  = lamp.event_scot_lux  = 0.0f;
    other.lamp_phot_lux  = other.lamp_scot_lux  = 0.0f;
    edge_dose_charge(&f->tally, &lamp, dt);
    edge_dose_charge(&f->event_pending, &other, dt);
}

static void transition(edge_fsm_t *f, const edge_fsm_input_t *in, bool event)
{
    const uint32_t now = in->now_ms;
    if (!f->sensor_ok) {
        enter(f, EDGE_ST_FAULT, now);
        return;
    }
    switch (f->state) {
    case EDGE_ST_FAULT:
        if (ms_diff(now, f->sensor_ok_since_ms) >= EDGE_FAULT_CLEAR_MS) {
            enter(f, f->budget_set ? settle(f, in->present, event) : EDGE_ST_STARTUP, now);
        }
        break;
    case EDGE_ST_STARTUP:
        if (f->budget_set && in->light.baseline_ready) {
            enter(f, settle(f, in->present, event), now);
        }
        break;
    case EDGE_ST_DARK_IDLE:
    case EDGE_ST_LIT:
    case EDGE_ST_FADING:
        if (event) {
            enter(f, EDGE_ST_YIELD, now);
        } else if (in->present) {
            enter(f, EDGE_ST_LIT, now);
        } else if (f->state == EDGE_ST_LIT) {
            enter(f, EDGE_ST_FADING, now);
        } else if (f->level <= 0.0f) {
            enter(f, EDGE_ST_DARK_IDLE, now);
        }
        break;
    case EDGE_ST_YIELD:
        if (!event) {
            enter(f, settle(f, in->present, false), now);
        }
        break;
    default:
        break;
    }
}

void edge_fsm_step(edge_fsm_t *f, const edge_fsm_input_t *in)
{
    const uint32_t now = in->now_ms;
    uint32_t dt = f->stepped ? (uint32_t)ms_diff(now, f->last_step_ms) : EDGE_STEP_MS;
    if (dt > 4u * EDGE_STEP_MS) {
        dt = 4u * EDGE_STEP_MS; /* a stalled loop must not jump the lamp or the budget */
    }
    f->stepped      = true;
    f->last_step_ms = now;

    update_sensor_health(f, &in->light, now);

    /* An invalid reading keeps the light layer's last event flag (edge_light.h),
     * so YIELD holds through dropped frames until FAULT takes over. */
    const bool event = f->sensor_ok && in->light.event;
    settle_event(f, event, in->light.room_epoch);

    /* Charge the light over the step just finished. Without a valid reading
     * the lamp is charged from the light layer's lamp_gain, which is
     * meaningful even if the sensor never answered. */
    f->rate = edge_dose_rate(f->calib, &in->light, f->sensor_ok, edge_fsm_light(f));
    charge(f, dt);

    transition(f, in, event);

    const float floor = EDGE_LEVEL_FLOOR;
    float target = 0.0f;
    uint32_t down_ms = EDGE_FADE_DOWN_MS;
    switch (f->state) {
    case EDGE_ST_LIT: {
        double share = edge_fsm_remaining(f) / f->budget_fmm;
        share = share < 0.0 ? 0.0 : (share > 1.0 ? 1.0 : share);
        target = floor + (EDGE_LEVEL_MAX - floor) * (float)share;
        break;
    }
    case EDGE_ST_YIELD:
    case EDGE_ST_FAULT:
        target  = in->present ? floor : 0.0f;
        down_ms = EDGE_DIM_TO_FLOOR_MS;
        break;
    default: /* STARTUP, DARK_IDLE, FADING */
        break;
    }
    /* The floor invariant (I1): a lit path is never dimmed below the floor
     * while someone stands on it, whichever state asked for less. */
    if (in->present && f->level >= floor && target < floor) {
        target = floor;
    }
    f->level = ramp(f->level, target, dt, EDGE_FADE_UP_MS, down_ms);
}
