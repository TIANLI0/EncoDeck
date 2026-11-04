// SPDX-License-Identifier: GPL-3.0-only
#include "iso_sync_bridge.h"

#define INTEL_READ_VERSION 0xfc05
#define LE_READ_ISO_TX_SYNC 0x2061
#define HCI_COMMAND_COMPLETE 0x0e
#define HCI_COMMAND_STATUS 0x0f

static uint16_t ReadU16(const uint8_t *data)
{
    return (uint16_t)((uint16_t)data[0] | ((uint16_t)data[1] << 8));
}

static uint32_t ReadU24(const uint8_t *data)
{
    return (uint32_t)data[0] | ((uint32_t)data[1] << 8) | ((uint32_t)data[2] << 16);
}

static void WriteU16(uint8_t *data, uint16_t value)
{
    data[0] = (uint8_t)value;
    data[1] = (uint8_t)(value >> 8);
}

int IsoSyncPrepare(IsoSyncBridge *bridge, uint16_t handle, const uint8_t *host_command,
    size_t size, uint8_t wire_command[ISO_SYNC_COMMAND_SIZE])
{
    // A genuine, read-only Intel command is the fallback if the filter is absent.
    if (bridge->pending || handle > 0x0eff || size != 4 ||
        ReadU16(host_command) != INTEL_READ_VERSION || host_command[2] != 1 || host_command[3] != 0xff) {
        return 0;
    }

    WriteU16(wire_command, LE_READ_ISO_TX_SYNC);
    wire_command[2] = 2;
    WriteU16(wire_command + 3, handle);
    bridge->handle = handle;
    bridge->pending = 1;
    return 1;
}

IsoSyncResult IsoSyncComplete(IsoSyncBridge *bridge, uint8_t *event, size_t size,
    uint8_t *status, IsoSyncSample *sample)
{
    if (!bridge->pending || size < 6 || size != (size_t)event[1] + 2) {
        return IsoSyncUnrelated;
    }

    if (event[0] == HCI_COMMAND_STATUS && size == 6 && ReadU16(event + 4) == LE_READ_ISO_TX_SYNC) {
        WriteU16(event + 4, INTEL_READ_VERSION);
        *status = event[2];
        if (*status == 0) {
            return IsoSyncAwaitingComplete;
        }
        bridge->pending = 0;
        return IsoSyncControllerError;
    }

    if (event[0] != HCI_COMMAND_COMPLETE || ReadU16(event + 3) != LE_READ_ISO_TX_SYNC) {
        return IsoSyncUnrelated;
    }

    // Restore the Windows scheduler's original opcode, including on an HCI error.
    // The event length, command credit and controller's status stay intact.
    WriteU16(event + 3, INTEL_READ_VERSION);
    bridge->pending = 0;
    *status = event[5];
    if (*status != 0) {
        return IsoSyncControllerError;
    }
    if (size != 17 || ReadU16(event + 6) != bridge->handle) {
        return IsoSyncInvalidPayload;
    }

    sample->handle = bridge->handle;
    sample->sequence = ReadU16(event + 8);
    sample->timestamp_us = ReadU24(event + 10) | ((uint32_t)event[13] << 24);
    sample->offset_us = ReadU24(event + 14);
    return IsoSyncReadSample;
}
