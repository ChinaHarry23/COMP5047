// Housing for the Core Electronics CE08483 / Makerverse "Enclosed Speaker - 8 Ohm, 5 Watt"
// (same moulding as the Waveshare "8Ω 5W Speaker").  See README.md and SPEAKER_SPEC.md.
//
// Two printed parts, no supports, M3 hardware:
//   tray   - back tray, printed floor-down.  A plain pocket swallows the whole
//            100 x 45 x 21 envelope (ears, notches and all); the speaker sits on
//            four foam-padded standoffs.
//   bezel  - front frame with an integral hex grille, printed face-down.  Four
//            countersunk M3 x 10 pull it onto the speaker's flat front land and onto
//            the tray rim in the same plane: the foam gives the preload, the rim is a
//            hard stop that cannot be over-tightened.
//   coupon - a 6 mm slice of the pocket to prove the fit before the long print.
// Nothing references the ears, the ear holes, the parting line or the cone centres;
// the only datums are the 100 x 45 outline and the two flat faces.
//
// Coordinates: origin at the centre of the tray's back face, X along the 100 mm
// length, Y across the width, Z up from the bed.  Millimetres throughout.
// Written for OpenSCAD 2021.01 and later (the manifold backend of 2024+ renders the
// grille in seconds; CGAL takes minutes).

use <speaker_ce08483.scad>

/* [What to show] */
// tray / bezel / coupon are print-ready.  printplate lays them out on one bed.
// bezel_inplace, envelope and speaker are in assembly coordinates, for check_model.py.
part = "assembly"; // [assembly, section, printplate, tray, bezel, coupon, bezel_inplace, envelope, speaker]
// show the speaker dummy in assembly / section views
show_speaker = true;
// lift the bezel (and half-lift the speaker) by this much in the assembly view
explode = 0;

/* [Speaker envelope] */
speaker_l = 100;
speaker_w = 45;
speaker_h = 21;
// diameter of the driver and the passive radiator (for the open-area check)
cone_d = 35;
// cone centre from each end of the speaker
cone_from_end = 27.5;

/* [Pocket fit] */
// pocket clearance per side; printed ribs take 0.3 of it back
clr_xy = 0.6;
// inner corner radius so a square-cornered ear tab still clears
pocket_corner_r = 1.0;
// 45 degree lead-in at the rim, long sides
pocket_entry_chamfer = 1.0;
// lead-in on the thin end walls
pocket_entry_chamfer_end = 0.5;
// locating ribs: how far they stand proud of the pocket wall (shave to fit)
rib_proud = 0.3;
rib_w = 3.0;
// x positions of the side ribs (on both long walls)
rib_x = [-30, 0, 30];
// z range of the side ribs, measured from the bed: on the full-width front shell
// for any parting plane 6-9 mm behind the face
rib_z = [21.5, 26.0];
end_rib_w = 6.0;
// end ribs bear on the full-length rear shell, below any parting plane
end_rib_z = [7.5, 12.5];

/* [Walls] */
wall_side = 4.0;
// pocket end to the outer end face (cable bay included)
end_zone = 12;
// wall between the pocket and the cable bay
pocket_wall_end = 1.6;
// cable bay to the outside
outer_wall_end = 2.0;
floor_t = 2.0;

/* [Z stack: standoffs, foam, preload] */
pad_h = 3.0;
pad_size = [10, 8];
pad_xy = [33, 13];
// self-adhesive foam under the speaker, uncompressed (0 = none)
foam_t = 2.0;
// nominal foam squash when the bezel is screwed down to the rim
z_preload = 1.0;

/* [Bezel and grille] */
band_t = 4.0;
// bezel band bearing on the speaker's front face, long sides
lip_bear_side = 2.5;
// window end from the speaker end (the front shell is short; the band floats here)
lip_bear_end = 7.0;
centre_bar = true;
centre_bar_w = 3.0;
grille_style = "hex"; // [hex, slots, bars, open]
grille_t = 1.6;
hole_d = 4.0;
hole_pitch = 5.2;
// hex style: radius of the round patch of holes centred on each cone (0 = fill the window)
grille_patch_r = 22;
// typical FDM hole undersize, used for the open-area check only
hole_shrink = 0.3;
grille_min_web = 1.2;
slot_w = 2.4;
slot_pitch = 4.0;
bar_w = 2.0;
bar_gap = 3.0;
// chamfer on the bed side of every grille opening (elephant foot)
hole_chamfer = 0.3;

