// Exhibit spectral input. No spectral sensor is fitted.
//
// begin() returns false. poll() reports valid=false frames at the frame
// period, so the policy core enters FAULT after EDGE_SENSOR_TIMEOUT_MS and
// can leave FAULT on its own if a later source starts delivering frames.
#include <string.h>

#include "edge_config.h"
#include "logbook.h"
#include "spectral_source.h"

namespace {

uint32_t g_last_ms = 0;

bool begin_absent()
{
    logbook::note(millis(), "bat sensor: none fitted");
    return false;
}

bool poll_absent(uint32_t now_ms, edge_spectral_frame_t *out)
{
    if (g_last_ms != 0 && now_ms - g_last_ms < EDGE_FRAME_PERIOD_MS) {
        return false;
    }
    g_last_ms = now_ms;
    memset(out, 0, sizeof *out);
    out->t_start_ms = now_ms;
    out->t_end_ms   = now_ms;
    out->valid      = false;
    return true;
}

}  // namespace

const SpectralSource kSpectralNone = { "no spectral sensor", begin_absent, poll_absent };
