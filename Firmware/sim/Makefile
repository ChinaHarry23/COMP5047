# Host build of the policy core: unit tests and the scenario simulator.
#
#   make test     build everything, run every unit test and every scenario;
#                 exits non-zero on the first failing suite
#   make clean
#
# The core in ../lib/core is plain C11 with no Arduino headers, so the same
# sources that ship on the FireBeetle compile here. test_kit also compiles the
# kit's light adapter and the shared needle/touch headers (C++) against a fake
# Arduino layer (fake_arduino/), with the firmware's own warning flags.
#
# CORE and BUILD may be absolute paths (e.g. a scratch copy of the core).

CC       ?= cc
CXX      ?= c++
CFLAGS   ?= -std=c11 -O2 -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wno-sign-conversion -Werror
CXXFLAGS ?= -std=c++17 -O2 -Wall -Wextra -Werror
CORE     ?= ../lib/core
BUILD    ?= build
CPPFLAGS  = -I$(CORE) -I.
LDLIBS    = -lm

CORE_SRC = $(wildcard $(CORE)/*.c)
CORE_HDR = $(wildcard $(CORE)/*.h) test_util.h plant_shapes.h
CORE_OBJ = $(patsubst $(CORE)/%.c,$(BUILD)/core/%.o,$(CORE_SRC))

KIT_SRC  = ../src/kit/kit_light.cpp
KIT_HDR  = ../src/kit/kit_light.h ../include/needle_servo.h ../include/touch_presence.h \
           fake_arduino/Arduino.h

UNIT_TESTS = test_sep2 test_dose test_forecast test_parts test_kit
SCENARIOS  = $(sort $(wildcard scenarios/*.scn))

.PHONY: all test unit scenarios clean
all: $(addprefix $(BUILD)/,$(UNIT_TESTS) edge_sim)

$(BUILD) $(BUILD)/core:
	mkdir -p $@

$(BUILD)/%: %.c $(CORE_SRC) $(CORE_HDR) | $(BUILD)
	$(CC) $(CPPFLAGS) $(CFLAGS) -o $@ $< $(CORE_SRC) $(LDLIBS)

$(BUILD)/core/%.o: $(CORE)/%.c $(CORE_HDR) | $(BUILD)/core
	$(CC) $(CPPFLAGS) $(CFLAGS) -c -o $@ $<

$(BUILD)/test_kit: test_kit.cpp $(KIT_SRC) $(KIT_HDR) $(CORE_OBJ) | $(BUILD)
	$(CXX) -Ifake_arduino -I../include -I../src/kit $(CPPFLAGS) $(CXXFLAGS) -o $@ test_kit.cpp $(KIT_SRC) $(CORE_OBJ) $(LDLIBS)

unit: $(addprefix $(BUILD)/,$(UNIT_TESTS))
	@set -e; for t in $(UNIT_TESTS); do $(BUILD)/$$t; done

scenarios: $(BUILD)/edge_sim
	$(BUILD)/edge_sim $(SCENARIOS)

test: unit scenarios

clean:
	rm -rf $(BUILD)
