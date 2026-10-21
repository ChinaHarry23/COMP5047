/*
 * forecast.h - tonight's sky from an Open-Meteo AIFS response, and the budget.
 *
 * Request (plan F5): hourly=cloud_cover, daily=sunset, timeformat=unixtime,
 * timezone=Australia/Sydney, models=ecmwf_aifs025_single, forecast_days=2.
 * With timeformat=unixtime every time is a UTC epoch in seconds and
 * utc_offset_seconds gives the site's offset (Open-Meteo docs).
 *
 *   C = mean cloud fraction over the hourly samples from the hour containing
 *       tonight's sunset up to local midnight, using only the response's own
 *       timestamps (no wall clock needed to compute it).
 *   B = B_max / (1 + (m - 1) C),  m = EDGE_CLOUD_AMPLIFICATION.
 *
 * Tighten-only: a missing, stale (> 24 h) or unknown-age forecast gives C = 1,
 * the smallest budget. Recorded nights (REC-CLEAR / REC-OVERCAST) are real
 * captured responses chosen deliberately by the operator, so age does not apply.
 *
 * The parser is purpose-built and tolerant of field order, extra fields and
 * null samples, but it first checks that the whole body is well-formed JSON,
 * so any truncated body is rejected rather than half-read.
 */
#ifndef EDGE_FORECAST_H
#define EDGE_FORECAST_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "edge_config.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    EDGE_FC_OK = 0,
    EDGE_FC_ERR_EMPTY,     /* nothing to parse                              */
    EDGE_FC_ERR_TRUNCATED, /* body ends inside a value                      */
    EDGE_FC_ERR_MALFORMED, /* not JSON, or a field has the wrong type       */
    EDGE_FC_ERR_API,       /* Open-Meteo returned {"error": true, ...}      */
    EDGE_FC_ERR_MISSING,   /* a required field is absent                    */
    EDGE_FC_ERR_MISMATCH   /* hourly.time and hourly.cloud_cover differ     */
} edge_fc_status_t;

typedef struct {
    int32_t utc_offset_s;
    int     n_hours;                   /* samples stored (extra ones ignored) */
    int64_t hour_t[EDGE_FC_MAX_HOURS]; /* UTC epoch seconds                    */
    float   cloud[EDGE_FC_MAX_HOURS];  /* fraction 0..1, NAN where null        */
    float   temp_c[EDGE_FC_MAX_HOURS]; /* deg C, NAN where null or not sent    */
    int     n_days;
    int64_t sunset[EDGE_FC_MAX_DAYS];  /* UTC epoch seconds                    */
} edge_forecast_t;

/* One evening: its sunset, the local midnight after it, C (NAN when the
 * response had no samples in that window) and the evening's mean air
 * temperature. Temperature is logged only; it never enters the control law
 * (plan section 4). */
typedef struct {
    int64_t sunset;
    int64_t midnight;
    float   cloud;
    float   temp_c;
} edge_night_t;

/* What the cache holds and what a live fetch produces. fetch_epoch is the
 * UTC time of the fetch (from the response's Date header or SNTP), or 0 when
 * neither had given the time. */
typedef struct {
    int64_t      fetch_epoch;
    int          n;
    edge_night_t night[EDGE_FC_MAX_DAYS];
} edge_sky_cache_t;

typedef enum {
    EDGE_SKY_LIVE,       /* fetched during this startup                 */
    EDGE_SKY_CACHED,     /* from the cache, younger than 24 h           */
    EDGE_SKY_RECORDED,   /* a recorded night picked with the selector   */
    EDGE_SKY_PROTECTIVE  /* no usable forecast: C = 1                   */
} edge_sky_source_t;

typedef enum {
    EDGE_SKY_WHY_OK,
    EDGE_SKY_WHY_NO_DATA,     /* nothing fetched and nothing cached      */
    EDGE_SKY_WHY_STALE,       /* cache older than 24 h                   */
    EDGE_SKY_WHY_AGE_UNKNOWN, /* cache or clock without a known time     */
    EDGE_SKY_WHY_NO_TONIGHT   /* data present but none covers tonight    */
} edge_sky_why_t;

typedef struct {
    edge_sky_source_t source;
    edge_sky_why_t    why;
    float   cloud;       /* C in [0, 1]                         */
    float   budget_fmm;  /* B                                   */
    int64_t sunset;      /* the evening it describes, 0 if none */
    int64_t age_s;       /* cache age, -1 when unknown or n/a   */
    float   temp_c;      /* that evening's mean air temperature, logged only (NAN if none) */
} edge_sky_t;

/* A recorded response and which of its evenings to use. */
typedef struct {
    const edge_sky_cache_t *cache;
    int night;
} edge_recorded_night_t;

edge_fc_status_t edge_forecast_parse(const char *body, size_t len, edge_forecast_t *out);

/* Summarise each evening in the response. Returns how many were written. */
int edge_forecast_nights(const edge_forecast_t *fc, edge_night_t *out, int max);

/* Parse a body and summarise it into a cache record in one go. */
edge_fc_status_t edge_sky_cache_from_body(const char *body, size_t len, int64_t fetch_epoch,
                                          edge_sky_cache_t *out);

/* Whether a fetched record should replace the one held (NVS or RAM; held
 * may be NULL). An undated record never replaces a dated one: the dated one
 * becomes usable whenever the clock is known, the undated one never does.
 * Between dated records the newer wins. */
bool edge_sky_cache_replaces(const edge_sky_cache_t *incoming, const edge_sky_cache_t *held);

/* now: UTC epoch seconds, or 0 when the clock is unknown. */
edge_sky_t edge_sky_live(const edge_sky_cache_t *live, int64_t now);
edge_sky_t edge_sky_cached(const edge_sky_cache_t *cache, int64_t now); /* cache may be NULL */
edge_sky_t edge_sky_recorded(edge_recorded_night_t rec);

float edge_budget_for_cloud(float cloud);

const char *edge_fc_status_name(edge_fc_status_t s);
const char *edge_sky_source_name(edge_sky_source_t s);
const char *edge_sky_why_name(edge_sky_why_t w);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_FORECAST_H */
