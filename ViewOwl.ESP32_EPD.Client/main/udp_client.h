/**
 * @file udp_client.h
 * @brief ViewOwl e-paper client - UDP protocol task.
 *
 * M2c: HELLO -> AUTH loop, logs the result + sends PING heartbeats. Frame receive
 * and e-paper rendering are added in M3.
 */
#pragma once

/** @brief FreeRTOS task entry: runs the HELLO/AUTH/PING loop forever. */
void udp_client_task(void *arg);

/** @brief Create the Class-C frame-player task (idles until a batch is committed). */
void frame_player_init(void);
