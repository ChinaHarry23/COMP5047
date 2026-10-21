#include "kit_presence.h"

#include "pins.h"

void KitPresence::begin(bool use_touch)
{
    use_touch_ = use_touch;
    pinMode(PIN_KIT_BUTTON, INPUT_PULLUP);
    edge_presence_init(&button_);
    edge_presence_set_thresholds(&button_, 0.5f, 0.5f); // digital: pressed reads 1
    if (use_touch_) {
        touch_.begin(PIN_TOUCH);
    }
}

bool KitPresence::update(uint32_t now_ms, bool needle_quiet)
{
    if (use_touch_) {
        return touch_.update(now_ms, needle_quiet);
    }
    const float pressed = digitalRead(PIN_KIT_BUTTON) == LOW ? 1.0f : 0.0f;
    return edge_presence_update(&button_, now_ms, pressed);
}

float KitPresence::reading() const
{
    return use_touch_ ? touch_.reading() : (digitalRead(PIN_KIT_BUTTON) == LOW ? 1.0f : 0.0f);
}
