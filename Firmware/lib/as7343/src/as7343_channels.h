// AS7343 data-register channel map for 18-channel auto-SMUX mode, and the two
// count facts that depend on it.
//
// Kept free of Arduino headers so host-side tools (tools/o1/host_check.cpp)
// can include it too.
//
// Source: ams-OSRAM AS7343 datasheet DS001046 v6-00 (2023-06-07),
// Figure 57 (CFG20 auto_smux = 3), Figure 7 (peak wavelengths), Eq. 2.
#pragma once

#include <stdint.h>

namespace as7343 {

// Index of each value in DATA_0..DATA_17 (registers 0x95..0xB8) when
// CFG20.auto_smux = 3. The device runs three integration cycles per frame;
// each cycle fills six registers: four filtered channels, then the two
// broadband ADCs (CH4, CH5), which are re-read in every cycle.
//
// CH4 is a clear (VIS) photodiode in every reference driver. What CH5 reads is
// not settled: Fig. 57 lists "2xVIS, FD" for the last two ADCs. Pimoroni and
// Adafruit call it VIS_BR (a second clear photodiode), SparkFun calls it FD
// (the flicker photodiode). It keeps a neutral name until bring-up settles it
// (lib/as7343/README.md, bring-up check 3).
enum Channel : uint8_t {
  kFZ = 0,  // cycle 1: 450 nm
  kFY,      //          555 nm (wide, ~100 nm FWHM)
  kFXL,     //          600 nm (wide, ~80 nm FWHM)
  kNIR,     //          855 nm
  kVis1,    //          CH4: clear photodiode
  kCh5_1,   //          CH5: VIS_BR (Pimoroni, Adafruit) or FD (SparkFun)
  kF2,      // cycle 2: 425 nm
  kF3,      //          475 nm
  kF4,      //          515 nm
  kF6,      //          640 nm
  kVis2,
  kCh5_2,
  kF1,      // cycle 3: 405 nm
  kF7,      //          690 nm
  kF8,      //          745 nm
  kF5,      //          550 nm
  kVis3,
  kCh5_3,
  kNumChannels
};

// True for the 12 filtered channels (indices 0-3, 6-9, 12-15). Auto-gain and
// the saturation flags that consumers act on look only at these: the
// broadband CH4/CH5 read brighter than any filter and feed neither the amber
// proxy nor the exhibit's separation.
constexpr bool isFilteredChannel(uint8_t index) { return index < kNumChannels && index % 6 < 4; }

// The count a saturated channel reads, given (ATIME + 1)(ASTEP + 1) steps.
// Eq. 2 puts full scale at the step count (capped at 65535), but the counter
// tops out one below it: hardware logs at ATIME 29 / ASTEP 599 (full scale
// 18000) show saturated channels reading 17999 (Adafruit_AS7343 hw_tests 02
// and 05). At the cap the register itself limits the count to 65535.
constexpr uint16_t saturationCount(uint32_t steps) {
  return steps <= 65535 ? static_cast<uint16_t>(steps - 1) : static_cast<uint16_t>(65535);
}

// Short CSV-safe column names, in data-register order.
inline const char *channelName(uint8_t index) {
  static const char *const kNames[kNumChannels] = {
      "FZ", "FY", "FXL", "NIR", "VIS1", "CH5_1",  //
      "F2", "F3", "F4",  "F6",  "VIS2", "CH5_2",  //
      "F1", "F7", "F8",  "F5",  "VIS3", "CH5_3"};
  return index < kNumChannels ? kNames[index] : "?";
}

}  // namespace as7343
