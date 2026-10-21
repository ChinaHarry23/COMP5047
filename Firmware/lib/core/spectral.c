#include "spectral.h"

const float edge_spec_centre_nm[EDGE_SPEC_NCH] = {
    405.0f, 425.0f, 450.0f, 475.0f, 515.0f, 550.0f,
    555.0f, 600.0f, 640.0f, 690.0f, 745.0f, 855.0f,
};

const char *const edge_spec_name[EDGE_SPEC_NCH] = {
    "F1", "F2", "FZ", "F3", "F4", "F5", "FY", "FXL", "F6", "F7", "F8", "NIR",
};
