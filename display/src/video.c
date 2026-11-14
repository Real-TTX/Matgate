// The picture: the remote desktop's frame buffer, encoded as H.264 (x264, tuned for latency - no B-frames,
// no lookahead, one picture in, one out) and sent the way the agent's screen sends it, so the same viewer
// shows both. At most three pictures are on their way; the browser acknowledges each one it has decoded.
#include "session.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <x264.h>

#define MAX_IN_FLIGHT 3

struct Video
{
    x264_t* encoder;
    x264_picture_t picture;
    int width;
    int height;
    int fps;
    int bitrate_per_full_hd;
    int64_t pts;
    int dirty;
    int key_requested;
    int refine;
    int64_t last_encode;
    int64_t sent;
    int64_t acked;
    int64_t last_ack;
    int64_t sent_at[64];

    // Statistics, once a second.
    int64_t stat_start;
    int64_t stat_frames;
    int64_t stat_bytes;
    int64_t stat_rtt;
    int64_t stat_rtt_count;
    double stat_encode_ms;
};

static int64_t now_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static double now_precise(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (double)now.tv_sec * 1000.0 + (double)now.tv_nsec / 1e6;
}

Video* video_new(Session* s)
{
    (void)s;
    Video* video = calloc(1, sizeof(Video));
    video->fps = 60;
    video->bitrate_per_full_hd = 8000;
    video->key_requested = 1;
    video->last_ack = now_ms();
    video->stat_start = now_ms();
    return video;
}

static void close_encoder(Video* video)
{
    if (video->encoder)
    {
        x264_encoder_close(video->encoder);
        x264_picture_clean(&video->picture);
        video->encoder = NULL;
    }
}

void video_free(Video* video)
{
    if (video)
    {
        close_encoder(video);
        free(video);
    }
}

static int open_encoder(Session* s, int width, int height)
{
    Video* video = s->video;
    close_encoder(video);
    x264_param_t param;
    if (x264_param_default_preset(&param, "superfast", "zerolatency") < 0)
    {
        return -1;
    }

    param.i_width = width;
    param.i_height = height;
    param.i_csp = X264_CSP_I420;
    param.i_fps_num = (uint32_t)video->fps;
    param.i_fps_den = 1;
    param.i_keyint_max = X264_KEYINT_MAX_INFINITE;
    param.b_repeat_headers = 1;
    param.b_annexb = 1;
    param.i_log_level = X264_LOG_NONE;
    // Sharp text where the screen stands still, a ceiling for the network where it moves.
    int kbps = (int)((int64_t)video->bitrate_per_full_hd * width * height / (1920 * 1080) * (15 + video->fps / 2) / 30);
    if (kbps < 500)
    {
        kbps = 500;
    }

    param.rc.i_rc_method = X264_RC_CRF;
    param.rc.f_rf_constant = 22;
    param.rc.i_vbv_max_bitrate = kbps;
    param.rc.i_vbv_buffer_size = kbps / 2;
    if (x264_param_apply_profile(&param, "baseline") < 0)
    {
        return -1;
    }

    video->encoder = x264_encoder_open(&param);
    if (!video->encoder)
    {
        return -1;
    }

    x264_picture_alloc(&video->picture, X264_CSP_I420, width, height);
    video->width = width;
    video->height = height;
    video->key_requested = 1;
    video->refine = 0;

    json_object* modes = session_modes();
    json_object* monitor = json_object_new_object();
    json_object_object_add(monitor, "monitor", json_object_new_int(0));
    json_object_object_add(monitor, "width", json_object_new_int(width));
    json_object_object_add(monitor, "height", json_object_new_int(height));
    json_object_object_add(monitor, "videoWidth", json_object_new_int(width));
    json_object_object_add(monitor, "videoHeight", json_object_new_int(height));
    json_object_object_add(monitor, "modes", modes);
    char saved[32];
    snprintf(saved, sizeof(saved), "%dx%d", s->native_width, s->native_height);
    json_object_object_add(monitor, "saved", json_object_new_string(saved));
    json_object_object_add(monitor, "resolution", json_object_new_string(s->resolution));
    wire_event(s, "screen.monitor", monitor, NULL, 0);
    return 0;
}