/* [Fasteners] */
// pilot centre from the pocket end
screw_x_inset = 6.0;
screw_y = 21.5;
pilot_d = 2.5;
pilot_depth = 15;
fastener = "selftap"; // [selftap, insert]
insert_d = 4.0;
insert_depth = 5.8;
screw_head = "csk"; // [csk, pan]
csk_d = 6.2;
pan_d = 6.4;
pan_depth = 2.2;
screw_clear_d = 3.4;
// through-bore from the back face up to the pilot (0 = none): rescue with an
// M3 x 30 and nut, or hidden M3 x 20 from behind a panel
rear_bore_d = 3.8;
// open the pilots to rear_bore_d all the way for wood screws from the front
wall_mount_prep = false;

/* [Cable] */
lead_end = "+x"; // [+x, -x]
// which side(s) of the lead end get a U-slot; "both" because the handedness is unknown
lead_slots = "both"; // [both, left, right]
lead_slot_w = 12;
// U-slot inner edge from the speaker centreline
lead_slot_y0 = 5.5;
lead_slot_floor_z = 13.0;
bay_y = 35;
saddle = true;
cable_exit = "end"; // [end, back, none]
exit_slot_w = 3.6;
exit_slot_h = 8.0;
cable_chamfer = 0.8;

/* [Mounting] */
keyholes = true;
keyhole_spacing = 48;
keyhole_head_d = 9.0;
keyhole_slot_w = 4.5;
keyhole_slot_len = 8;
keyhole_dir = "+y"; // [+y, +x]
strap_slots = true;
strap_w = 5.0;
strap_t = 2.0;
strap_x = 40;
strap_y = 20;
feet_recess = true;
foot_d = 8.0;
foot_depth = 0.6;
foot_pos = [51, 17];

/* [Optional ear bosses (experimental)] */
ear_bosses = false;
// depth of the ear's rear face behind the speaker's front face
ear_plane = 7.5;
ear_pattern = [91.2, 36.2];
ear_boss_d = 5.0;

/* [Optional amp bay (unverified)] */
amp_bay = false;
amp_board = [21, 17, 1.6];
amp_holddown = "zip"; // [zip, tape, screws]
// [x, y] hole positions on the board for amp_holddown = "screws"
amp_holes = [];
amp_exit_slot = [5.0, 10];
// extra length added to the lead-end block for the amp bay
amp_bay_extra = 28;

/* [Cosmetics] */
outer_r = 6;
seam_chamfer = 0.4;
face_chamfer = 0.6;
elephant_foot = 0.3;
bed = 180;
$fn = 64;

// ---------------------------------------------------------------- derived
pocket_l = speaker_l + 2 * clr_xy;
pocket_w = speaker_w + 2 * clr_xy;
end_zone_lead = end_zone + (amp_bay ? amp_bay_extra : 0);
bay_l = end_zone - pocket_wall_end - outer_wall_end;            // 8.4
bay_l_lead = end_zone_lead - pocket_wall_end - outer_wall_end;
x_far = -(pocket_l / 2 + end_zone);                              // far end face
x_lead = pocket_l / 2 + end_zone_lead;                           // lead end face
body_l = x_lead - x_far;
body_cx = (x_lead + x_far) / 2;
body_w = pocket_w + 2 * wall_side;
rim_z = floor_t + pad_h + foam_t + speaker_h - z_preload;        // 27.0
spk_back_z = rim_z - speaker_h;                                   // 6.0 nominal
win_l = speaker_l - 2 * lip_bear_end;                             // 86
win_w = speaker_w - 2 * lip_bear_side;                            // 40
win_r = 2;
screw_x = pocket_l / 2 + screw_x_inset;                           // 56.6: in the solid block beside the bay
screw_x2 = screw_x + amp_bay_extra;                               // second pair with the amp bay
screw_pts = concat([for (sx = [-1, 1], sy = [-1, 1]) [sx * screw_x, sy * screw_y]],
                   amp_bay ? [for (sy = [-1, 1]) [screw_x2, sy * screw_y]] : []);
