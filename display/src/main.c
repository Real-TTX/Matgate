// matgate-display: Matgate's own remote desktop engine for RDP (FreeRDP 3) and VNC (libvncclient).
//
// Matgate opens a TCP connection per viewer and asks for a screen ("screen.open" with host, port and
// credentials); this process forks, the child connects to the remote desktop and from then on speaks the
// screen protocol of the Matgate agent: H.264 pictures, the mouse pointer, sound, a clipboard in both
// directions (text, pictures, files) and the resolution of the viewer's window. Only Matgate may talk to
// it: the first message has to carry the key Matgate keeps in its secrets volume.
#include "session.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static char expected_token[256];

static void read_token(void)
{
    const char* direct = getenv("MATGATE_DISPLAY_TOKEN");
    if (direct && *direct)
    {
        snprintf(expected_token, sizeof(expected_token), "%s", direct);
        return;
    }

    const char* path = getenv("MATGATE_DISPLAY_TOKEN_FILE");
    FILE* file = fopen(path && *path ? path : "/run/matgate-secrets/display.key", "r");
    expected_token[0] = 0;
    if (file)
    {
        if (fgets(expected_token, sizeof(expected_token), file))
        {
            expected_token[strcspn(expected_token, "\r\n ")] = 0;
        }

        fclose(file);
    }
}

static int same_token(const char* offered)
{
    // Matgate writes the key on its first start - read again each time, it may not have been there yet.
    read_token();
    size_t expected = strlen(expected_token);
    size_t given = strlen(offered);
    if (expected < 16)
    {
        return 0;
    }

    unsigned char difference = (unsigned char)(expected != given);
    for (size_t i = 0; i < expected; i++)
    {
        difference |= (unsigned char)(expected_token[i] ^ (i < given ? offered[i] : 0));
    }

    return difference == 0;
}

// ---- shared by the backends --------------------------------------------------------------------------

void session_status(Session* s, const char* state, const char* message)
{
    json_object* data = json_object_new_object();
    json_object_object_add(data, "state", json_object_new_string(state));
    if (message)
    {
        json_object_object_add(data, "message", json_object_new_string(message));
    }

    wire_event(s, "screen.status", data, NULL, 0);
}

static void parse_size(const char* text, int* width, int* height)
{
    int w = 0;
    int h = 0;
    if (sscanf(text, "%dx%d", &w, &h) == 2 && w >= 320 && h >= 200)
    {
        *width = w;
        *height = h;
    }
}

void session_wanted_size(Session* s, int* width, int* height)
{
    *width = 0;
    *height = 0;
    if (strcmp(s->resolution, "auto") == 0)
    {
        *width = s->fit_width;
        *height = s->fit_height;
    }
    else if (strcmp(s->resolution, "native") == 0)
    {
        *width = s->native_width;
        *height = s->native_height;
    }
    else
    {
        parse_size(s->resolution, width, height);
    }

    if (*width <= 0 || *height <= 0)
    {
        *width = 0;
        *height = 0;
        return;
    }

    // What remote desktops accept: even sizes within sensible bounds.
    if (*width < 640) *width = 640;
    if (*height < 480) *height = 480;
    if (*width > 4096) *width = 4096;
    if (*height > 2304) *height = 2304;
    *width &= ~1;
    *height &= ~1;
}

json_object* session_modes(void)
{
    static const char* modes[] = {
        "3840x2160", "2560x1440", "1920x1200", "1920x1080", "1680x1050", "1600x900",
        "1440x900", "1366x768", "1280x1024", "1280x800", "1280x720", "1024x768",
    };
    json_object* list = json_object_new_array();
    for (size_t i = 0; i < sizeof(modes) / sizeof(modes[0]); i++)
    {
        json_object_array_add(list, json_object_new_string(modes[i]));
    }

    return list;
}

uint16_t* utf8_to_utf16(const char* text, size_t length, size_t* units)
{
    uint16_t* out = malloc((length + 1) * sizeof(uint16_t) * 2);
    size_t count = 0;
    const unsigned char* p = (const unsigned char*)text;
    const unsigned char* end = p + length;
    while (p < end)
    {
        uint32_t code;
        if (*p < 0x80)
        {
            code = *p++;
        }
        else if ((*p & 0xE0) == 0xC0 && p + 1 < end)
        {
            code = ((uint32_t)(p[0] & 0x1F) << 6) | (p[1] & 0x3F);
            p += 2;
        }
        else if ((*p & 0xF0) == 0xE0 && p + 2 < end)
        {
            code = ((uint32_t)(p[0] & 0x0F) << 12) | ((uint32_t)(p[1] & 0x3F) << 6) | (p[2] & 0x3F);
            p += 3;
        }
        else if ((*p & 0xF8) == 0xF0 && p + 3 < end)
        {
            code = ((uint32_t)(p[0] & 0x07) << 18) | ((uint32_t)(p[1] & 0x3F) << 12) | ((uint32_t)(p[2] & 0x3F) << 6) | (p[3] & 0x3F);
            p += 4;
        }
        else
        {
            code = 0xFFFD;
            p++;
        }

        if (code >= 0x10000)
        {
            code -= 0x10000;
            out[count++] = (uint16_t)(0xD800 + (code >> 10));
            out[count++] = (uint16_t)(0xDC00 + (code & 0x3FF));
        }
        else
        {
            out[count++] = (uint16_t)code;
        }
    }

    out[count] = 0;
    *units = count;
    return out;
}

