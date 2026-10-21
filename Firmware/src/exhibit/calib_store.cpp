#include "calib_store.h"

#include <Preferences.h>

namespace calib_store {
namespace {

Preferences g_prefs;
int         g_unit = 1;

String key()
{
    return String("scale") + g_unit;
}

} // namespace

int begin(bool advance)
{
    g_prefs.begin("edge-cal", false);
    g_unit = constrain(static_cast<int>(g_prefs.getUChar("unit", 1)), 1, kUnits);
    if (advance) {
        g_unit = g_unit % kUnits + 1;
        g_prefs.putUChar("unit", static_cast<uint8_t>(g_unit));
    }
    return g_unit;
}

int active_unit()
{
    return g_unit;
}

float lamp_scale()
{
    const String k = key();
    return g_prefs.isKey(k.c_str()) ? g_prefs.getFloat(k.c_str(), 0.0f) : 0.0f;
}

void set_lamp_scale(float lux_per_amp)
{
    g_prefs.putFloat(key().c_str(), lux_per_amp);
}

} // namespace calib_store
