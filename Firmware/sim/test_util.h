/* test_util.h - tiny assertion helpers for the host unit tests. */
#ifndef EDGE_TEST_UTIL_H
#define EDGE_TEST_UTIL_H

#include <math.h>
#include <stdio.h>

static int g_checks;
static int g_failures;

#define CHECK(cond)                                                              \
    do {                                                                         \
        g_checks++;                                                              \
        if (!(cond)) {                                                           \
            g_failures++;                                                        \
            printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);              \
        }                                                                        \
    } while (0)

#define CHECK_NEAR(a, b, tol)                                                    \
    do {                                                                         \
        g_checks++;                                                              \
        const double a_ = (double)(a), b_ = (double)(b);                         \
        if (!(fabs(a_ - b_) <= (tol))) {                                         \
            g_failures++;                                                        \
            printf("  FAIL %s:%d: %s = %.6g, expected %.6g +/- %.3g\n",          \
                   __FILE__, __LINE__, #a, a_, b_, (double)(tol));               \
        }                                                                        \
    } while (0)

static int test_summary(const char *suite)
{
    printf("%s: %d checks, %d failed -> %s\n", suite, g_checks, g_failures,
           g_failures == 0 ? "PASS" : "FAIL");
    return g_failures == 0 ? 0 : 1;
}

#endif /* EDGE_TEST_UTIL_H */