char* utf16_to_utf8(const uint16_t* text, size_t units)
{
    char* out = malloc(units * 3 + 1);
    size_t length = 0;
    for (size_t i = 0; i < units && text[i]; i++)
    {
        uint32_t code = text[i];
        if (code >= 0xD800 && code < 0xDC00 && i + 1 < units && text[i + 1] >= 0xDC00 && text[i + 1] < 0xE000)
        {
            code = 0x10000 + ((code - 0xD800) << 10) + (text[i + 1] - 0xDC00);
            i++;
        }

        if (code < 0x80)
        {
            out[length++] = (char)code;
        }
        else if (code < 0x800)
        {
            out[length++] = (char)(0xC0 | (code >> 6));
            out[length++] = (char)(0x80 | (code & 0x3F));
        }
        else if (code < 0x10000)
        {
            out[length++] = (char)(0xE0 | (code >> 12));
            out[length++] = (char)(0x80 | ((code >> 6) & 0x3F));
            out[length++] = (char)(0x80 | (code & 0x3F));
        }
        else
        {
            out[length++] = (char)(0xF0 | (code >> 18));
            out[length++] = (char)(0x80 | ((code >> 12) & 0x3F));
            out[length++] = (char)(0x80 | ((code >> 6) & 0x3F));
            out[length++] = (char)(0x80 | (code & 0x3F));
        }
    }

    out[length] = 0;
    return out;
}

static void apply_resolution(Session* s)
{
    int width = 0;
    int height = 0;
    session_wanted_size(s, &width, &height);
    if (width > 0 && s->backend->resize)
    {
        s->backend->resize(s, width, height);
    }
}

void session_dispatch(Session* s, const Frame* frame)
{
    if (strcmp(frame->kind, "req") == 0)
    {
        if (strcmp(frame->op, "screen.clip.read") == 0 && s->backend->clip_read)
        {
            s->backend->clip_read(s, frame);
        }
        else
        {
            wire_fail(s, frame, "The display engine does not know this.");
        }

        return;
    }

    const char* op = frame->op;
    if (!s->ready && strcmp(op, "screen.close") != 0)
    {
        return;
    }

    if (strcmp(op, "screen.input") == 0)
    {
        s->backend->input(s, frame->data);
    }
    else if (strcmp(op, "screen.ack") == 0)
    {
        video_ack(s, json_int(frame->data, "seq", 0));
    }
    else if (strcmp(op, "screen.key") == 0)
    {
        video_request_key(s);
    }
    else if (strcmp(op, "screen.config") == 0)
    {
        const char* quality = json_text(frame->data, "quality", NULL);
        if (quality)
        {
            video_quality(s, quality);
        }

        const char* resolution = json_text(frame->data, "resolution", NULL);
        if (resolution)
        {
            snprintf(s->resolution, sizeof(s->resolution), "%s", resolution);
        }

        s->fit_width = (int)json_int(frame->data, "fitWidth", s->fit_width);
        s->fit_height = (int)json_int(frame->data, "fitHeight", s->fit_height);
        if (resolution || json_int(frame->data, "fitWidth", -1) >= 0)
        {
            apply_resolution(s);
        }
    }
    else if (strcmp(op, "screen.audio") == 0)
    {
        audio_enable(s, json_flag(frame->data, "on", 0), json_text(frame->data, "codec", "opus"));
    }
    else if (strcmp(op, "screen.sas") == 0)
    {
        if (s->backend->secure_attention)
        {
            s->backend->secure_attention(s);
        }
    }
    else if (strncmp(op, "screen.clip.", 12) == 0)
    {
        if (s->backend->clip)
        {
            s->backend->clip(s, frame);
        }
    }
    else if (strcmp(op, "screen.close") == 0)
    {
        s->running = 0;
    }
}

int session_pump(Session* s)
{
    if (wire_receive(s) < 0)
    {
        s->running = 0;
        return -1;
    }

    Frame frame;
    while (s->running && wire_next(s, &frame))
    {
        session_dispatch(s, &frame);
        frame_free(&frame);
    }

    return s->running ? 0 : -1;
}

// ---- one viewer ---------------------------------------------------------------------------------------