bay_x0 = pocket_l / 2 + pocket_wall_end;                          // bay starts here
bay_x1 = bay_x0 + bay_l_lead;                                     // bay ends here (outer wall inner face)
saddle_x = amp_bay ? bay_x0 + 3.5 : bay_x0 + bay_l / 2;
exit_z0 = floor_t + 2.0;
le = lead_end == "+x" ? 1 : -1;
eps = 0.01;

// ---------------------------------------------------------------- checks
assert(wall_side >= 1.6 && floor_t >= 1.6 && outer_wall_end >= 1.6 && pocket_wall_end >= 1.2, "structural wall under minimum");
assert(band_t - grille_t >= 1.0, "grille standoff under 1 mm: the cones could touch the grille");
assert(grille_style != "hex" || hole_pitch - hole_d >= grille_min_web, "hex web under grille_min_web");
assert(screw_y - pilot_d / 2 - bay_y / 2 >= 2.0, "pilot too close to the cable bay");
assert(screw_x - pilot_d / 2 - pocket_l / 2 >= 2.0, "pilot too close to the pocket");
assert(body_w / 2 - screw_y - (screw_head == "csk" ? csk_d : pan_d) / 2 - face_chamfer >= 1.5, "screw head breaks out of the bezel side");
assert(x_lead - (amp_bay ? screw_x2 : screw_x) - (screw_head == "csk" ? csk_d : pan_d) / 2 >= 1.5, "screw head breaks out of the bezel end");
assert(foot_d <= 20 && (!saddle || 6 <= 20), "bridge over 20 mm");
assert(max(body_l, body_w) <= bed, "footprint exceeds the bed");
assert(rib_proud <= 0.4, "ribs over 0.4 mm proud will not print as intended; raise clr_xy instead");
assert(lead_slot_floor_z > end_rib_z[1], "lead slot floor must sit above the end rib");

// ---------------------------------------------------------------- 2D helpers
module rrect(l, w, r) { offset(r = r) square([l - 2 * r, w - 2 * r], center = true); }

// outer outline of tray and bezel, inset by `d`
module outline(d = 0) { translate([body_cx, 0]) rrect(body_l - 2 * d, body_w - 2 * d, outer_r - d); }

// window outline, inset by `d`
module window(d = 0) { rrect(win_l - 2 * d, win_w - 2 * d, win_r - d); }

// distance from a point inside a centred rounded rectangle to its boundary
function dist_in_rrect(x, y, l, w, r) =
    let(dx = l / 2 - abs(x), dy = w / 2 - abs(y))
    (dx >= r || dy >= r) ? min(dx, dy) : r - norm([r - dx, r - dy]);

// a 2D profile in (y, z), extruded along +X from x = 0 to x = len
module along_x(len) { rotate([90, 0, 90]) linear_extrude(len) children(); }

// ---------------------------------------------------------------- grille
cone_x = speaker_l / 2 - cone_from_end;                            // cone centres at +-cone_x
rowh = hole_pitch * sqrt(3) / 2;
nj = ceil(win_w / 2 / rowh) + 1;
ni = ceil(win_l / 2 / hole_pitch) + 1;
// one lattice per cone (round patches) or one lattice over the window
hex_origins = grille_patch_r > 0 ? [-cone_x, cone_x] : [0];
// each point carries the origin of the lattice it belongs to: [x, y, ox]
hex_pts = [for (ox = hex_origins) for (j = [-nj : nj]) for (i = [-ni : ni])
           [(i + (abs(j) % 2) * 0.5) * hole_pitch + ox, j * rowh, ox]];
assert(grille_patch_r <= 0 || grille_patch_r - hole_d / 2 < cone_x, "grille patches would overlap at the centre");

