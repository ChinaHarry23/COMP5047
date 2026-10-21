// fake_arduino/Arduino.h - just enough of the Arduino-ESP32 API to compile
// src/kit/kit_light.cpp and include/needle_servo.h / touch_presence.h on the
// host (test_kit.cpp). The test decides what the ADC and the touch pad read;
// LEDC calls do nothing. Not used by any firmware build.
#pragma once

#include <math.h>
#include <stdint.h>

namespace fake {
inline uint16_t (*analog_read)(uint8_t pin) = nullptr; // test-supplied ADC
inline uint32_t (*touch_read)(uint8_t pin)  = nullptr; // test-supplied touch pad
} // namespace fake

inline uint16_t analogRead(uint8_t pin) { return fake::analog_read != nullptr ? fake::analog_read(pin) : 0; }
inline uint32_t touchRead(uint8_t pin) { return fake::touch_read != nullptr ? fake::touch_read(pin) : 0; }
inline void delay(uint32_t) {}
inline bool ledcAttachChannel(uint8_t, uint32_t, uint8_t, uint8_t) { return true; }
inline bool ledcWrite(uint8_t, uint32_t) { return true; }

#define constrain(amt, low, high) ((amt) < (low) ? (low) : ((amt) > (high) ? (high) : (amt)))
