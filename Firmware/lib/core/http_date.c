#include "http_date.h"

#include <stddef.h>
#include <string.h>

/* IMF-fixdate is fixed width: "Sun, 06 Nov 1994 08:49:37 GMT". */
#define IMF_FIXDATE_LEN 29

static const char k_days[]   = "MonTueWedThuFriSatSun";
static const char k_months[] = "JanFebMarAprMayJunJulAugSepOctNovDec";

/* Index of the 3-letter name at p in a packed name list, or -1. */
static int name_index(const char *names, int count, const char *p)
{
    for (int i = 0; i < count; i++) {
        if (strncmp(names + 3 * i, p, 3) == 0) {
            return i;
        }
    }
    return -1;
}

/* n decimal digits at p, or -1 if any is not a digit. */
static int digits(const char *p, int n)
{
    int v = 0;
    for (int i = 0; i < n; i++) {
        if (p[i] < '0' || p[i] > '9') {
            return -1;
        }
        v = v * 10 + (p[i] - '0');
    }
    return v;
}

static bool leap_year(int y)
{
    return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
}

static int days_in_month(int y, int m)
{
    static const int k_len[12] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    return m == 2 && leap_year(y) ? 29 : k_len[m - 1];
}

/* Days from 1970-01-01 to y-m-d in the Gregorian calendar, for years from
 * 1970 on (H. Hinnant's days_from_civil with March-based years). */
static int64_t days_from_civil(int y, int m, int d)
{
    const int64_t yy  = y - (m <= 2 ? 1 : 0);
    const int64_t era = yy / 400;
    const int64_t yoe = yy - era * 400;
    const int64_t doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1;
    const int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

bool edge_http_date_parse(const char *s, int64_t *epoch)
{
    if (s == NULL) {
        return false;
    }
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t')) {
        n--;
    }
    if (n != IMF_FIXDATE_LEN || s[3] != ',' || s[4] != ' ' || s[7] != ' ' || s[11] != ' ' ||
        s[16] != ' ' || s[19] != ':' || s[22] != ':' || s[25] != ' ' || strncmp(s + 26, "GMT", 3) != 0 ||
        name_index(k_days, 7, s) < 0) {
        return false;
    }
    const int day   = digits(s + 5, 2);
    const int month = name_index(k_months, 12, s + 8) + 1;
    const int year  = digits(s + 12, 4);
    const int hh    = digits(s + 17, 2);
    const int mm    = digits(s + 20, 2);
    const int ss    = digits(s + 23, 2);
    if (month < 1 || year < 1970 || day < 1 || day > days_in_month(year, month) || hh < 0 || hh > 23 ||
        mm < 0 || mm > 59 || ss < 0 || ss > 60) { /* 60: a leap second is allowed */
        return false;
    }
    *epoch = days_from_civil(year, month, day) * 86400 + hh * 3600 + mm * 60 + ss;
    return true;
}
