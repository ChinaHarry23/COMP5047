#include "net.h"

#include <Preferences.h>
#include <string.h>
#include <strings.h>
#include <sys/time.h>
#include <time.h>

#include "edge_config.h"
#include "http_date.h"
#include "esp_crt_bundle.h"
#include "esp_event.h"
#include "esp_http_client.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "freertos/event_groups.h"

#if __has_include("secrets.h")
#include "secrets.h"
#define EDGE_HAVE_SECRETS 1
#else
#define EDGE_HAVE_SECRETS 0
#endif

// The fetch is written on the ESP-IDF Wi-Fi and HTTP client APIs that the
// Arduino core already links. (The Arduino WiFi library's dependency on its
// Network library sits behind an sdkconfig #if that PlatformIO's library
// finder cannot evaluate, so that library does not build in this project.)

namespace net {
namespace {

constexpr char kUrl[] = "https://api.open-meteo.com/v1/forecast"
                        "?latitude=" EDGE_SITE_LAT "&longitude=" EDGE_SITE_LON
                        "&hourly=cloud_cover,temperature_2m&daily=sunset"
                        "&models=ecmwf_aifs025_single&timezone=Australia%2FSydney"
                        "&forecast_days=2&timeformat=unixtime";
constexpr int      kHttpTimeoutMs   = 4000;
constexpr uint32_t kTaskStackBytes  = 12288;      // TLS handshake plus the parser's buffers
constexpr int64_t  kClockValidAfter = 1700000000; // earlier than Nov 2023: SNTP has not answered
constexpr uint8_t  kCacheVersion    = 1;          // bump when edge_sky_cache_t changes
constexpr char     kCacheNs[]       = "edge-sky";

portMUX_TYPE  g_mux  = portMUX_INITIALIZER_UNLOCKED;
volatile bool g_busy = false;
bool          g_done = false;
Result        g_result;

void post(const Result &r)
{
    portENTER_CRITICAL(&g_mux);
    g_result = r;
    g_done   = true;
    g_busy   = false;
    portEXIT_CRITICAL(&g_mux);
}

#if EDGE_HAVE_SECRETS
constexpr EventBits_t kGotIp = BIT0;
EventGroupHandle_t    g_events = nullptr;
esp_netif_t          *g_sta    = nullptr;

void on_event(void *, esp_event_base_t base, int32_t id, void *)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        xEventGroupSetBits(g_events, kGotIp);
    }
}

// Join the hotspot as a station; true once it has given us an address.
bool wifi_up(uint32_t deadline_ms)
{
    // After the first fetch these report "already initialised", which is fine.
    esp_netif_init();
    esp_event_loop_create_default();
    if (g_sta == nullptr) {
        g_sta = esp_netif_create_default_wifi_sta();
    }
    if (g_events == nullptr) {
        g_events = xEventGroupCreate();
    }
    xEventGroupClearBits(g_events, kGotIp);

    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    if (esp_wifi_init(&init) != ESP_OK) {
        return false;
    }
    esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_STA_START, on_event, nullptr);
    esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_event, nullptr);
    wifi_config_t wc = {};
    strlcpy(reinterpret_cast<char *>(wc.sta.ssid), EDGE_WIFI_SSID, sizeof wc.sta.ssid);
    strlcpy(reinterpret_cast<char *>(wc.sta.password), EDGE_WIFI_PASS, sizeof wc.sta.password);
    wc.sta.threshold.authmode = WIFI_AUTH_WPA2_PSK;
    esp_wifi_set_storage(WIFI_STORAGE_RAM);
    esp_wifi_set_mode(WIFI_MODE_STA);
    esp_wifi_set_config(WIFI_IF_STA, &wc);
    if (esp_wifi_start() != ESP_OK) {
        return false;
    }
    const int32_t wait_ms = static_cast<int32_t>(deadline_ms - millis());
    return wait_ms > 0 &&
           (xEventGroupWaitBits(g_events, kGotIp, pdFALSE, pdTRUE, pdMS_TO_TICKS(wait_ms)) & kGotIp);
}

void wifi_down()
{
    esp_event_handler_unregister(WIFI_EVENT, WIFI_EVENT_STA_START, on_event);
    esp_event_handler_unregister(IP_EVENT, IP_EVENT_STA_GOT_IP, on_event);
    esp_wifi_disconnect();
    esp_wifi_stop();
    esp_wifi_deinit();
}

// Response headers: keep the Date header, the server's own clock.
esp_err_t on_http_event(esp_http_client_event_t *evt)
{
    if (evt->event_id == HTTP_EVENT_ON_HEADER && evt->header_key != nullptr &&
        evt->header_value != nullptr && strcasecmp(evt->header_key, "Date") == 0) {
        edge_http_date_parse(evt->header_value, static_cast<int64_t *>(evt->user_data));
    }
    return ESP_OK;
}

