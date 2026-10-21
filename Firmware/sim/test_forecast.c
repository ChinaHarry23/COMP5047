/*
 * test_forecast.c - Open-Meteo parsing, tonight's C, tighten-only resolution,
 * dating a fetch from its HTTP Date header, and which cache record is kept.
 *
 * fixtures/openmeteo_aifs_20261005.json is a real response captured on
 * 5 Oct 2026 at 08:01:41 UTC (19:01 AEDT) for the plan's site coordinates.
 * The *_synthetic fixtures are edited or documented-format bodies.
 */
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "edge_config.h"
#include "forecast.h"
#include "http_date.h"
#include "test_util.h"

#define FIXTURE_FETCH_EPOCH 1791187301LL /* HTTP Date header of the capture */

static size_t read_file(const char *path, char *buf, size_t cap)
{
    FILE *fp = fopen(path, "rb");
    if (fp == NULL) {
        printf("  cannot open %s\n", path);
        return 0;
    }
    const size_t n = fread(buf, 1, cap - 1, fp);
    fclose(fp);
    buf[n] = '\0';
    return n;
}

static char g_body[8192];

static void test_parse_captured_response(void)
{
    const size_t len = read_file("fixtures/openmeteo_aifs_20261005.json", g_body, sizeof g_body);
    edge_forecast_t fc;
    CHECK(edge_forecast_parse(g_body, len, &fc) == EDGE_FC_OK);
    CHECK(fc.utc_offset_s == 39600);
    CHECK(fc.n_hours == 48);
    CHECK(fc.n_days == 2);
    CHECK(fc.hour_t[0] == 1791118800LL);
    CHECK_NEAR(fc.cloud[0], 0.65, 1e-6);
    CHECK(fc.sunset[0] == 1791187278LL);
    CHECK(fc.sunset[1] == 1791273723LL);

    edge_night_t n[EDGE_FC_MAX_DAYS];
    CHECK(edge_forecast_nights(&fc, n, EDGE_FC_MAX_DAYS) == 2);
    /* 5 Oct: sunset 19:01 AEDT, samples 19:00..23:00 = 95,96,96,96,96 % */
    CHECK(n[0].midnight == 1791205200LL); /* = daily.time[1], local midnight */
    CHECK_NEAR(n[0].cloud, 0.958, 1e-5);
    /* 6 Oct: 0,0,0,0,4 % */
    CHECK(n[1].midnight == 1791291600LL);
    CHECK_NEAR(n[1].cloud, 0.008, 1e-5);
    /* Logged only: evening mean air temperature. */
    CHECK_NEAR(n[0].temp_c, (19.6 + 18.7 + 17.8 + 16.9 + 16.3) / 5.0, 1e-4);
    CHECK_NEAR(n[1].temp_c, (23.9 + 21.9 + 19.7 + 17.8 + 16.4) / 5.0, 1e-4);
}

/* Any body cut short, at any byte, must be rejected, never half-read. */
static void test_every_truncation_rejected(void)
{
    const size_t len = read_file("fixtures/openmeteo_aifs_20261005.json", g_body, sizeof g_body);
    static char cut[8192];
    edge_forecast_t fc;
    int accepted = 0;
    for (size_t k = 0; k < len; k++) {
        memcpy(cut, g_body, k);
        cut[k] = '\0';
        if (edge_forecast_parse(cut, k, &fc) == EDGE_FC_OK) {
            accepted++;
            printf("  accepted a body truncated to %zu bytes\n", k);
        }
    }
    CHECK(accepted == 0);
    CHECK(edge_forecast_parse(g_body, len / 2, &fc) == EDGE_FC_ERR_TRUNCATED);
}

static void test_error_and_malformed_bodies(void)
{
    edge_forecast_t fc;
    const size_t len = read_file("fixtures/openmeteo_error_synthetic.json", g_body, sizeof g_body);
    CHECK(edge_forecast_parse(g_body, len, &fc) == EDGE_FC_ERR_API);

    CHECK(edge_forecast_parse("", 0, &fc) == EDGE_FC_ERR_EMPTY);
    CHECK(edge_forecast_parse("   ", 3, &fc) == EDGE_FC_ERR_EMPTY);
    const char *html = "<html>502 Bad Gateway</html>";
    CHECK(edge_forecast_parse(html, strlen(html), &fc) == EDGE_FC_ERR_MALFORMED);
    const char *no_daily = "{\"utc_offset_seconds\":0,\"hourly\":{\"time\":[1],\"cloud_cover\":[5]}}";
    CHECK(edge_forecast_parse(no_daily, strlen(no_daily), &fc) == EDGE_FC_ERR_MISSING);
    const char *mismatch = "{\"utc_offset_seconds\":0,\"hourly\":{\"time\":[1,2],\"cloud_cover\":[5]},"
                           "\"daily\":{\"sunset\":[1]}}";
    CHECK(edge_forecast_parse(mismatch, strlen(mismatch), &fc) == EDGE_FC_ERR_MISMATCH);
    const char *wrong_type = "{\"utc_offset_seconds\":0,\"hourly\":{\"time\":\"x\",\"cloud_cover\":[5]},"
                             "\"daily\":{\"sunset\":[1]}}";
    CHECK(edge_forecast_parse(wrong_type, strlen(wrong_type), &fc) == EDGE_FC_ERR_MALFORMED);
}

