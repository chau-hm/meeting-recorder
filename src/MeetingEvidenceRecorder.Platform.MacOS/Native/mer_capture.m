#import <AudioToolbox/AudioToolbox.h>
#import <CoreGraphics/CGWindow.h>
#import <CoreMedia/CMSampleBuffer.h>
#import <CoreVideo/CoreVideo.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <dispatch/dispatch.h>
#import <Foundation/Foundation.h>

#include "mer_capture.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void mer_set_error(char *buffer, size_t buffer_size, NSString *message)
{
    if (buffer == NULL || buffer_size == 0)
        return;
    const char *utf8 = message.UTF8String ?: "Unknown native capture error.";
    snprintf(buffer, buffer_size, "%s", utf8);
}

static int mer_error_code(NSError *error)
{
    if (error == nil)
        return MER_CAPTURE_NATIVE_ERROR;
    if ([error.domain isEqualToString:SCStreamErrorDomain] &&
        (error.code == SCStreamErrorUserDeclined || error.code == SCStreamErrorMissingEntitlements))
        return MER_CAPTURE_PERMISSION_DENIED;
    if ([error.domain isEqualToString:SCStreamErrorDomain] &&
        (error.code == SCStreamErrorNoCaptureSource || error.code == SCStreamErrorNoDisplayList))
        return MER_CAPTURE_SOURCE_LOST;
    return MER_CAPTURE_NATIVE_ERROR;
}

@interface MERStreamOutput : NSObject <SCStreamOutput, SCStreamDelegate>
@property(nonatomic, assign) mer_sample_callback sampleCallback;
@property(nonatomic, assign) mer_error_callback errorCallback;
@property(nonatomic, assign) void *context;
@end

@interface MERCaptureSession : NSObject
@property(nonatomic, strong) SCStream *stream;
@property(nonatomic, strong) MERStreamOutput *output;
@property(nonatomic, strong) dispatch_queue_t sampleQueue;
@property(nonatomic, assign) mer_sample_callback sampleCallback;
@property(nonatomic, assign) mer_error_callback errorCallback;
@property(nonatomic, assign) void *context;
@property(nonatomic, assign) BOOL running;
@end

static void mer_report_error(MERStreamOutput *output, int code, NSError *error)
{
    if (output.errorCallback == NULL)
        return;
    NSString *message = error.localizedDescription ?: @"Native ScreenCaptureKit error.";
    output.errorCallback(code, message.UTF8String, output.context);
}

static BOOL mer_is_complete_video_frame(CMSampleBufferRef sample_buffer)
{
    CFTypeRef attachment = CMGetAttachment(sample_buffer, (__bridge CFStringRef)SCStreamFrameInfoStatus, NULL);
    if (attachment == NULL || CFGetTypeID(attachment) != CFNumberGetTypeID())
        return YES;
    int32_t status = 0;
    CFNumberGetValue((CFNumberRef)attachment, kCFNumberSInt32Type, &status);
    return status == SCFrameStatusComplete;
}

