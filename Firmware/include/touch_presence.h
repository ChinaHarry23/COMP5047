// touch_presence.h - capacitive touch as a presence input (kit foil; exhibit fallback).
//
// The no-touch reading is learned at boot. Presence is the size of the
// departure from it, in either direction, so the code does not depend on
// which way the touch peripheral's reading moves on this core version.
// Readings are skipped while the caller says the needle is not quiet (a short
// window after each move, needle_servo.h), in case the servo couples into the
// large electrode. The debounce counts time, not samples, so a skipped window
// delays a change by at most its own length.
#pragma once

#include <Arduino.h>
#include <math.h>

#include "edge_config.h"
#include "presence_filter.h"

class TouchPresence {
public:
    // Blocking: samples the electrode for EDGE_PRESENCE_LEARN_MS.
    void begin(uint8_t pin)
    {
        pin_ = pin;
        const int n = EDGE_PRESENCE_LEARN_MS / kSampleMs;
        float samples[EDGE_PRESENCE_LEARN_MS / kSampleMs];
        double sum = 0.0;
        for (int i = 0; i < n; i++) {
            samples[i] = static_cast<float>(touchRead(pin_));
            sum += samples[i];
            delay(kSampleMs);
        }
        mean_ = static_cast<float>(sum / n);
        edge_presence_init(&filter_);
        for (int i = 0; i < n; i++) {
            edge_presence_learn(&filter_, fabsf(samples[i] - mean_));
        }
        // No ceiling: any boot reading is accepted as "untouched".
        edge_presence_finish(&filter_, EDGE_TOUCH_MIN_DELTA, INFINITY, 0.0f);
    }

    bool update(uint32_t now_ms, bool quiet)
    {
        if (quiet) {
            last_ = static_cast<float>(touchRead(pin_));
            edge_presence_update(&filter_, now_ms, fabsf(last_ - mean_));
        }
        return filter_.state;
    }

    float reading() const { return last_; }
    float baseline() const { return mean_; }

private:
    static constexpr int kSampleMs = 10;
    uint8_t         pin_  = 0;
    float           mean_ = 0.0f;
    float           last_ = 0.0f;
    edge_presence_t filter_{};
};
