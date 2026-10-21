// operator_io.h - the rear hatch: two buttons, the selector, the bicolour LED.
//
// Selector (3-position, two input-only pins with external 10k pull-ups):
//   both high = LIVE, SEL_A low = REC-CLEAR, SEL_B low = REC-OVERCAST.
//   Both low cannot happen with a healthy switch; it reads as LIVE.
// Operator LED (plan F5): green = live forecast, amber = cached, amber
// blinking = protective budget (no usable forecast), green blinking =
// recorded night, red blinking = FAULT, off = startup still running.
#pragma once

#include <Arduino.h>

#include "forecast.h"
#include "startup_seq.h"

namespace operator_io {

void begin();

// Read once at boot, before anything else.
bool rebase_held();
bool night_held();

// Read at startup and at each new night only (plan F5).
edge_selector_t selector();

struct Presses {
    bool rebase;
    bool night;
};
Presses update(uint32_t now_ms);

void show(bool budget_known, edge_sky_source_t source, bool fault, uint32_t now_ms);

// Blocking: blink amber `count` times (which bat unit is active).
void blink_count(int count);

} // namespace operator_io