/* Field order, whitespace, escapes and unknown fields do not matter. */
static void test_tolerant_layout(void)
{
    const char *body =
        "{ \"daily\" : { \"sunset\" : [ 7200 ], \"time\": [0] },\n"
        "  \"note\": \"a \\\"quoted\\\" \\\\ string\", \"nested\": {\"a\": [true, false, null, -1.5e2]},\n"
        "  \"hourly\": { \"cloud_cover\": [ 10, 20.5, 30, 40 ], \"time\": [ 3600, 7200, 10800, 86400 ] },\n"
        "  \"utc_offset_seconds\": 0 }\n";
    edge_forecast_t fc;
    CHECK(edge_forecast_parse(body, strlen(body), &fc) == EDGE_FC_OK);
    edge_night_t n[1];
    CHECK(edge_forecast_nights(&fc, n, 1) == 1);
    CHECK(n[0].midnight == 86400);
    CHECK(isnan(n[0].temp_c)); /* temperature not requested: still parses */
    CHECK_NEAR(n[0].cloud, (0.205 + 0.30) / 2.0, 1e-6); /* 02:00 and 03:00; 24:00 is tomorrow */
}

static void test_null_samples(void)
{
    const size_t len = read_file("fixtures/openmeteo_nulls_synthetic.json", g_body, sizeof g_body);
    edge_sky_cache_t c;
    CHECK(edge_sky_cache_from_body(g_body, len, FIXTURE_FETCH_EPOCH, &c) == EDGE_FC_OK);
    CHECK(c.n == 2);
    CHECK_NEAR(c.night[0].cloud, (0.95 + 0.96 + 0.96) / 3.0, 1e-5); /* nulls skipped */
    CHECK(isnan(c.night[1].cloud));                                /* all null      */

    /* An evening with no samples is missing data: protective. */
    edge_recorded_night_t rec = { &c, 1 };
    const edge_sky_t s = edge_sky_recorded(rec);
    CHECK(s.source == EDGE_SKY_PROTECTIVE);
    CHECK(s.cloud == 1.0f);
}

static void test_budget_formula(void)
{
    CHECK_NEAR(edge_budget_for_cloud(0.0f), EDGE_B_MAX_FMM, 1e-4);
    CHECK_NEAR(edge_budget_for_cloud(1.0f), EDGE_B_MAX_FMM / EDGE_CLOUD_AMPLIFICATION, 1e-4);
    CHECK_NEAR(edge_budget_for_cloud(0.958f), EDGE_B_MAX_FMM / (1.0 + 2.0 * 0.958), 1e-3);
    CHECK_NEAR(edge_budget_for_cloud(NAN), EDGE_B_MAX_FMM / EDGE_CLOUD_AMPLIFICATION, 1e-4);
    CHECK_NEAR(edge_budget_for_cloud(7.0f), EDGE_B_MAX_FMM / EDGE_CLOUD_AMPLIFICATION, 1e-4);
    CHECK_NEAR(edge_budget_for_cloud(-1.0f), EDGE_B_MAX_FMM, 1e-4);
}

/* Tighten-only: stale, missing or unknown-age data gives C = 1 (I6). */
static void test_resolution_rules(void)
{
    const size_t len = read_file("fixtures/openmeteo_aifs_20261005.json", g_body, sizeof g_body);
    edge_sky_cache_t c;
    CHECK(edge_sky_cache_from_body(g_body, len, FIXTURE_FETCH_EPOCH, &c) == EDGE_FC_OK);
    const int64_t now = FIXTURE_FETCH_EPOCH;

    edge_sky_t s = edge_sky_live(&c, now);
    CHECK(s.source == EDGE_SKY_LIVE);
    CHECK_NEAR(s.cloud, 0.958, 1e-5);
    CHECK_NEAR(s.budget_fmm, edge_budget_for_cloud(0.958f), 1e-4);

    s = edge_sky_live(&c, 0); /* clock unknown: the response's first evening */
    CHECK(s.source == EDGE_SKY_LIVE && s.sunset == c.night[0].sunset);

    s = edge_sky_live(&c, c.night[0].midnight + 60); /* after midnight: next evening */
    CHECK_NEAR(s.cloud, 0.008, 1e-5);

    c.fetch_epoch = now - 2 * 3600;
    s = edge_sky_cached(&c, now);
    CHECK(s.source == EDGE_SKY_CACHED && s.age_s == 2 * 3600);
    CHECK_NEAR(s.cloud, 0.958, 1e-5);

    c.fetch_epoch = now - 30 * 3600;
    s = edge_sky_cached(&c, now);
    CHECK(s.source == EDGE_SKY_PROTECTIVE && s.why == EDGE_SKY_WHY_STALE && s.cloud == 1.0f);

    c.fetch_epoch = 0;
    s = edge_sky_cached(&c, now);
    CHECK(s.source == EDGE_SKY_PROTECTIVE && s.why == EDGE_SKY_WHY_AGE_UNKNOWN && s.cloud == 1.0f);

    c.fetch_epoch = now - 3600;
    s = edge_sky_cached(&c, 0); /* clock unknown: age unknown */
    CHECK(s.source == EDGE_SKY_PROTECTIVE && s.why == EDGE_SKY_WHY_AGE_UNKNOWN && s.cloud == 1.0f);

    s = edge_sky_cached(&c, c.night[1].midnight + 1); /* fresh enough, but no evening left */
    CHECK(s.source == EDGE_SKY_PROTECTIVE && s.cloud == 1.0f);

    s = edge_sky_cached(NULL, now);
    CHECK(s.source == EDGE_SKY_PROTECTIVE && s.why == EDGE_SKY_WHY_NO_DATA && s.cloud == 1.0f);
    CHECK_NEAR(s.budget_fmm, EDGE_B_MAX_FMM / EDGE_CLOUD_AMPLIFICATION, 1e-4);

    edge_recorded_night_t clear = { &c, 1 }, overcast = { &c, 0 }, bad = { &c, 5 };
    CHECK_NEAR(edge_sky_recorded(clear).cloud, 0.008, 1e-5);
    CHECK(edge_sky_recorded(overcast).source == EDGE_SKY_RECORDED);
    CHECK(edge_sky_recorded(bad).source == EDGE_SKY_PROTECTIVE);
}

