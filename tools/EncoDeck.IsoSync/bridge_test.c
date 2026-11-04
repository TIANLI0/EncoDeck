// SPDX-License-Identifier: GPL-3.0-only
#include "iso_sync_bridge.h"

#include <assert.h>
#include <stdio.h>
#include <string.h>

int main(void)
{
    const uint8_t version_request[] = { 0x05, 0xfc, 1, 0xff };
    const uint8_t left_request[] = { 0x61, 0x20, 2, 0x00, 0x09 };
    const uint8_t right_request[] = { 0x61, 0x20, 2, 0x01, 0x09 };
    uint8_t wire_command[ISO_SYNC_COMMAND_SIZE];
    uint8_t version_reply[132] = { 0x0e, 130, 1, 0x05, 0xfc, 0 };
    uint8_t sync_reply[] = { 0x0e, 15, 2, 0x61, 0x20, 0, 0x00, 0x09, 0x34, 0x12,
        0x78, 0x56, 0x34, 0x12, 0x03, 0x02, 0x01 };
    uint8_t wrong_handle[] = { 0x0e, 15, 1, 0x61, 0x20, 0, 0x00, 0x09, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    uint8_t unknown_command[] = { 0x0e, 4, 1, 0x61, 0x20, 0x01 };
    uint8_t disallowed[] = { 0x0f, 4, 0x0c, 1, 0x61, 0x20 };
    uint8_t command_status[] = { 0x0f, 4, 0, 0, 0x61, 0x20 };
    uint8_t status = 0xff;
    IsoSyncBridge bridge = { 0 };
    IsoSyncSample sample = { 0 };

    assert(IsoSyncComplete(&bridge, sync_reply, sizeof(sync_reply), &status, &sample) == IsoSyncUnrelated);
    assert(sync_reply[3] == 0x61 && sync_reply[4] == 0x20);
    assert(!IsoSyncPrepare(&bridge, 0x0900, version_request, 3, wire_command));
    assert(!IsoSyncPrepare(&bridge, 0x0f00, version_request, sizeof(version_request), wire_command));
    assert(IsoSyncPrepare(&bridge, 0x0900, version_request, sizeof(version_request), wire_command));
    assert(memcmp(wire_command, left_request, sizeof(left_request)) == 0);
    assert(!IsoSyncPrepare(&bridge, 0x0901, version_request, sizeof(version_request), wire_command));
    assert(IsoSyncComplete(&bridge, version_reply, sizeof(version_reply), &status, &sample) == IsoSyncUnrelated);
    assert(IsoSyncComplete(&bridge, sync_reply, sizeof(sync_reply) - 1, &status, &sample) == IsoSyncUnrelated);
    assert(bridge.pending && status == 0xff);
    assert(IsoSyncComplete(&bridge, command_status, sizeof(command_status), &status, &sample) == IsoSyncAwaitingComplete);
    assert(bridge.pending && command_status[3] == 0 && command_status[4] == 0x05 && command_status[5] == 0xfc);
    assert(IsoSyncComplete(&bridge, sync_reply, sizeof(sync_reply), &status, &sample) == IsoSyncReadSample);
    assert(!bridge.pending && status == 0 && sync_reply[2] == 2 && sync_reply[3] == 0x05 && sync_reply[4] == 0xfc);
    assert(sample.handle == 0x0900 && sample.sequence == 0x1234 &&
        sample.timestamp_us == 0x12345678 && sample.offset_us == 0x010203);

    assert(IsoSyncPrepare(&bridge, 0x0901, version_request, sizeof(version_request), wire_command));
    assert(memcmp(wire_command, right_request, sizeof(right_request)) == 0);
    assert(IsoSyncComplete(&bridge, wrong_handle, sizeof(wrong_handle), &status, &sample) == IsoSyncInvalidPayload);
    assert(!bridge.pending && wrong_handle[2] == 1 && wrong_handle[3] == 0x05 && wrong_handle[4] == 0xfc);
    assert(IsoSyncPrepare(&bridge, 0x0901, version_request, sizeof(version_request), wire_command));
    assert(IsoSyncComplete(&bridge, unknown_command, sizeof(unknown_command), &status, &sample) == IsoSyncControllerError);
    assert(!bridge.pending && status == 0x01 && unknown_command[3] == 0x05 && unknown_command[4] == 0xfc);
    assert(IsoSyncPrepare(&bridge, 0x0901, version_request, sizeof(version_request), wire_command));
    assert(IsoSyncComplete(&bridge, disallowed, sizeof(disallowed), &status, &sample) == IsoSyncControllerError);
    assert(!bridge.pending && status == 0x0c && disallowed[3] == 1 && disallowed[4] == 0x05 && disallowed[5] == 0xfc);

    puts("ISO sync bridge passed: wire bytes, credits, fragmented input, errors and handle validation.");
    return 0;
}
