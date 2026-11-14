// Sound from the remote desktop: 16-bit PCM in, 48 kHz stereo out - as Opus (20 ms packets, low delay) or
// as PCM for a browser without an Opus decoder - exactly what the agent's screen sends.
#include "session.h"

#include <opus/opus.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#define RATE 48000
#define FRAME 960 // 20 ms

struct Audio
{
    // Sound comes in on a thread of the remote desktop library; switching it on and off happens on the
    // session's loop.
    pthread_mutex_t lock;
    int on;
    int opus;
    OpusEncoder* encoder;
    int16_t pending[FRAME * 2];
    int pending_frames;
    // Resampling position, carried from one block to the next.
    double position;
    int16_t last[2];
};

Audio* audio_new(Session* s)
{
    (void)s;
    Audio* audio = calloc(1, sizeof(Audio));
    pthread_mutex_init(&audio->lock, NULL);
    return audio;
}

void audio_free(Audio* audio)
{
    if (audio)
    {
        if (audio->encoder)
        {
            opus_encoder_destroy(audio->encoder);
        }

        free(audio);
    }
}

void audio_enable(Session* s, int on, const char* codec)
{
    Audio* audio = s->audio;
    pthread_mutex_lock(&audio->lock);
    audio->on = on;
    audio->opus = codec && strcmp(codec, "opus") == 0;
    audio->pending_frames = 0;
    if (on && audio->opus && !audio->encoder)
    {
        int error = 0;
        audio->encoder = opus_encoder_create(RATE, 2, OPUS_APPLICATION_RESTRICTED_LOWDELAY, &error);
        if (error != OPUS_OK)
        {
            audio->encoder = NULL;
            audio->opus = 0;
        }
        else
        {
            opus_encoder_ctl(audio->encoder, OPUS_SET_BITRATE(128000));
        }
    }

    pthread_mutex_unlock(&audio->lock);
}

static void flush_frame(Session* s)
{
    Audio* audio = s->audio;
    json_object* data = json_object_new_object();
    if (audio->opus && audio->encoder)
    {
        uint8_t packet[4000];
        int length = opus_encode(audio->encoder, audio->pending, FRAME, packet, sizeof(packet));
        if (length > 0)
        {
            json_object_object_add(data, "codec", json_object_new_string("opus"));
            wire_event(s, "screen.audio", data, packet, (size_t)length);
            return;
        }
    }

    json_object_object_add(data, "codec", json_object_new_string("pcm"));
    wire_event(s, "screen.audio", data, (const uint8_t*)audio->pending, FRAME * 4);
}

static void push(Session* s, int16_t left, int16_t right)
{
    Audio* audio = s->audio;
    audio->pending[audio->pending_frames * 2] = left;
    audio->pending[audio->pending_frames * 2 + 1] = right;
    if (++audio->pending_frames == FRAME)
    {
        flush_frame(s);
        audio->pending_frames = 0;
    }
}

static void resample(Session* s, const int16_t* samples, size_t frames, int rate, int channels);

void audio_pcm(Session* s, const int16_t* samples, size_t frames, int rate, int channels)
{
    Audio* audio = s->audio;
    if (!audio || frames == 0 || rate <= 0 || channels <= 0)
    {
        return;
    }

    pthread_mutex_lock(&audio->lock);
    if (audio->on)
    {
        resample(s, samples, frames, rate, channels);
    }

    pthread_mutex_unlock(&audio->lock);
}

static void resample(Session* s, const int16_t* samples, size_t frames, int rate, int channels)
{
    Audio* audio = s->audio;
    if (rate == RATE)
    {
        for (size_t i = 0; i < frames; i++)
        {
            int16_t left = samples[i * channels];
            push(s, left, channels > 1 ? samples[i * channels + 1] : left);
        }

        return;
    }

    // Another rate (44.1 kHz, 22.05 kHz): linear interpolation between neighbouring samples.
    double step = (double)rate / RATE;
    while (audio->position < (double)frames)
    {
        size_t index = (size_t)audio->position;
        double fraction = audio->position - (double)index;
        int16_t l0 = index == 0 ? audio->last[0] : samples[(index - 1) * channels];
        int16_t r0 = index == 0 ? audio->last[1] : samples[(index - 1) * channels + (channels > 1 ? 1 : 0)];
        int16_t l1 = samples[index * channels];
        int16_t r1 = samples[index * channels + (channels > 1 ? 1 : 0)];
        push(s, (int16_t)(l0 + (l1 - l0) * fraction), (int16_t)(r0 + (r1 - r0) * fraction));
        audio->position += step;
    }

    audio->position -= (double)frames;
    audio->last[0] = samples[(frames - 1) * channels];
    audio->last[1] = samples[(frames - 1) * channels + (channels > 1 ? 1 : 0)];
}
