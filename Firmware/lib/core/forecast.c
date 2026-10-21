#include "forecast.h"

#include <math.h>
#include <string.h>

/* ---- a minimal JSON scanner ---------------------------------------------
 * Only what this one response needs: skip any value, find a member of an
 * object, and read an array of numbers (or nulls). Every step is bounded by
 * `end`, so a truncated body can only ever produce an error. */

#define MAX_DEPTH 16

typedef struct {
    const char *p;
    const char *end;
} cursor_t;

static void skip_ws(cursor_t *c)
{
    while (c->p < c->end && (*c->p == ' ' || *c->p == '\t' || *c->p == '\n' || *c->p == '\r')) {
        c->p++;
    }
}

static edge_fc_status_t skip_string(cursor_t *c)
{
    if (c->p >= c->end) {
        return EDGE_FC_ERR_TRUNCATED;
    }
    if (*c->p != '"') {
        return EDGE_FC_ERR_MALFORMED;
    }
    for (c->p++; c->p < c->end; c->p++) {
        if (*c->p == '\\') {
            if (c->end - c->p < 2) {
                return EDGE_FC_ERR_TRUNCATED;
            }
            c->p++; /* the escaped character is skipped by the loop step */
        } else if (*c->p == '"') {
            c->p++;
            return EDGE_FC_OK;
        }
    }
    return EDGE_FC_ERR_TRUNCATED;
}

static bool is_digit(char ch)
{
    return ch >= '0' && ch <= '9';
}

/* JSON number -> double. Integer parts are exact up to 2^53, which covers
 * epoch seconds. Truncation inside a number is only detectable later (the
 * enclosing array never closes), which the whole-document check catches. */
static edge_fc_status_t parse_number(cursor_t *c, double *out)
{
    double sign = 1.0;
    double v = 0.0;
    if (c->p < c->end && *c->p == '-') {
        sign = -1.0;
        c->p++;
    }
    if (c->p >= c->end) {
        return EDGE_FC_ERR_TRUNCATED;
    }
    if (!is_digit(*c->p)) {
        return EDGE_FC_ERR_MALFORMED;
    }
    while (c->p < c->end && is_digit(*c->p)) {
        v = v * 10.0 + (double)(*c->p++ - '0');
    }
    if (c->p < c->end && *c->p == '.') {
        double scale = 0.1;
        c->p++;
        while (c->p < c->end && is_digit(*c->p)) {
            v += scale * (double)(*c->p++ - '0');
            scale *= 0.1;
        }
    }
    if (c->p < c->end && (*c->p == 'e' || *c->p == 'E')) {
        int exp_sign = 1;
        int e = 0;
        c->p++;
        if (c->p < c->end && (*c->p == '+' || *c->p == '-')) {
            exp_sign = (*c->p++ == '-') ? -1 : 1;
        }
        while (c->p < c->end && is_digit(*c->p) && e < 400) {
            e = e * 10 + (*c->p++ - '0');
        }
        for (; e > 0; e--) {
            v = exp_sign > 0 ? v * 10.0 : v / 10.0;
        }
    }
    *out = sign * v;
    return EDGE_FC_OK;
}

static edge_fc_status_t skip_literal(cursor_t *c, const char *word)
{
    const size_t n = strlen(word);
    if ((size_t)(c->end - c->p) < n) {
        return strncmp(c->p, word, (size_t)(c->end - c->p)) == 0 ? EDGE_FC_ERR_TRUNCATED
                                                                  : EDGE_FC_ERR_MALFORMED;
    }
    if (strncmp(c->p, word, n) != 0) {
        return EDGE_FC_ERR_MALFORMED;
    }
    c->p += n;
    return EDGE_FC_OK;
}

static edge_fc_status_t skip_value(cursor_t *c, int depth);

