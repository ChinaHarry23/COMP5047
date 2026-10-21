#include "presence_filter.h"

#include <math.h>
#include <string.h>

#include "edge_config.h"

void edge_presence_init(edge_presence_t *p)
{
    memset(p, 0, sizeof *p);
}

void edge_presence_learn(edge_presence_t *p, float reading)
{
    p->learn_sum += reading;
    p->learn_sq  += (double)reading * reading;
    p->learn_n++;
}

void edge_presence_finish(edge_presence_t *p, float min_delta, float ceiling, float default_mean)
{
    float mean = default_mean;
    float sd   = 0.0f;
    if (p->learn_n > 0) {
        mean = (float)(p->learn_sum / p->learn_n);
        const double var = p->learn_sq / p->learn_n - (double)mean * mean;
        sd = var > 0.0 ? (float)sqrt(var) : 0.0f;
    }
    p->learn_refused = p->learn_n == 0 || mean > ceiling;
    if (p->learn_refused) {
        mean = default_mean;
        sd   = 0.0f;
    }
    const float margin = fmaxf(EDGE_PRESENCE_K_SIGMA * sd, min_delta);
    edge_presence_set_thresholds(p, mean + margin, mean + margin * (1.0f - EDGE_PRESENCE_HYST));
}

void edge_presence_set_thresholds(edge_presence_t *p, float press, float release)
{
    p->press_threshold   = press;
    p->release_threshold = release;
}

bool edge_presence_update(edge_presence_t *p, uint32_t now_ms, float reading)
{
    const bool raw = p->state ? reading > p->release_threshold : reading > p->press_threshold;
    if (raw == p->state) {
        p->pending = false;
    } else if (!p->pending) {
        p->pending          = true;
        p->pending_since_ms = now_ms;
    } else if (now_ms - p->pending_since_ms >= EDGE_PRESENCE_DEBOUNCE_MS) {
        p->state   = raw;
        p->pending = false;
    }
    return p->state;
}