// inside the round patch of its OWN lattice (a hole from the other cone's lattice is never kept)
function in_patch(x, y, ox) = grille_patch_r <= 0 || norm([x - ox, y]) <= grille_patch_r - hole_d / 2;
// a hex hole is kept only inside its patch and if its web to the window edge and the centre bar is wide enough
function hole_ok(x, y, ox) =
    in_patch(x, y, ox)
    && dist_in_rrect(x, y, win_l, win_w, win_r) - hole_d / 2 >= grille_min_web
    && (!centre_bar || abs(x) - centre_bar_w / 2 - hole_d / 2 >= grille_min_web);

// slots across the width: centred at x = k * slot_pitch
slot_len = win_w - 2 * grille_min_web;
nk = ceil(win_l / 2 / slot_pitch) + 1;
function slot_ok(x) =
    win_l / 2 - abs(x) - slot_w / 2 >= grille_min_web
    && (!centre_bar || abs(x) - centre_bar_w / 2 - slot_w / 2 >= grille_min_web);
slot_xs = [for (k = [-nk : nk]) if (slot_ok(k * slot_pitch)) k * slot_pitch];

// bars along the length: openings bar_gap wide centred at y = m * (bar_w + bar_gap)
bar_pitch = bar_w + bar_gap;
nm = ceil(win_w / 2 / bar_pitch) + 1;
function bar_ok(y) = win_w / 2 - abs(y) - bar_gap / 2 >= grille_min_web;
bar_ys = [for (m = [-nm : nm]) if (bar_ok(m * bar_pitch)) m * bar_pitch];
bar_x0 = centre_bar ? centre_bar_w / 2 + grille_min_web : 0;      // opening starts here
bar_x1 = win_l / 2 - grille_min_web;                               // and ends here

// is point (x, y) open to air in the printed grille?
function open_hex(x, y) =
    let(ox = grille_patch_r > 0 ? (x < 0 ? -cone_x : cone_x) : 0,   // which lattice the point belongs to
        j0 = round(y / rowh), rp = (hole_d - hole_shrink) / 2)
    len([for (j = [j0 - 1 : j0 + 1])
         let(off = (abs(j) % 2) * 0.5 * hole_pitch, i = round((x - ox - off) / hole_pitch),
             cx = i * hole_pitch + off + ox, cy = j * rowh)
         if (hole_ok(cx, cy, ox) && norm([x - cx, y - cy]) <= rp) 1]) > 0;
function open_slots(x, y) =
    let(k = round(x / slot_pitch), cx = k * slot_pitch)
    slot_ok(cx) && abs(x - cx) <= (slot_w - hole_shrink) / 2 && abs(y) <= slot_len / 2 - hole_shrink / 2;
function open_bars(x, y) =
    let(m = round(y / bar_pitch), cy = m * bar_pitch)
    bar_ok(cy) && abs(y - cy) <= (bar_gap - hole_shrink) / 2 && abs(x) >= bar_x0 && abs(x) <= bar_x1;
function open_open(x, y) =
    dist_in_rrect(x, y, win_l, win_w, win_r) > 0 && (!centre_bar || abs(x) > centre_bar_w / 2);
function open_at(x, y) =
    grille_style == "hex" ? open_hex(x, y) :
    grille_style == "slots" ? open_slots(x, y) :
    grille_style == "bars" ? open_bars(x, y) : open_open(x, y);

// fraction of a cone disc centred at (cx, 0) that is open, sampled on a 0.5 mm grid
function cone_open_fraction(cx) =
    let(s = 0.5, n = ceil(cone_d / 2 / s),
        pts = [for (i = [-n : n]) for (k = [-n : n]) if (norm([i * s, k * s]) <= cone_d / 2) open_at(cx + i * s, k * s)])
    len([for (v = pts) if (v) 1]) / len(pts);
open_a = cone_open_fraction(-cone_x);
open_b = cone_open_fraction(cone_x);
echo(str("grille '", grille_style, "': printed open area over the cones = ",
         round(open_a * 1000) / 10, " % and ", round(open_b * 1000) / 10,
         " % (holes assumed ", hole_shrink, " mm under size)"));