static void mer_emit_video(MERStreamOutput *output, CMSampleBufferRef sample_buffer)
{
    if (!mer_is_complete_video_frame(sample_buffer))
        return;

    CVPixelBufferRef pixel_buffer = CMSampleBufferGetImageBuffer(sample_buffer);
    if (pixel_buffer == NULL)
        return;

    if (CVPixelBufferLockBaseAddress(pixel_buffer, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess)
        return;

    size_t width = CVPixelBufferGetWidth(pixel_buffer);
    size_t height = CVPixelBufferGetHeight(pixel_buffer);
    size_t row_bytes = CVPixelBufferGetBytesPerRow(pixel_buffer);
    const uint8_t *base_address = CVPixelBufferGetBaseAddress(pixel_buffer);
    size_t packed_row_bytes = width * 4;
    size_t data_size = packed_row_bytes * height;
    uint8_t *copy = malloc(data_size);
    if (copy != NULL && base_address != NULL && CVPixelBufferGetPixelFormatType(pixel_buffer) == kCVPixelFormatType_32BGRA)
    {
        for (size_t row = 0; row < height; row++)
            memcpy(copy + row * packed_row_bytes, base_address + row * row_bytes, packed_row_bytes);

        CMTime timestamp = CMSampleBufferGetPresentationTimeStamp(sample_buffer);
        CMTime duration = CMSampleBufferGetDuration(sample_buffer);
        if (output.sampleCallback != NULL)
        {
            output.sampleCallback(
                MER_SAMPLE_VIDEO,
                timestamp.value,
                timestamp.timescale,
                duration.value,
                duration.timescale,
                0,
                copy,
                data_size,
                (uint32_t)width,
                (uint32_t)height,
                (uint32_t)packed_row_bytes,
                0,
                0,
                output.context);
        }
    }
    else
    {
        free(copy);
        copy = NULL;
    }

    CVPixelBufferUnlockBaseAddress(pixel_buffer, kCVPixelBufferLock_ReadOnly);
}

static float mer_read_audio_sample(
    const uint8_t *source,
    const AudioStreamBasicDescription *description)
{
    if ((description->mFormatFlags & kAudioFormatFlagIsFloat) != 0 && description->mBitsPerChannel == 32)
        return *(const float *)source;
    if ((description->mFormatFlags & kAudioFormatFlagIsSignedInteger) != 0 && description->mBitsPerChannel == 16)
        return (float)(*(const int16_t *)source) / 32768.0f;
    if ((description->mFormatFlags & kAudioFormatFlagIsSignedInteger) != 0 && description->mBitsPerChannel == 32)
        return (float)(*(const int32_t *)source) / 2147483648.0f;
    return 0.0f;
}

static BOOL mer_supports_audio_format(const AudioStreamBasicDescription *description)
{
    return description != NULL &&
        description->mSampleRate > 0 &&
        (((description->mFormatFlags & kAudioFormatFlagIsFloat) != 0 &&
          description->mBitsPerChannel == 32) ||
         ((description->mFormatFlags & kAudioFormatFlagIsSignedInteger) != 0 &&
          (description->mBitsPerChannel == 16 || description->mBitsPerChannel == 32)));
}

static void mer_emit_audio(MERStreamOutput *output, CMSampleBufferRef sample_buffer)
{
    CMFormatDescriptionRef format = CMSampleBufferGetFormatDescription(sample_buffer);
    const AudioStreamBasicDescription *description = format == NULL
        ? NULL
        : CMAudioFormatDescriptionGetStreamBasicDescription(format);
    if (description == NULL || description->mChannelsPerFrame == 0 ||
        !mer_supports_audio_format(description))
    {
        if (output.errorCallback != NULL)
            output.errorCallback(MER_CAPTURE_AUDIO_UNAVAILABLE, "ScreenCaptureKit returned an unsupported audio format.", output.context);
        return;
    }

    size_t list_size = 0;
    OSStatus status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sample_buffer,
        &list_size,
        NULL,
        0,
        NULL,
        NULL,
        0,
        NULL);
    if (status != noErr || list_size < sizeof(AudioBufferList))
        return;

    AudioBufferList *buffer_list = calloc(1, list_size);
    CMBlockBufferRef retained_block = NULL;
    if (buffer_list == NULL)
        return;

    status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sample_buffer,
        NULL,
        buffer_list,
        list_size,
        NULL,
        NULL,
        kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment,
        &retained_block);
    if (status != noErr || retained_block == NULL)
    {
        free(buffer_list);
        return;
    }

    CMItemCount frame_count = CMSampleBufferGetNumSamples(sample_buffer);
    uint32_t channels = (uint32_t)description->mChannelsPerFrame;
    const BOOL non_interleaved =
        (description->mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0 ||
        buffer_list->mNumberBuffers > 1;
    const size_t bytes_per_sample = description->mBitsPerChannel / 8;
    if (bytes_per_sample == 0)
    {
        if (output.errorCallback != NULL)
            output.errorCallback(MER_CAPTURE_AUDIO_UNAVAILABLE, "ScreenCaptureKit returned an audio format without sample width.", output.context);
        if (retained_block != NULL)
            CFRelease(retained_block);
        free(buffer_list);
        return;
    }
    if (buffer_list->mNumberBuffers == 0 ||
        (non_interleaved && buffer_list->mNumberBuffers < channels))
    {
        if (output.errorCallback != NULL)
            output.errorCallback(MER_CAPTURE_AUDIO_UNAVAILABLE, "ScreenCaptureKit returned an invalid audio buffer list.", output.context);
        if (retained_block != NULL)
            CFRelease(retained_block);
        free(buffer_list);
        return;
    }

    const size_t required_bytes = non_interleaved
        ? (size_t)frame_count * bytes_per_sample
        : (size_t)frame_count * description->mBytesPerFrame;
    const size_t buffer_count = non_interleaved ? channels : 1;
    for (size_t index = 0; index < buffer_count; index++)
    {
        if (buffer_list->mBuffers[index].mData == NULL ||
            buffer_list->mBuffers[index].mDataByteSize < required_bytes)
        {
            if (output.errorCallback != NULL)
                output.errorCallback(MER_CAPTURE_AUDIO_UNAVAILABLE, "ScreenCaptureKit returned truncated audio data.", output.context);
            if (retained_block != NULL)
                CFRelease(retained_block);
            free(buffer_list);
            return;
        }
    }

    size_t sample_count = (size_t)frame_count * channels;
    float *copy = malloc(sample_count * sizeof(float));
    if (copy != NULL)
    {
        for (size_t frame = 0; frame < (size_t)frame_count; frame++)
        {
            for (uint32_t channel = 0; channel < channels; channel++)
            {
                const AudioBuffer *buffer = non_interleaved
                    ? &buffer_list->mBuffers[channel]
                    : &buffer_list->mBuffers[0];
                size_t offset = non_interleaved
                    ? frame * bytes_per_sample
                    : frame * description->mBytesPerFrame + channel * bytes_per_sample;
                const uint8_t *source = (const uint8_t *)buffer->mData + offset;
                copy[frame * channels + channel] = mer_read_audio_sample(source, description);
            }
        }

        CMTime timestamp = CMSampleBufferGetPresentationTimeStamp(sample_buffer);
        CMTime duration = CMSampleBufferGetDuration(sample_buffer);
        if (output.sampleCallback != NULL)
        {
            output.sampleCallback(
                MER_SAMPLE_SYSTEM_AUDIO,
                timestamp.value,
                timestamp.timescale,
                duration.value,
                duration.timescale,
                (uint32_t)description->mSampleRate,
                (const uint8_t *)copy,
                sample_count * sizeof(float),
                0,
                0,
                0,
                (uint32_t)frame_count,
                channels,
                output.context);
        }
    }

    if (retained_block != NULL)
        CFRelease(retained_block);
    free(buffer_list);
}