static void serve(int fd)
{
    Session session;
    memset(&session, 0, sizeof(session));
    Session* s = &session;
    s->fd = fd;
    s->running = 1;
    pthread_mutex_init(&s->send_lock, NULL);
    snprintf(s->resolution, sizeof(s->resolution), "auto");

    Frame open;
    if (!wire_wait_for(s, "screen.open", &open, 15000))
    {
        return;
    }

    json_object* params = open.data;
    if (!same_token(json_text(params, "token", "")))
    {
        wire_fail(s, &open, "The display engine refused Matgate (the key does not match).");
        frame_free(&open);
        return;
    }

    snprintf(s->id, sizeof(s->id), "%s", json_text(params, "session", ""));
    const char* protocol = json_text(params, "protocol", "rdp");
    s->backend = strcmp(protocol, "vnc") == 0 ? &vnc_backend : &rdp_backend;
    snprintf(s->resolution, sizeof(s->resolution), "%s", json_text(params, "resolution", "auto"));
    s->fit_width = (int)json_int(params, "fitWidth", 0);
    s->fit_height = (int)json_int(params, "fitHeight", 0);
    s->video = video_new(s);
    s->audio = audio_new(s);
    video_quality(s, json_text(params, "quality", "auto"));

    char error[512] = { 0 };
    session_log(s, "connecting to %s:%lld", json_text(params, "host", "?"), (long long)json_int(params, "port", 0));
    if (s->backend->open(s, params, error, sizeof(error)) != 0)
    {
        session_log(s, "failed: %s", error);
        wire_fail(s, &open, error[0] ? error : "The connection could not be established.");
        frame_free(&open);
        if (s->backend->close)
        {
            s->backend->close(s);
        }

        return;
    }

    const uint8_t* pixels = NULL;
    int stride = 0;
    int width = 0;
    int height = 0;
    if (s->backend->frame(s, &pixels, &stride, &width, &height))
    {
        s->backend->frame_done(s);
    }

    json_object* answer = json_object_new_object();
    json_object_object_add(answer, "session", json_object_new_string(s->id));
    json_object_object_add(answer, "monitor", json_object_new_int(0));
    json_object_object_add(answer, "width", json_object_new_int(width));
    json_object_object_add(answer, "height", json_object_new_int(height));
    json_object* monitors = json_object_new_array();
    json_object* monitor = json_object_new_object();
    json_object_object_add(monitor, "index", json_object_new_int(0));
    json_object_object_add(monitor, "name", json_object_new_string(s->backend->name));
    json_object_object_add(monitor, "width", json_object_new_int(width));
    json_object_object_add(monitor, "height", json_object_new_int(height));
    json_object_object_add(monitor, "primary", json_object_new_boolean(1));
    json_object_array_add(monitors, monitor);
    json_object_object_add(answer, "monitors", monitors);
    // What the clipboard of this protocol takes: RDP pictures as a Windows bitmap, files at all.
    json_object_object_add(answer, "clipImage", json_object_new_string(s->backend == &rdp_backend ? "dib" : "none"));
    json_object_object_add(answer, "clipFiles", json_object_new_boolean(s->backend == &rdp_backend));
    json_object_object_add(answer, "audio", json_object_new_boolean(s->backend == &rdp_backend));
    wire_answer(s, &open, answer, NULL, 0);
    frame_free(&open);
    session_log(s, "connected, %dx%d", width, height);

    s->ready = 1;
    s->backend->run(s);
    session_log(s, "ended");
    wire_event(s, "screen.ended", json_object_new_object(), NULL, 0);
    if (s->backend->close)
    {
        s->backend->close(s);
    }

    video_free(s->video);
    audio_free(s->audio);
    free(s->inbox);
}

int main(int argc, char** argv)
{
    int port = 8092;
    const char* listen_on = getenv("MATGATE_DISPLAY_PORT");
    if (listen_on)
    {
        port = atoi(listen_on);
    }

    if (argc > 1 && strcmp(argv[1], "--version") == 0)
    {
        puts("matgate-display 1.0");
        return 0;
    }

    signal(SIGPIPE, SIG_IGN);
    // Finished sessions are reaped by the system.
    signal(SIGCHLD, SIG_IGN);
    int server = socket(AF_INET, SOCK_STREAM, 0);
    int yes = 1;
    setsockopt(server, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
    struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons((uint16_t)port), .sin_addr.s_addr = htonl(INADDR_ANY) };
    if (bind(server, (struct sockaddr*)&address, sizeof(address)) < 0 || listen(server, 16) < 0)
    {
        perror("listen");
        return 1;
    }

    fprintf(stderr, "matgate-display listening on %d\n", port);
    for (;;)
    {
        int fd = accept(server, NULL, NULL);
        if (fd < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }

            perror("accept");
            continue;
        }

        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &yes, sizeof(yes));
        pid_t child = fork();
        if (child == 0)
        {
            close(server);
            serve(fd);
            close(fd);
            _exit(0);
        }

        close(fd);
    }
}
