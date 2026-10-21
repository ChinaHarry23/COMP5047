// EDGE tag firmware: MPU-6050 + BH1750 -> UDP to Unity port 1234.
//
// Packets (ASCII, one per datagram, tag -> Unity):
//   ORI <roll_deg> <pitch_deg> <yaw_deg> <gx_dps> <gy_dps> <gz_dps> <btn> <seq>   ~100 Hz, streaming
//   LUX <lux>                                                                   ~10 Hz, streaming
//   HELLO EDGE-TAG paused <seq>                                                 1 Hz, paused
// Unity's parser also accepts the legacy 4-field "ORI r p y". <btn>=1 for 300 ms after a
// long press = "zero" request (Unity EdgeTagInput re-captures neutral).
// Roll/pitch: accel+gyro complementary filter; yaw: integrated gyro Z (drifts; no magnetometer).
// Unity -> tag:  "VIB <0..1>"  -> LED (D3) PWM duty; off if Unity silent 1.5 s.
// Button (D2): short press = start/stop streaming, long press (>=1 s) = zero.
// LED patterns: boot 3 quick blinks; start = 1 blink; stop = 2 blinks; zero = 1 long;
//   paused = 40 ms flash every 2 s; streaming = VIB brightness.
// Serial (115200): status + 10 Hz debug; 's' = I2C scan, 'g' = re-cal gyro, 't' = toggle stream.
// Wi-Fi via ESP-IDF directly (the Arduino WiFi library does not build here, see net.cpp).
#include <Arduino.h>
#include <Wire.h>
#include <math.h>
#include <string.h>

#include "esp_event.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "lwip/sockets.h"
#include "nvs_flash.h"
#include "tag_config.h"

#if __has_include("secrets.h")
#include "secrets.h"
#define TAG_HAVE_SECRETS 1
#else
#define TAG_HAVE_SECRETS 0
#endif
#ifndef EDGE_UNITY_HOST
#define EDGE_UNITY_HOST "255.255.255.255"
#endif