/* Walk a '{...}' or '[...]' container, skipping its members. */
static edge_fc_status_t skip_container(cursor_t *c, int depth, char close, bool keyed)
{
    if (depth > MAX_DEPTH) {
        return EDGE_FC_ERR_MALFORMED;
    }
    c->p++; /* the opening bracket */
    skip_ws(c);
    if (c->p < c->end && *c->p == close) {
        c->p++;
        return EDGE_FC_OK;
    }
    for (;;) {
        edge_fc_status_t st;
        if (keyed) {
            if ((st = skip_string(c)) != EDGE_FC_OK) {
                return st;
            }
            skip_ws(c);
            if (c->p >= c->end) {
                return EDGE_FC_ERR_TRUNCATED;
            }
            if (*c->p++ != ':') {
                return EDGE_FC_ERR_MALFORMED;
            }
        }
        if ((st = skip_value(c, depth + 1)) != EDGE_FC_OK) {
            return st;
        }
        skip_ws(c);
        if (c->p >= c->end) {
            return EDGE_FC_ERR_TRUNCATED;
        }
        const char ch = *c->p++;
        if (ch == close) {
            return EDGE_FC_OK;
        }
        if (ch != ',') {
            return EDGE_FC_ERR_MALFORMED;
        }
        skip_ws(c);
    }
}

static edge_fc_status_t skip_value(cursor_t *c, int depth)
{
    skip_ws(c);
    if (c->p >= c->end) {
        return EDGE_FC_ERR_TRUNCATED;
    }
    double ignored;
    switch (*c->p) {
    case '{': return skip_container(c, depth, '}', true);
    case '[': return skip_container(c, depth, ']', false);
    case '"': return skip_string(c);
    case 't': return skip_literal(c, "true");
    case 'f': return skip_literal(c, "false");
    case 'n': return skip_literal(c, "null");
    default:  return parse_number(c, &ignored);
    }
}

/* Find `key` among the members of the object at c->p (already validated).
 * On success c->p is left at the member's value. */
static bool find_member(cursor_t obj, const char *key, cursor_t *value)
{
    const size_t klen = strlen(key);
    cursor_t c = obj;
    skip_ws(&c);
    if (c.p >= c.end || *c.p != '{') {
        return false;
    }
    c.p++;
    for (;;) {
        skip_ws(&c);
        if (c.p >= c.end || *c.p != '"') {
            return false;
        }
        const char *name = c.p + 1;
        if (skip_string(&c) != EDGE_FC_OK) {
            return false;
        }
        const size_t nlen = (size_t)(c.p - 1 - name);
        skip_ws(&c);
        c.p++; /* ':' (the document was validated) */
        skip_ws(&c);
        if (nlen == klen && memcmp(name, key, klen) == 0) {
            *value = c;
            return true;
        }
        if (skip_value(&c, 0) != EDGE_FC_OK) {
            return false;
        }
        skip_ws(&c);
        if (c.p >= c.end || *c.p != ',') {
            return false;
        }
        c.p++;
    }
}

/* Read an array of numbers; null becomes NAN. Stores up to `cap` values and
 * reports the total count so the caller can check lengths. */
static edge_fc_status_t read_numbers(cursor_t c, double *dst, int cap, int *total)
{
    *total = 0;
    skip_ws(&c);
    if (c.p >= c.end || *c.p != '[') {
        return EDGE_FC_ERR_MALFORMED;
    }
    c.p++;
    skip_ws(&c);
    if (c.p < c.end && *c.p == ']') {
        return EDGE_FC_OK;
    }
    for (;;) {
        double v = NAN;
        skip_ws(&c);
        if (c.p < c.end && *c.p == 'n') {
            if (skip_literal(&c, "null") != EDGE_FC_OK) {
                return EDGE_FC_ERR_MALFORMED;
            }
        } else if (parse_number(&c, &v) != EDGE_FC_OK) {
            return EDGE_FC_ERR_MALFORMED;
        }
        if (*total < cap) {
            dst[*total] = v;
        }
        (*total)++;
        skip_ws(&c);
        if (c.p >= c.end) {
            return EDGE_FC_ERR_TRUNCATED;
        }
        const char ch = *c.p++;
        if (ch == ']') {
            return EDGE_FC_OK;
        }
        if (ch != ',') {
            return EDGE_FC_ERR_MALFORMED;
        }
    }
}

