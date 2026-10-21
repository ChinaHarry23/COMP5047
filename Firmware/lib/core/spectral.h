/*
 * spectral.h - one normalised spectral frame from the bat's sensor.
 *
 * This is the contract between a spectral driver (the AS7343 package, or a
 * stub) and the policy core. The driver owns everything chip-specific; the
 * core only ever sees this struct.
 *
 * Driver obligations:
 *  - ch[] holds the 12 filtered AS7343 channels in wavelength order (see
 *    edge_spec_centre_nm). Each value is in "basic counts":
 *        raw ADC counts / (analog gain multiplier x integration time in ms)
 *    so that auto-gain and integration changes do not change the number.
 *  - clear holds the VIS (clear) channel in the same unit. It is diagnostic
 *    only; separation does not use it. The flicker channel is not carried.
 *  - t_start_ms / t_end_ms bracket the whole read on the application's
 *    millisecond clock. With auto-SMUX the AS7343 reads the channels in
 *    three sequential integrations, so this is the first start and the last
 *    end; sep2 uses the window to reject frames taken while the lamp changed.
 *  - valid is false when no sensor answered or the read failed. A driver that
 *    loses the chip keeps delivering valid=false frames (or none at all); the
 *    core turns a long enough gap into FAULT and recovers on its own.
 *  - saturated is true when any channel hit analog or digital saturation at
 *    the lowest gain the driver tried. The frame is still delivered: very
 *    bright light (a torch) is exactly what F6 has to see.
 */
#ifndef EDGE_SPECTRAL_H
#define EDGE_SPECTRAL_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define EDGE_SPEC_NCH 12

typedef enum {
    EDGE_CH_F1 = 0, /* 405 nm */
    EDGE_CH_F2,     /* 425 nm */
    EDGE_CH_FZ,     /* 450 nm */
    EDGE_CH_F3,     /* 475 nm */
    EDGE_CH_F4,     /* 515 nm */
    EDGE_CH_F5,     /* 550 nm */
    EDGE_CH_FY,     /* 555 nm */
    EDGE_CH_FXL,    /* 600 nm */
    EDGE_CH_F6,     /* 640 nm */
    EDGE_CH_F7,     /* 690 nm */
    EDGE_CH_F8,     /* 745 nm */
    EDGE_CH_NIR     /* 855 nm */
} edge_spec_channel_t;

typedef struct {
    float    ch[EDGE_SPEC_NCH]; /* basic counts, wavelength order          */
    float    clear;             /* VIS channel, basic counts (diagnostic)  */
    uint32_t t_start_ms;        /* integration window on the app clock     */
    uint32_t t_end_ms;
    bool     valid;             /* sensor answered and the read succeeded  */
    bool     saturated;         /* some channel saturated at minimum gain  */
} edge_spectral_frame_t;

/* Typical peak wavelength of each channel, nm (ams AS7343 datasheet). */
extern const float edge_spec_centre_nm[EDGE_SPEC_NCH];

/* Short channel names for logs ("F1", ..., "NIR"). */
extern const char *const edge_spec_name[EDGE_SPEC_NCH];

#ifdef __cplusplus
}
#endif

#endif /* EDGE_SPECTRAL_H */