namespace {

uint8_t  g_mpu = 0, g_bh = 0;
float    g_roll = 0, g_pitch = 0, g_yaw = 0;
float    g_bias[3] = {0, 0, 0};
float    g_g[3] = {0, 0, 0};
float    g_lux = -1;
int      g_sock = -1;
sockaddr_in g_dest{};
volatile bool g_wifiUp = false;
bool     g_streaming = TAG_STREAM_AT_BOOT;
uint32_t g_seq = 0, g_sent = 0;
uint32_t g_lastVibMs = 0, g_zeroUntil = 0;
float    g_vib = 0;

// ---- LED (D3) --------------------------------------------------------------
// Blink pattern overrides VIB brightness while active.
uint8_t  g_blinks = 0;
uint16_t g_blinkOn = 0;
uint32_t g_blinkT = 0;

void ledRaw(float duty01) {
    duty01 = constrain(duty01, 0.f, 1.f) * TAG_MOTOR_MAX_DUTY;
    ledcWrite(TAG_PIN_MOTOR, (uint32_t)(duty01 * 2047));
}
void blink(uint8_t n, uint16_t onMs) { g_blinks = n; g_blinkOn = onMs; g_blinkT = millis(); }

void ledTick() {
    uint32_t now = millis();
    if (g_blinks) {
        uint32_t ph = (now - g_blinkT) % (g_blinkOn + 150u);
        uint32_t idx = (now - g_blinkT) / (g_blinkOn + 150u);
        if (idx >= g_blinks) { g_blinks = 0; }
        else { ledRaw(ph < g_blinkOn ? 1.f : 0.f); return; }
    }
    if (g_lastVibMs && now - g_lastVibMs > 1500) { g_vib = 0; g_lastVibMs = 0; }  // Unity gone
    if (!g_streaming) { ledRaw(now % 2000 < 40 ? 0.5f : 0.f); return; }
    ledRaw(g_vib);
}

// ---- I2C -------------------------------------------------------------------
void i2cScan() {
    Serial.println("[TAG] I2C scan on SDA=21 SCL=22:");
    int n = 0;
    for (uint8_t a = 1; a < 127; ++a) {
        Wire.beginTransmission(a);
        if (Wire.endTransmission() == 0) {
            const char *name = a == 0x68 || a == 0x69 ? "  (MPU-6050)"
                             : a == 0x23 || a == 0x5C ? "  (BH1750)"
                             : a == 0x39 ? "  (AS7343)" : "";
            Serial.printf("  found 0x%02X%s\n", a, name);
            ++n;
        }
    }
    Serial.printf("[TAG] %d device(s) (expected 0x23 + 0x68)\n", n);
}

bool present(uint8_t a) { Wire.beginTransmission(a); return Wire.endTransmission() == 0; }

bool wr(uint8_t reg, uint8_t v) {
    Wire.beginTransmission(g_mpu);
    Wire.write(reg);
    Wire.write(v);
    return Wire.endTransmission() == 0;
}

bool rd(uint8_t reg, uint8_t *buf, size_t n) {
    Wire.beginTransmission(g_mpu);
    Wire.write(reg);
    if (Wire.endTransmission(false) != 0) return false;
    if (Wire.requestFrom(g_mpu, (uint8_t)n) != n) return false;
    for (size_t i = 0; i < n; ++i) buf[i] = Wire.read();
    return true;
}

bool mpuInit() {
    g_mpu = 0;
    if (present(TAG_MPU_ADDR_LOW)) g_mpu = TAG_MPU_ADDR_LOW;
    else if (present(TAG_MPU_ADDR_HIGH)) g_mpu = TAG_MPU_ADDR_HIGH;
    if (!g_mpu) return false;
    uint8_t who = 0;
    rd(0x75, &who, 1);
    Serial.printf("[TAG] MPU at 0x%02X WHO_AM_I=0x%02X (0x68 genuine; 0x70/0x72/0x98 = clone, usually OK)\n", g_mpu, who);
    return wr(0x6B, 0x01)        // wake, PLL on gyro X
        && wr(0x1A, 0x03)        // DLPF ~44 Hz
        && wr(0x19, 0x04)        // 200 Hz sample rate
        && wr(0x1B, 0x08)        // gyro +-500 dps (65.5 LSB/dps)
        && wr(0x1C, 0x08);       // accel +-4 g (8192 LSB/g)
}

bool bhInit() {
    g_bh = 0;
    if (present(TAG_BH1750_ADDR_LOW)) g_bh = TAG_BH1750_ADDR_LOW;
    else if (present(TAG_BH1750_ADDR_HIGH)) g_bh = TAG_BH1750_ADDR_HIGH;
    if (!g_bh) { Serial.println("[TAG] BH1750 not found (0x23/0x5C) - LUX disabled"); return false; }
    Wire.beginTransmission(g_bh); Wire.write(0x01); Wire.endTransmission();  // power on
    Wire.beginTransmission(g_bh); Wire.write(0x10); Wire.endTransmission();  // continuous H-res, 1 lx, 120 ms
    Serial.printf("[TAG] BH1750 at 0x%02X, continuous high-res\n", g_bh);
    return true;
}

void bhRead() {
    if (!g_bh) return;
    if (Wire.requestFrom(g_bh, (uint8_t)2) != 2) { g_lux = -1; return; }
    uint16_t raw = (Wire.read() << 8) | Wire.read();
    g_lux = raw / 1.2f;
}

bool readImu(float a[3], float g[3]) {
    uint8_t b[14];
    if (!rd(0x3B, b, 14)) return false;
    auto s = [&](int i) { return (int16_t)((b[i] << 8) | b[i + 1]); };
    for (int i = 0; i < 3; ++i) {
        a[i] = s(i * 2) / 8192.0f;
        g[i] = s(8 + i * 2) / 65.5f;
    }
    return true;
}

void calibrateGyro() {
    Serial.println("[TAG] gyro calibration: keep still...");
    float sum[3] = {0, 0, 0}, a[3], g[3];
    int ok = 0;
    for (int i = 0; i < TAG_GYRO_CAL_SAMPLES; ++i) {
        if (readImu(a, g)) { for (int k = 0; k < 3; ++k) sum[k] += g[k]; ++ok; }
        delay(2);
    }
    if (ok) for (int k = 0; k < 3; ++k) g_bias[k] = sum[k] / ok;
    Serial.printf("[TAG] gyro bias %.2f %.2f %.2f dps\n", g_bias[0], g_bias[1], g_bias[2]);
    readImu(a, g);
    g_roll = atan2f(a[1], a[2]) * RAD_TO_DEG;
    g_pitch = atan2f(-a[0], sqrtf(a[1] * a[1] + a[2] * a[2])) * RAD_TO_DEG;
    g_yaw = 0;
}

// ---- network ---------------------------------------------------------------
#if TAG_HAVE_SECRETS
void onWifi(void *, esp_event_base_t base, int32_t id, void *) {
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) esp_wifi_connect();
    else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) { g_wifiUp = false; esp_wifi_connect(); }
    else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) g_wifiUp = true;
}
#endif

