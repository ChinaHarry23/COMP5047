#include "bench_cal.h"

#include "edge_config.h"

namespace {

constexpr uint32_t kTickMs    = 20;
constexpr uint32_t kSettleMs  = 600;  // let at least one whole frame start at full light
constexpr uint32_t kMeasureMs = 2000;
constexpr int      kMinFrames = 3;

struct Tally {
    double sum = 0.0;
    int    n   = 0;
};

// Drive the lamp from `from` to `to` over `ms`, keeping sep2 fed so its
// steady-frame gate sees the real light history. Steady lamp readings are
// added to `tally` when one is given.
void drive(const BenchCal &ctx, float from, float to, uint32_t ms, Tally *tally)
{
    for (uint32_t t = 0; t <= ms; t += kTickMs) {
        const uint32_t now = millis();
        const float light = ms == 0 ? to : from + (to - from) * static_cast<float>(t) / ms;
        ctx.lamp.set_light(light);
        edge_sep2_note_light(&ctx.sep, now, light);
        edge_spectral_frame_t frame;
        if (ctx.spectral.poll(now, &frame)) {
            const edge_light_t *l = edge_sep2_update(&ctx.sep, &frame);
            if (tally != nullptr && l->valid && l->lamp_measured && !l->event) {
                tally->sum += l->lamp_amp;
                tally->n++;
            }
        }
        delay(kTickMs);
    }
}

} // namespace

float bench_cal_run(const BenchCal &ctx, float lux_at_max, float resume_light)
{
    if (!edge_sep2_light(&ctx.sep)->valid || !edge_sep2_light(&ctx.sep)->baseline_ready) {
        Serial.println("[edge] cal: needs the bat sensor answering and the room learned");
        return 0.0f;
    }
    Serial.printf("[edge] cal: lamp to full for %.1f s; %.2f lux assumed at the perch\n",
                  (EDGE_FADE_UP_MS * 2 + kSettleMs + kMeasureMs) / 1000.0f, lux_at_max);
    drive(ctx, resume_light, 1.0f, EDGE_FADE_UP_MS, nullptr);
    drive(ctx, 1.0f, 1.0f, kSettleMs, nullptr);
    Tally full;
    drive(ctx, 1.0f, 1.0f, kMeasureMs, &full);
    drive(ctx, 1.0f, resume_light, EDGE_FADE_UP_MS, nullptr);

    if (full.n < kMinFrames || !(full.sum > 0.0)) {
        Serial.printf("[edge] cal: only %d usable frames; nothing changed\n", full.n);
        return 0.0f;
    }
    const float amp = static_cast<float>(full.sum / full.n);
    ctx.sep.lamp_gain = amp; // amplitude at full light: also the transfer the FSM falls back on
    Serial.printf("[edge] cal: lamp amplitude %.4f at full light over %d frames\n", amp, full.n);
    return lux_at_max / amp;
}
