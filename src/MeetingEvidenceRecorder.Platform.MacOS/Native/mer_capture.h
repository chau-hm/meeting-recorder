#pragma once

#include <stddef.h>
#include <stdint.h>

enum
{
    MER_CAPTURE_OK = 0,
    MER_CAPTURE_PERMISSION_DENIED = 1,
    MER_CAPTURE_UNAVAILABLE = 2,
    MER_CAPTURE_SOURCE_LOST = 3,
    MER_CAPTURE_NATIVE_ERROR = 4,
    MER_CAPTURE_AUDIO_UNAVAILABLE = 5
};

enum
{
    MER_SAMPLE_VIDEO = 1,
    MER_SAMPLE_SYSTEM_AUDIO = 2
};

typedef struct
{
    uint32_t display_id;
    uint32_t width;
    uint32_t height;
    double point_pixel_scale;
    char name[128];
} MERDisplayInfo;

typedef void (*mer_sample_callback)(
    int kind,
    int64_t timestamp_value,
    int32_t timestamp_scale,
    int64_t duration_value,
    int32_t duration_scale,
    uint32_t sample_rate,
    const uint8_t *data,
    size_t data_size,
    uint32_t width,
    uint32_t height,
    uint32_t row_bytes,
    uint32_t sample_count,
    uint32_t channels,
    void *context);

typedef void (*mer_error_callback)(int code, const char *message, void *context);

void *mer_capture_create(mer_sample_callback sample_callback, mer_error_callback error_callback, void *context);
int mer_capture_screen_permission(int request);
/* Returns a nonnegative display count or a negative MER_CAPTURE_* error code. */
int mer_capture_list_displays(MERDisplayInfo *displays, int capacity, char *error_message, size_t error_message_size);
int mer_capture_start(void *handle, uint32_t display_id, uint32_t width, uint32_t height, double frames_per_second, int show_cursor, char *error_message, size_t error_message_size);
int mer_capture_stop(void *handle, char *error_message, size_t error_message_size);
void mer_capture_destroy(void *handle);
void mer_capture_release_payload(const uint8_t *payload);