static edge_fc_status_t read_array_member(cursor_t obj, const char *key, double *dst, int cap,
                                          int *total)
{
    cursor_t v;
    if (!find_member(obj, key, &v)) {
        return EDGE_FC_ERR_MISSING;
    }
    return read_numbers(v, dst, cap, total);
}

/* ---- parse -------------------------------------------------------------- */

edge_fc_status_t edge_forecast_parse(const char *body, size_t len, edge_forecast_t *out)
{
    memset(out, 0, sizeof *out);
    if (body == NULL || len == 0) {
        return EDGE_FC_ERR_EMPTY;
    }
    const cursor_t root = { body, body + len };

    /* The whole body must be one complete value: this is what rejects a
     * response cut off by a dropped connection. */
    cursor_t whole = root;
    skip_ws(&whole);
    if (whole.p >= whole.end) {
        return EDGE_FC_ERR_EMPTY;
    }
    if (*whole.p != '{') {
        return EDGE_FC_ERR_MALFORMED;
    }
    edge_fc_status_t st = skip_value(&whole, 0);
    if (st != EDGE_FC_OK) {
        return st;
    }
    skip_ws(&whole);
    if (whole.p != whole.end && *whole.p != '\0') {
        return EDGE_FC_ERR_MALFORMED;
    }

    cursor_t v;
    if (find_member(root, "error", &v) && skip_literal(&v, "true") == EDGE_FC_OK) {
        return EDGE_FC_ERR_API;
    }

    double num;
    if (!find_member(root, "utc_offset_seconds", &v)) {
        return EDGE_FC_ERR_MISSING;
    }
    if (parse_number(&v, &num) != EDGE_FC_OK) {
        return EDGE_FC_ERR_MALFORMED;
    }
    out->utc_offset_s = (int32_t)num;

    cursor_t hourly, daily;
    if (!find_member(root, "hourly", &hourly) || !find_member(root, "daily", &daily)) {
        return EDGE_FC_ERR_MISSING;
    }

    double times[EDGE_FC_MAX_HOURS];
    double clouds[EDGE_FC_MAX_HOURS];
    double temps[EDGE_FC_MAX_HOURS];
    int n_times = 0, n_clouds = 0, n_temps = 0;
    if ((st = read_array_member(hourly, "time", times, EDGE_FC_MAX_HOURS, &n_times)) != EDGE_FC_OK ||
        (st = read_array_member(hourly, "cloud_cover", clouds, EDGE_FC_MAX_HOURS, &n_clouds)) != EDGE_FC_OK) {
        return st;
    }
    if (n_times != n_clouds) {
        return EDGE_FC_ERR_MISMATCH;
    }
    /* Temperature is optional: it is only logged. */
    st = read_array_member(hourly, "temperature_2m", temps, EDGE_FC_MAX_HOURS, &n_temps);
    if (st != EDGE_FC_OK || n_temps != n_times) {
        n_temps = 0;
    }

    double sunsets[EDGE_FC_MAX_DAYS];
    int n_sunsets = 0;
    if ((st = read_array_member(daily, "sunset", sunsets, EDGE_FC_MAX_DAYS, &n_sunsets)) != EDGE_FC_OK) {
        return st;
    }
    if (n_times == 0 || n_sunsets == 0) {
        return EDGE_FC_ERR_MISSING;
    }

    out->n_hours = n_times < EDGE_FC_MAX_HOURS ? n_times : EDGE_FC_MAX_HOURS;
    for (int i = 0; i < out->n_hours; i++) {
        if (isnan(times[i])) {
            return EDGE_FC_ERR_MALFORMED; /* a sample with no time cannot be placed */
        }
        out->hour_t[i] = (int64_t)times[i];
        double pct = clouds[i];
        if (!isnan(pct)) {
            pct = pct < 0.0 ? 0.0 : (pct > 100.0 ? 100.0 : pct);
        }
        out->cloud[i]  = isnan(pct) ? NAN : (float)(pct / 100.0);
        out->temp_c[i] = i < n_temps && !isnan(temps[i]) ? (float)temps[i] : NAN;
    }
    out->n_days = n_sunsets < EDGE_FC_MAX_DAYS ? n_sunsets : EDGE_FC_MAX_DAYS;
    for (int i = 0; i < out->n_days; i++) {
        if (isnan(sunsets[i])) {
            return EDGE_FC_ERR_MALFORMED;
        }
        out->sunset[i] = (int64_t)sunsets[i];
    }
    return EDGE_FC_OK;
}

