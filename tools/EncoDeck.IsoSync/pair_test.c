// SPDX-License-Identifier: GPL-3.0-only
#include "iso_pair.h"

#include <assert.h>
#include <stdio.h>
#include <string.h>

typedef struct TestQueue {
    uint64_t event;
    const void *left;
    const void *right;
    int accepted;
    int reject;
} TestQueue;

static int Commit(void *context, uint64_t event, const IsoPairFrame *left, const IsoPairFrame *right)
{
    TestQueue *queue = context;
    if (queue->reject) {
        return 0;
    }
    queue->event = event;
    queue->left = left->payload;
    queue->right = right->payload;
    queue->accepted++;
    return 1;
}

static void CheckProfile(uint32_t interval, uint16_t samples)
{
    const uint8_t left_payload[] = { 0xaa, 0x91 };
    const uint8_t right_payload[] = { 0x10, 0x99 };
    TestQueue queue = { 0 };
    IsoPairState state;
    assert(IsoPairInitialize(&state, 11, interval));
    assert(state.frame_samples == samples);

    IsoPairFrame left = { 11, 0, left_payload };
    IsoPairFrame right = { 11, samples, right_payload };
    IsoPairWindow window = { 101, 102, 103 };
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairInsufficientEvidence);
    assert(!state.started && queue.accepted == 0);
    right.first_pcm_sample = 0;
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairCommitted);
    assert(queue.accepted == 1 && queue.event == 102);
    assert(queue.left == left_payload && queue.right == right_payload);
    assert(memcmp(queue.left, "\xaa\x91", 2) == 0 && memcmp(queue.right, "\x10\x99", 2) == 0);

    // Retry after an atomic queue rejection. The cadence and sample cursor survive.
    left.first_pcm_sample = right.first_pcm_sample = samples;
    queue.reject = 1;
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairQueueRejected);
    assert(state.next_sample == samples && state.next_event == 103 && queue.accepted == 1);
    queue.reject = 0;
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairCommitted);
    assert(queue.event == 103 && queue.accepted == 2);

    // A late ear during playback cannot move one side or insert a hole in the stream.
    left.first_pcm_sample = right.first_pcm_sample = 2u * samples;
    window = (IsoPairWindow){ 104, 105, 106 };
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairMissedDeadline);
    assert(state.next_event == 104 && queue.accepted == 2);
    assert(IsoPairInitialize(&state, 12, interval));
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairInsufficientEvidence);
    assert(queue.accepted == 2);
    assert(!IsoPairInitialize(&state, 12, 12500));
    assert(IsoPairSubmit(&state, &left, &right, window, Commit, &queue) == IsoPairInsufficientEvidence);

    IsoPairObservation left_obs = { 11, 0, 100000, 0x0900 };
    IsoPairObservation right_obs = { 11, 0, 100000, 0x0901 };
    assert(IsoPairCompare(interval, 0, left_obs, right_obs) == IsoPairUnknown);
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairAligned);
    right_obs.first_pcm_sample = samples;
    right_obs.cig_reference_us += interval;
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairUnknown);
    left_obs.first_pcm_sample = samples;
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairRightLate);
    left_obs.cig_reference_us = 0;
    right_obs.cig_reference_us = (uint32_t)0 - interval;
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairLeftLate);
    right_obs.cig_reference_us--;
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairOtherOffset);
    right_obs.generation++;
    assert(IsoPairCompare(interval, 1, left_obs, right_obs) == IsoPairUnknown);
}

int main(void)
{
    CheckProfile(7500, 360);
    CheckProfile(10000, 480);
    puts("Both LC3 profiles passed: unchanged payloads, pair commit, deadlines, clock wrap and CIG reset.");
    return 0;
}