assert(min(open_a, open_b) >= 0.40, "grille open area over a cone is under 40 %");

// the openings, in the bezel's local frame (front face at z = 0), cut through grille_t
module grille_cuts() {
    if (grille_style == "hex")
        for (p = hex_pts) if (hole_ok(p[0], p[1], p[2])) translate([p[0], p[1], 0]) {
            translate([0, 0, -1]) cylinder(d = hole_d, h = grille_t + 2, $fn = 48);
            translate([0, 0, -eps]) cylinder(d1 = hole_d + 2 * hole_chamfer, d2 = hole_d, h = hole_chamfer, $fn = 48);
        }
    if (grille_style == "slots")
        for (x = slot_xs) translate([x, 0, 0]) {
            translate([0, 0, -1]) linear_extrude(grille_t + 2) rrect(slot_w, slot_len, slot_w / 2 - eps);
            hull() {
                translate([0, 0, -eps]) linear_extrude(eps) rrect(slot_w + 2 * hole_chamfer, slot_len + 2 * hole_chamfer, slot_w / 2 + hole_chamfer - eps);
                translate([0, 0, hole_chamfer]) linear_extrude(eps) rrect(slot_w, slot_len, slot_w / 2 - eps);
            }
        }
    if (grille_style == "bars")
        for (y = bar_ys, s = (centre_bar ? [-1, 1] : [0]))
            translate([s * (bar_x0 + bar_x1) / 2, y, 0]) {
                translate([0, 0, -1]) linear_extrude(grille_t + 2) rrect(bar_x1 - bar_x0, bar_gap, bar_gap / 2 - eps);
                hull() {
                    translate([0, 0, -eps]) linear_extrude(eps) rrect(bar_x1 - bar_x0 + 2 * hole_chamfer, bar_gap + 2 * hole_chamfer, bar_gap / 2 + hole_chamfer - eps);
                    translate([0, 0, hole_chamfer]) linear_extrude(eps) rrect(bar_x1 - bar_x0, bar_gap, bar_gap / 2 - eps);
                }
            }
    if (grille_style == "open")
        difference() {
            translate([0, 0, -1]) linear_extrude(grille_t + 2) window();
            if (centre_bar) translate([-centre_bar_w / 2, -win_w, -2]) cube([centre_bar_w, 2 * win_w, grille_t + 4]);
        }
}

// ---------------------------------------------------------------- bezel (local frame: front face on the bed)
module bezel_local() {
    difference() {
        hull() {
            linear_extrude(eps) outline(face_chamfer);
            translate([0, 0, face_chamfer]) linear_extrude(band_t - face_chamfer - seam_chamfer) outline(0);
            translate([0, 0, band_t - eps]) linear_extrude(eps) outline(seam_chamfer);
        }
        // thin the window down to the grille plate, from the back, leaving the centre bar
        difference() {
            translate([0, 0, grille_t]) linear_extrude(band_t) window();
            if (centre_bar) translate([-centre_bar_w / 2, -win_w, 0]) cube([centre_bar_w, 2 * win_w, band_t + 2]);
        }
        grille_cuts();
        for (p = screw_pts) translate([p[0], p[1], 0]) {
            translate([0, 0, -1]) cylinder(d = screw_clear_d, h = band_t + 2, $fn = 32);
            if (screw_head == "csk")
                translate([0, 0, -eps]) cylinder(d1 = csk_d + 2 * eps, d2 = screw_clear_d, h = (csk_d - screw_clear_d) / 2 + eps, $fn = 48);
            else
                translate([0, 0, -eps]) cylinder(d = pan_d, h = pan_depth + eps, $fn = 48);
        }
    }
}

// ---------------------------------------------------------------- tray
module tray_body() {
    hull() {
        linear_extrude(eps) outline(elephant_foot);
        translate([0, 0, elephant_foot]) linear_extrude(rim_z - elephant_foot - seam_chamfer) outline(0);
        translate([0, 0, rim_z - eps]) linear_extrude(eps) outline(seam_chamfer);
    }
}

