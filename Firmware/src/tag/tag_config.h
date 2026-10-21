// Tag (bat controller) build settings. No secrets here: Wi-Fi credentials
// live in include/secrets.h (git-ignored, copy from secrets.h.example).
#pragma once
#include "pins.h"

#define TAG_UDP_PORT        1234     // BUILD_SPEC 8.1, Unity UdpLink listens here
#define TAG_SEND_HZ         100      // ORI rate
#define TAG_MPU_ADDR_LOW    0x68     // AD0 -> GND (or floating on most breakouts)
#define TAG_MPU_ADDR_HIGH   0x69     // AD0 -> 3V3
#define TAG_I2C_HZ          400000
#define TAG_COMP_ALPHA      0.98f    // complementary filter gyro weight
#define TAG_GYRO_CAL_SAMPLES 300     // ~0.6 s, keep the tag still at boot
// Prototype v1 wiring (2026-10-15), FireBeetle 2 ESP32-E (DFR0654) silkscreen:
//   D2 = GPIO25  pushbutton to GND (internal pull-up)  short press: start/stop streaming
//                                                      long press >= 1 s: zero (calibrate)
//   D3 = GPIO26  LED (+ series resistor) to GND, stands in for the vibration motor
// GPIO25/26 are not strapping pins. Note: this overrides pins.h PIN_LAMP(25)/PIN_NEEDLE(26)
// for the tag build only; other envs are unchanged.
#define TAG_PIN_BUTTON      25       // D2
#define TAG_PIN_MOTOR       26       // D3: LED now; motor stage later (needs driver + flyback)
#define TAG_MOTOR_MAX_DUTY  1.0f     // LED: no cap. Set 0.6f again when the coin motor returns
#define TAG_LONG_PRESS_MS   1000
#define TAG_DEBOUNCE_MS     30
#define TAG_STREAM_AT_BOOT  1        // 1 = start streaming after boot, 0 = wait for button
#define TAG_HELLO_HZ        1        // heartbeat while paused: "HELLO EDGE-TAG paused <seq>"
#define TAG_LUX_HZ          10       // "LUX <lux>" packets while streaming
#define TAG_BH1750_ADDR_LOW  0x23    // ADDR pin low/open (GY-302 default)
#define TAG_BH1750_ADDR_HIGH 0x5C    // ADDR pin high
#define TAG_SERIAL_DEBUG_HZ 10       // Serial print rate (0 = off)