@implementation MERStreamOutput
- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type
{
    (void)stream;
    if (type == SCStreamOutputTypeScreen)
        mer_emit_video(self, sampleBuffer);
    else if (type == SCStreamOutputTypeAudio)
        mer_emit_audio(self, sampleBuffer);
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error
{
    (void)stream;
    mer_report_error(self, mer_error_code(error), error);
}

- (void)streamDidBecomeInactive:(SCStream *)stream
{
    (void)stream;
    if (self.errorCallback != NULL)
        self.errorCallback(MER_CAPTURE_SOURCE_LOST, "The selected display became inactive.", self.context);
}
@end

@implementation MERCaptureSession
@end

static SCShareableContent *mer_get_content(NSError **error)
{
    __block SCShareableContent *content = nil;
    __block NSError *callback_error = nil;
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
    [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *shareableContent, NSError *nativeError) {
        content = shareableContent;
        callback_error = nativeError;
        dispatch_semaphore_signal(semaphore);
    }];
    dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);
    if (error != NULL)
        *error = callback_error;
    return content;
}

void *mer_capture_create(mer_sample_callback sample_callback, mer_error_callback error_callback, void *context)
{
    MERCaptureSession *session = [MERCaptureSession new];
    session.sampleCallback = sample_callback;
    session.errorCallback = error_callback;
    session.context = context;
    return (__bridge_retained void *)session;
}

int mer_capture_screen_permission(int request)
{
    BOOL preflight = CGPreflightScreenCaptureAccess();
    if (preflight)
        return MER_CAPTURE_OK;
    if (request && CGRequestScreenCaptureAccess())
        return MER_CAPTURE_OK;

    // ScreenCaptureKit can observe a newly granted responsible-app permission
    // before the legacy CoreGraphics preflight result refreshes.
    NSError *error = nil;
    SCShareableContent *content = mer_get_content(&error);
    if (content != nil)
        return MER_CAPTURE_OK;

    return request ? MER_CAPTURE_PERMISSION_DENIED : MER_CAPTURE_UNAVAILABLE;
}

int mer_capture_list_displays(MERDisplayInfo *displays, int capacity, char *error_message, size_t error_message_size)
{
    NSError *error = nil;
    SCShareableContent *content = mer_get_content(&error);
    if (content == nil)
    {
        mer_set_error(error_message, error_message_size, error.localizedDescription ?: @"ScreenCaptureKit could not enumerate displays.");
        return -mer_error_code(error);
    }

    NSInteger count = content.displays.count;
    if (displays == NULL || capacity < count)
    {
        mer_set_error(error_message, error_message_size, @"Display output buffer is too small.");
        return -MER_CAPTURE_NATIVE_ERROR;
    }

    for (NSInteger index = 0; index < count; index++)
    {
        SCDisplay *display = content.displays[index];
        MERDisplayInfo *info = &displays[index];
        memset(info, 0, sizeof(MERDisplayInfo));
        info->display_id = display.displayID;
        info->width = (uint32_t)CGDisplayPixelsWide(display.displayID);
        info->height = (uint32_t)CGDisplayPixelsHigh(display.displayID);
        info->point_pixel_scale = (display.width > 0) ? (double)info->width / display.width : 1.0;
        snprintf(info->name, sizeof(info->name), "Display %u", info->display_id);
    }
    return (int)count;
}

