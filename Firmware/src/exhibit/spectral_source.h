// spectral_source.h - how the exhibit gets spectral frames from the bat.
//
// The policy core only sees edge_spectral_frame_t (lib/core/spectral.h).
// A source is two functions, so the policy never depends on a particular chip:
// main.cpp picks the source in one line, and nothing in lib/core changes.
//
// Contract for implementations:
//  - begin() never blocks for long and never aborts: a missing chip returns
//    false and the app runs on (the core enters FAULT and recovers later).
//  - poll() is non-blocking. When a read has finished it fills *out and
//    returns true: a valid frame, or valid=false when the chip did not answer.
//    A driver that has lost the chip keeps retrying inside poll() (bus
//    recovery, re-initialisation) and reports valid=false frames meanwhile.
//  - Frame timestamps use millis(), the same clock as the control loop.
#pragma once

#include <Arduino.h>

#include "spectral.h"

struct SpectralSource {
    const char *name;
    bool (*begin)();
    bool (*poll)(uint32_t now_ms, edge_spectral_frame_t *out);
};

// No spectral sensor is fitted (spectral_absent.cpp). begin() returns false
// and poll() reports valid=false at the frame rate, so the exhibit sits in
// FAULT.
extern const SpectralSource kSpectralNone;
