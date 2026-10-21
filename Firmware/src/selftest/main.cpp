// EDGE circuit self-test (for whoever solders the protoboard).
// Checks: I2C bus, MPU-6050 (0x68/0x69), BH1750 (0x23/0x5C), LED on D3 (GPIO26),
// button on D2 (GPIO25, to GND, INPUT_PULLUP). Prints PASS/FAIL + a fix hint.
// Serial 115200. Type 'r' + Enter in the monitor to rerun.
#include <Arduino.h>
#include <Wire.h>

static const int SDA_PIN = 21, SCL_PIN = 22, BTN = 25, LED = 26;
static uint8_t mpu = 0, bh = 0;
static int passes = 0, fails = 0;

static bool present(uint8_t a) { Wire.beginTransmission(a); return Wire.endTransmission() == 0; }
static void result(const char* name, bool ok, const char* hint) {
  Serial.printf("  [%s] %s\n", ok ? "PASS" : "FAIL", name);
  if (!ok) Serial.printf("         hint: %s\n", hint);
  ok ? passes++ : fails++;
}
static uint8_t rd8(uint8_t dev, uint8_t reg) {
  Wire.beginTransmission(dev); Wire.write(reg); Wire.endTransmission(false);
  Wire.requestFrom(dev, (uint8_t)1); return Wire.available() ? Wire.read() : 0xFF;
}
static bool mpuAccel(float& g) {
  Wire.beginTransmission(mpu); Wire.write(0x3B); if (Wire.endTransmission(false)) return false;
  if (Wire.requestFrom(mpu, (uint8_t)6) != 6) return false;
  float s = 0; for (int i = 0; i < 3; i++) { int16_t v = (Wire.read() << 8) | Wire.read(); float a = v / 16384.0f; s += a * a; }
  g = sqrtf(s); return true;
}
static float bhLux() {
  if (Wire.requestFrom(bh, (uint8_t)2) != 2) return -1;
  uint16_t raw = (Wire.read() << 8) | Wire.read(); return raw / 1.2f;
}

static void runTests() {
  passes = fails = 0; mpu = bh = 0;
  Serial.println("\n================ EDGE CIRCUIT SELF-TEST ================");
  Serial.println("1) I2C scan (SDA=GPIO21, SCL=GPIO22):");
  int n = 0;
  for (uint8_t a = 1; a < 127; a++) if (present(a)) { Serial.printf("     found 0x%02X\n", a); n++; }
  result("I2C bus has devices", n > 0, "nothing on I2C: check 3V3 + GND to both sensors, SDA->21, SCL->22, cold solder joints");

  Serial.println("2) MPU-6050:");
  mpu = present(0x68) ? 0x68 : present(0x69) ? 0x69 : 0;
  result("MPU-6050 found (0x68/0x69)", mpu, "check MPU VCC/GND/SDA/SCL solder; swapped SDA/SCL is common");
  if (mpu) {
    Wire.beginTransmission(mpu); Wire.write(0x6B); Wire.write(0); Wire.endTransmission(); delay(100);
    Serial.printf("     WHO_AM_I = 0x%02X (0x68 genuine, 0x70/0x72/0x98 clone OK)\n", rd8(mpu, 0x75));
    float g = 0; bool ok = mpuAccel(g);
    Serial.printf("     accel magnitude = %.2f g (expect ~1.00 when still)\n", g);
    result("MPU reads ~1 g", ok && g > 0.8f && g < 1.2f, "readings bad: loose joint or board not flat/still");
  }

  Serial.println("3) BH1750 light sensor:");
  bh = present(0x23) ? 0x23 : present(0x5C) ? 0x5C : 0;
  result("BH1750 found (0x23/0x5C)", bh, "check BH1750 VCC/GND/SDA/SCL solder");
  if (bh) {
    Wire.beginTransmission(bh); Wire.write(0x01); Wire.endTransmission();
    Wire.beginTransmission(bh); Wire.write(0x10); Wire.endTransmission(); delay(200);
    float bright = bhLux(); Serial.printf("     lux now = %.1f\n", bright);
    Serial.println("     >> COVER the light sensor with your finger (5 s)...");
    float dark = bright; uint32_t t0 = millis();
    while (millis() - t0 < 5000) { float l = bhLux(); if (l >= 0 && l < dark) dark = l; delay(150); }
    Serial.printf("     darkest = %.1f lux\n", dark);
    result("BH1750 responds to covering", bright >= 0 && dark < bright * 0.5f + 1, "lux didn't drop: sensor blocked, wrong side up, or already dark room");
  }

  Serial.println("4) LED on D3 (GPIO26): blinking 3x, then fading...");
  pinMode(LED, OUTPUT);
  for (int i = 0; i < 3; i++) { digitalWrite(LED, HIGH); delay(300); digitalWrite(LED, LOW); delay(300); }
  for (int d = 0; d <= 255; d += 5) { analogWrite(LED, d); delay(8); }
  for (int d = 255; d >= 0; d -= 5) { analogWrite(LED, d); delay(8); }
  digitalWrite(LED, LOW);
  Serial.println("     (can't self-check: did you SEE it blink? If not, LED is backwards or resistor joint open)");

  Serial.println("5) Button on D2 (GPIO25):");
  pinMode(BTN, INPUT_PULLUP); delay(20);
  bool idleHigh = digitalRead(BTN) == HIGH;
  result("button idle reads HIGH", idleHigh, "reads pressed while untouched: button shorted to GND or wrong legs used (try diagonal legs)");
  Serial.println("     >> PRESS the button within 10 s...");
  bool pressed = false; uint32_t t0 = millis();
  while (millis() - t0 < 10000) { if (digitalRead(BTN) == LOW) { pressed = true; break; } delay(10); }
  if (pressed) { digitalWrite(LED, HIGH); delay(300); digitalWrite(LED, LOW); }
  result("button press detected", pressed, "no press seen: button not to GND / not on D2, or using same-side legs");

  Serial.println("--------------------------------------------------------");
  Serial.printf("RESULT: %d PASS, %d FAIL  ->  %s\n", passes, fails, fails ? "FIX THE FAILS ABOVE" : "CIRCUIT OK");
  Serial.println("Live view below (button, lux, g). Type r + Enter to rerun.");
  Serial.println("========================================================\n");
}

void setup() {
  Serial.begin(115200); delay(1500);
  Wire.begin(SDA_PIN, SCL_PIN, 100000);
  runTests();
}

void loop() {
  if (Serial.available() && tolower(Serial.read()) == 'r') { while (Serial.available()) Serial.read(); runTests(); }
  static uint32_t last = 0;
  if (millis() - last > 500) {
    last = millis(); float g = -1, l = -1;
    if (mpu) mpuAccel(g);
    if (bh) l = bhLux();
    bool p = digitalRead(BTN) == LOW; digitalWrite(LED, p);
    Serial.printf("button=%s  lux=%.1f  g=%.2f\n", p ? "PRESSED" : "up", l, g);
  }
}
