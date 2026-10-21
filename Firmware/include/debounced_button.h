// debounced_button.h - an active-low pushbutton with an internal pull-up.
// An unwired pin reads "not pressed", so a missing button is harmless.
#pragma once

#include <Arduino.h>

class DebouncedButton {
public:
    void begin(uint8_t pin)
    {
        pin_ = pin;
        pinMode(pin_, INPUT_PULLUP);
        stable_ = held();
    }

    bool held() const { return digitalRead(pin_) == LOW; }

    // True once per press, after the contact has settled.
    bool pressed(uint32_t now_ms)
    {
        const bool raw = held();
        if (raw != last_raw_) {
            last_raw_  = raw;
            change_ms_ = now_ms;
        }
        if (raw != stable_ && now_ms - change_ms_ >= kSettleMs) {
            stable_ = raw;
            return stable_;
        }
        return false;
    }

private:
    static constexpr uint32_t kSettleMs = 30;
    uint8_t  pin_       = 0;
    bool     stable_    = false;
    bool     last_raw_  = false;
    uint32_t change_ms_ = 0;
};
