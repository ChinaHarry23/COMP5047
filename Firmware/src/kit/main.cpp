// EDGE FW0 - the check-in loop on unit-kit parts only.
//
// Kit button (or foil) = presence, kit LED = lamp, kit phototransistor = the
// bat's light sensor (lamp component only), FS90 = budget needle. The policy
// is the exhibit's: lib/core's budget FSM, dose accounting and startup
// sequence. There is no network in FW0, so the protective sky (C = 1) sizes
// the budget, which also makes the pull-back come quickly.
//
// What it shows: hand on -> 1.5 s fade up; the budget drains -> the light
// pulls back to the floor and never below; hand off -> 5 s fade; the needle
// falls and holds. Nobody there -> nothing is spent. If the phototransistor
// cannot see the LED (unplugged, saturated) the core enters FAULT, as the
// exhibit does without its bat sensor. Serial (115200): a status line every
// second; type `help`.
#include <Arduino.h>
#include <Preferences.h>

#include "budget_fsm.h"
#include "calib.h"
#include "debounced_button.h"
#include "dial.h"
#include "edge_config.h"
#include "kit_light.h"
#include "kit_presence.h"
#include "lamp_pwm.h"
#include "needle_servo.h"
#include "pins.h"
#include "startup_seq.h"

namespace {

constexpr uint8_t  kLampChannel   = 0; // LEDC timer 0
constexpr uint8_t  kNeedleChannel = 2; // LEDC timer 1
constexpr uint32_t kPresenceMs    = 10;

edge_calib_t    g_calib;
edge_fsm_t      g_fsm;
edge_startup_t  g_startup;
LampPwm         g_lamp;
NeedleServo     g_needle;
KitLight        g_light;
KitPresence     g_presence;
DebouncedButton g_btn_rebase, g_btn_night;
Preferences     g_prefs;
bool            g_present = false;

// Lux per ADC count, so the kit's counts feed the same dose code.
void apply_delta(float delta)
{
    g_light.set_delta_at_max(delta);
    g_calib.phot_per_amp[EDGE_EM_LAMP] = EDGE_LAMP_PHOT_LUX_AT_MAX / delta;
}

void begin_night(bool sweep)
{
    edge_startup_begin(&g_startup, millis(), EDGE_SEL_LIVE, sweep);
    edge_startup_fetch_result(&g_startup, nullptr); // FW0: no network
}

void new_night()
{
    edge_fsm_new_night(&g_fsm, millis());
    g_light.rebaseline();
    begin_night(false);
    Serial.println("[kit] new night");
}

void rebaseline()
{
    g_light.rebaseline();
    Serial.println("[kit] re-baseline (budget unchanged)");
}

// Bench calibration: how many ADC counts the LED adds at full light, with the
// phototransistor where it will sit. The FSM is paused (not stepped) and this
// light is not charged to the budget.
void ramp_lamp(float from, float to, uint32_t ms)
{
    for (uint32_t t = 0; t <= ms; t += 20) {
        g_lamp.set_light(from + (to - from) * static_cast<float>(t) / ms);
        delay(20);
    }
}

float average_raw(uint32_t ms)
{
    float sum = 0.0f;
    int n = 0;
    for (uint32_t t = 0; t < ms; t += 20, n++) {
        sum += g_light.read_raw();
        delay(20);
    }
    return sum / n;
}

void calibrate()
{
    Serial.println("[kit] cal: lamp ramps to full and back; keep the phototransistor still");
    const float resume = edge_fsm_light(&g_fsm);
    ramp_lamp(resume, 1.0f, EDGE_FADE_UP_MS);
    delay(500);
    const float on = average_raw(1000);
    ramp_lamp(1.0f, 0.0f, EDGE_FADE_UP_MS);
    delay(500);
    const float off = average_raw(500);
    const float delta = on - off;
    if (delta < EDGE_KIT_MIN_LED_COUNTS) {
        Serial.printf("[kit] cal failed: the LED adds only %.0f counts; nothing changed\n", delta);
    } else {
        apply_delta(delta);
        g_prefs.putFloat("delta", delta);
        Serial.printf("[kit] cal: LED adds %.0f counts at full light (saved)\n", delta);
    }
    ramp_lamp(0.0f, resume, EDGE_FADE_UP_MS);
}

void set_presence_mode(bool touch)
{
    g_prefs.putBool("touch", touch);
    g_presence.begin(touch);
    Serial.printf("[kit] presence: %s (saved)\n", touch ? "foil touch on GPIO4" : "button on GPIO23");
}

void handle_command(const String &cmd)
{
    if (cmd == "cal") {
        calibrate();
    } else if (cmd == "night") {
        new_night();
    } else if (cmd == "rebase") {
        rebaseline();
    } else if (cmd == "touch" || cmd == "button") {
        set_presence_mode(cmd == "touch");
    } else {
        Serial.println("[kit] commands: cal | night | rebase | touch | button");
    }
}

void poll_serial()
{
    static String line;
    while (Serial.available() > 0) {
        const char c = static_cast<char>(Serial.read());
        if (c == '\n' || c == '\r') {
            line.trim();
            if (line.length() > 0) {
                handle_command(line);
            }
            line = "";
        } else if (line.length() < 32) {
            line += c;
        }
    }
}

void print_status(uint32_t now)
{
    const double remaining = edge_fsm_remaining(&g_fsm);
    Serial.printf("[kit] t=%.1fs %-9s hand=%d level=%.2f photo=%.0f dark=%.0f lamp=%.2flx "
                  "V'=%.2flx spent=%.1f/%.1fFMM%s needle=%.0f\n",
                  now / 1000.0, edge_fsm_state_name(g_fsm.state), g_present, g_fsm.level,
                  g_light.raw(), g_light.dark(), g_fsm.rate.lamp_phot_lux, g_fsm.rate.lamp_scot_lux,
                  g_fsm.tally.spent_fmm, g_fsm.budget_fmm, remaining < 0.0 ? " PAST-ZERO" : "",
                  g_needle.position());
}

} // namespace

