// needle_servo.h - the FS90 needle, shared by the kit and exhibit builds.
//
// 50 Hz pulses on its own LEDC channel. After each move the pulses are kept
// for EDGE_NEEDLE_HOLD_MS and then stopped: the needle holds by friction and
// the servo stops humming. Small changes are ignored so the needle does not
// jitter.
//
// Touch presence (kit foil, exhibit fallback) skips its samples for
// EDGE_NEEDLE_QUIET_MS after each move command, in case the servo's slewing
// couples into the electrode (unverified). Moves are kept at least
// EDGE_NEEDLE_MIN_INTERVAL_MS apart, so touch is always sampled for most of
// the time even while a draining budget moves the needle continuously, and
// a hand leaving is seen within F1's 150 ms.
#pragma once

#include <Arduino.h>

#include "edge_config.h"

static_assert(EDGE_NEEDLE_MIN_INTERVAL_MS - EDGE_NEEDLE_QUIET_MS >= 150,
              "touch presence needs at least 150 ms of sampling between needle moves (F1)");

class NeedleServo {
public:
    bool begin(uint8_t pin, uint8_t channel)
    {
        pin_ = pin;
        ok_  = ledcAttachChannel(pin, EDGE_SERVO_HZ, EDGE_SERVO_BITS, channel);
        ledcWrite(pin_, 0);
        return ok_;
    }

    // Ask for an angle in servo degrees (0..EDGE_SERVO_RANGE_DEG). Call every
    // loop with the current target: a request that comes too soon after the
    // last move is simply made again on a later call.
    void move_to(float deg, uint32_t now_ms)
    {
        if (!ok_) {
            return;
        }
        deg = constrain(deg, 0.0f, EDGE_SERVO_RANGE_DEG);
        if (has_pos_ && (fabsf(deg - pos_deg_) < EDGE_NEEDLE_MIN_STEP_DEG ||
                         now_ms - moved_ms_ < EDGE_NEEDLE_MIN_INTERVAL_MS)) {
            return;
        }
        const float us = EDGE_SERVO_MIN_US +
                         (EDGE_SERVO_MAX_US - EDGE_SERVO_MIN_US) * deg / EDGE_SERVO_RANGE_DEG;
        const float period_us = 1e6f / EDGE_SERVO_HZ;
        const uint32_t duty =
            static_cast<uint32_t>(lroundf(us / period_us * static_cast<float>(1u << EDGE_SERVO_BITS)));
        ledcWrite(pin_, duty);
        pos_deg_  = deg;
        has_pos_  = true;
        moving_   = true;
        moved_ms_ = now_ms;
    }

    // Call every loop: stops the pulses once the move has had time to finish.
    void service(uint32_t now_ms)
    {
        if (moving_ && now_ms - moved_ms_ >= EDGE_NEEDLE_HOLD_MS) {
            ledcWrite(pin_, 0);
            moving_ = false;
        }
    }

    // False just after a move command, while the servo is slewing.
    bool quiet(uint32_t now_ms) const { return !has_pos_ || now_ms - moved_ms_ >= EDGE_NEEDLE_QUIET_MS; }
    float position() const { return pos_deg_; }

private:
    uint8_t  pin_      = 0;
    bool     ok_       = false;
    bool     has_pos_  = false;
    bool     moving_   = false;
    float    pos_deg_  = 0.0f;
    uint32_t moved_ms_ = 0;
};
