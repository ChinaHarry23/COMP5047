/*
 * presence_filter.h - raw sensor reading -> debounced "hand on the mark".
 *
 * Shared by the force sensor, the touch fallback and the kit button, so the
 * simulator's chatter scenario exercises the same code the exhibit runs.
 *
 * Analog sensors learn their no-hand reading at boot (mean and noise SD over
 * EDGE_PRESENCE_LEARN_MS). The press threshold sits max(K x SD, min_delta)
 * above that mean; the release threshold sits EDGE_PRESENCE_HYST of that
 * margin lower (hysteresis). A change of state must hold for
 * EDGE_PRESENCE_DEBOUNCE_MS before it is reported. Larger reading = more
 * presence; callers invert sensors that fall when touched.
 */
#ifndef EDGE_PRESENCE_FILTER_H
#define EDGE_PRESENCE_FILTER_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    double   learn_sum;
    double   learn_sq;
    int      learn_n;
    float    press_threshold;
    float    release_threshold;
    bool     learn_refused;   /* boot reading looked like a hand: defaults used */
    bool     state;           /* debounced presence                            */
    bool     pending;         /* raw state differs from the debounced one      */
    uint32_t pending_since_ms;
} edge_presence_t;

void edge_presence_init(edge_presence_t *p);

/* Feed one reading during the boot learning window. */
void edge_presence_learn(edge_presence_t *p, float reading);

/* End learning and set thresholds. If the learned mean exceeds `ceiling`
 * (someone was already pressing), fall back to `default_mean`. */
void edge_presence_finish(edge_presence_t *p, float min_delta, float ceiling, float default_mean);

/* Fixed thresholds for a digital input (no learning). */
void edge_presence_set_thresholds(edge_presence_t *p, float press, float release);

/* One reading; returns the debounced state. */
bool edge_presence_update(edge_presence_t *p, uint32_t now_ms, float reading);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_PRESENCE_FILTER_H */
