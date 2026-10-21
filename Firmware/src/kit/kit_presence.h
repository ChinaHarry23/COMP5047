// kit_presence.h - FW0 presence: the kit pushbutton, or kitchen foil as a touch pad.
//
// Both go through the core's presence filter (100 ms debounce). The choice
// is stored in NVS by the `button` / `touch` serial commands and survives a
// reboot, so the check-in demo comes up the way it was set up.
#pragma once

#include <Arduino.h>

#include "presence_filter.h"
#include "touch_presence.h"

class KitPresence {
public:
    void begin(bool use_touch);
    bool update(uint32_t now_ms, bool needle_quiet); // needle_quiet: NeedleServo::quiet()
    bool using_touch() const { return use_touch_; }
    float reading() const;

private:
    bool            use_touch_ = false;
    edge_presence_t button_{};
    TouchPresence   touch_;
};
