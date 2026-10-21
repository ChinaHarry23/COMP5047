#include "kit_light.h"

#include "edge_config.h"

namespace {
constexpr int      kDarkCaptureSamples = 10;  // first dark level: plain average
constexpr uint32_t kLedSettleMs        = 200; // LED light gone, phototransistor settled
constexpr int      kAdcAverage         = 4;
} // namespace

void KitLight::begin(uint8_t pin, float delta_at_max)
{
    // No analogSetPinAttenuation(): 11 dB (about 0-3.1 V) is already the
    // core's default, and core 3.3 ignores the call (logging an error) before
    // the pin's first analogRead().
    pin_           = pin;
    delta_at_max_  = delta_at_max;
    out_.lamp_gain = delta_at_max;
}

float KitLight::read_raw() const
{
    uint32_t sum = 0;
    for (int i = 0; i < kAdcAverage; i++) {
        sum += analogRead(pin_);
    }
    return static_cast<float>(sum) / kAdcAverage;
}

void KitLight::sample(uint32_t now_ms)
{
    if (samples_ > 0 && now_ms == last_sample_ms_) {
        return;
    }
    last_sample_ms_ = now_ms;
    sample_sum_ += analogRead(pin_);
    samples_++;
}

float KitLight::take_step_mean()
{
    const float mean = samples_ > 0 ? static_cast<float>(sample_sum_) / static_cast<float>(samples_)
                                    : read_raw();
    sample_sum_ = 0;
    samples_    = 0;
    return mean;
}

const edge_light_t &KitLight::update(uint32_t now_ms, float light)
{
    raw_ = take_step_mean();
    const uint32_t dt = last_ms_ == 0 ? EDGE_STEP_MS : now_ms - last_ms_;
    last_ms_ = now_ms;
    const bool saturated = raw_ >= EDGE_KIT_SATURATED_COUNTS;

    if (light > 0.0f) {
        led_off_ = false;
    } else if (!led_off_) {
        led_off_      = true;
        off_since_ms_ = now_ms;
    }
    // A clipped reading says nothing about the room's dark level.
    if (led_off_ && !saturated && now_ms - off_since_ms_ >= kLedSettleMs) {
        if (relearn_) {
            relearn_      = false;
            dark_samples_ = 0;
        }
        if (dark_samples_ < kDarkCaptureSamples) {
            dark_ = (dark_ * dark_samples_ + raw_) / (dark_samples_ + 1);
            dark_samples_++;
            if (dark_samples_ == kDarkCaptureSamples) {
                have_dark_ = true;
            }
        } else {
            const float alpha = fminf(1.0f, dt / (EDGE_KIT_DARK_TAU_S * 1000.0f));
            dark_ += alpha * (raw_ - dark_);
        }
    }
    if (have_dark_ && !saturated && light >= EDGE_KIT_CHECK_LIGHT) {
        led_seen_ = raw_ - dark_ >= EDGE_KIT_MIN_LED_COUNTS * light;
    }

    out_.valid = !saturated && led_seen_;
    if (!out_.valid) {
        return out_; // only `valid` changes on an invalid reading (edge_light.h)
    }
    out_.t_ms           = now_ms;
    out_.baseline_ready = have_dark_;
    out_.lamp_amp       = have_dark_ && light > 0.0f ? fmaxf(0.0f, raw_ - dark_) : 0.0f;
    out_.lamp_measured  = have_dark_;
    out_.lamp_gain      = delta_at_max_;
    out_.broad_delta    = 0.0f;
    out_.event          = false;
    return out_;
}