// HTTPS GET into body[0..cap). The server certificate is checked against the
// ESP-IDF root CA bundle (the build does not check certificate dates, so this
// works before the clock is set). *server_date gets the response's Date
// header, if any. Returns the HTTP status, or 0 if nothing answered.
int http_get(char *body, size_t cap, size_t *len, int64_t *server_date)
{
    esp_http_client_config_t cfg = {};
    cfg.url               = kUrl;
    cfg.timeout_ms        = kHttpTimeoutMs;
    cfg.crt_bundle_attach = esp_crt_bundle_attach;
    cfg.event_handler     = on_http_event;
    cfg.user_data         = server_date;
    esp_http_client_handle_t client = esp_http_client_init(&cfg);
    if (client == nullptr) {
        return 0;
    }
    int status = 0;
    *len = 0;
    if (esp_http_client_open(client, 0) == ESP_OK && esp_http_client_fetch_headers(client) >= 0) {
        status = esp_http_client_get_status_code(client);
        int n;
        while (*len < cap && (n = esp_http_client_read(client, body + *len, static_cast<int>(cap - *len))) > 0) {
            *len += static_cast<size_t>(n);
        }
    }
    esp_http_client_close(client);
    esp_http_client_cleanup(client);
    return status;
}

void save_cache(const edge_sky_cache_t &sky)
{
    edge_sky_cache_t held;
    if (!edge_sky_cache_replaces(&sky, load_cache(&held) ? &held : nullptr)) {
        return; // never trade a dated cache for an undated one
    }
    Preferences prefs;
    if (prefs.begin(kCacheNs, false)) {
        prefs.putUChar("ver", kCacheVersion);
        prefs.putBytes("sky", &sky, sizeof sky);
        prefs.end();
    }
}

// The ESP32 has no battery-backed clock: after a power-up the time is
// unknown until SNTP or a server's Date header gives it.
void set_clock(int64_t epoch)
{
    const timeval tv = { static_cast<time_t>(epoch), 0 };
    settimeofday(&tv, nullptr);
}

void wait_for_clock(uint32_t deadline_ms)
{
    while (now_epoch() == 0 && static_cast<int32_t>(deadline_ms - millis()) > 0) {
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}

void fetch(Result &r, uint32_t start_ms)
{
    const uint32_t deadline = start_ms + EDGE_FETCH_CAP_MS;
    if (!wifi_up(deadline)) {
        return; // no hotspot, so no SNTP either: the clock stays as it was
    }
    // SNTP is the fallback for dating. lwIP sends its first request after a
    // random 0-5 s delay (CONFIG_LWIP_SNTP_MAXIMUM_STARTUP_DELAY), so it often
    // misses the 5 s cap; the Date header below usually comes first.
    configTime(0, 0, "pool.ntp.org", "time.google.com");

    static char body[EDGE_HTTP_BODY_MAX + 1]; // one byte more than any accepted body
    size_t  len         = 0;
    int64_t server_date = 0;
    r.http_code = http_get(body, sizeof body, &len, &server_date);
    if (now_epoch() == 0 && server_date > kClockValidAfter) {
        set_clock(server_date); // any answer dates itself, error answers included
    }
    // No answer (API down) or no Date header: give SNTP until the cap, so a
    // dated cache can still be used while the radio is up (TP7).
    wait_for_clock(deadline);
    // 400 carries Open-Meteo's {"error": true} object: parse it so the log says why.
    if (r.http_code == 200 || r.http_code == 400) {
        r.parse = len >= sizeof body ? EDGE_FC_ERR_MALFORMED
                                     : edge_sky_cache_from_body(body, len, now_epoch(), &r.sky);
        r.ok = r.parse == EDGE_FC_OK && r.sky.n > 0;
    }
    if (r.ok) {
        save_cache(r.sky);
    }
}
#endif

void fetch_task(void *)
{
    Result r{};
    r.parse = EDGE_FC_ERR_EMPTY;
#if EDGE_HAVE_SECRETS
    fetch(r, millis());
    wifi_down(); // connect, fetch, disconnect: the radio is off between fetches
#endif
    post(r);
    vTaskDelete(nullptr);
}

void post_failure()
{
    Result r{};
    r.parse = EDGE_FC_ERR_EMPTY;
    post(r);
}

} // namespace

void begin()
{
    // Nothing to start: Wi-Fi is brought up only for the length of a fetch.
}

bool has_credentials()
{
    return EDGE_HAVE_SECRETS != 0;
}

bool start_fetch()
{
    if (g_busy) {
        return false;
    }
    g_busy = true;
    if (!has_credentials()) {
        post_failure();
        return true;
    }
    // Core 0 runs the Wi-Fi stack; the control loop stays on core 1.
    if (xTaskCreatePinnedToCore(fetch_task, "edge-fetch", kTaskStackBytes, nullptr, 1, nullptr, 0) != pdPASS) {
        post_failure();
    }
    return true;
}

bool poll(Result *out)
{
    bool done = false;
    portENTER_CRITICAL(&g_mux);
    if (g_done) {
        *out   = g_result;
        g_done = false;
        done   = true;
    }
    portEXIT_CRITICAL(&g_mux);
    return done;
}

bool load_cache(edge_sky_cache_t *out)
{
    Preferences prefs;
    if (!prefs.begin(kCacheNs, true)) {
        return false; // never written
    }
    const bool ok = prefs.getUChar("ver", 0) == kCacheVersion &&
                    prefs.getBytesLength("sky") == sizeof *out &&
                    prefs.getBytes("sky", out, sizeof *out) == sizeof *out && out->n > 0;
    prefs.end();
    return ok;
}

int64_t now_epoch()
{
    const time_t t = time(nullptr);
    return static_cast<int64_t>(t) > kClockValidAfter ? static_cast<int64_t>(t) : 0;
}

} // namespace net