/* ---- tonight ------------------------------------------------------------ */

static int64_t floor_div(int64_t a, int64_t b)
{
    const int64_t q = a / b;
    return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
}

int edge_forecast_nights(const edge_forecast_t *fc, edge_night_t *out, int max)
{
    const int64_t off = fc->utc_offset_s;
    int n = 0;
    for (int d = 0; d < fc->n_days && n < max; d++) {
        const int64_t sunset = fc->sunset[d];
        const int64_t midnight = (floor_div(sunset + off, 86400) + 1) * 86400 - off;
        /* Include the sample at the top of the sunset hour, so a 19:01 sunset
         * still counts the 19:00 sample: the evening's first hour. */
        const int64_t start = floor_div(sunset + off, 3600) * 3600 - off;
        double cloud_sum = 0.0, temp_sum = 0.0;
        int clouds = 0, temps = 0;
        for (int h = 0; h < fc->n_hours; h++) {
            if (fc->hour_t[h] < start || fc->hour_t[h] >= midnight) {
                continue;
            }
            if (!isnan(fc->cloud[h])) {
                cloud_sum += fc->cloud[h];
                clouds++;
            }
            if (!isnan(fc->temp_c[h])) {
                temp_sum += fc->temp_c[h];
                temps++;
            }
        }
        out[n].sunset   = sunset;
        out[n].midnight = midnight;
        out[n].cloud    = clouds > 0 ? (float)(cloud_sum / clouds) : NAN;
        out[n].temp_c   = temps > 0 ? (float)(temp_sum / temps) : NAN;
        n++;
    }
    return n;
}

edge_fc_status_t edge_sky_cache_from_body(const char *body, size_t len, int64_t fetch_epoch,
                                          edge_sky_cache_t *out)
{
    edge_forecast_t fc; /* ~0.9 KB of stack; callers run on 8 KB+ task stacks */
    memset(out, 0, sizeof *out);
    const edge_fc_status_t st = edge_forecast_parse(body, len, &fc);
    if (st != EDGE_FC_OK) {
        return st;
    }
    out->fetch_epoch = fetch_epoch;
    out->n = edge_forecast_nights(&fc, out->night, EDGE_FC_MAX_DAYS);
    return EDGE_FC_OK;
}

bool edge_sky_cache_replaces(const edge_sky_cache_t *incoming, const edge_sky_cache_t *held)
{
    if (incoming == NULL || incoming->n <= 0) {
        return false;
    }
    if (held == NULL || held->n <= 0 || held->fetch_epoch == 0) {
        return true;
    }
    return incoming->fetch_epoch >= held->fetch_epoch; /* an undated one (0) loses */
}

/* ---- budget ------------------------------------------------------------- */

float edge_budget_for_cloud(float cloud)
{
    if (isnan(cloud) || cloud > 1.0f) { /* no evidence of a clear sky */
        cloud = 1.0f;
    } else if (cloud < 0.0f) {
        cloud = 0.0f;
    }
    return EDGE_B_MAX_FMM / (1.0f + (EDGE_CLOUD_AMPLIFICATION - 1.0f) * cloud);
}

