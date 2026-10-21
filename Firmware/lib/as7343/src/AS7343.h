// Register-level Arduino (Wire) driver for the ams-OSRAM AS7343 14-channel
// spectral sensor, as fitted to the Pimoroni PIM707 breakout.
//
// Written from datasheet DS001046 v6-00 (2023-06-07); see README.md for the
// register notes and sources.
//
// Design:
//  - Non-blocking. update() is called from loop(); it never waits for a
//    measurement and every Wire call is bounded by Wire.setTimeOut().
//  - One frame = one fresh 18-channel auto-SMUX measurement. The driver stops
//    the engine (SP_EN = 0) after each frame, applies any settings change, and
//    starts it again, because the datasheet says configuration must only change
//    while SP_EN = 0. Each frame therefore has exactly one gain and one
//    integration time.
//  - A missing or failing sensor is a normal state, not an error: after a few
//    consecutive failures the driver reports kLost, clears the bus (9 clocks +
//    STOP) and re-probes every kProbeIntervalMs until the sensor answers again.
//  - The breakout's two illumination LEDs (driven from the AS7343 LDR pin) are
//    written off and verified off on every (re)connection.
#pragma once

#include <Arduino.h>
#include <Wire.h>

#include "as7343_channels.h"

namespace as7343 {

constexpr uint8_t kI2cAddress = 0x39;

// CFG1.AGAIN codes: 0 = 0.5x, n = 2^(n-1) x, 12 = 2048x (DS001046 Fig. 31).
constexpr uint8_t kGainCodeMin = 0;
constexpr uint8_t kGainCodeMax = 12;
constexpr uint8_t kGainCodeDefault = 9;  // 256x, the device's reset value

// Integration time per SMUX cycle, in multiples of 10 ms (1..42 -> 10..420 ms).
// A multiple of 10 ms spans whole periods of 100 Hz mains flicker.
constexpr uint8_t kIntegration10msMin = 1;
constexpr uint8_t kIntegration10msMax = 42;

// Nominal gain multiplier for a CFG1.AGAIN code (0.5, 1, 2, ... 2048).
float gainMultiplier(uint8_t gainCode);

struct Frame {
  uint16_t raw[kNumChannels];  // DATA_0..DATA_17, order as in as7343_channels.h
  uint8_t gainCode;            // CFG1.AGAIN in force for the whole frame
  uint8_t atime;               // ATIME register value
  uint16_t astep;              // ASTEP register value
  uint32_t integrationUs;      // per SMUX cycle: (ATIME+1)(ASTEP+1) x 2.78 us
  uint16_t fullScale;          // digital full scale: min(65535, (ATIME+1)(ASTEP+1)) (Eq. 2)
  uint16_t saturatedCount;     // what a saturated channel reads: saturationCount(), one below full scale

  // Saturation of the 12 filtered channels, the only ones the exhibit's
  // separation and the O1 amber proxy use. analogSaturation || digitalSaturation
  // means "do not trust this frame's filtered channels".
  bool digitalSaturation;      // a filtered channel reads saturatedCount
  bool analogSaturation;       // chipAnalogFlag while a filtered channel is above 80% of full scale

  // The chip's own flags, for logging. They are global (no channel named) and
  // the broadband CH4/CH5 trip them long before any filter does.
  bool chipAnalogFlag;         // STATUS2.ASAT_ANALOG
  bool chipDigitalFlag;        // STATUS2.ASAT_DIGITAL or ASTATUS.ASAT_STATUS

  uint32_t tMs;                // millis() at the middle of the measurement window
};

// Raw counts normalised by nominal gain and integration time:
// raw / (gain x integration_ms). Lets frames taken at different settings be
// compared, but only roughly: DS001046 Fig. 10 gives typical gain ratios whose
// adjacent steps are 1.94-2.10 rather than 2.00, and at 2048x a min-max range
// of 28.2-41.3 against a nominal 32 (-12% to +29%). Until E0 calibration,
// compare frames only at one gain (the O1 analysis never pairs across gains).
float basicCounts(const Frame &frame, Channel channel);

enum class Event : uint8_t {
  kNone,       // nothing new
  kFrame,      // frame() holds a new frame
  kConnected,  // sensor found and configured; frames follow
  kLost,       // sensor stopped answering; the driver is re-probing
};

class AS7343 {
 public:
  // Starts the I2C bus and tries the sensor once. Returns true if it answered.
  // If it did not, update() keeps probing, so a late or replugged sensor is
  // picked up without a reset.
  bool begin(TwoWire &wire, int sdaPin, int sclPin);

  // Advances the driver. Call every loop(); returns quickly in every state.
  Event update(uint32_t nowMs);

  bool connected() const { return state_ == State::kMeasuring; }
  const Frame &frame() const { return frame_; }

  // Discards the frame in progress, e.g. when the scene was just changed on
  // purpose, so that no frame mixes two conditions.
  void restartMeasurement();

  // Settings take effect at the next frame (the frame in progress is discarded).
  void setIntegration10ms(uint8_t multiple);
  void setGainCode(uint8_t gainCode);  // also turns auto-gain off
  void setAutoGain(bool enabled);

  uint8_t integration10ms() const { return integration10ms_; }
  uint8_t gainCode() const { return gainCode_; }
  bool autoGain() const { return autoGain_; }

  // Identification read at connection (REVID, AUXID registers).
  uint8_t revisionId() const { return revisionId_; }
  uint8_t auxId() const { return auxId_; }

  // Why the last failure happened, for diagnostics ("i2c", "timeout", ...).
  const char *lastFailure() const;

 private:
  enum class State : uint8_t { kAbsent, kMeasuring };
  enum class Failure : uint8_t { kNone, kI2c, kTimeout, kSettingsLost, kNoId, kLedVerify };

  Failure configure();
  bool startMeasurement(uint32_t nowMs);
  Failure readFrame(uint8_t status2, uint32_t nowMs);
  void adjustGain();
  Event fail(Failure why, uint32_t nowMs);
  void recoverBus();

  bool writeTiming(uint8_t multiple);
  bool writeReg(uint8_t reg, uint8_t value);
  bool writeRegs(uint8_t reg, const uint8_t *data, size_t length);
  bool readReg(uint8_t reg, uint8_t &value);
  bool readRegs(uint8_t reg, uint8_t *data, size_t length);
  bool updateReg(uint8_t reg, uint8_t mask, uint8_t bits);

  TwoWire *wire_ = nullptr;
  int sdaPin_ = -1;
  int sclPin_ = -1;

  State state_ = State::kAbsent;
  Failure lastFailure_ = Failure::kNone;
  uint8_t consecutiveErrors_ = 0;
  bool needConfigure_ = false;  // rewrite every register before the next start
  bool needStart_ = false;      // (re)start the engine on the next update()
  uint32_t lastProbeMs_ = 0;
  uint32_t measureStartMs_ = 0;
  uint32_t lastPollMs_ = 0;

  // Requested settings.
  uint8_t integration10ms_ = 5;
  uint8_t gainCode_ = kGainCodeDefault;
  bool autoGain_ = true;

  // Settings written to the device for the measurement in progress.
  uint8_t activeIntegration10ms_ = 0;
  uint8_t activeGainCode_ = 0;

  uint8_t revisionId_ = 0;
  uint8_t auxId_ = 0;
  Frame frame_ = {};
};

}  // namespace as7343
