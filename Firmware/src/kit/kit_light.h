// kit_light.h - the kit phototransistor as a stand-in for the bat's sensor.
//
// One channel cannot separate the lamp from other light, so FW0 treats the
// whole rise over the dark level as the lamp component (no F6 events). The
// dark level is learned while the LED is off, so slow room changes between
// visits are followed. Amplitude unit: ADC counts. The reading the LED adds
// at full light is measured on the bench with `cal`; until then it is the
// placeholder EDGE_KIT_PHOTO_DELTA_AT_MAX.
//
// The same rules as the exhibit's light layer (lib/core/edge_light.h):
//  - LED off means no lamp light (a = 0), so ADC noise is never charged;
//  - a reading that cannot show the LED is invalid: the ADC at its top
//    (EDGE_KIT_SATURATED_COUNTS), or the LED adding less than
//    EDGE_KIT_MIN_LED_COUNTS per unit of light while lit (phototransistor
//    unplugged, or looking away). The core then charges lamp_gain x light and
//    enters FAULT after 1 s. Visibility can only be tested while the LED is
//    lit, so with the LED off the last verdict stands.
#pragma once

#include <Arduino.h>

#include "edge_light.h"

class KitLight {
public:
    void begin(uint8_t pin, float delta_at_max);

    // Call every loop: adds at most one ADC sample per millisecond to the
    // current control step's mean, so 100 Hz mains flicker averages out.
    void sample(uint32_t now_ms);

    // Once per control step. `light` is the lamp light commanded for the
    // step just finished (the FSM's output before this step).
    const edge_light_t &update(uint32_t now_ms, float light);

    // Learn the dark level again next time the LED is off. Until then the
    // current one stays in use, so the lamp is never left uncharged.
    void rebaseline() { relearn_ = true; }

    float read_raw() const;           // one averaged ADC reading, for `cal`
    void set_delta_at_max(float delta) { delta_at_max_ = delta; out_.lamp_gain = delta; }
    float delta_at_max() const { return delta_at_max_; }
    float raw() const { return raw_; }
    float dark() const { return dark_; }

private:
    float take_step_mean();

    uint8_t      pin_          = 0;
    float        delta_at_max_ = 0.0f;
    uint32_t     sample_sum_   = 0;
    uint32_t     samples_      = 0;
    uint32_t     last_sample_ms_ = 0;
    float        raw_          = 0.0f;
    float        dark_         = 0.0f;
    int          dark_samples_ = 0;
    bool         have_dark_    = false;
    bool         relearn_      = false;
    bool         led_off_      = false;
    bool         led_seen_     = true;  // last verdict of the visibility test
    uint32_t     off_since_ms_ = 0;
    uint32_t     last_ms_      = 0;
    edge_light_t out_{};
};