void wifiStart() {
#if TAG_HAVE_SECRETS
    nvs_flash_init();
    esp_netif_init();
    esp_event_loop_create_default();
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    esp_wifi_init(&init);
    esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, onWifi, nullptr);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, onWifi, nullptr);
    wifi_config_t wc = {};
    strlcpy(reinterpret_cast<char *>(wc.sta.ssid), EDGE_WIFI_SSID, sizeof wc.sta.ssid);
    strlcpy(reinterpret_cast<char *>(wc.sta.password), EDGE_WIFI_PASS, sizeof wc.sta.password);
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    esp_wifi_set_mode(WIFI_MODE_STA);
    esp_wifi_set_config(WIFI_IF_STA, &wc);
    esp_wifi_start();
    esp_wifi_set_ps(WIFI_PS_NONE);   // lowest latency
    Serial.printf("[TAG] Wi-Fi joining '%s', sending to %s:%d\n", EDGE_WIFI_SSID, EDGE_UNITY_HOST, TAG_UDP_PORT);
#else
    Serial.println("[TAG] no include/secrets.h: Wi-Fi off, Serial debug only");
#endif
}

void sockOpen() {
    g_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    int yes = 1;
    setsockopt(g_sock, SOL_SOCKET, SO_BROADCAST, &yes, sizeof yes);
    sockaddr_in me{};
    me.sin_family = AF_INET;
    me.sin_port = htons(TAG_UDP_PORT);
    me.sin_addr.s_addr = htonl(INADDR_ANY);
    bind(g_sock, (sockaddr *)&me, sizeof me);
    fcntl(g_sock, F_SETFL, O_NONBLOCK);
    g_dest.sin_family = AF_INET;
    g_dest.sin_port = htons(TAG_UDP_PORT);
    g_dest.sin_addr.s_addr = inet_addr(EDGE_UNITY_HOST);
}

void udpSend(const char *s, int n) {
    if (!g_wifiUp || g_sock < 0) return;
    if (sendto(g_sock, s, n, 0, (sockaddr *)&g_dest, sizeof g_dest) > 0) ++g_sent;
}

void pollRx() {
    if (g_sock < 0) return;
    char buf[64];
    int n;
    while ((n = recv(g_sock, buf, sizeof buf - 1, 0)) > 0) {
        buf[n] = 0;
        float v;
        if (sscanf(buf, "VIB %f", &v) == 1) { g_vib = v; g_lastVibMs = millis(); }
    }
}

// ---- button (D2) -----------------------------------------------------------
void setStreaming(bool on) {
    g_streaming = on;
    blink(on ? 1 : 2, 120);
    Serial.printf("[TAG] streaming %s\n", on ? "ON" : "OFF (HELLO 1 Hz)");
}

void buttonTick() {
    static bool stable = false, lastRaw = false, longFired = false;
    static uint32_t changeT = 0, downT = 0;
    bool raw = digitalRead(TAG_PIN_BUTTON) == LOW;
    uint32_t now = millis();
    if (raw != lastRaw) { lastRaw = raw; changeT = now; }
    if (now - changeT < TAG_DEBOUNCE_MS || raw == stable) {
        if (stable && !longFired && now - downT >= TAG_LONG_PRESS_MS) {
            longFired = true;
            g_zeroUntil = now + 300;          // btn=1 in ORI for 300 ms -> Unity zero
            blink(1, 500);
            Serial.println("[TAG] long press: zero request sent");
        }
        return;
    }
    stable = raw;
    if (stable) { downT = now; longFired = false; }
    else if (!longFired) setStreaming(!g_streaming);   // short press released
}

}  // namespace

void setup() {
    pinMode(PIN_ONBOARD_LED, OUTPUT);
    digitalWrite(PIN_ONBOARD_LED, LOW);   // no stray light
    pinMode(TAG_PIN_BUTTON, INPUT_PULLUP);
    ledcAttach(TAG_PIN_MOTOR, 20000, 11);
    ledRaw(0);
    Serial.begin(115200);
    delay(300);
    Serial.println("\n[TAG] EDGE tag firmware (D2=GPIO25 button, D3=GPIO26 LED)");
    for (int i = 0; i < 3; ++i) { ledRaw(1); delay(80); ledRaw(0); delay(80); }   // boot
    Wire.begin(PIN_I2C_SDA, PIN_I2C_SCL, TAG_I2C_HZ);
    i2cScan();
#ifdef TAG_I2C_SCAN_ONLY
    for (;;) { delay(3000); i2cScan(); }
#endif
    if (!mpuInit()) Serial.println("[TAG] MPU-6050 NOT FOUND - check 3V3/GND/SDA21/SCL22; retrying in loop");
    else calibrateGyro();
    bhInit();
    wifiStart();
    sockOpen();
    Serial.printf("[TAG] streaming %s at boot\n", g_streaming ? "ON" : "OFF");
}

