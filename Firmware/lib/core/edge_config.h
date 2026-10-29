/*
 * edge_config.h - every declared or tunable number in EDGE Revision 3.
 *
 * One place, one line of basis each. "PLACEHOLDER" marks a value that has not
 * been measured: it stands in until the named test (O1, E0, E2, E4, TP2, ...)
 * replaces it. Change a number here, then log the change and the reason in
 * BUILD_LOG (records/BUILD_LOG.md). Pure C: included by the host simulator and by both firmware apps.
 */
#ifndef EDGE_CONFIG_H
#define EDGE_CONFIG_H

/* ---- control timing ------------------------------------------------------ */
#define EDGE_STEP_MS              50      /* the single control step (plan 7.2)                          */
#define EDGE_FRAME_PERIOD_MS      200     /* ~5 Hz spectral frame (plan F3); sim cadence                  */
#define EDGE_SENSOR_TIMEOUT_MS    1000    /* no valid frame for this long means the bat sensor is missing */
#define EDGE_FAULT_CLEAR_MS       1000    /* sensor back this long before FAULT clears (plan 7.3, I5)     */

/* ---- dose unit: the full-moon-minute (plan section 4) -------------------- */
#define EDGE_FMM_PHOT_LUX         0.2f    /* full-moon illuminance, declared; Gaston et al. 2012: 0.1-0.3 lux */
#define EDGE_SP_MOON              1.91f   /* full-moon S/P ratio (plan section 4, Longcore et al. 2018 data) */
#define EDGE_FMM_SCOT_LUX         (EDGE_FMM_PHOT_LUX * EDGE_SP_MOON) /* = 0.382 V'-proxy lux        */
#define EDGE_TIME_COMPRESSION     60.0f   /* declared: 1 exhibit second = 1 night minute                  */

/* ---- endmember S/P ratios (scotopic V' over photopic V) ------------------ */
#define EDGE_SP_AMBER             0.23f   /* PLACEHOLDER until E4: 590/15 nm value, Longcore 2018 via plan */
#define EDGE_SP_BROAD             2.09f   /* PLACEHOLDER until E4 measures the torch: cool-white streetlight */

/* ---- lamp levels and fades ---------------------------------------------- */
/* The FSM works in a perceptual level 0..1; emitted light = level^gamma.     */
#define EDGE_LAMP_GAMMA           2.2f    /* conventional display gamma, so fades look even to the eye    */
#define EDGE_LEVEL_MAX            1.0f    /* PLACEHOLDER until tuned in measured tile lux (W10)           */
#define EDGE_LEVEL_FLOOR          0.45f   /* PLACEHOLDER: (1/0.45)^2.2 = 5.8:1 in light; retune to >=5:1 in tile lux */
#define EDGE_FADE_UP_MS           1500    /* full-scale fade up (plan section 4)                          */
#define EDGE_FADE_DOWN_MS         5000    /* full-scale fade out (plan section 4)                         */
#define EDGE_DIM_TO_FLOOR_MS      600     /* full-scale dim in YIELD/FAULT: (max-floor) x this + 0.4 s hold + one frame <= 1.0 s (TP6) */

/* ---- budget (plan sections 4 and 9) -------------------------------------- */
#define EDGE_LAMP_PHOT_LUX_AT_MAX 4.0f    /* PLACEHOLDER until O1/E2: photopic lux at the bat, lamp at max */
#define EDGE_LAMP_HOLD_S          25.0f   /* the LAMP's hold time after the walker passes.
                                              NOT a time the visitor holds anything. Motion-triggered
                                              street lighting typically holds 30 s to 2 min; 25 s is at the
                                              short end. Moving to 30.0 s (a standard setting, directly
                                              citable) raises B_max to ~72 FMM -- still ~10 crossings. */
#define EDGE_B_MAX_FMM            (EDGE_LAMP_HOLD_S * EDGE_LAMP_PHOT_LUX_AT_MAX * EDGE_SP_AMBER \
                                   / EDGE_FMM_SCOT_LUX * EDGE_TIME_COMPRESSION / 60.0f) /* ~60 FMM */
#define EDGE_CLOUD_AMPLIFICATION  3.0f    /* m, declared: Kyba et al. 2011 x2.8 at 32 km from Berlin      */

/* ---- forecast (plan F5 / section 9) -------------------------------------- */
#define EDGE_SITE_LAT             "-33.745" /* PLACEHOLDER: coordinates of the plan's forecast check; set to the field site */
#define EDGE_SITE_LON             "151.04"
#define EDGE_FORECAST_STALE_S     (24L * 3600L)  /* older cache is ignored: C = 1 (tighten-only)        */
#define EDGE_FETCH_CAP_MS         5000    /* startup waits at most this long for a live fetch (plan F5)    */
#define EDGE_FORECAST_REFRESH_MS  (6UL * 3600UL * 1000UL) /* background refresh period (plan F5)         */
#define EDGE_FC_MAX_HOURS         72      /* hourly samples kept; forecast_days=2 returns 48             */
#define EDGE_FC_MAX_DAYS          3       /* sunsets kept; forecast_days=2 returns 2                     */
#define EDGE_HTTP_BODY_MAX        4096    /* body buffer; the 2-day response is ~1.4 KB (plan 7.1: >= 4 KB) */

