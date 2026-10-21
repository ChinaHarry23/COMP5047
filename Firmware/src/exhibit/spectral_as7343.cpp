// The bat's AS7343 (lib/as7343) as the exhibit's SpectralSource.
//
// The driver owns the chip: register setup, the illumination LEDs (written
// off and read back on every connection), software auto-gain, and bus
// recovery (it clears the bus and re-probes every 0.5 s while the chip is
// missing). This file only translates its frames into the core's
// edge_spectral_frame_t (lib/core/spectral.h):
//  - the 12 filter channels, re-ordered from data-register order to the
//    core's wavelength order;
//  - basic counts (raw / (nominal gain x integration ms)), so an auto-gain
//    step does not move the numbers sep2 compares against its baseline;
//  - the measurement window, which sep2's steady-frame gate needs;
//  - saturation as a flag on a valid frame, not as a missing frame. sep2
//    already keeps saturated frames out of the unmixing and the baseline, but
//    F6 must still see a torch that overloads the chip; reporting those frames
//    as invalid would turn a torch held for more than 1 s into FAULT, with its
//    dose uncharged.
// While the chip is missing, this source reports valid=false frames at the
// frame rate, so the core enters FAULT after EDGE_SENSOR_TIMEOUT_MS and leaves
// it by itself once frames return (plan 7.3, TP1).
#include <Wire.h>

#include <iterator>
#include <string.h>

#include "AS7343.h"
#include "edge_config.h"
#include "logbook.h"
#include "pins.h"
#include "spectral_source.h"

namespace {

// 50 ms per SMUX cycle: whole periods of 100 Hz mains flicker (plan section 4),
// and the three cycles of an 18-channel frame fit the core's ~5 Hz frame period.
constexpr uint8_t  kIntegration10ms = 5;
constexpr uint32_t kNominalFrameMs  = 3u * 10u * kIntegration10ms; // autozero and SMUX switching come on top
static_assert(kNominalFrameMs < EDGE_FRAME_PERIOD_MS,
              "an AS7343 frame must fit the frame period the core's timing assumes (TP6)");

// Driver data slot of each core channel, in the core's wavelength order
// (edge_spec_channel_t; peaks from ams DS001046 Fig. 7, as in spectral.c).
constexpr as7343::Channel kSlot[] = {
    as7343::kF1,  // EDGE_CH_F1   405 nm
    as7343::kF2,  // EDGE_CH_F2   425 nm
    as7343::kFZ,  // EDGE_CH_FZ   450 nm
    as7343::kF3,  // EDGE_CH_F3   475 nm
    as7343::kF4,  // EDGE_CH_F4   515 nm
    as7343::kF5,  // EDGE_CH_F5   550 nm
    as7343::kFY,  // EDGE_CH_FY   555 nm
    as7343::kFXL, // EDGE_CH_FXL  600 nm
    as7343::kF6,  // EDGE_CH_F6   640 nm
    as7343::kF7,  // EDGE_CH_F7   690 nm
    as7343::kF8,  // EDGE_CH_F8   745 nm
    as7343::kNIR, // EDGE_CH_NIR  855 nm
};
static_assert(std::size(kSlot) == EDGE_SPEC_NCH, "one driver slot per core channel");

as7343::AS7343 g_sensor;
uint32_t       g_last_missing_ms = 0;

void fill(const as7343::Frame &f, uint32_t now_ms, edge_spectral_frame_t *out)
{
    for (int i = 0; i < EDGE_SPEC_NCH; i++) {
        out->ch[i] = as7343::basicCounts(f, kSlot[i]);
    }
    // VIS is read once per SMUX cycle; their mean covers the whole frame.
    out->clear = (as7343::basicCounts(f, as7343::kVis1) + as7343::basicCounts(f, as7343::kVis2) +
                  as7343::basicCounts(f, as7343::kVis3)) / 3.0f;
    // The driver stamps the middle of the window that runs from starting the
    // engine to the poll that found the frame complete, and that poll is this
    // call. The window includes the autozero, so it is slightly wider than the
    // three integrations: that only makes sep2's steady-frame gate stricter.
    out->t_end_ms   = now_ms;
    out->t_start_ms = f.tMs - (now_ms - f.tMs);
    out->valid      = true;
    out->saturated  = f.analogSaturation || f.digitalSaturation;
}

bool report_missing(uint32_t now_ms, edge_spectral_frame_t *out)
{
    g_last_missing_ms = now_ms;
    memset(out, 0, sizeof *out);
    out->t_start_ms = now_ms;
    out->t_end_ms   = now_ms;
    out->valid      = false;
    return true;
}

bool as7343_begin()
{
    g_sensor.setIntegration10ms(kIntegration10ms);
    g_sensor.setAutoGain(true); // safe for sep2 because it works in basic counts (see above)
    const bool found = g_sensor.begin(Wire, PIN_I2C_SDA, PIN_I2C_SCL);
    if (found) {
        logbook::note(millis(), "bat sensor: AS7343 rev %u found, its LEDs verified off, %u ms x 3 per frame",
                      g_sensor.revisionId(), 10u * kIntegration10ms);
    } else {
        logbook::note(millis(), "bat sensor: no AS7343 (%s); re-probing every 0.5 s", g_sensor.lastFailure());
    }
    return found;
}

bool as7343_poll(uint32_t now_ms, edge_spectral_frame_t *out)
{
    switch (g_sensor.update(now_ms)) {
    case as7343::Event::kFrame:
        fill(g_sensor.frame(), now_ms, out);
        return true;
    case as7343::Event::kConnected:
        logbook::note(now_ms, "bat sensor: AS7343 rev %u connected, its LEDs verified off", g_sensor.revisionId());
        return false;
    case as7343::Event::kLost:
        logbook::note(now_ms, "bat sensor: lost (%s); clearing the bus and re-probing every 0.5 s",
                      g_sensor.lastFailure());
        return report_missing(now_ms, out); // at once, so no stale reading is used meanwhile
    case as7343::Event::kNone:
        break;
    }
    if (!g_sensor.connected() && now_ms - g_last_missing_ms >= EDGE_FRAME_PERIOD_MS) {
        return report_missing(now_ms, out);
    }
    return false;
}

} // namespace

const SpectralSource kSpectralAs7343 = { "AS7343 (bat)", as7343_begin, as7343_poll };
