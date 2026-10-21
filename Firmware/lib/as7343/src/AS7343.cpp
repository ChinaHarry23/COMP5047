// AS7343 driver implementation. Register facts cite ams-OSRAM datasheet
// DS001046 v6-00 (2023-06-07) by section or figure number.
#include "AS7343.h"

#include <algorithm>

namespace as7343 {
namespace {

// ---- Register map (DS001046 section 10) ----
// 0x58-0x66 need CFG0.REG_BANK = 1; 0x80 and above need REG_BANK = 0.
constexpr uint8_t kRegAuxId = 0x58;     // bank 1; AUXID, REVID, ID are consecutive
constexpr uint8_t kRegEnable = 0x80;
constexpr uint8_t kRegAtime = 0x81;
constexpr uint8_t kRegStatus2 = 0x90;
constexpr uint8_t kRegAstatus = 0x94;   // reading it latches DATA_0..17 (0x95..0xB8)
constexpr uint8_t kRegCfg0 = 0xBF;
constexpr uint8_t kRegCfg1 = 0xC6;
constexpr uint8_t kRegLed = 0xCD;
constexpr uint8_t kRegAstepLow = 0xD4;  // 16-bit field: low byte, then high byte
constexpr uint8_t kRegCfg20 = 0xD6;

constexpr uint8_t kPartId = 0x81;             // ID register, Fig. 38
constexpr uint8_t kEnablePon = 0x01;          // ENABLE.PON
constexpr uint8_t kEnableSpEn = 0x02;         // ENABLE.SP_EN
constexpr uint8_t kCfg0Bank1 = 0x10;          // CFG0.REG_BANK (LOW_POWER, WLONG unused: left 0)
constexpr uint8_t kStatus2Avalid = 0x40;      // measurement complete
constexpr uint8_t kStatus2AsatDigital = 0x10;
constexpr uint8_t kStatus2AsatAnalog = 0x08;
constexpr uint8_t kAstatusAsat = 0x80;        // latched data affected by saturation
constexpr uint8_t kCfg1AgainMask = 0x1F;
constexpr uint8_t kCfg20AutoSmuxMask = 0x60;
constexpr uint8_t kCfg20AutoSmux18 = 0x60;    // auto_smux = 3: 18 channels in 3 cycles
constexpr uint8_t kLedActBit = 0x80;
constexpr uint8_t kLedAllOff = 0x00;          // LED_ACT = 0, and the minimum 4 mA drive if ever enabled

// ---- Integration timing ----
// t_int = (ATIME + 1)(ASTEP + 1) x 2.78 us (Eq. 1). With ASTEP = 599 one ATIME
// step is 1.668 ms, so ATIME = 6k - 1 gives k x 10.008 ms; Fig. 10 uses
// ASTEP 599 / ATIME 29 for its 50 ms example. 10.008 ms is within the
// oscillator's own tolerance of 10 ms.
constexpr uint16_t kAstep = 599;
constexpr uint8_t kAtimeStepsPer10ms = 6;
constexpr uint32_t kStepNanoseconds = 2780;
constexpr uint8_t kCyclesPerFrame = 3;        // 18-channel auto-SMUX = three integrations

// ---- Bus, polling and recovery ----
constexpr uint32_t kI2cClockHz = 100000;       // standard mode: margin for the 500 mm Qwiic cable
constexpr uint16_t kI2cTimeoutMs = 20;         // upper bound on any single Wire call
constexpr uint32_t kProbeIntervalMs = 500;     // re-probe period while the sensor is absent
constexpr uint32_t kPollIntervalMs = 5;        // STATUS2 poll period once a frame is due
constexpr uint8_t kMaxConsecutiveErrors = 3;   // ride out an isolated glitch before reporting kLost
constexpr uint32_t kTimeoutMarginMs = 200;     // beyond 2x the nominal frame time (autozero ~15 ms, 10.2.3)
constexpr uint32_t kRecoveryHalfPeriodUs = 5;  // 100 kHz bit-banged recovery clock
constexpr uint8_t kRecoveryClocks = 9;         // NXP UM10204 3.1.16 bus clear

// ---- Auto-gain thresholds, as fractions of digital full scale (filtered channels only) ----
// Above kNearFullScale auto-gain steps down, and only above it is the chip's
// global analog flag taken to concern a filtered channel.
constexpr float kNearFullScale = 0.80f;
constexpr float kAutoGainLow = 0.20f;  // step up below this: one step (at most 2.10x, Fig. 10) lands below 0.42, so no hunting

uint8_t atimeFor(uint8_t multiple) {
  return static_cast<uint8_t>(kAtimeStepsPer10ms * multiple - 1);
}

uint32_t stepsFor(uint8_t atime) {
  return (static_cast<uint32_t>(atime) + 1) * (static_cast<uint32_t>(kAstep) + 1);
}

uint32_t frameMsFor(uint8_t multiple) {
  return kCyclesPerFrame * stepsFor(atimeFor(multiple)) * kStepNanoseconds / 1000000;
}

// Brightest of the 12 filtered channels. The broadband CH4/CH5 are left out:
// in hardware logs CH5 reads 2-26x the clear channel and pins at full scale
// while every filter is far below it (README, "Gain, auto-gain and
// saturation"), so letting it set the gain would starve the amber channels.
uint16_t filteredPeak(const Frame &frame) {
  uint16_t peak = 0;
  for (uint8_t i = 0; i < kNumChannels; ++i) {
    if (isFilteredChannel(i)) peak = std::max(peak, frame.raw[i]);
  }
  return peak;
}

}  // namespace

float gainMultiplier(uint8_t gainCode) {
  gainCode = std::min(gainCode, kGainCodeMax);
  return gainCode == 0 ? 0.5f : static_cast<float>(1u << (gainCode - 1));
}

float basicCounts(const Frame &frame, Channel channel) {
  const float denominator = gainMultiplier(frame.gainCode) * (frame.integrationUs / 1000.0f);
  return denominator > 0.0f ? frame.raw[channel] / denominator : 0.0f;
}

bool AS7343::begin(TwoWire &wire, int sdaPin, int sclPin) {
  wire_ = &wire;
  sdaPin_ = sdaPin;
  sclPin_ = sclPin;
  recoverBus();  // also starts the bus; frees it if an MCU reset interrupted a transfer
  lastProbeMs_ = millis();
  lastFailure_ = configure();
  if (lastFailure_ != Failure::kNone) {
    state_ = State::kAbsent;
    return false;
  }
  state_ = State::kMeasuring;
  needStart_ = true;
  return true;
}

Event AS7343::update(uint32_t nowMs) {
  if (wire_ == nullptr) return Event::kNone;

  if (state_ == State::kAbsent) {
    if (nowMs - lastProbeMs_ < kProbeIntervalMs) return Event::kNone;
    lastProbeMs_ = nowMs;
    recoverBus();
    const Failure why = configure();
    if (why != Failure::kNone) {
      lastFailure_ = why;
      return Event::kNone;
    }
    state_ = State::kMeasuring;
    consecutiveErrors_ = 0;
    needConfigure_ = false;
    needStart_ = true;
    return Event::kConnected;
  }

  if (needConfigure_ || needStart_) {
    if (needConfigure_) {
      const Failure why = configure();
      if (why != Failure::kNone) return fail(why, nowMs);
      needConfigure_ = false;
    }
    if (!startMeasurement(nowMs)) return fail(Failure::kI2c, nowMs);
    needStart_ = false;
    return Event::kNone;
  }

  // No point asking before three integrations can have finished.
  const uint32_t elapsed = nowMs - measureStartMs_;
  const uint32_t frameMs = frameMsFor(activeIntegration10ms_);
  if (elapsed < frameMs || nowMs - lastPollMs_ < kPollIntervalMs) return Event::kNone;
  lastPollMs_ = nowMs;

  uint8_t status2 = 0;
  if (!readReg(kRegStatus2, status2)) return fail(Failure::kI2c, nowMs);
  if ((status2 & kStatus2Avalid) == 0) {
    return elapsed > 2 * frameMs + kTimeoutMarginMs ? fail(Failure::kTimeout, nowMs) : Event::kNone;
  }

  const Failure why = readFrame(status2, nowMs);
  if (why != Failure::kNone) return fail(why, nowMs);
  consecutiveErrors_ = 0;
  if (autoGain_) adjustGain();
  needStart_ = true;  // next update() stops, applies settings, and starts a fresh frame
  return Event::kFrame;
}

void AS7343::restartMeasurement() {
  if (state_ == State::kMeasuring) needStart_ = true;
}

void AS7343::setIntegration10ms(uint8_t multiple) {
  integration10ms_ = std::clamp(multiple, kIntegration10msMin, kIntegration10msMax);
  restartMeasurement();
}

void AS7343::setGainCode(uint8_t gainCode) {
  gainCode_ = std::min(gainCode, kGainCodeMax);
  autoGain_ = false;
  restartMeasurement();
}

void AS7343::setAutoGain(bool enabled) { autoGain_ = enabled; }

const char *AS7343::lastFailure() const {
  switch (lastFailure_) {
    case Failure::kNone: return "none";
    case Failure::kI2c: return "i2c";                     // NACK or bus timeout
    case Failure::kTimeout: return "timeout";             // no AVALID within 2 frame times
    case Failure::kSettingsLost: return "settings-lost";  // sensor reset itself (e.g. brown-out)
    case Failure::kNoId: return "wrong-id";               // something at 0x39 that is not an AS7343
    case Failure::kLedVerify: return "led-verify";        // LED_ACT did not read back as off
  }
  return "?";
}

// Identifies the part and writes every register this driver relies on, so it
// is also the recovery path after a replug or a sensor reset.
AS7343::Failure AS7343::configure() {
  uint8_t id[3] = {};  // AUXID, REVID, ID
  if (!writeReg(kRegCfg0, kCfg0Bank1)) return Failure::kI2c;
  const bool idRead = readRegs(kRegAuxId, id, sizeof id);
  if (!writeReg(kRegCfg0, 0x00) || !idRead) return Failure::kI2c;
  if (id[2] != kPartId) return Failure::kNoId;
  auxId_ = id[0] & 0x0F;
  revisionId_ = id[1] & 0x07;

  // 10.2.1: set PON first, configure while SP_EN = 0, then enable.
  if (!writeReg(kRegEnable, kEnablePon)) return Failure::kI2c;

  // The PIM707's illumination LEDs hang off LDR: keep them off and prove it.
  uint8_t led = kLedActBit;
  if (!writeReg(kRegLed, kLedAllOff) || !readReg(kRegLed, led)) return Failure::kI2c;
  if (led & kLedActBit) return Failure::kLedVerify;

  if (!updateReg(kRegCfg20, kCfg20AutoSmuxMask, kCfg20AutoSmux18)) return Failure::kI2c;
  if (!writeTiming(integration10ms_)) return Failure::kI2c;
  if (!updateReg(kRegCfg1, kCfg1AgainMask, gainCode_)) return Failure::kI2c;
  activeIntegration10ms_ = integration10ms_;
  activeGainCode_ = gainCode_;
  return Failure::kNone;
}

bool AS7343::startMeasurement(uint32_t nowMs) {
  // Stop first: changing configuration while SP_EN = 1 "may result in invalid
  // results" (10.2.1). Stopping also discards any partial frame.
  if (!writeReg(kRegEnable, kEnablePon)) return false;
  if (integration10ms_ != activeIntegration10ms_) {
    if (!writeTiming(integration10ms_)) return false;
    activeIntegration10ms_ = integration10ms_;
  }
  if (gainCode_ != activeGainCode_) {
    if (!updateReg(kRegCfg1, kCfg1AgainMask, gainCode_)) return false;
    activeGainCode_ = gainCode_;
  }
  if (!writeReg(kRegEnable, kEnablePon | kEnableSpEn)) return false;
  measureStartMs_ = nowMs;
  lastPollMs_ = nowMs;
  return true;
}

AS7343::Failure AS7343::readFrame(uint8_t status2, uint32_t nowMs) {
  // One burst from ASTATUS: reading ASTATUS latches all 36 data bytes, so the
  // 18 values belong to the same measurement (10.2.7). Low byte comes first.
  uint8_t buffer[1 + 2 * kNumChannels];
  if (!readRegs(kRegAstatus, buffer, sizeof buffer)) return Failure::kI2c;

  // A sensor that browned out restarts with reset defaults (ATIME = 0) and
  // would otherwise deliver plausible-looking but wrong frames.
  uint8_t atime = 0;
  if (!readReg(kRegAtime, atime)) return Failure::kI2c;
  if (atime != atimeFor(activeIntegration10ms_)) return Failure::kSettingsLost;

  Frame &f = frame_;
  for (uint8_t i = 0; i < kNumChannels; ++i) {
    f.raw[i] = static_cast<uint16_t>(buffer[1 + 2 * i] | (buffer[2 + 2 * i] << 8));
  }
  const uint32_t steps = stepsFor(atime);
  f.gainCode = activeGainCode_;
  f.atime = atime;
  f.astep = kAstep;
  f.integrationUs = steps * kStepNanoseconds / 1000;
  f.fullScale = static_cast<uint16_t>(std::min<uint32_t>(steps, 65535));  // Eq. 2, footnote 1
  f.saturatedCount = saturationCount(steps);
  f.chipAnalogFlag = (status2 & kStatus2AsatAnalog) != 0;
  f.chipDigitalFlag = (status2 & kStatus2AsatDigital) != 0 || (buffer[0] & kAstatusAsat) != 0;
  const uint16_t peak = filteredPeak(f);
  f.digitalSaturation = peak >= f.saturatedCount;
  // The analog flag names no channel, so it is pinned on the filters only when
  // one of them is near full scale as well. This assumes an over-range analog
  // stage drives its channel's count high; bring-up check 3 tests it.
  f.analogSaturation = f.chipAnalogFlag && peak > kNearFullScale * f.fullScale;
  f.tMs = measureStartMs_ + (nowMs - measureStartMs_) / 2;
  return Failure::kNone;
}

// Software auto-gain: the AS7343's built-in AGC covers only the flicker
// channel (AGC_GAIN_MAX, Fig. 34), so spectral gain is managed here. It keeps
// the brightest filtered channel between 20% and 80% of full scale; a
// digitally saturated filter is above 80% by definition.
void AS7343::adjustGain() {
  const uint16_t peak = filteredPeak(frame_);
  uint8_t code = gainCode_;
  if (peak > kNearFullScale * frame_.fullScale) {
    // Analog saturation hides how far over range the light is, so step 4x.
    const uint8_t step = frame_.analogSaturation ? 2 : 1;
    code = code >= kGainCodeMin + step ? code - step : kGainCodeMin;
  } else if (peak < kAutoGainLow * frame_.fullScale && code < kGainCodeMax) {
    ++code;
  }
  gainCode_ = code;  // applied by the next startMeasurement()
}

Event AS7343::fail(Failure why, uint32_t nowMs) {
  lastFailure_ = why;
  needConfigure_ = true;  // the sensor may have reset: rewrite everything before retrying
  needStart_ = true;
  if (++consecutiveErrors_ < kMaxConsecutiveErrors) return Event::kNone;
  state_ = State::kAbsent;
  consecutiveErrors_ = 0;
  lastProbeMs_ = nowMs;
  return Event::kLost;
}

// Bus clear (NXP UM10204 3.1.16): a slave interrupted mid-byte, for example by
// a hot unplug, can hold SDA low indefinitely. Clocking SCL lets it finish the
// byte and release SDA; a STOP then resets every slave's interface. Takes
// about 0.1 ms, so it is cheap enough to run before every probe.
void AS7343::recoverBus() {
  wire_->end();
  pinMode(sdaPin_, INPUT_PULLUP);
  pinMode(sclPin_, OUTPUT_OPEN_DRAIN);
  digitalWrite(sclPin_, HIGH);
  delayMicroseconds(kRecoveryHalfPeriodUs);
  for (uint8_t i = 0; i < kRecoveryClocks && digitalRead(sdaPin_) == LOW; ++i) {
    digitalWrite(sclPin_, LOW);
    delayMicroseconds(kRecoveryHalfPeriodUs);
    digitalWrite(sclPin_, HIGH);
    delayMicroseconds(kRecoveryHalfPeriodUs);
  }
  // STOP: take SDA low while SCL is low (so no START is generated), then
  // release SCL and finally SDA.
  digitalWrite(sclPin_, LOW);
  pinMode(sdaPin_, OUTPUT_OPEN_DRAIN);
  digitalWrite(sdaPin_, LOW);
  delayMicroseconds(kRecoveryHalfPeriodUs);
  digitalWrite(sclPin_, HIGH);
  delayMicroseconds(kRecoveryHalfPeriodUs);
  digitalWrite(sdaPin_, HIGH);
  delayMicroseconds(kRecoveryHalfPeriodUs);

  wire_->begin(sdaPin_, sclPin_, kI2cClockHz);
  wire_->setTimeOut(kI2cTimeoutMs);
}

bool AS7343::writeTiming(uint8_t multiple) {
  const uint8_t astep[2] = {static_cast<uint8_t>(kAstep & 0xFF), static_cast<uint8_t>(kAstep >> 8)};
  return writeReg(kRegAtime, atimeFor(multiple)) && writeRegs(kRegAstepLow, astep, sizeof astep);
}

bool AS7343::writeReg(uint8_t reg, uint8_t value) { return writeRegs(reg, &value, 1); }

bool AS7343::writeRegs(uint8_t reg, const uint8_t *data, size_t length) {
  wire_->beginTransmission(kI2cAddress);
  wire_->write(reg);
  wire_->write(data, length);
  return wire_->endTransmission() == 0;
}

bool AS7343::readReg(uint8_t reg, uint8_t &value) { return readRegs(reg, &value, 1); }

bool AS7343::readRegs(uint8_t reg, uint8_t *data, size_t length) {
  wire_->beginTransmission(kI2cAddress);
  wire_->write(reg);
  if (wire_->endTransmission(false) != 0) return false;  // repeated START, then read
  if (wire_->requestFrom(kI2cAddress, length) != length) return false;
  for (size_t i = 0; i < length; ++i) data[i] = static_cast<uint8_t>(wire_->read());
  return true;
}

// Read-modify-write so reserved bits keep their values (section 10: reserved
// fields "must not be changed").
bool AS7343::updateReg(uint8_t reg, uint8_t mask, uint8_t bits) {
  uint8_t value = 0;
  if (!readReg(reg, value)) return false;
  return writeReg(reg, static_cast<uint8_t>((value & ~mask) | (bits & mask)));
}

}  // namespace as7343
