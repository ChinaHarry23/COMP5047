/*
 * edge_light.h - what the light layer tells the policy about the bat's light.
 *
 * Two producers fill this struct: sep2 (the exhibit's spectral separation)
 * and the kit's phototransistor adapter. The FSM and the dose code consume
 * only this, which is why the kit and the exhibit run the same policy.
 *
 * Amplitudes are in endmember units (see calib.h); calib turns them into lux.
 *
 * Producer obligations:
 *  - An invalid reading changes only `valid`: every other field keeps its
 *    last value. The FSM relies on this to hold an F6 event through dropped
 *    frames, and to keep charging the lamp as lamp_gain x commanded light,
 *    until the sensor timeout turns the gap into FAULT.
 *  - lamp_gain is meaningful from the start (the expected transfer until the
 *    first measurement), so a sensor missing from boot never makes light free.
 *  - room_epoch changes whenever the producer re-learns the room (re-baseline
 *    button, scene change, sensor return). An event that ends because the
 *    room was re-learned was the room, not another light, and is not charged.
 */
#ifndef EDGE_LIGHT_H
#define EDGE_LIGHT_H

#include <stdbool.h>
#include <stdint.h>

typedef struct {
    bool     valid;          /* a usable reading arrived at t_ms                     */
    uint32_t t_ms;           /* time of the reading on the app clock                 */
    bool     baseline_ready; /* the room's own light has been learned                */
    float    lamp_amp;       /* lamp component at the bat (>= 0)                     */
    bool     lamp_measured;  /* lamp_amp came from this reading, not from lamp_gain  */
    float    lamp_gain;      /* lamp amplitude per unit commanded light, last measured */
    float    broad_delta;    /* broadband change over the baseline (signed)          */
    bool     event;          /* an F6 other-light event is in progress               */
    unsigned room_epoch;     /* changes each time the room is re-learned             */
} edge_light_t;

#endif /* EDGE_LIGHT_H */
