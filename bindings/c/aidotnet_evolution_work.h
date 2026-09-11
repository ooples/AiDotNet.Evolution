#ifndef AIDOTNET_EVOLUTION_WORK_H
#define AIDOTNET_EVOLUTION_WORK_H
#include <stdint.h>
#if defined(_WIN32)
#define AIEV_CALL __cdecl
#else
#define AIEV_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* ABI v1; all pointers belong to the caller. UTF-8 JSON, no trailing NUL.
 * Valid readable/writable buffers are REQUIRED; invalid pointers can crash the process.
 * Calls are serialized; callers must serialize each handle's submit/read sequence.
 * Max 8 live handles, 16 MiB request/reply. No automatic retry or resource refund.
 * Status: 0 success; -1 invalid/stale handle; -2 invalid length/null/UTF-8;
 * -3 reply pending; -4 no reply; -5 faulted/uncertain (close, reopen, reconcile).
 * JSON protocol errors are ordinary replies with ok:false, not ABI failures.
 */
int32_t AIEV_CALL aiev_work_abi_version(void);
uint64_t AIEV_CALL aiev_work_create(void); /* 0 = capacity/exhaustion/failure; creates no store. */
int32_t AIEV_CALL aiev_work_submit(uint64_t handle, const uint8_t *request, int32_t length);
/* Returns required positive bytes. NULL/0 queries without consuming. A short
 * buffer is untouched; a sufficient buffer receives the reply and consumes it.
 * Resizing a reply buffer never resubmits the state-mutating request. */
int32_t AIEV_CALL aiev_work_read(uint64_t handle, uint8_t *destination, int32_t capacity);
int32_t AIEV_CALL aiev_work_close(uint64_t handle); /* Discards unread reply, never refunds work. */

#ifdef __cplusplus
}
#endif
#endif
