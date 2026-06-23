#pragma once

/**
 * @brief FreeRTOS task: ViewOwl C3 frame client.
 *
 * Loops HELLO -> AUTH -> DATA(+ACK) -> DONE over the shared packet.h protocol,
 * receiving one frame per cycle from the UDP server and sending PING heartbeats
 * while idle. Single-frame path only (Class A/B).
 *
 * M3b-1: logs the received frame's size + format flag (no decode/render yet).
 * M3b-2 will decode the frame and blit it to the round display.
 *
 * Must be started after Wi-Fi is connected. Never returns.
 *
 * @param arg Unused (FreeRTOS task signature).
 */
void udp_client_task(void *arg);
