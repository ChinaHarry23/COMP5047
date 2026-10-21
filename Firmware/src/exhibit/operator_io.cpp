#include "operator_io.h"

#include "debounced_button.h"
#include "pins.h"

namespace operator_io {
namespace {

DebouncedButton g_rebase, g_night;

void led(bool green, bool red)
{
    digitalWrite(PIN_OP_LED_GREEN, green ? HIGH : LOW);
    digitalWrite(PIN_OP_LED_RED, red ? HIGH : LOW);
}

bool blink(uint32_t now_ms, uint32_t period_ms)
{
    return (now_ms % period_ms) < period_ms / 2;
}

} // namespace

void begin()
{
    g_rebase.begin(PIN_BTN_REBASE);
    g_night.begin(PIN_BTN_NIGHT);
    pinMode(PIN_SEL_A, INPUT); // input-only pins: the pull-ups are on the board
    pinMode(PIN_SEL_B, INPUT);
    pinMode(PIN_OP_LED_GREEN, OUTPUT);
    pinMode(PIN_OP_LED_RED, OUTPUT);
    led(false, false);
}

bool rebase_held()
{
    return g_rebase.held();
}

bool night_held()
{
    return g_night.held();
}

edge_selector_t selector()
{
    const bool a = digitalRead(PIN_SEL_A) == LOW;
    const bool b = digitalRead(PIN_SEL_B) == LOW;
    if (a && !b) {
        return EDGE_SEL_REC_CLEAR;
    }
    if (b && !a) {
        return EDGE_SEL_REC_OVERCAST;
    }
    return EDGE_SEL_LIVE;
}

Presses update(uint32_t now_ms)
{
    return Presses{ g_rebase.pressed(now_ms), g_night.pressed(now_ms) };
}

void show(bool budget_known, edge_sky_source_t source, bool fault, uint32_t now_ms)
{
    if (fault) {
        led(false, blink(now_ms, 500));
        return;
    }
    if (!budget_known) {
        led(false, false);
        return;
    }
    switch (source) {
    case EDGE_SKY_LIVE:       led(true, false); break;
    case EDGE_SKY_CACHED:     led(true, true); break;
    case EDGE_SKY_RECORDED:   led(blink(now_ms, 2000), false); break;
    case EDGE_SKY_PROTECTIVE:
    default: {
        const bool on = blink(now_ms, 1000);
        led(on, on);
        break;
    }
    }
}

void blink_count(int count)
{
    for (int i = 0; i < count; i++) {
        led(true, true);
        delay(250);
        led(false, false);
        delay(250);
    }
}

} // namespace operator_io
