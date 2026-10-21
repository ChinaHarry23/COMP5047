// calib_store.h - the per-bat calibration kept in NVS (plan 7.6).
//
// Two bat units (the exhibit bat and its spare) each keep their own lamp
// scale: photopic lux at the bat per unit of lamp amplitude, measured with
// the `cal` serial command. Holding re-baseline at boot switches the active
// unit; the operator LED then blinks the unit number.
#pragma once

#include <Arduino.h>

namespace calib_store {

constexpr int kUnits = 2;

// Opens NVS and returns the active unit (1..kUnits); `advance` selects the next one.
int begin(bool advance);

int active_unit();

// Lux per amplitude for the active unit, or 0 if it was never calibrated.
float lamp_scale();
void  set_lamp_scale(float lux_per_amp);

} // namespace calib_store
