// SPDX-License-Identifier: GPL-3.0-only
#ifndef ENCODECK_ISO_PAIR_H
#define ENCODECK_ISO_PAIR_H

#include <stdint.h>

typedef struct IsoPairFrame {
    uint64_t generation;
    uint64_t first_pcm_sample;
    const void *payload;
} IsoPairFrame;

typedef struct IsoPairWindow {
    // Controller event indices, unwrapped within this CIG generation.
    uint64_t earliest_left;
    uint64_t earliest_right;
    uint64_t latest_common;
} IsoPairWindow;

typedef struct IsoPairState {
    uint64_t generation;
    uint64_t next_sample;
    uint64_t next_event;
    uint16_t frame_samples;
    int started;
} IsoPairState;

typedef enum IsoPairResult {
    IsoPairCommitted,
    IsoPairInsufficientEvidence,
    IsoPairMissedDeadline,
    IsoPairQueueRejected,
} IsoPairResult;

typedef struct IsoPairObservation {
    uint64_t generation;
    uint64_t first_pcm_sample;
    uint32_t cig_reference_us;
    uint16_t cis_handle;
} IsoPairObservation;

typedef enum IsoPairAlignment {
    IsoPairUnknown,
    IsoPairAligned,
    IsoPairLeftLate,
    IsoPairRightLate,
    IsoPairOtherOffset,
} IsoPairAlignment;

// The actual encoder/ISO queue must commit both frames or neither. Return 0
// on rejection without retaining either frame. The caller serializes use of
// each state, including resets, and validates the handle-to-ear mapping.
typedef int (*IsoPairCommit)(void *queue, uint64_t event, const IsoPairFrame *left,
    const IsoPairFrame *right);

// For 48 kHz, one LC3 frame per SDU, unframed with equal SDU and ISO intervals.
int IsoPairInitialize(IsoPairState *state, uint64_t generation, uint32_t interval_us);
IsoPairResult IsoPairSubmit(IsoPairState *state, const IsoPairFrame *left,
    const IsoPairFrame *right, IsoPairWindow window, IsoPairCommit commit, void *queue);

// Each observation must already map the CIG reference to a verified source
// sample. The caller establishes that timestamps advance on a shared clock.
IsoPairAlignment IsoPairCompare(uint32_t interval_us, int verified_clock,
    IsoPairObservation left, IsoPairObservation right);

#endif
