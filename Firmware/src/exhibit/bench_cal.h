// bench_cal.h - the `cal` serial command: lamp lux per unit of lamp amplitude.
//
// With the hood set up and the room as it will be, the lamp ramps to full,
// sep2 measures the lamp component on steady frames, and the lamp ramps back.
// lux_at_max is the photopic lux at the bat's perch at full light: the QM1587
// reading once E0 has one, otherwise the declared EDGE_LAMP_PHOT_LUX_AT_MAX.
// The control loop is paused meanwhile (the FSM is not stepped) and this
// light is not charged to the budget: it is a bench operation, not the exhibit.
#pragma once

#include <Arduino.h>

#include "calib.h"
#include "lamp_pwm.h"
#include "sep2.h"
#include "spectral_source.h"

struct BenchCal {
    LampPwm              &lamp;
    const SpectralSource &spectral;
    edge_sep2_t          &sep;
};

// Returns the measured lux per amplitude, or 0 if it could not measure.
// `resume_light` is where the lamp is left afterwards (the FSM's light).
float bench_cal_run(const BenchCal &ctx, float lux_at_max, float resume_light);