static edge_sky_t protective(edge_sky_why_t why, int64_t age_s)
{
    edge_sky_t s = { EDGE_SKY_PROTECTIVE, why, 1.0f, edge_budget_for_cloud(1.0f), 0, age_s, NAN };
    return s;
}

static edge_sky_t from_night(edge_sky_source_t src, const edge_night_t *n, int64_t age_s)
{
    if (isnan(n->cloud)) {
        return protective(EDGE_SKY_WHY_NO_TONIGHT, age_s);
    }
    float c = n->cloud < 0.0f ? 0.0f : (n->cloud > 1.0f ? 1.0f : n->cloud);
    edge_sky_t s = { src, EDGE_SKY_WHY_OK, c, edge_budget_for_cloud(c), n->sunset, age_s, n->temp_c };
    return s;
}

/* Tonight is the first evening whose local midnight is still ahead. Without
 * a clock, the response's first evening is tonight: Open-Meteo starts the
 * forecast at the site's local midnight of the current day. */
static const edge_night_t *tonight(const edge_sky_cache_t *c, int64_t now)
{
    if (c->n <= 0) {
        return NULL;
    }
    if (now == 0) {
        return &c->night[0];
    }
    for (int i = 0; i < c->n; i++) {
        if (now < c->night[i].midnight) {
            return &c->night[i];
        }
    }
    return NULL;
}

edge_sky_t edge_sky_live(const edge_sky_cache_t *live, int64_t now)
{
    const edge_night_t *n = live != NULL ? tonight(live, now) : NULL;
    if (n == NULL) {
        return protective(live != NULL && live->n > 0 ? EDGE_SKY_WHY_NO_TONIGHT : EDGE_SKY_WHY_NO_DATA, -1);
    }
    return from_night(EDGE_SKY_LIVE, n, 0);
}

edge_sky_t edge_sky_cached(const edge_sky_cache_t *cache, int64_t now)
{
    if (cache == NULL || cache->n <= 0) {
        return protective(EDGE_SKY_WHY_NO_DATA, -1);
    }
    if (cache->fetch_epoch == 0 || now == 0 || now < cache->fetch_epoch) {
        return protective(EDGE_SKY_WHY_AGE_UNKNOWN, -1);
    }
    const int64_t age = now - cache->fetch_epoch;
    if (age > EDGE_FORECAST_STALE_S) {
        return protective(EDGE_SKY_WHY_STALE, age);
    }
    const edge_night_t *n = tonight(cache, now);
    if (n == NULL) {
        return protective(EDGE_SKY_WHY_NO_TONIGHT, age);
    }
    return from_night(EDGE_SKY_CACHED, n, age);
}

edge_sky_t edge_sky_recorded(edge_recorded_night_t rec)
{
    if (rec.cache == NULL || rec.night < 0 || rec.night >= rec.cache->n) {
        return protective(EDGE_SKY_WHY_NO_DATA, -1);
    }
    return from_night(EDGE_SKY_RECORDED, &rec.cache->night[rec.night], -1);
}

/* ---- names for logs ----------------------------------------------------- */

const char *edge_fc_status_name(edge_fc_status_t s)
{
    static const char *const names[] = { "ok", "empty", "truncated", "malformed",
                                         "api-error", "missing-field", "length-mismatch" };
    return (unsigned)s < sizeof names / sizeof names[0] ? names[s] : "?";
}

const char *edge_sky_source_name(edge_sky_source_t s)
{
    static const char *const names[] = { "LIVE", "CACHED", "RECORDED", "PROTECTIVE" };
    return (unsigned)s < sizeof names / sizeof names[0] ? names[s] : "?";
}

const char *edge_sky_why_name(edge_sky_why_t w)
{
    static const char *const names[] = { "ok", "no-data", "stale", "age-unknown", "no-tonight" };
    return (unsigned)w < sizeof names / sizeof names[0] ? names[w] : "?";
}
