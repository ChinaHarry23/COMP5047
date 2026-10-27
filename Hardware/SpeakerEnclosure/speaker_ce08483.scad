// Dummy model of the Core Electronics CE08483 / Waveshare "8Ω 5W Speaker".
// Numbers and their confidence are in SPEAKER_SPEC.md. The dummy is used for
// the preview assembly and for the automated clearance check (check_model.py);
// it models the *envelope* the housing must clear, not cosmetics.
//
// Frame: X along the 100 mm length, Y across the 45 mm width, Z through the
// 21 mm thickness. Origin at the centre of the front face; the body extends
// toward -Z (front face z = 0, back face z = -21).

spk_len     = 100;   // overall length, ears included           (high confidence)
spk_wid     = 45;    // overall width                           (high)
spk_thk     = 21;    // overall thickness                       (high)
spk_hole_dx = 91.2;  // ear-hole pattern along X                (high)
spk_hole_dy = 36.2;  // ear-hole pattern along Y                (high)
spk_hole_d  = 3.4;   // ear hole, design value for an M3 screw  (low: Core says "6 mm")
spk_ear_w   = 7;     // ear tab width, outside                  (low)
spk_ear_t   = 2;     // ear tab thickness                       (low)
spk_ear_z   = 7.5;   // ear REAR face behind the front face     (low: 6-9)
spk_cone_d  = 35;    // driver and passive radiator diameter    (high)
spk_cone_x  = 27.5;  // cone centre from each end along X       (medium)
spk_recess_d = 40;   // shallow recess around each cone         (low)
spk_rear_inset = 1;  // rear shell inset per side, in Y         (low)
spk_lead_w  = 4;     // two side-by-side 1.5 mm leads            (medium)
spk_lead_h  = 2;

// derived
spk_hole_x  = spk_hole_dx / 2;                      // 45.6
spk_hole_y  = spk_hole_dy / 2;                      // 18.1
spk_front_len = 2 * (spk_hole_x - spk_ear_w / 2 - 1); // front shell length, inferred (82.2)
spk_notch_len = (spk_len - spk_front_len) / 2;      // corner notch length in the rear shell
spk_notch_wid = spk_wid / 2 - spk_hole_y + spk_ear_w / 2 + 1; // corner notch width
spk_lead_y  = spk_hole_y - spk_ear_w / 2 - 3;       // lead exit beside the +Y ear, +X end

module speaker_ce08483(leads = true, cones = true) {
    color("DimGray") {
        // front shell: full width, shorter than overall length
        translate([-spk_front_len / 2, -spk_wid / 2, -spk_ear_z])
            cube([spk_front_len, spk_wid, spk_ear_z]);
        // rear shell: full length, inset in Y, notched at the corners for the ears
        difference() {
            translate([-spk_len / 2, -spk_wid / 2 + spk_rear_inset, -spk_thk])
                cube([spk_len, spk_wid - 2 * spk_rear_inset, spk_thk - spk_ear_z]);
            for (sx = [-1, 1], sy = [-1, 1])
                translate([sx > 0 ? spk_front_len / 2 : -spk_len / 2 - 1,
                           sy > 0 ? spk_wid / 2 - spk_notch_wid : -spk_wid / 2 - 1,
                           -spk_thk - 1])
                    cube([spk_notch_len + 1, spk_notch_wid + 1, spk_thk]);
        }
        // the four ears, in the parting plane
        for (sx = [-1, 1], sy = [-1, 1])
            translate([0, 0, -spk_ear_z])
                difference() {
                    hull() {
                        translate([sx * spk_hole_x, sy * spk_hole_y, 0])
                            cylinder(d = spk_ear_w, h = spk_ear_t, $fn = 32);
                        translate([sx > 0 ? spk_front_len / 2 - 0.01 : -spk_front_len / 2,
                                   sy * spk_hole_y - spk_ear_w / 2, 0])
                            cube([0.01, spk_ear_w, spk_ear_t]);
                    }
                    translate([sx * spk_hole_x, sy * spk_hole_y, -1])
                        cylinder(d = spk_hole_d, h = spk_ear_t + 2, $fn = 24);
                }
    }
    if (cones)
        color("Black")
            for (sx = [-1, 1])
                translate([sx * (spk_len / 2 - spk_cone_x), 0, 0])
                    cylinder(d = spk_cone_d, h = 0.2, $fn = 64);
    if (leads)
        color("Red")
            translate([spk_len / 2 - 0.01, spk_lead_y - spk_lead_w / 2, -spk_ear_z - spk_lead_h / 2])
                cube([30, spk_lead_w, spk_lead_h]);
}

// Solid envelope: the 100 x 45 x 21 brick a pocket must clear, plus `clearance`
// on the sides and back (the front face stays at z = 0).
module speaker_envelope(clearance = 0) {
    translate([-spk_len / 2 - clearance, -spk_wid / 2 - clearance, -spk_thk - clearance])
        cube([spk_len + 2 * clearance, spk_wid + 2 * clearance, spk_thk + clearance]);
}

speaker_ce08483();
