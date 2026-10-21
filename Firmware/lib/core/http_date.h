/*
 * http_date.h - an HTTP Date header as a UTC epoch.
 *
 * The FireBeetle has no battery-backed clock, so after a power-up the only
 * time sources are SNTP and the server that answers the forecast request.
 * An origin server with a clock must send a Date header in every 2xx and 4xx
 * response (RFC 9110 section 6.6.1), so a fetch that gets any answer can date
 * itself, and the device's clock, without waiting for SNTP.
 *
 * Only IMF-fixdate is parsed, the form every sender must generate (RFC 9110
 * section 5.6.7), e.g. "Mon, 05 Oct 2026 08:01:41 GMT", which is what
 * Open-Meteo sends. RFC 9110 also asks recipients to accept the two obsolete
 * forms; they are refused here on purpose, because a server that used one
 * would only cost the SNTP fallback.
 */
#ifndef EDGE_HTTP_DATE_H
#define EDGE_HTTP_DATE_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* True and *epoch set (UTC seconds) when s is a valid IMF-fixdate on or
 * after 1970; false and *epoch untouched otherwise. Trailing blanks are
 * allowed. */
bool edge_http_date_parse(const char *s, int64_t *epoch);

#ifdef __cplusplus
}
#endif

#endif /* EDGE_HTTP_DATE_H */
