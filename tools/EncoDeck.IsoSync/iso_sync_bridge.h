// SPDX-License-Identifier: GPL-3.0-only
#ifndef ENCODECK_ISO_SYNC_BRIDGE_H
#define ENCODECK_ISO_SYNC_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

#define ISO_SYNC_COMMAND_SIZE 5

typedef struct IsoSyncBridge {
    uint16_t handle;
    int pending;
} IsoSyncBridge;

typedef struct IsoSyncSample {
    uint16_t handle;
    uint16_t sequence;
    uint32_t timestamp_us;
    uint32_t offset_us;
} IsoSyncSample;

typedef enum IsoSyncResult {
    IsoSyncUnrelated,
    IsoSyncAwaitingComplete,
    IsoSyncControllerError,
    IsoSyncInvalidPayload,
    IsoSyncReadSample,
} IsoSyncResult;

// The transport must serialize access and authorize this exact host transaction
// before calling Prepare. It must provide a complete event from the same radio.
int IsoSyncPrepare(IsoSyncBridge *bridge, uint16_t handle, const uint8_t *host_command,
    size_t size, uint8_t wire_command[ISO_SYNC_COMMAND_SIZE]);
IsoSyncResult IsoSyncComplete(IsoSyncBridge *bridge, uint8_t *event, size_t size,
    uint8_t *status, IsoSyncSample *sample);

#endif