/* IMF-fixdate only; anything malformed leaves the epoch alone (SNTP then). */
static void test_http_date(void)
{
    int64_t t = 0;
    CHECK(edge_http_date_parse("Mon, 05 Oct 2026 08:01:41 GMT", &t)); /* the fixture's capture */
    CHECK(t == FIXTURE_FETCH_EPOCH);
    CHECK(edge_http_date_parse("Sun, 06 Nov 1994 08:49:37 GMT", &t)); /* RFC 9110's example */
    CHECK(t == 784111777LL);
    CHECK(edge_http_date_parse("Thu, 29 Feb 2024 23:59:59 GMT  ", &t)); /* leap day, trailing blanks */
    CHECK(t == 1709251199LL);
    CHECK(edge_http_date_parse("Thu, 01 Jan 1970 00:00:00 GMT", &t));
    CHECK(t == 0);

    static const char *const bad[] = {
        "",
        "Mon, 05 Oct 2026 08:01:41",        /* no zone          */
        "Mon, 05 Oct 2026 08:01:41 UTC",    /* not GMT          */
        "Mon, 5 Oct 2026 08:01:41 GMT",     /* one-digit day    */
        "Mon, 05 Okt 2026 08:01:41 GMT",    /* no such month    */
        "Xyz, 05 Oct 2026 08:01:41 GMT",    /* no such day name */
        "Mon, 31 Sep 2026 08:01:41 GMT",    /* no 31 Sep        */
        "Tue, 29 Feb 2025 08:01:41 GMT",    /* 2025 not leap    */
        "Mon, 05 Oct 2026 24:00:00 GMT",
        "Mon, 05 Oct 1969 08:01:41 GMT",
        "Monday, 05-Oct-26 08:01:41 GMT",   /* obsolete RFC 850 */
        "Mon Oct  5 08:01:41 2026",         /* obsolete asctime */
    };
    for (size_t i = 0; i < sizeof bad / sizeof bad[0]; i++) {
        t = 42;
        CHECK(!edge_http_date_parse(bad[i], &t) && t == 42);
    }
    CHECK(!edge_http_date_parse(NULL, &t));
}

/* An undated record never replaces a dated one; between dated, the newer. */
static void test_cache_replacement(void)
{
    edge_sky_cache_t dated, older, undated, empty;
    memset(&dated, 0, sizeof dated);
    dated.n = 1;
    dated.fetch_epoch = FIXTURE_FETCH_EPOCH;
    older = dated;
    older.fetch_epoch -= 3600;
    undated = dated;
    undated.fetch_epoch = 0;
    memset(&empty, 0, sizeof empty);

    CHECK(edge_sky_cache_replaces(&dated, NULL));
    CHECK(edge_sky_cache_replaces(&undated, NULL));
    CHECK(edge_sky_cache_replaces(&dated, &undated));
    CHECK(!edge_sky_cache_replaces(&undated, &dated));
    CHECK(edge_sky_cache_replaces(&undated, &undated));
    CHECK(edge_sky_cache_replaces(&dated, &older));
    CHECK(!edge_sky_cache_replaces(&older, &dated));
    CHECK(edge_sky_cache_replaces(&undated, &empty));
    CHECK(!edge_sky_cache_replaces(&empty, &undated));
}

int main(void)
{
    test_parse_captured_response();
    test_every_truncation_rejected();
    test_error_and_malformed_bodies();
    test_tolerant_layout();
    test_null_samples();
    test_budget_formula();
    test_resolution_rules();
    test_http_date();
    test_cache_replacement();
    return test_summary("test_forecast");
}
