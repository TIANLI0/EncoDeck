// SPDX-License-Identifier: GPL-3.0-only
#include "iso_pair.h"

int IsoPairInitialize(IsoPairState *state, uint64_t generation, uint32_t interval_us)
{
    *state = (IsoPairState){ 0 };
    if (!generation || (interval_us != 7500 && interval_us != 10000)) {
        return 0;
    }

    state->generation = generation;
    state->frame_samples = (uint16_t)(interval_us * 48 / 1000);
    return 1;
}

IsoPairResult IsoPairSubmit(IsoPairState *state, const IsoPairFrame *left,
    const IsoPairFrame *right, IsoPairWindow window, IsoPairCommit commit, void *queue)
{
    if (!state->generation || !left->payload || !right->payload ||
        left->generation != state->generation || right->generation != state->generation ||
        left->first_pcm_sample != right->first_pcm_sample ||
        left->first_pcm_sample > UINT64_MAX - state->frame_samples) {
        return IsoPairInsufficientEvidence;
    }
    if (state->started && left->first_pcm_sample != state->next_sample) {
        return IsoPairInsufficientEvidence;
    }

    uint64_t first_common = window.earliest_left > window.earliest_right ?
        window.earliest_left : window.earliest_right;
    uint64_t target = state->started ? state->next_event : first_common;
    // An already-playing stream keeps its cadence. Report a missed slot without
    // making a gap, repeating a frame or committing only one channel.
    if (target < first_common || target > window.latest_common || target == UINT64_MAX) {
        return IsoPairMissedDeadline;
    }
    uint64_t next_sample = left->first_pcm_sample + state->frame_samples;
    if (!commit(queue, target, left, right)) {
        return IsoPairQueueRejected;
    }

    state->started = 1;
    state->next_sample = next_sample;
    state->next_event = target + 1;
    return IsoPairCommitted;
}

IsoPairAlignment IsoPairCompare(uint32_t interval_us, int verified_clock,
    IsoPairObservation left, IsoPairObservation right)
{
    if (!verified_clock || (interval_us != 7500 && interval_us != 10000) ||
        !left.generation || left.generation != right.generation ||
        left.cis_handle == right.cis_handle || left.first_pcm_sample != right.first_pcm_sample) {
        return IsoPairUnknown;
    }

    uint32_t delta = left.cig_reference_us - right.cig_reference_us;
    if (delta == 0) {
        return IsoPairAligned;
    }
    if (delta == interval_us) {
        return IsoPairLeftLate;
    }
    if (delta == (uint32_t)0 - interval_us) {
        return IsoPairRightLate;
    }
    return IsoPairOtherOffset;
}
