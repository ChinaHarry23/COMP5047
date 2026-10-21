// Pin map for the DFRobot FireBeetle 2 ESP32-E (DFR0654), REVISION_3_PLAN.md section 6.
//
// Checked against the DFRobot wiki pin table (wiki.dfrobot.com/dfr0654) and the
// board variant shipped with Arduino-ESP32 3.3 (variants/dfrobot_firebeetle2_esp32e):
//  - GPIO 34, 35, 36, 39 are input-only with no internal pull-ups;
//    36 = A0 (ADC1_CH0) and 39 = A1 (ADC1_CH3) stay usable while Wi-Fi runs.
//  - GPIO 0, 2, 5, 12, 15 are ESP32 strapping pins, 1/3 are the USB serial
//    link, 2 drives the onboard LED, 5 the onboard WS2812, 27 the onboard
//    user button: none of these carry a signal here.
//  - GPIO 4, 13, 14 are touch-capable (T0, T4, T6). GPIO 18, 19, 23 are the
//    SPI/GDI pins, free because no display or SD card is fitted.
// Fixed pins (shared by every build target): I2C, lamp, phototransistor, force sensor.
#pragma once

#define PIN_I2C_SDA       21
#define PIN_I2C_SCL       22
#define PIN_LAMP          25   // LEDC 20 kHz, 11-bit -> BC337 base via 220R (kit: LED + 220R)
#define PIN_NEEDLE        26   // FS90 signal, LEDC 50 Hz on a separate timer
#define PIN_FORCE         36   // ADC1_CH0 (A0), RP-S40-ST divider with 10k to GND, input only
#define PIN_PHOTO         39   // ADC1_CH3 (A1), kit phototransistor stand-in, input only
#define PIN_TOUCH         4    // T0, guarded foil electrode (presence fallback; kit foil)
#define PIN_BTN_REBASE    13   // internal pull-up, active low
#define PIN_BTN_NIGHT     14   // internal pull-up, active low
#define PIN_SEL_A         34   // selector, input only, external 10k pull-up (low = REC-CLEAR)
#define PIN_SEL_B         35   // selector, input only, external 10k pull-up (low = REC-OVERCAST)
#define PIN_OP_LED_GREEN  18   // bicolour operator LED, green leg via 330R
#define PIN_OP_LED_RED    19   // bicolour operator LED, red leg via 330R (both = amber)
#define PIN_KIT_BUTTON    23   // kit pushbutton to GND, internal pull-up (FW0 presence)
#define PIN_ONBOARD_LED   2    // held off: no stray light in the hood
#define PIN_ONBOARD_RGB   5    // WS2812, held off
