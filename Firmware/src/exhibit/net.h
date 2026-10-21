// net.h - tonight's forecast over Wi-Fi (plan F5), never blocking the loop.
//
// A fetch runs in its own FreeRTOS task: join the hotspot (Wi-Fi STA), start
// SNTP, HTTPS GET the Open-Meteo AIFS forecast (certificate checked against
// the ESP-IDF CA bundle), parse it with lib/core/forecast, save it to NVS
// with the fetch time, then disconnect and switch Wi-Fi off. The loop polls
// for the result; the startup sequence stops waiting after EDGE_FETCH_CAP_MS.
//
// The clock. The board has no battery-backed clock, so after every power-up
// the time is unknown. A fetch sets it from the response's Date header (any
// answer, error answers included) or, failing that, from SNTP, waiting for
// SNTP no longer than the cap. The time only dates the cache: without it a
// cache's age is unknown and the core treats it as unusable (C = 1). So with
// the hotspot off at power-up the cache cannot be used (TP7's "cache used"
// needs the radio up, or a clock set earlier since power-up). An undated
// record never overwrites a dated one in NVS.
//
// Credentials come from include/secrets.h (copy secrets.h.example; git-ignored).
// Without that file the exhibit still builds: fetches fail at once.
#pragma once

#include <Arduino.h>

#include "forecast.h"

namespace net {

struct Result {
    bool             ok;
    int              http_code; // 0: the server was never reached
    edge_fc_status_t parse;     // why a body was rejected
    edge_sky_cache_t sky;       // valid when ok
};

void begin();
bool has_credentials();

// Start a background fetch. False if one is already running.
bool start_fetch();

// True once per finished fetch, with its result.
bool poll(Result *out);

// The forecast saved by the last good fetch, if any.
bool load_cache(edge_sky_cache_t *out);

// UTC seconds, or 0 until a Date header or SNTP has set the clock.
int64_t now_epoch();

} // namespace net
