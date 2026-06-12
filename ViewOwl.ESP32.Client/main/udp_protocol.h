#pragma once

#include <stdint.h>

#include <sys/socket.h>
#include <netinet/in.h>

#include "packet.h"

/**
 * @brief Sends a HELLO packet with device identity, display info, and last frame CRC.
 *
 * @param sock       Bound UDP socket.
 * @param dest       Server endpoint.
 * @param buf        Scratch send buffer (at least PACKET_SIZE bytes).
 * @param frame_crc  CRC32 of the last rendered frame (0 = no frame yet).
 */
void udp_protocol_send_hello(int sock, const struct sockaddr_in *dest,
                              uint8_t *buf, uint32_t frame_crc);

/**
 * @brief Sends an ACK for a DATA packet.
 *
 * @param sock        Bound UDP socket.
 * @param dest        Server endpoint.
 * @param buf         Scratch send buffer.
 * @param session_id  Current session id from AUTH.
 * @param seq         Sequence number copied from the DATA header.
 */
void udp_protocol_send_ack(int sock, const struct sockaddr_in *dest,
                            uint8_t *buf, uint16_t session_id, uint32_t seq);

/**
 * @brief Sends a PING heartbeat with current device health metrics.
 *
 * @param sock      Bound UDP socket.
 * @param dest      Server endpoint.
 * @param buf       Scratch send buffer.
 * @param uptime_s  Device uptime in seconds.
 */
void udp_protocol_send_ping(int sock, const struct sockaddr_in *dest,
                             uint8_t *buf, uint32_t uptime_s);

/**
 * @brief Parses and applies a PACKET_CONFIG received from the server.
 *
 * Updates the ping interval if a new value is provided.
 * Calls esp_restart() immediately when FLAG_RESTART is set.
 *
 * @param hdr     Parsed packet header.
 * @param payload Pointer to payload bytes (may be NULL when payload_length is 0).
 */
void udp_protocol_handle_config(const packet_header_t *hdr, const uint8_t *payload);

/**
 * @brief Returns the current PING heartbeat interval in seconds.
 *
 * Default is PING_INTERVAL_S from config.h; may be updated by the server
 * via PACKET_CONFIG.
 */
uint16_t udp_protocol_get_ping_interval(void);