module pocket() {
    translate([0, 0, floor_t]) linear_extrude(rim_z) rrect(pocket_l, pocket_w, pocket_corner_r);
    // lead-in at the rim: full chamfer on the long sides, a small one on the end walls
    hull() {
        translate([0, 0, rim_z - pocket_entry_chamfer]) linear_extrude(eps) rrect(pocket_l, pocket_w, pocket_corner_r);
        translate([0, 0, rim_z]) linear_extrude(eps)
            rrect(pocket_l + 2 * pocket_entry_chamfer_end, pocket_w + 2 * pocket_entry_chamfer, pocket_corner_r + pocket_entry_chamfer_end);
    }
}

module standoff_pads() {
    for (sx = [-1, 1], sy = [-1, 1])
        translate([sx * pad_xy[0] - pad_size[0] / 2, sy * pad_xy[1] - pad_size[1] / 2, floor_t - eps])
            cube([pad_size[0], pad_size[1], pad_h + eps]);
}

// Locating ribs: an elliptical post whose centre sits `rib_embed` inside the wall, so
// it crosses the wall plane cleanly (no tangent sliver faces) and stands rib_proud
// into the pocket with a chord of rib_w at the wall.
rib_embed = 0.3;
rib_scale = sqrt(1 - pow(rib_embed / (rib_proud + rib_embed), 2));   // chord factor at the wall plane
module ribs(z0, z1, side_x = rib_x) {
    for (x = side_x, s = [-1, 1])
        translate([x, s * (pocket_w / 2 + rib_embed), z0])
            scale([rib_w / 2 / rib_scale, rib_proud + rib_embed, 1]) cylinder(r = 1, h = z1 - z0, $fn = 24);
}
module end_ribs(z0, z1) {
    for (s = [-1, 1])
        translate([s * (pocket_l / 2 + rib_embed), 0, z0])
            scale([rib_proud + rib_embed, end_rib_w / 2 / rib_scale, 1]) cylinder(r = 1, h = z1 - z0, $fn = 24);
}

// U-slot in the lead-end pocket wall, open at the top, laid in from above
module lead_slot(side) {
    y0 = side * lead_slot_y0;
    y1 = side * (lead_slot_y0 + lead_slot_w);
    r = 2;
    translate([pocket_l / 2 - 1, 0, 0]) along_x(pocket_wall_end + 2)
        hull() {
            translate([min(y0, y1) + r, lead_slot_floor_z + r]) circle(r = r, $fn = 24);
            translate([max(y0, y1) - r, lead_slot_floor_z + r]) circle(r = r, $fn = 24);
            translate([min(y0, y1), rim_z]) square([lead_slot_w, 5]);
        }
    // soften both edges of the slot floor that the leads bend over (they cross the wall in X)
    for (xe = [pocket_l / 2, pocket_l / 2 + pocket_wall_end])
        translate([xe, (y0 + y1) / 2, lead_slot_floor_z]) rotate([0, 45, 0])
            cube([cable_chamfer * sqrt(2), lead_slot_w - 2 * r, cable_chamfer * sqrt(2)], center = true);
}

module cable_bay() {
    translate([bay_x0, -bay_y / 2, floor_t]) cube([bay_l_lead, bay_y, rim_z]);
}

// teardrop: round bottom, straight sides, 45 degree apex (no bridge)
module teardrop2d(w, h, z0) {
    hull() {
        translate([0, z0 + w / 2]) circle(d = w, $fn = 32);
        polygon([[-w / 2, z0 + w / 2], [w / 2, z0 + w / 2], [w / 2, z0 + h - w / 2], [0, z0 + h], [-w / 2, z0 + h - w / 2]]);
    }
}
module exit_slot() {
    w = amp_bay ? amp_exit_slot[0] : exit_slot_w;
    h = amp_bay ? amp_exit_slot[1] : exit_slot_h;
    if (cable_exit == "end") {
        translate([bay_x1 - 1, 0, 0]) along_x(outer_wall_end + 2) teardrop2d(w, h, exit_z0);
        // 45 degree flare on the outer edge the cable bends over
        hull() {
            translate([x_lead - cable_chamfer, 0, 0]) along_x(eps) teardrop2d(w, h, exit_z0);
            translate([x_lead + 1, 0, 0]) along_x(eps) offset(r = cable_chamfer + 1) teardrop2d(w, h, exit_z0);
        }
    }
    if (cable_exit == "back")
        translate([saddle_x, 10, -1]) linear_extrude(floor_t + 2) rrect(w, h, w / 2 - eps);
}