void setup()
{
    Serial.begin(115200);
    // No stray light from the board itself.
    pinMode(PIN_ONBOARD_LED, OUTPUT);
    digitalWrite(PIN_ONBOARD_LED, LOW);
    rgbLedWrite(PIN_ONBOARD_RGB, 0, 0, 0);

    g_prefs.begin("edge-kit", false);
    edge_calib_defaults(&g_calib);
    g_light.begin(PIN_PHOTO, EDGE_KIT_PHOTO_DELTA_AT_MAX);
    apply_delta(g_prefs.getFloat("delta", EDGE_KIT_PHOTO_DELTA_AT_MAX));

    if (!g_lamp.begin(PIN_LAMP, kLampChannel, 0.0f)) {
        Serial.println("[kit] lamp PWM did not start");
    }
    if (!g_needle.begin(PIN_NEEDLE, kNeedleChannel)) {
        Serial.println("[kit] needle PWM did not start");
    }
    g_btn_rebase.begin(PIN_BTN_REBASE);
    g_btn_night.begin(PIN_BTN_NIGHT);
    g_presence.begin(g_prefs.getBool("touch", false));

    edge_fsm_init(&g_fsm, &g_calib, millis());
    begin_night(true);
    Serial.printf("[kit] EDGE FW0: B_max %.1f FMM, protective B %.1f FMM, floor %.2f, presence %s, "
                  "LED adds %.0f counts at max%s\n",
                  EDGE_B_MAX_FMM, EDGE_B_MAX_FMM / EDGE_CLOUD_AMPLIFICATION, EDGE_LEVEL_FLOOR,
                  g_presence.using_touch() ? "touch" : "button", g_light.delta_at_max(),
                  g_prefs.isKey("delta") ? "" : " (placeholder: run `cal`)");
}

void loop()
{
    static uint32_t last_presence = 0, last_step = 0, last_status = 0;
    const uint32_t now = millis();

    if (now - last_presence >= kPresenceMs) {
        last_presence = now;
        g_present = g_presence.update(now, g_needle.quiet(now));
    }
    g_light.sample(now);
    if (g_btn_rebase.pressed(now)) {
        rebaseline();
    }
    if (g_btn_night.pressed(now)) {
        new_night();
    }
    poll_serial();

    const edge_startup_env_t env = { nullptr, 0, { nullptr, 0 }, { nullptr, 0 } };
    if (edge_startup_step(&g_startup, now, &env)) {
        edge_fsm_set_budget(&g_fsm, g_startup.sky.budget_fmm, g_startup.sky.cloud);
        Serial.printf("[kit] budget %.1f FMM (%s sky, C = %.2f)\n", g_startup.sky.budget_fmm,
                      edge_sky_source_name(g_startup.sky.source), g_startup.sky.cloud);
    }

    // The single control step.
    if (now - last_step >= EDGE_STEP_MS) {
        last_step = now;
        edge_fsm_input_t in;
        in.now_ms  = now;
        in.present = g_present;
        in.light   = g_light.update(now, edge_fsm_light(&g_fsm));
        edge_fsm_step(&g_fsm, &in);
        g_lamp.set_light(edge_fsm_light(&g_fsm));
    }

    g_needle.move_to(edge_dial_target_deg(&g_startup, &g_fsm, now), now);
    g_needle.service(now);

    if (now - last_status >= 1000) {
        last_status = now;
        print_status(now);
    }
}
