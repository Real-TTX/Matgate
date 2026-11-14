// One remote screen: the connection to Matgate on one side, a remote desktop (RDP or VNC) on the other.
// Every session runs in a process of its own (see main.c), so nothing here is shared between viewers.
#pragma once

#include <json-c/json.h>
#include <pthread.h>
#include <stdint.h>
#include <stddef.h>

typedef struct Session Session;

// A message as Matgate and the agent speak it: a JSON header (kind/op/id/data/error) and raw payload.
typedef struct
{
    json_object* root;
    const char* kind;
    const char* op;
    int64_t id;
    json_object* data;
    const uint8_t* payload;
    size_t payload_len;
} Frame;

// What a remote desktop protocol has to provide.
typedef struct
{
    const char* name;
    // Connects; returns 0, or fills `error` with a sentence for a person.
    int (*open)(Session* s, json_object* params, char* error, size_t error_len);
    // Runs until the remote side or Matgate ends the session.
    void (*run)(Session* s);
    // The current picture (BGRX, 4 bytes a pixel). Called from the backend's own loop only. When it returns
    // 1 the picture stays as it is until frame_done - a backend whose library draws on a thread of its own
    // (FreeRDP's graphics pipeline) holds that thread off meanwhile.
    int (*frame)(Session* s, const uint8_t** pixels, int* stride, int* width, int* height);
    void (*frame_done)(Session* s);
    void (*input)(Session* s, json_object* data);
    void (*resize)(Session* s, int width, int height);
    void (*clip)(Session* s, const Frame* frame);
    void (*clip_read)(Session* s, const Frame* request);
    void (*secure_attention)(Session* s);
    void (*close)(Session* s);
} Backend;

typedef struct Video Video;
typedef struct Audio Audio;

struct Session
{
    int fd;
    pthread_mutex_t send_lock;
    char id[96];
    const Backend* backend;
    void* impl;
    volatile int running;
    // Connected: from now on input and clipboard reach the remote side.
    int ready;

    // Reading from Matgate.
    uint8_t* inbox;
    size_t inbox_len;
    size_t inbox_cap;

    Video* video;
    Audio* audio;

    // The resolution the viewer wants: "auto" (fit), "native" (as connected), or "WxH".
    char resolution[24];
    int fit_width;
    int fit_height;
    int native_width;
    int native_height;

    // Pressed keys and buttons, so that nothing stays down when the viewer goes away.
    uint16_t keys_down[64];
    int keys_count;
    uint32_t keysyms_down[64];
    int keysyms_count;
    int buttons_down;
};

// ---- wire.c -------------------------------------------------------------------------------------------

// Sends one message; `data` is taken over (may be NULL).
int wire_send(Session* s, const char* kind, const char* op, int64_t id, json_object* data, const char* error,
              const uint8_t* payload, size_t payload_len);
int wire_event(Session* s, const char* op, json_object* data, const uint8_t* payload, size_t payload_len);
int wire_answer(Session* s, const Frame* request, json_object* data, const uint8_t* payload, size_t payload_len);
int wire_fail(Session* s, const Frame* request, const char* error);
// Reads what has arrived; returns -1 when Matgate is gone.
int wire_receive(Session* s);
// The next complete message, or 0. Free with frame_free.
int wire_next(Session* s, Frame* frame);
void frame_free(Frame* frame);
// Blocks up to `timeout_ms` for a message with this op (credentials while connecting); other messages
// that arrive meanwhile are handled as usual. Returns 1 when found.
int wire_wait_for(Session* s, const char* op, Frame* found, int timeout_ms);

const char* json_text(json_object* object, const char* name, const char* fallback);
int64_t json_int(json_object* object, const char* name, int64_t fallback);
int json_flag(json_object* object, const char* name, int fallback);
void session_log(Session* s, const char* format, ...);

// ---- main.c: what every backend shares --------------------------------------------------------------

// Handles one message from Matgate (input, acknowledgements, settings, clipboard ...).
void session_dispatch(Session* s, const Frame* frame);
// Reads and handles everything that has arrived; returns -1 when Matgate is gone.
int session_pump(Session* s);
void session_status(Session* s, const char* state, const char* message);
// The size the viewer wants right now (0 when it leaves it as it is).
void session_wanted_size(Session* s, int* width, int* height);
// Modes offered in the resolution dialog.
json_object* session_modes(void);
// UTF-8 <-> UTF-16LE.
uint16_t* utf8_to_utf16(const char* text, size_t length, size_t* units);
char* utf16_to_utf8(const uint16_t* text, size_t units);

// ---- video.c ------------------------------------------------------------------------------------------

Video* video_new(Session* s);
void video_free(Video* video);
// The picture changed.
void video_dirty(Session* s);
// Encodes and sends when it is time; returns how many milliseconds the loop may sleep.
int video_tick(Session* s);
void video_ack(Session* s, int64_t sequence);
void video_request_key(Session* s);
void video_quality(Session* s, const char* quality);
// The remote's own mouse pointer: RGBA pixels and hot spot, or hidden / the system arrow.
void video_cursor(Session* s, int hot_x, int hot_y, int width, int height, const uint8_t* rgba);
void video_cursor_hidden(Session* s, int hidden);
void video_cursor_default(Session* s);

// ---- audio.c ------------------------------------------------------------------------------------------

Audio* audio_new(Session* s);
void audio_free(Audio* audio);
void audio_enable(Session* s, int on, const char* codec);
// Interleaved 16-bit PCM from the remote.
void audio_pcm(Session* s, const int16_t* samples, size_t frames, int rate, int channels);

extern const Backend rdp_backend;
extern const Backend vnc_backend;
