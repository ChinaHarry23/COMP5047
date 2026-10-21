// EDGE Revision 3 exhibit firmware.
//
// Wiring of the parts (all policy is in lib/core):
//   presence (force sensor / touch) --+
//   spectral source -> sep2 ----------+--> edge_fsm_step (every 50 ms) --> lamp
//   net / cache / recorded nights --> startup sequence --> budget      --> needle
// The loop blocks only for operator bench commands (`cal`; `dump`, which is
// refused unless the lamp is dark and nobody is on the mark) and for the
// logbook's flash writes (logbook.h). Missing hardware never stops the
// program: a missing bat sensor is FAULT, a missing network is the cache or
// the protective budget, a missing filesystem only disables the logbook.
#include <Arduino.h>

#include <iterator>

#include "bench_cal.h"
#include "budget_fsm.h"
#include "calib.h"
#include "calib_store.h"
#include "dial.h"
#include "edge_config.h"
#include "forecast.h"
#include "lamp_pwm.h"
#include "logbook.h"
#include "needle_servo.h"
#include "net.h"
#include "operator_io.h"
#include "pins.h"
#include "presence.h"
#include "recorded_nights.h"
#include "sep2.h"
#include "spectral_source.h"
#include "startup_seq.h"

namespace {

// The bat's light input. Its driver owns the I2C bus (PIN_I2C_SDA/SCL): bus
// recovery restarts Wire and drives the pins directly, so no other device may
// share that bus without reviewing lib/as7343's recoverBus().
const SpectralSource &g_spectral = kSpectralAs7343;

// Every pin this build claims, once each: assigning one pin twice fails the build.
constexpr uint8_t kPinsUsed[] = {
    PIN_I2C_SDA,     PIN_I2C_SCL,                                           // bat sensor
    PIN_LAMP,        PIN_NEEDLE,                                            // LEDC outputs
    PIN_FORCE,       PIN_TOUCH,                                             // presence
    PIN_BTN_REBASE,  PIN_BTN_NIGHT, PIN_SEL_A, PIN_SEL_B,                   // rear hatch inputs
    PIN_OP_LED_GREEN, PIN_OP_LED_RED,                                       // operator LED
    PIN_ONBOARD_LED, PIN_ONBOARD_RGB,                                       // held off
};

constexpr bool all_distinct(const uint8_t *pins, size_t n)
{
    for (size_t i = 0; i < n; i++) {
        for (size_t j = i + 1; j < n; j++) {
            if (pins[i] == pins[j]) {
                return false;
            }
        }
    }
    return true;
}
static_assert(all_distinct(kPinsUsed, std::size(kPinsUsed)), "pins.h: the exhibit uses one pin twice");

// Separate LEDC channels. Core 3.3 shares a timer only between channels with the
// same frequency and resolution, so 20 kHz and 50 Hz always get timers of their own.
constexpr uint8_t  kLampChannel   = 0;
constexpr uint8_t  kNeedleChannel = 2;
constexpr uint32_t kPresenceMs    = 10;

edge_calib_t     g_calib;
edge_sep2_t      g_sep;
edge_fsm_t       g_fsm;
edge_startup_t   g_startup;
edge_sky_cache_t g_cache;
bool             g_have_cache = false;
edge_sky_cache_t g_recorded;
bool             g_have_recorded = false;
edge_sky_t       g_sky;              // what sized tonight's budget
bool             g_budget_known = false;
bool             g_live_night   = false;
uint32_t         g_last_fetch_ms = 0;
LampPwm          g_lamp;
NeedleServo      g_needle;
bool             g_present = false;

// What has already been logged, so each change is noted once.
edge_state_t g_noted_state  = EDGE_ST_STARTUP;
unsigned     g_noted_events = 0, g_noted_scenes = 0, g_noted_rebaselines = 0, g_noted_settled = 0;

float broad_lux(const edge_light_t &l)
{
    return l.broad_delta * g_calib.phot_per_amp[EDGE_EM_BROAD];
}

void begin_night(uint32_t now, bool sweep)
{
    const edge_selector_t sel = operator_io::selector();
    edge_startup_begin(&g_startup, now, sel, sweep);
    g_live_night   = sel == EDGE_SEL_LIVE;
    g_budget_known = false;
    if (edge_startup_wants_fetch(&g_startup)) {
        net::start_fetch();
        g_last_fetch_ms = now;
    }
    logbook::note(now, "night begins: selector %s%s", edge_selector_name(sel),
                  g_live_night && !net::has_credentials() ? " (no secrets.h: no live fetch)" : "");
}

void on_budget(uint32_t now)
{
    g_sky          = g_startup.sky;
    g_budget_known = true;
    edge_fsm_set_budget(&g_fsm, g_sky.budget_fmm, g_sky.cloud);
    logbook::note(now, "sky %s (%s): C=%.3f B=%.1f FMM, cache age %lld s, evening %.1f C (logged only)",
                  edge_sky_source_name(g_sky.source), edge_sky_why_name(g_sky.why), g_sky.cloud,
                  g_sky.budget_fmm, static_cast<long long>(g_sky.age_s), g_sky.temp_c);
}

void on_fetch(const net::Result &r, uint32_t now)
{
    logbook::note(now, "fetch %s: http %d, body %s, clock %s", r.ok ? "ok" : "failed", r.http_code,
                  edge_fc_status_name(r.parse), net::now_epoch() != 0 ? "set" : "unknown");
    if (r.ok && edge_sky_cache_replaces(&r.sky, g_have_cache ? &g_cache : nullptr)) {
        g_cache      = r.sky; // the same rule net.cpp applies to NVS
        g_have_cache = true;
    }
    // Until the hand-over the startup sequence weighs the answer (an answer
    // after the 5 s cap can only tighten what it shows and hands over).
    if (g_startup.phase != EDGE_SU_DONE) {
        edge_startup_fetch_result(&g_startup, r.ok ? &r.sky : nullptr);
        return;
    }
    if (!r.ok || !g_live_night) {
        return;
    }
    // After startup a newer forecast may only tighten tonight's budget.
    const edge_sky_t s = edge_sky_live(&r.sky, net::now_epoch());
    if (s.source == EDGE_SKY_LIVE && edge_fsm_tighten_budget(&g_fsm, s.budget_fmm, s.cloud)) {
        g_sky = s;
        logbook::note(now, "budget tightened: C=%.3f B=%.1f FMM", s.cloud, s.budget_fmm);
    }
}

// The light layer re-learns the room; any event in progress ends there and,
// being the room, is not charged (budget_fsm.h).
void rebaseline(uint32_t now)
{
    edge_sep2_rebaseline(&g_sep);
    g_noted_rebaselines = g_sep.rebaselines;
    logbook::note(now, "re-baseline (button); budget unchanged at %.2f of %.1f FMM",
                  g_fsm.tally.spent_fmm, g_fsm.budget_fmm);
}

void new_night(uint32_t now)
{
    edge_fsm_new_night(&g_fsm, now);
    edge_sep2_rebaseline(&g_sep);
    g_noted_rebaselines = g_sep.rebaselines;
    begin_night(now, false);
}

// The single control step: the only place the lamp moves or budget is spent.
void control_step(uint32_t now)
{
    edge_fsm_input_t in;
    in.now_ms  = now;
    in.present = g_present;
    in.light   = *edge_sep2_light(&g_sep);
    edge_fsm_step(&g_fsm, &in);
    const float light = edge_fsm_light(&g_fsm);
    edge_sep2_note_light(&g_sep, now, light);
    g_lamp.set_light(light);
}

void note_changes(uint32_t now)
{
    if (g_fsm.state != g_noted_state) {
        logbook::note(now, "%s -> %s", edge_fsm_state_name(g_noted_state), edge_fsm_state_name(g_fsm.state));
        g_noted_state = g_fsm.state;
    }
    if (g_sep.events != g_noted_events) {
        g_noted_events = g_sep.events;
        logbook::note(now, "F6 event %u: broadband +%.2f lux over the baseline", g_sep.events,
                      broad_lux(*edge_sep2_light(&g_sep)));
    }
    if (g_fsm.events_settled != g_noted_settled) {
        g_noted_settled = g_fsm.events_settled;
        if (g_fsm.last_event_charged) {
            logbook::note(now, "F6 event over: %.2f FMM charged", g_fsm.last_event_fmm);
        } else {
            logbook::note(now, "F6 event was the room (re-baselined): %.2f FMM not charged",
                          g_fsm.last_event_fmm);
        }
    }
    if (g_sep.scene_changes != g_noted_scenes) {
        g_noted_scenes = g_sep.scene_changes;
        logbook::note(now, "scene change: re-baselined automatically (budget unchanged)");
    }
    if (g_sep.rebaselines != g_noted_rebaselines) {
        g_noted_rebaselines = g_sep.rebaselines;
        logbook::note(now, "re-learning the room (sensor back, or scene change)");
    }
}

void print_status(uint32_t now)
{
    const edge_light_t &l = *edge_sep2_light(&g_sep);
    Serial.printf("[edge] t=%.1fs %-9s hand=%d (%.0f/%.0f%s) level=%.2f duty=%lu lamp=%.2flx "
                  "broad=%+.2flx%s spent=%.2f/%.1fFMM (events %.2f) sky=%s C=%.2f sensor=%s\n",
                  now / 1000.0, edge_fsm_state_name(g_fsm.state), g_present, presence::reading(),
                  presence::threshold(), presence::using_touch() ? " touch" : "", g_fsm.level,
                  static_cast<unsigned long>(g_lamp.duty()), g_fsm.rate.lamp_phot_lux, broad_lux(l),
                  l.event ? " EVENT" : "", g_fsm.tally.spent_fmm, g_fsm.budget_fmm,
                  g_fsm.tally.event_fmm, g_budget_known ? edge_sky_source_name(g_sky.source) : "-",
                  g_fsm.sky_cloud, g_fsm.sensor_ok ? (l.baseline_ready ? "ok" : "learning") : "MISSING");
}

void calibrate(const String &arg, uint32_t now)
{
    const float lux = arg.length() > 0 ? arg.toFloat() : EDGE_LAMP_PHOT_LUX_AT_MAX;
    if (!(lux > 0.0f)) {
        Serial.println("[edge] cal: give the lux at the perch, e.g. `cal 3.8`");
        return;
    }
    const BenchCal ctx{ g_lamp, g_spectral, g_sep };
    const float scale = bench_cal_run(ctx, lux, edge_fsm_light(&g_fsm));
    if (scale > 0.0f) {
        edge_calib_set_lamp_scale(&g_calib, scale);
        calib_store::set_lamp_scale(scale);
        logbook::note(now, "cal: unit %d lamp scale %.4f lux per amplitude (%.2f lux at full light)",
                      calib_store::active_unit(), scale, lux);
    }
}

// `dump` prints up to 2 x 512 KB at 115200 baud (about 90 s) and the loop
// stops meanwhile, so it runs only while the lamp is dark and nobody is on the
// mark: a visitor arriving during a dump finds the path dark (fail dark).
// Afterwards the bat sensor looks as if it had been away (FAULT for about 1 s,
// the room re-learned); the budget is untouched.
void dump_logbook(uint32_t now)
{
    if (g_present || g_fsm.level > 0.0f) {
        Serial.println("[edge] dump: only while the lamp is dark and nobody is on the mark");
        return;
    }
    logbook::note(now, "dump: control paused (lamp dark) while the logbook prints");
    logbook::dump(Serial);
}

void handle_command(String cmd, uint32_t now)
{
    cmd.trim();
    const int space = cmd.indexOf(' ');
    const String word = space < 0 ? cmd : cmd.substring(0, space);
    const String arg  = space < 0 ? String() : cmd.substring(space + 1);
    if (word == "dump") {
        dump_logbook(now);
    } else if (word == "cal") {
        calibrate(arg, now);
    } else if (word == "rebase") {
        rebaseline(now);
    } else if (word == "night") {
        new_night(now);
    } else {
        Serial.println("[edge] commands: dump | cal [lux at perch] | rebase | night");
    }
}

void poll_serial(uint32_t now)
{
    static String line;
    while (Serial.available() > 0) {
        const char c = static_cast<char>(Serial.read());
        if (c == '\n' || c == '\r') {
            if (line.length() > 0) {
                handle_command(line, now);
            }
            line = "";
        } else if (line.length() < 40) {
            line += c;
        }
    }
}

} // namespace