// BGRX -> I420 (BT.601 limited range, what x264 assumes by default). Two rows at a time: each chroma sample
// is the average of a 2x2 block.
static void convert(const uint8_t* source, int stride, x264_image_t* image, int width, int height)
{
    uint8_t* planeY = image->plane[0];
    uint8_t* planeU = image->plane[1];
    uint8_t* planeV = image->plane[2];
    int strideY = image->i_stride[0];
    int strideU = image->i_stride[1];
    int strideV = image->i_stride[2];
    for (int y = 0; y < height; y += 2)
    {
        const uint8_t* row0 = source + (size_t)y * stride;
        const uint8_t* row1 = source + (size_t)(y + 1) * stride;
        uint8_t* outY0 = planeY + (size_t)y * strideY;
        uint8_t* outY1 = planeY + (size_t)(y + 1) * strideY;
        uint8_t* outU = planeU + (size_t)(y / 2) * strideU;
        uint8_t* outV = planeV + (size_t)(y / 2) * strideV;
        for (int x = 0; x < width; x += 2)
        {
            int sumR = 0;
            int sumG = 0;
            int sumB = 0;
            for (int dy = 0; dy < 2; dy++)
            {
                const uint8_t* row = dy ? row1 : row0;
                uint8_t* out = dy ? outY1 : outY0;
                for (int dx = 0; dx < 2; dx++)
                {
                    const uint8_t* pixel = row + (x + dx) * 4;
                    int b = pixel[0];
                    int g = pixel[1];
                    int r = pixel[2];
                    out[x + dx] = (uint8_t)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    sumR += r;
                    sumG += g;
                    sumB += b;
                }
            }

            sumR /= 4;
            sumG /= 4;
            sumB /= 4;
            outU[x / 2] = (uint8_t)(((-38 * sumR - 74 * sumG + 112 * sumB + 128) >> 8) + 128);
            outV[x / 2] = (uint8_t)(((112 * sumR - 94 * sumG - 18 * sumB + 128) >> 8) + 128);
        }
    }
}

static void send_stats(Session* s)
{
    Video* video = s->video;
    int64_t now = now_ms();
    double seconds = (double)(now - video->stat_start) / 1000.0;
    if (seconds < 1.0)
    {
        return;
    }

    json_object* stats = json_object_new_object();
    json_object_object_add(stats, "fps", json_object_new_double((double)(int)(video->stat_frames / seconds * 10) / 10.0));
    json_object_object_add(stats, "kbps", json_object_new_int64((int64_t)(video->stat_bytes * 8 / 1000 / seconds)));
    json_object_object_add(stats, "rtt", video->stat_rtt_count ? json_object_new_int64(video->stat_rtt / video->stat_rtt_count) : NULL);
    json_object_object_add(stats, "agentMs", video->stat_frames ? json_object_new_double((double)(int)(video->stat_encode_ms / (double)video->stat_frames * 10) / 10.0) : NULL);
    json_object_object_add(stats, "encoder", json_object_new_string("x264"));
    json_object_object_add(stats, "fpsLimit", json_object_new_int(video->fps));
    wire_event(s, "screen.stats", stats, NULL, 0);
    video->stat_start = now;
    video->stat_frames = 0;
    video->stat_bytes = 0;
    video->stat_rtt = 0;
    video->stat_rtt_count = 0;
    video->stat_encode_ms = 0;
}

void video_dirty(Session* s)
{
    s->video->dirty = 1;
}

