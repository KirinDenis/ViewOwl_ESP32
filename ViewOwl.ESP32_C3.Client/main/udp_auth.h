#pragma once

#include <stdbool.h>

/**
 * @brief M3a - send one HELLO to the server and check the AUTH response.
 *
 * Proves the wire contract (shared packet.h) + that this device's token is
 * registered server-side. No frame transfer yet. Uses the token from NVS
 * (provisioned) with the config.h TOKEN_BYTES fallback.
 *
 * @return true if the server replied PACKET_AUTH; false on PACKET_ERROR / timeout.
 */
bool udp_auth_test(void);