void loop() {
    static uint32_t last = micros(), lastDbg = 0, lastRetry = 0, lastLux = 0, lastLuxRead = 0, lastHello = 0;
    uint32_t ms = millis();
    if (Serial.available()) {
        int c = Serial.read();
        if (c == 's') i2cScan();
        if (c == 'g' && g_mpu) calibrateGyro();
        if (c == 't') setStreaming(!g_streaming);
    }
    buttonTick();
    pollRx();
    ledTick();

    if (g_bh && ms - lastLuxRead >= 120) { lastLuxRead = ms; bhRead(); }   // H-res conversion 120 ms

    if (!g_streaming) {
        if (ms - lastHello >= 1000 / TAG_HELLO_HZ) {
            lastHello = ms;
            char out[48];
            int n = snprintf(out, sizeof out, "HELLO EDGE-TAG paused %lu", (unsigned long)g_seq);
            udpSend(out, n);
        }
    } else if (g_bh && g_lux >= 0 && ms - lastLux >= 1000 / TAG_LUX_HZ) {
        lastLux = ms;
        char out[32];
        int n = snprintf(out, sizeof out, "LUX %.1f", g_lux);
        udpSend(out, n);
    }

    if (!g_mpu) {
        if (ms - lastRetry > 2000) { lastRetry = ms; if (mpuInit()) calibrateGyro(); if (!g_bh) bhInit(); }
        delay(5);
        return;
    }
    uint32_t now = micros();
    const uint32_t period = 1000000UL / TAG_SEND_HZ;
    if (now - last < period) return;
    float dt = (now - last) * 1e-6f;
    last = now;

    float a[3], g[3];
    if (!readImu(a, g)) { Serial.println("[TAG] I2C read failed"); Wire.begin(PIN_I2C_SDA, PIN_I2C_SCL, TAG_I2C_HZ); return; }
    for (int k = 0; k < 3; ++k) g_g[k] = g[k] - g_bias[k];
    float accRoll = atan2f(a[1], a[2]) * RAD_TO_DEG;
    float accPitch = atan2f(-a[0], sqrtf(a[1] * a[1] + a[2] * a[2])) * RAD_TO_DEG;
    // unwrap roll near +-180 so the filter never averages across the seam
    float r = g_roll + g_g[0] * dt;
    float d = accRoll - r;
    if (d > 180) accRoll -= 360; else if (d < -180) accRoll += 360;
    g_roll = TAG_COMP_ALPHA * r + (1 - TAG_COMP_ALPHA) * accRoll;
    if (g_roll > 180) g_roll -= 360; else if (g_roll < -180) g_roll += 360;
    g_pitch = TAG_COMP_ALPHA * (g_pitch + g_g[1] * dt) + (1 - TAG_COMP_ALPHA) * accPitch;
    g_yaw += g_g[2] * dt;
    if (g_yaw > 180) g_yaw -= 360; else if (g_yaw < -180) g_yaw += 360;

    int btn = (int32_t)(g_zeroUntil - ms) > 0 ? 1 : 0;
    if (g_streaming) {
        char out[112];
        int n = snprintf(out, sizeof out, "ORI %.2f %.2f %.2f %.1f %.1f %.1f %d %lu",
                         g_roll, g_pitch, g_yaw, g_g[0], g_g[1], g_g[2], btn, (unsigned long)g_seq++);
        udpSend(out, n);
    }
#if TAG_SERIAL_DEBUG_HZ > 0
    if (ms - lastDbg >= 1000 / TAG_SERIAL_DEBUG_HZ) {
        lastDbg = ms;
        Serial.printf("r=%7.2f p=%7.2f y=%7.2f g=%6.1f %6.1f %6.1f lux=%7.1f stream=%d wifi=%d sent=%lu vib=%.2f\n",
                      g_roll, g_pitch, g_yaw, g_g[0], g_g[1], g_g[2], g_lux, (int)g_streaming,
                      (int)g_wifiUp, (unsigned long)g_sent, g_vib);
    }
#endif
}
