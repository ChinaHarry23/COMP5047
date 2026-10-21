// presence.h - F1: is a hand resting on the printed hand mark?
//
// Primary: the RP-S40-ST force sensor on PIN_FORCE (3V3 -> sensor -> GPIO36,
// 10k to GND, so more force = higher reading). Its no-hand reading is
// learned at boot; the press threshold, hysteresis and 100 ms debounce come
// from lib/core/presence_filter. Fallback, chosen by holding the new-night
// button at boot: the guarded foil electrode on PIN_TOUCH, not sampled for a
// short window after each needle move (NeedleServo::quiet()).
#pragma once

#include <Arduino.h>

namespace presence {

// Blocking for EDGE_PRESENCE_LEARN_MS: keep hands off the mark at boot.
void begin(bool use_touch);

// Call every ~10 ms. Returns the debounced presence.
bool update(uint32_t now_ms, bool needle_quiet);

bool  using_touch();
bool  learn_refused(); // a hand seemed to be on the mark at boot: default threshold in use
float reading();       // last raw reading, for the status line
float threshold();     // press threshold (force sensor; 0 for touch)

} // namespace presence