void setup()
{
    Serial.begin(115200);
    // Fail dark: the lamp output is claimed and set to zero before anything else.
    g_lamp.begin(PIN_LAMP, kLampChannel, EDGE_LAMP_MIN_DUTY);
    pinMode(PIN_ONBOARD_LED, OUTPUT);
    digitalWrite(PIN_ONBOARD_LED, LOW);
    rgbLedWrite(PIN_ONBOARD_RGB, 0, 0, 0);
    g_needle.begin(PIN_NEEDLE, kNeedleChannel);

    // Boot-time choices, read before anything can be pressed by accident.
    operator_io::begin();
    const bool use_touch = operator_io::night_held();
    const bool next_unit = operator_io::rebase_held();
    const int unit = calib_store::begin(next_unit);
    if (next_unit) {
        operator_io::blink_count(unit);
    }

    edge_calib_defaults(&g_calib);
    const float scale = calib_store::lamp_scale();
    edge_calib_set_lamp_scale(&g_calib, scale); // ignored when the unit was never calibrated

    const bool log_ok = logbook::begin();
    presence::begin(use_touch);
    const bool sensor_ok = g_spectral.begin();

    edge_sep2_init(&g_sep, &g_calib, EDGE_LAMP_PHOT_LUX_AT_MAX / g_calib.phot_per_amp[EDGE_EM_LAMP]);
    edge_fsm_init(&g_fsm, &g_calib, millis());
    g_have_recorded = edge_sky_cache_from_body(kRecordedResponse, strlen(kRecordedResponse), 0,
                                               &g_recorded) == EDGE_FC_OK;
    net::begin();
    g_have_cache = net::load_cache(&g_cache);

    const uint32_t now = millis();
    // Two notes: one would overflow logbook::note's 160-character line.
    logbook::note(now, "boot: bat unit %d%s, spectral source '%s' %s, presence %s%s",
                  unit, scale > 0.0f ? "" : " (uncalibrated: run `cal`)", g_spectral.name,
                  sensor_ok ? "up" : "not answering", use_touch ? "touch fallback" : "force sensor",
                  presence::learn_refused() ? " (hand at boot: default threshold)" : "");
    logbook::note(now, "boot: logbook %s, cache %s, recorded nights %s, B_max %.1f FMM, floor %.2f",
                  log_ok ? "ok" : "off", g_have_cache ? "present" : "none",
                  g_have_recorded ? "ok" : "MISSING", EDGE_B_MAX_FMM, EDGE_LEVEL_FLOOR);
    begin_night(now, true);
}