module saddle_posts() {
    if (saddle) {
        for (s = [-1, 1]) translate([saddle_x - 1.5, s * 4.5 - 1.5, floor_t - eps]) cube([3, 3, 7 + eps]);
        translate([saddle_x - 1.5, -6, floor_t + 4.5]) cube([3, 12, 2.5]);   // 6 mm bridge at z 6.5-9.0
    }
}

module pilot_hole() {
    translate([0, 0, rim_z - pilot_depth]) cylinder(d = pilot_d, h = pilot_depth + 1, $fn = 24);
    if (fastener == "insert")
        translate([0, 0, rim_z - insert_depth]) cylinder(d = insert_d, h = insert_depth + 1, $fn = 32);
    if (rear_bore_d > 0)
        translate([0, 0, -1]) cylinder(d = rear_bore_d, h = wall_mount_prep ? rim_z + 2 : rim_z - pilot_depth + 1, $fn = 32);
}

module keyhole_cut() {
    // slot runs toward +y from the entry circle
    translate([0, -keyhole_slot_len / 2, -1]) cylinder(d = keyhole_head_d, h = floor_t + 2, $fn = 48);
    hull() {
        translate([0, -keyhole_slot_len / 2, -1]) cylinder(d = keyhole_slot_w, h = floor_t + 2, $fn = 32);
        translate([0, keyhole_slot_len / 2, -1]) cylinder(d = keyhole_slot_w, h = floor_t + 2, $fn = 32);
    }
}
module keyholes_cut() {
    if (keyholes) {
        if (keyhole_dir == "+y")
            for (s = [-1, 1]) translate([s * keyhole_spacing / 2, 0, 0]) keyhole_cut();
        else
            for (s = [-1, 1]) translate([0, s * 14, 0]) rotate([0, 0, -90]) keyhole_cut();
    }
}

module strap_slots_cut() {
    if (strap_slots)
        for (sx = [-1, 1], sy = [-1, 1])
            translate([sx * strap_x - strap_w / 2, sy * strap_y - strap_t / 2, -1]) cube([strap_w, strap_t, floor_t + 2]);
}

module feet_cut() {
    if (feet_recess)
        for (sx = [-1, 1], sy = [-1, 1])
            translate([sx * foot_pos[0], sy * foot_pos[1], -1]) cylinder(d = foot_d, h = 1 + foot_depth, $fn = 48);
}

module ear_boss_posts() {
    if (ear_bosses) {
        top = rim_z - ear_plane - 0.5;   // 0.5 under the ear's rear face
        for (sx = [-1, 1], sy = [-1, 1])
            translate([sx * ear_pattern[0] / 2, sy * ear_pattern[1] / 2, floor_t - eps])
                difference() {
                    cylinder(d = ear_boss_d, h = top - floor_t + eps, $fn = 32);
                    translate([0, 0, top - floor_t - 10]) cylinder(d = pilot_d, h = 11, $fn = 24);
                }
    }
}

// amp bay furniture: corner pads for the board, hold-down features
amp_cx = bay_x0 + 3.5 + 3.5 + 1 + (amp_board[0] + 0.4) / 2 + 2.6;  // past the saddle, zip slot, clearance
module amp_pads() {
    if (amp_bay) {
        bl = amp_board[0] + 0.4; bw = amp_board[1] + 0.4;
        for (sx = [-1, 1], sy = [-1, 1])
            translate([amp_cx + sx * (bl / 2 - 1.5) - 1.5, sy * (bw / 2 - 1.5) - 1.5, floor_t - eps]) cube([3, 3, 3 + eps]);
        if (amp_holddown == "screws")
            for (h = amp_holes)
                translate([amp_cx + h[0], h[1], floor_t - eps]) difference() {
                    cylinder(d = 5, h = 3 + eps, $fn = 24);
                    translate([0, 0, -1]) cylinder(d = 2.0, h = 5, $fn = 16);
                }
    }
}
module amp_cuts() {
    if (amp_bay && amp_holddown == "zip") {
        bl = amp_board[0] + 0.4;
        for (sx = [-1, 1])
            translate([amp_cx + sx * (bl / 2 + 1.8) - 0.8, -1.75, -1]) cube([1.6, 3.5, floor_t + 2]);
    }
}

