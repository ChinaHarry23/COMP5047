#include "presence.h"

#include "edge_config.h"
#include "pins.h"
#include "presence_filter.h"
#include "touch_presence.h"

namespace presence {
namespace {

bool            g_touch = false;
edge_presence_t g_force{};
TouchPresence   g_touch_input;
float           g_reading = 0.0f;
bool            g_state = false;

float read_force()
{
    return static_cast<float>(analogRead(PIN_FORCE));
}

} // namespace

void begin(bool use_touch)
{
    g_touch = use_touch;
    if (g_touch) {
        g_touch_input.begin(PIN_TOUCH);
        return;
    }
    // No analogSetPinAttenuation(): 11 dB (about 0-3.1 V) is already the
    // core's default, and core 3.3 ignores the call (logging an error) before
    // the pin's first analogRead().
    edge_presence_init(&g_force);
    for (uint32_t t = 0; t < EDGE_PRESENCE_LEARN_MS; t += 10) {
        edge_presence_learn(&g_force, read_force());
        delay(10);
    }
    edge_presence_finish(&g_force, EDGE_FORCE_MIN_DELTA, EDGE_FORCE_LEARN_CEILING,
                         EDGE_FORCE_DEFAULT_MEAN);
}

bool update(uint32_t now_ms, bool needle_quiet)
{
    if (g_touch) {
        g_state   = g_touch_input.update(now_ms, needle_quiet);
        g_reading = g_touch_input.reading();
    } else {
        g_reading = read_force();
        g_state   = edge_presence_update(&g_force, now_ms, g_reading);
    }
    return g_state;
}

bool using_touch()
{
    return g_touch;
}

bool learn_refused()
{
    return !g_touch && g_force.learn_refused;
}

float reading()
{
    return g_reading;
}

float threshold()
{
    return g_touch ? 0.0f : g_force.press_threshold;
}

} // namespace presence