void loop()
{
    static uint32_t last_presence = 0, last_step = 0, last_status = 0;
    const uint32_t now = millis();

    if (now - last_presence >= kPresenceMs) {
        last_presence = now;
        g_present = presence::update(now, g_needle.quiet(now));
    }

    edge_spectral_frame_t frame;
    if (g_spectral.poll(now, &frame)) {
        edge_sep2_update(&g_sep, &frame);
    }

    net::Result fetched;
    if (net::poll(&fetched)) {
        on_fetch(fetched, now);
    }
    const edge_startup_env_t env = {
        g_have_cache ? &g_cache : nullptr,
        net::now_epoch(),
        { g_have_recorded ? &g_recorded : nullptr, kRecordedClearNight },
        { g_have_recorded ? &g_recorded : nullptr, kRecordedOvercastNight },
    };
    if (edge_startup_step(&g_startup, now, &env)) {
        on_budget(now);
    }

    const operator_io::Presses pressed = operator_io::update(now);
    if (pressed.rebase) {
        rebaseline(now);
    }
    if (pressed.night) {
        new_night(now);
    }
    poll_serial(now);

    if (now - last_step >= EDGE_STEP_MS) {
        last_step = now;
        control_step(now);
    }

    g_needle.move_to(edge_dial_target_deg(&g_startup, &g_fsm, now), now);
    g_needle.service(now);
    operator_io::show(g_budget_known, g_sky.source, g_fsm.state == EDGE_ST_FAULT, now);

    if (g_live_night && g_budget_known && now - g_last_fetch_ms >= EDGE_FORECAST_REFRESH_MS &&
        net::start_fetch()) {
        g_last_fetch_ms = now;
    }

    note_changes(now);
    if (now - last_status >= 1000) {
        last_status = now;
        print_status(now);
        const edge_light_t &l = *edge_sep2_light(&g_sep);
        logbook::row(now, g_fsm, broad_lux(l), l.event, g_present,
                     g_budget_known ? edge_sky_source_name(g_sky.source) : "-");
    }
    logbook::service(now);
}
