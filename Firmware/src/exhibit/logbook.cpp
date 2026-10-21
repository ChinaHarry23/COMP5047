#include "logbook.h"

#include <stdarg.h>
#include <stdio.h>
#include <sys/stat.h>

#include "esp_littlefs.h"

// Storage goes through ESP-IDF's LittleFS VFS and plain C files. (The Arduino
// LittleFS wrapper hides its FS dependency behind an sdkconfig #ifdef that
// PlatformIO's library finder cannot see, so it does not build here.)

namespace logbook {
namespace {

constexpr char     kMount[]      = "/littlefs";
constexpr char     kPartition[]  = "spiffs"; // the data partition in the default 4 MB layout
constexpr char     kPath[]       = "/littlefs/log.csv";
constexpr char     kOldPath[]    = "/littlefs/log.old.csv";
constexpr long     kMaxBytes     = 512L * 1024L;
constexpr size_t   kBufferBytes  = 4096;
constexpr uint32_t kFlushEveryMs = 5000;
constexpr char     kHeader[] =
    "ms,kind,state,present,level,lamp_lux,broad_delta_lux,event,lamp_scot_lux,event_scot_lux,"
    "spent_fmm,event_fmm,budget_fmm,cloud,sky\n";

bool     g_ok = false;
char     g_buf[kBufferBytes];
size_t   g_len = 0;
uint32_t g_last_flush = 0;

long file_size(const char *path)
{
    struct stat st;
    return stat(path, &st) == 0 ? static_cast<long>(st.st_size) : -1;
}

void write_header_if_new()
{
    if (file_size(kPath) < 0) {
        FILE *f = fopen(kPath, "w");
        if (f != nullptr) {
            fputs(kHeader, f);
            fclose(f);
        }
    }
}

void flush()
{
    if (!g_ok || g_len == 0) {
        return;
    }
    if (file_size(kPath) + static_cast<long>(g_len) > kMaxBytes) {
        remove(kOldPath);
        rename(kPath, kOldPath);
        write_header_if_new();
    }
    FILE *f = fopen(kPath, "a");
    if (f != nullptr) {
        fwrite(g_buf, 1, g_len, f);
        fclose(f);
    }
    g_len = 0;
}

void append(const char *line, int n)
{
    if (!g_ok || n <= 0) {
        return;
    }
    const size_t len = static_cast<size_t>(n);
    if (g_len + len > kBufferBytes) {
        flush();
    }
    if (len <= kBufferBytes) {
        memcpy(g_buf + g_len, line, len);
        g_len += len;
    }
}

void print_file(Stream &out, const char *path)
{
    FILE *f = fopen(path, "r");
    if (f == nullptr) {
        return;
    }
    char chunk[128];
    size_t n;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) {
        out.write(reinterpret_cast<const uint8_t *>(chunk), n);
    }
    fclose(f);
}

} // namespace

bool begin()
{
    esp_vfs_littlefs_conf_t conf = {};
    conf.base_path              = kMount;
    conf.partition_label        = kPartition;
    conf.format_if_mount_failed = 1; // first use of a fresh board
    g_ok = esp_vfs_littlefs_register(&conf) == ESP_OK;
    if (g_ok) {
        write_header_if_new();
    }
    return g_ok;
}

void row(uint32_t now_ms, const edge_fsm_t &fsm, float broad_delta_lux, bool event, bool present,
         const char *sky_source)
{
    char line[200];
    const int n = snprintf(line, sizeof line,
                           "%lu,row,%s,%d,%.3f,%.3f,%.3f,%d,%.3f,%.3f,%.3f,%.3f,%.2f,%.2f,%s\n",
                           static_cast<unsigned long>(now_ms), edge_fsm_state_name(fsm.state),
                           present, fsm.level, fsm.rate.lamp_phot_lux, broad_delta_lux,
                           event, fsm.rate.lamp_scot_lux, fsm.rate.event_scot_lux,
                           fsm.tally.spent_fmm, fsm.tally.event_fmm, fsm.budget_fmm,
                           fsm.sky_cloud, sky_source);
    append(line, n < static_cast<int>(sizeof line) ? n : static_cast<int>(sizeof line) - 1);
}

void note(uint32_t now_ms, const char *fmt, ...)
{
    char text[160];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    Serial.printf("[edge] %s\n", text);
    char line[200];
    const int n = snprintf(line, sizeof line, "%lu,note,%s\n", static_cast<unsigned long>(now_ms), text);
    append(line, n < static_cast<int>(sizeof line) ? n : static_cast<int>(sizeof line) - 1);
}

void service(uint32_t now_ms)
{
    if (now_ms - g_last_flush >= kFlushEveryMs) {
        g_last_flush = now_ms;
        flush();
    }
}

void dump(Stream &out)
{
    if (!g_ok) {
        out.println("[edge] logbook unavailable (filesystem did not mount)");
        return;
    }
    flush();
    print_file(out, kOldPath);
    print_file(out, kPath);
    out.println("[edge] end of log");
}

} // namespace logbook