/* ---- two-endmember separation and F6 (plan F3, F6) ----------------------- */
#define EDGE_STEADY_FRACTION      0.05f   /* a frame is steady if lamp light changed < 5% across it (plan F3) */
#define EDGE_MIN_LIT_LIGHT        0.02f   /* below this lamp light a frame cannot measure the lamp transfer */
#define EDGE_BASELINE_FRAMES      5       /* frames averaged for a (re)baseline, ~1 s                     */
#define EDGE_BASELINE_TAU_S       30.0f   /* PLACEHOLDER: slow baseline adaptation (lamp off, no event)    */
#define EDGE_LAMP_GAIN_ALPHA      0.3f    /* smoothing of the measured lamp transfer between steady frames */
#define EDGE_COLLINEAR_LIMIT      0.02f   /* 1 - cos^2 below this: endmembers too alike to separate        */
#define EDGE_F6_HIGH_LUX          2.0f    /* PLACEHOLDER until O1: at least 5x the room-drift SD (plan 4)  */
#define EDGE_F6_LOW_LUX           1.0f    /* PLACEHOLDER until O1: event ends below this                   */
#define EDGE_F6_ONSET_MS          2000    /* "fast onset": from below LOW to above HIGH within this         */
#define EDGE_F6_HOLD_MS           400     /* above HIGH this long before YIELD (plan F6)                   */
#define EDGE_F6_END_MS            1000    /* below LOW this long ends the event (plan F6)                  */
#define EDGE_SCENE_CHANGE_MS      30000   /* an offset lasting this long is a scene change: re-baseline (plan F6) */

/* ---- presence (plan F1) -------------------------------------------------- */
#define EDGE_PRESENCE_DEBOUNCE_MS 100     /* plan F1                                                      */
#define EDGE_PRESENCE_LEARN_MS    500     /* boot window for learning the no-hand reading                 */
#define EDGE_PRESENCE_K_SIGMA     6.0f    /* threshold at least this many noise SDs above the learned mean */
#define EDGE_PRESENCE_HYST        0.4f    /* release threshold sits this fraction of the margin below press */
#define EDGE_FORCE_MIN_DELTA      400.0f  /* PLACEHOLDER until TP2: ADC counts a resting hand adds (~0.3 V) */
#define EDGE_FORCE_LEARN_CEILING  1500.0f /* boot reading above this means a hand was on the mark: use defaults */
#define EDGE_FORCE_DEFAULT_MEAN   50.0f   /* PLACEHOLDER: no-hand reading assumed when learning was refused */
#define EDGE_TOUCH_MIN_DELTA      8.0f    /* PLACEHOLDER: touchRead drop for the guarded 70x50 mm electrode */

/* ---- actuators ----------------------------------------------------------- */
#define EDGE_LAMP_PWM_HZ          20000   /* above hearing, no visible flicker (plan F2)                   */
#define EDGE_LAMP_PWM_BITS        11      /* 12-bit exceeds the 80 MHz LEDC clock at 20 kHz (plan 4)        */
#define EDGE_LAMP_MIN_DUTY        0.02f   /* PLACEHOLDER: BC337 stage onset ("bottom 1-2%"), measure on arrival */
#define EDGE_SERVO_HZ             50      /* standard hobby-servo frame                                    */
#define EDGE_SERVO_BITS           14      /* 1.2 us per count at 50 Hz                                     */
#define EDGE_SERVO_MIN_US         900     /* FEETECH FS90: ~120 deg over 900-2100 us (Pololu 2818 listing) */
#define EDGE_SERVO_MAX_US         2100
#define EDGE_SERVO_RANGE_DEG      120.0f
#define EDGE_NEEDLE_HOLD_MS       400     /* pulses kept on after a move, then stopped (detach)            */
#define EDGE_NEEDLE_MIN_STEP_DEG  1.0f    /* smaller changes are not worth waking the servo for            */
#define EDGE_NEEDLE_QUIET_MS      40      /* PLACEHOLDER: touch samples skipped after a move (two servo frames); coupling unverified, 0 if none */
#define EDGE_NEEDLE_MIN_INTERVAL_MS 200   /* moves at least this far apart: >= 150 ms of touch sampling between them (F1) */
#define EDGE_NEEDLE_SWEEP_MS      2000    /* startup self-test sweep, there and back                       */
#define EDGE_SKY_SHOW_MS          3000    /* sky factor shown on the inner arc (plan 1.3)                  */

/* ---- dial geometry in servo degrees (PLACEHOLDER until the dial face is drawn) */
#define EDGE_DIAL_FULL_DEG        110.0f  /* outer arc: budget = B_max (clear night, untouched)           */
#define EDGE_DIAL_ZERO_DEG        30.0f   /* outer arc: budget spent                                      */
#define EDGE_DIAL_PAST_ZERO_DEG   12.0f   /* end of the past-zero sector                                  */
#define EDGE_DIAL_PAST_ZERO_FMM   (0.5f * EDGE_B_MAX_FMM) /* overspend shown across the past-zero sector  */
#define EDGE_DIAL_FAULT_DEG       2.0f    /* FAULT tick: needle parked                                    */
#define EDGE_DIAL_SKY_CLEAR_DEG   110.0f  /* inner arc: C = 0                                             */
#define EDGE_DIAL_SKY_OVERCAST_DEG 30.0f  /* inner arc: C = 1                                             */

/* ---- kit (FW0) stand-in sensor ------------------------------------------- */
#define EDGE_KIT_PHOTO_DELTA_AT_MAX 1000.0f /* PLACEHOLDER until `cal`: ADC counts the kit LED adds at max  */
#define EDGE_KIT_DARK_TAU_S       2.0f    /* phototransistor dark level adapts this slowly while lamp is off */
#define EDGE_KIT_SATURATED_COUNTS 4000.0f /* 12-bit ADC tops out at 4095: a reading this high cannot show the LED */
#define EDGE_KIT_MIN_LED_COUNTS   20.0f   /* LED must add this many counts per unit of light to be seen (`cal` refuses below it) */
#define EDGE_KIT_CHECK_LIGHT      0.15f   /* LED visibility tested only at or above this light; keep below the floor's (0.45^2.2 = 0.17) */

#endif /* EDGE_CONFIG_H */