module tray() {
    difference() {
        tray_body();
        pocket();
        cable_bay();
        if (lead_slots == "both" || lead_slots == "left") lead_slot(1);
        if (lead_slots == "both" || lead_slots == "right") lead_slot(-1);
        exit_slot();
        for (p = screw_pts) translate([p[0], p[1], 0]) pilot_hole();
        keyholes_cut();
        strap_slots_cut();
        feet_cut();
        amp_cuts();
    }
    standoff_pads();
    ribs(rib_z[0], rib_z[1]);
    end_ribs(end_rib_z[0], end_rib_z[1]);
    saddle_posts();
    ear_boss_posts();
    amp_pads();
}

// a slice of the pocket, with every rib, to test the fit before printing the tray
coupon_h = 6;
coupon_wall = 2.0;
module coupon() {
    difference() {
        union() {
            difference() {
                linear_extrude(coupon_h) rrect(pocket_l + 2 * coupon_wall, pocket_w + 2 * coupon_wall, pocket_corner_r + coupon_wall);
                translate([0, 0, -1]) linear_extrude(coupon_h + 2) rrect(pocket_l, pocket_w, pocket_corner_r);
            }
            ribs(0, coupon_h);
            end_ribs(0, coupon_h);
        }
        // the entry chamfer trims the rib tops too
        hull() {
            translate([0, 0, coupon_h - pocket_entry_chamfer]) linear_extrude(eps) rrect(pocket_l, pocket_w, pocket_corner_r);
            translate([0, 0, coupon_h + 1]) linear_extrude(eps)
                rrect(pocket_l + 2 * (pocket_entry_chamfer_end + 1), pocket_w + 2 * (pocket_entry_chamfer + 1), pocket_corner_r + pocket_entry_chamfer_end + 1);
        }
    }
}

// ---------------------------------------------------------------- views
module orient() { if (le < 0) mirror([1, 0, 0]) children(); else children(); }

module bezel_in_place() { translate([0, 0, rim_z + band_t]) mirror([0, 0, 1]) bezel_local(); }

module speaker_in_place() { translate([0, 0, rim_z]) speaker_ce08483(); }

module assembly(explode = 0) {
    orient() {
        color("SteelBlue") tray();
        color("LightSteelBlue") translate([0, 0, explode]) bezel_in_place();
        if (show_speaker) translate([0, 0, explode / 2]) speaker_in_place();
    }
}

module printplate() {
    gap = 5;
    translate([-body_cx, 0, 0]) orient() tray();
    translate([-body_cx, body_w + gap, 0]) orient() bezel_local();
    translate([0, body_w + gap + body_w / 2 + gap + (pocket_w + 2 * coupon_wall) / 2, 0]) coupon();
}

if (part == "tray") orient() tray();
else if (part == "bezel") orient() bezel_local();
else if (part == "coupon") coupon();
else if (part == "printplate") printplate();
else if (part == "bezel_inplace") orient() bezel_in_place();
else if (part == "envelope") orient() translate([0, 0, rim_z]) speaker_envelope();
else if (part == "speaker") orient() translate([0, 0, rim_z]) speaker_ce08483(leads = false, cones = false);
else if (part == "section")
    intersection() {
        assembly();
        translate([-200, -200, -1]) cube([400, 200, 100]);
    }
else assembly(explode);

echo(str("outer size ", body_l, " x ", body_w, " x ", rim_z + band_t, " mm; rim at z = ", rim_z,
         "; speaker back at z = ", spk_back_z, " (floor gap ", spk_back_z - floor_t, ")"));