int mer_capture_start(
    void *handle,
    uint32_t display_id,
    uint32_t width,
    uint32_t height,
    double frames_per_second,
    int show_cursor,
    char *error_message,
    size_t error_message_size)
{
    MERCaptureSession *session = (__bridge MERCaptureSession *)handle;
    if (session == nil || width == 0 || height == 0 || frames_per_second <= 0)
    {
        mer_set_error(error_message, error_message_size, @"Invalid ScreenCaptureKit start configuration.");
        return MER_CAPTURE_NATIVE_ERROR;
    }
    if (session.running)
    {
        mer_set_error(error_message, error_message_size, @"ScreenCaptureKit stream is already running.");
        return MER_CAPTURE_NATIVE_ERROR;
    }
    NSError *content_error = nil;
    SCShareableContent *content = mer_get_content(&content_error);
    if (content == nil)
    {
        mer_set_error(error_message, error_message_size, content_error.localizedDescription ?: @"ScreenCaptureKit could not enumerate displays.");
        return mer_error_code(content_error);
    }

    SCDisplay *selected = nil;
    for (SCDisplay *display in content.displays)
    {
        if (display.displayID == display_id)
        {
            selected = display;
            break;
        }
    }
    if (selected == nil)
    {
        mer_set_error(error_message, error_message_size, @"The selected display is no longer available.");
        return MER_CAPTURE_SOURCE_LOST;
    }

    SCContentFilter *filter = [[SCContentFilter alloc] initWithDisplay:selected excludingWindows:@[]];
    SCStreamConfiguration *configuration = [SCStreamConfiguration new];
    configuration.width = width;
    configuration.height = height;
    configuration.minimumFrameInterval = CMTimeMakeWithSeconds(1.0 / frames_per_second, 1000000000);
    configuration.pixelFormat = kCVPixelFormatType_32BGRA;
    configuration.showsCursor = show_cursor;
    configuration.queueDepth = 8;
    configuration.capturesAudio = YES;
    configuration.sampleRate = 48000;
    configuration.channelCount = 2;
    configuration.captureMicrophone = NO;

    MERStreamOutput *output = [MERStreamOutput new];
    output.sampleCallback = session.sampleCallback;
    output.errorCallback = session.errorCallback;
    output.context = session.context;
    dispatch_queue_t queue = dispatch_queue_create("com.meetingevidencerecorder.sckit.samples", DISPATCH_QUEUE_SERIAL);
    SCStream *stream = [[SCStream alloc] initWithFilter:filter configuration:configuration delegate:output];
    NSError *add_error = nil;
    if (![stream addStreamOutput:output type:SCStreamOutputTypeScreen sampleHandlerQueue:queue error:&add_error] ||
        ![stream addStreamOutput:output type:SCStreamOutputTypeAudio sampleHandlerQueue:queue error:&add_error])
    {
        mer_set_error(error_message, error_message_size, add_error.localizedDescription ?: @"ScreenCaptureKit could not attach stream outputs.");
        return mer_error_code(add_error);
    }

    __block NSError *start_error = nil;
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
    [stream startCaptureWithCompletionHandler:^(NSError *error) {
        start_error = error;
        dispatch_semaphore_signal(semaphore);
    }];
    dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);
    if (start_error != nil)
    {
        mer_set_error(error_message, error_message_size, start_error.localizedDescription);
        return mer_error_code(start_error);
    }

    session.stream = stream;
    session.output = output;
    session.sampleQueue = queue;
    session.running = YES;
    return MER_CAPTURE_OK;
}

int mer_capture_stop(void *handle, char *error_message, size_t error_message_size)
{
    MERCaptureSession *session = (__bridge MERCaptureSession *)handle;
    if (session == nil || !session.running)
        return MER_CAPTURE_OK;

    __block NSError *stop_error = nil;
    dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
    [session.stream stopCaptureWithCompletionHandler:^(NSError *error) {
        stop_error = error;
        dispatch_semaphore_signal(semaphore);
    }];
    dispatch_semaphore_wait(semaphore, DISPATCH_TIME_FOREVER);
    session.running = NO;
    session.stream = nil;
    session.output = nil;
    session.sampleQueue = nil;
    if (stop_error != nil)
    {
        mer_set_error(error_message, error_message_size, stop_error.localizedDescription);
        return mer_error_code(stop_error);
    }
    return MER_CAPTURE_OK;
}

void mer_capture_destroy(void *handle)
{
    MERCaptureSession *session = (__bridge_transfer MERCaptureSession *)handle;
    if (session.running)
        mer_capture_stop((__bridge void *)session, NULL, 0);
    session.stream = nil;
    session.output = nil;
    session.sampleQueue = nil;
}

void mer_capture_release_payload(const uint8_t *payload)
{
    free((void *)payload);
}
