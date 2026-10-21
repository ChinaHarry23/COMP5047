// logbook.h - what happened, in a CSV on LittleFS (not a feature: plan 3.1).
//
// One "row" line a second with the state, presence, light and budget, plus
// "note" lines for events (state changes, F6 events, re-baselines, forecasts).
//
// Lines are buffered in RAM (4 KB) and appended to flash from loop() every
// 5 s, or sooner if the buffer fills. Each flush is a synchronous
// fopen/fwrite/fclose (plus, rarely, the rotation's remove/rename), so it
// stalls the control loop for as long as LittleFS takes: not measured; SPI
// flash sector erases typically take tens of ms. Moving it to another task
// would not remove the stall, because ESP-IDF suspends code running from
// flash on both cores while the flash is erased or written. The FSM clamps
// its step to 4 x EDGE_STEP_MS, so a stall cannot jump the lamp or the budget.
// The file rotates at kMaxBytes (one old copy kept). `dump` prints both files
// synchronously (see main.cpp: it runs only while the lamp is dark).
#pragma once

#include <Arduino.h>

#include "budget_fsm.h"

namespace logbook {

bool begin(); // false: the filesystem would not mount; logging is then skipped

// broad_delta_lux: broadband change over the baseline at the bat (photopic lux).
void row(uint32_t now_ms, const edge_fsm_t &fsm, float broad_delta_lux, bool event, bool present,
         const char *sky_source);

void note(uint32_t now_ms, const char *fmt, ...) __attribute__((format(printf, 2, 3)));

void service(uint32_t now_ms); // call every loop: flushes when due (blocks for the write)

void dump(Stream &out);        // blocks until both files are printed

} // namespace logbook
