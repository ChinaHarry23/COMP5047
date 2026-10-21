// lamp_pwm.h - the lamp's PWM output, shared by the kit and exhibit builds.
//
// Header-only because PlatformIO compiles one src/ subfolder per build
// target, so code both targets need lives in include/.
//
// The FSM hands over linear light (level^gamma, already gamma-corrected).
// This maps it onto the duty range above the driver stage's onset duty: the
// BC337 stage barely conducts below a measured minimum duty, so light 0+ is
// placed at that duty rather than at 0 (EDGE_LAMP_MIN_DUTY; 0 for the kit LED).
#pragma once

#include <Arduino.h>

#include "edge_config.h"

class LampPwm {
public:
    // Own LEDC channel; the servo uses a different channel and frequency, so
    // the core gives it a different timer.
    bool begin(uint8_t pin, uint8_t channel, float min_duty)
    {
        pin_      = pin;
        min_duty_ = min_duty;
        ok_       = ledcAttachChannel(pin, EDGE_LAMP_PWM_HZ, EDGE_LAMP_PWM_BITS, channel);
        set_light(0.0f);
        return ok_;
    }

    void set_light(float light)
    {
        if (!ok_) {
            return;
        }
        const uint32_t full = 1u << EDGE_LAMP_PWM_BITS; // the core treats this as 100% on
        uint32_t duty = 0;
        if (light > 0.0f) {
            const float x = min_duty_ + (1.0f - min_duty_) * (light > 1.0f ? 1.0f : light);
            duty = static_cast<uint32_t>(lroundf(x * static_cast<float>(full)));
        }
        duty_ = duty;
        ledcWrite(pin_, duty);
    }

    uint32_t duty() const { return duty_; }

private:
    uint8_t  pin_      = 0;
    float    min_duty_ = 0.0f;
    bool     ok_       = false;
    uint32_t duty_     = 0;
};