int video_tick(Session* s)
{
    Video* video = s->video;
    int64_t now = now_ms();
    send_stats(s);

    int refine_due = video->refine > 0 && now - video->last_encode >= 300;
    if (!video->dirty && !video->key_requested && !refine_due)
    {
        return video->refine > 0 ? (int)(300 - (now - video->last_encode)) : 500;
    }

    // The browser is behind: wait for it instead of piling up pictures it would show late.
    if (video->sent - video->acked >= MAX_IN_FLIGHT)
    {
        if (now - video->last_ack > 3000)
        {
            video->acked = video->sent;
            video->key_requested = 1;
        }

        return 15;
    }

    int64_t slot = video->last_encode + 1000 / video->fps - now;
    if (slot > 0)
    {
        return (int)slot;
    }

    const uint8_t* pixels = NULL;
    int stride = 0;
    int width = 0;
    int height = 0;
    if (!s->backend->frame(s, &pixels, &stride, &width, &height))
    {
        return 100;
    }

    if (!pixels || width < 2 || height < 2)
    {
        s->backend->frame_done(s);
        return 100;
    }

    // H.264 wants even sizes; an odd last row or column is left out.
    width &= ~1;
    height &= ~1;
    if (!video->encoder || width != video->width || height != video->height)
    {
        if (open_encoder(s, width, height) < 0)
        {
            s->backend->frame_done(s);
            session_status(s, "error", "The picture could not be encoded.");
            return 1000;
        }
    }

    double started = now_precise();
    if (video->dirty)
    {
        video->dirty = 0;
        video->refine = 2;
    }
    else if (!video->key_requested)
    {
        // Nothing new: the same picture once more, so it gets sharp where the last ones had to be quick.
        video->refine--;
    }

    convert(pixels, stride, &video->picture.img, width, height);
    // The encoder works on its own copy from here on.
    s->backend->frame_done(s);
    video->picture.i_pts = video->pts++;
    video->picture.i_type = video->key_requested ? X264_TYPE_IDR : X264_TYPE_AUTO;
    int key = video->key_requested;
    video->key_requested = 0;
    x264_nal_t* nals = NULL;
    int count = 0;
    x264_picture_t out;
    int size = x264_encoder_encode(video->encoder, &nals, &count, &video->picture, &out);
    video->last_encode = now;
    if (size <= 0 || count <= 0)
    {
        return 1;
    }

    // The NAL units lie one after another in one buffer.
    const uint8_t* bytes = nals[0].p_payload;
    int64_t sequence = ++video->sent;
    video->sent_at[sequence % 64] = now;
    video->stat_frames++;
    video->stat_bytes += size;
    video->stat_encode_ms += now_precise() - started;
    json_object* data = json_object_new_object();
    json_object_object_add(data, "seq", json_object_new_int64(sequence));
    json_object_object_add(data, "key", json_object_new_boolean(key || out.b_keyframe));
    json_object_object_add(data, "width", json_object_new_int(width));
    json_object_object_add(data, "height", json_object_new_int(height));
    wire_event(s, "screen.video", data, bytes, (size_t)size);
    return 1;
}

void video_ack(Session* s, int64_t sequence)
{
    Video* video = s->video;
    if (sequence > video->sent || sequence <= video->acked)
    {
        return;
    }

    int64_t now = now_ms();
    if (video->sent - sequence < 64)
    {
        video->stat_rtt += now - video->sent_at[sequence % 64];
        video->stat_rtt_count++;
    }

    video->acked = sequence;
    video->last_ack = now;
}

void video_request_key(Session* s)
{
    s->video->key_requested = 1;
}

// Fast enough for a mouse that feels like one's own; fewer pictures where data is scarce.
void video_quality(Session* s, const char* quality)
{
    Video* video = s->video;
    int fps = 60;
    int bitrate = 8000;
    if (strcmp(quality, "medium") == 0)
    {
        fps = 30;
        bitrate = 4000;
    }
    else if (strcmp(quality, "low") == 0)
    {
        fps = 20;
        bitrate = 1500;
    }

    if (fps != video->fps || bitrate != video->bitrate_per_full_hd)
    {
        video->fps = fps;
        video->bitrate_per_full_hd = bitrate;
        // Made again with the next picture.
        close_encoder(video);
        video->dirty = 1;
    }
}

void video_cursor(Session* s, int hot_x, int hot_y, int width, int height, const uint8_t* rgba)
{
    json_object* data = json_object_new_object();
    json_object_object_add(data, "x", json_object_new_int(hot_x));
    json_object_object_add(data, "y", json_object_new_int(hot_y));
    json_object_object_add(data, "width", json_object_new_int(width));
    json_object_object_add(data, "height", json_object_new_int(height));
    json_object_object_add(data, "format", json_object_new_string("rgba"));
    wire_event(s, "screen.cursor", data, rgba, (size_t)width * height * 4);
    video_cursor_hidden(s, 0);
}

void video_cursor_hidden(Session* s, int hidden)
{
    json_object* data = json_object_new_object();
    json_object_object_add(data, "visible", json_object_new_boolean(!hidden));
    json_object_object_add(data, "local", json_object_new_boolean(1));
    wire_event(s, "screen.pointer", data, NULL, 0);
}

void video_cursor_default(Session* s)
{
    json_object* data = json_object_new_object();
    json_object_object_add(data, "format", json_object_new_string("default"));
    wire_event(s, "screen.cursor", data, NULL, 0);
    video_cursor_hidden(s, 0);
}
