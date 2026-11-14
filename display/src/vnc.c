// VNC through libvncclient. The frame buffer is asked for as BGRX, which is what the encoder reads; keys go
// as X keysyms (VNC knows no scan codes - the page sends the keysym of every key along with it); the
// clipboard carries text (UTF-8 where the server can, Latin-1 otherwise); files are not part of VNC.
#include "session.h"

#include <poll.h>
#include <rfb/rfbclient.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static int session_tag;

typedef struct
{
    rfbClient* client;
    Session* session;
    char* password;
    char* username;
    int x;
    int y;
    int buttons;
    // The size the viewer wants, and what was last asked for and when.
    int want_width;
    int want_height;
    int asked_width;
    int asked_height;
    int64_t asked_at;
} Vnc;

static int64_t now_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static Session* session_of(rfbClient* client)
{
    return (Session*)rfbClientGetClientData(client, &session_tag);
}

static rfbBool allocate_frame_buffer(rfbClient* client)
{
    free(client->frameBuffer);
    size_t size = (size_t)client->width * client->height * 4;
    client->frameBuffer = calloc(1, size ? size : 4);
    client->updateRect.x = 0;
    client->updateRect.y = 0;
    client->updateRect.w = client->width;
    client->updateRect.h = client->height;
    Session* s = session_of(client);
    if (s)
    {
        video_dirty(s);
    }

    return client->frameBuffer != NULL;
}

static void got_update(rfbClient* client, int x, int y, int w, int h)
{
    (void)x;
    (void)y;
    (void)w;
    (void)h;
    video_dirty(session_of(client));
}

// Credentials the connection did not bring: the viewer is asked (its page shows a dialog).
static int ask(Session* s, const char* need, char** username, char** password)
{
    json_object* data = json_object_new_object();
    json_object* fields = json_object_new_array();
    if (strstr(need, "username"))
    {
        json_object_array_add(fields, json_object_new_string("username"));
    }

    json_object_array_add(fields, json_object_new_string("password"));
    json_object_object_add(data, "need", fields);
    wire_event(s, "screen.auth", data, NULL, 0);
    Frame answer;
    if (!wire_wait_for(s, "screen.auth", &answer, 180000))
    {
        return 0;
    }

    int ok = !json_flag(answer.data, "cancel", 0);
    if (ok)
    {
        if (username)
        {
            *username = strdup(json_text(answer.data, "username", ""));
        }

        *password = strdup(json_text(answer.data, "password", ""));
    }

    frame_free(&answer);
    return ok;
}

static char* get_password(rfbClient* client)
{
    Session* s = session_of(client);
    Vnc* vnc = s->impl;
    if (vnc->password && *vnc->password)
    {
        return strdup(vnc->password);
    }

    char* password = NULL;
    return ask(s, "password", NULL, &password) ? password : strdup("");
}

static rfbCredential* get_credential(rfbClient* client, int type)
{
    Session* s = session_of(client);
    Vnc* vnc = s->impl;
    if (type != rfbCredentialTypeUser)
    {
        return NULL;
    }

    rfbCredential* credential = calloc(1, sizeof(rfbCredential));
    if (vnc->username && *vnc->username && vnc->password && *vnc->password)
    {
        credential->userCredential.username = strdup(vnc->username);
        credential->userCredential.password = strdup(vnc->password);
        return credential;
    }

    if (!ask(s, "username password", &credential->userCredential.username, &credential->userCredential.password))
    {
        free(credential);
        return NULL;
    }

    return credential;
}

static void emit_text(Session* s, const char* text, size_t length)
{
    // Some servers send the terminating zero along.
    while (length > 0 && text[length - 1] == 0)
    {
        length--;
    }

    json_object* data = json_object_new_object();
    json_object_object_add(data, "kind", json_object_new_string("text"));
    wire_event(s, "screen.clip", data, (const uint8_t*)text, length);
}

static void got_cut_text(rfbClient* client, const char* text, int length)
{
    // Latin-1 -> UTF-8.
    char* utf8 = malloc((size_t)length * 2 + 1);
    size_t out = 0;
    for (int i = 0; i < length; i++)
    {
        unsigned char c = (unsigned char)text[i];
        if (c < 0x80)
        {
            utf8[out++] = (char)c;
        }
        else
        {
            utf8[out++] = (char)(0xC0 | (c >> 6));
            utf8[out++] = (char)(0x80 | (c & 0x3F));
        }
    }

    emit_text(session_of(client), utf8, out);
    free(utf8);
}

static void got_cut_text_utf8(rfbClient* client, const char* text, int length)
{
    emit_text(session_of(client), text, (size_t)length);
}

static void got_cursor_shape(rfbClient* client, int hot_x, int hot_y, int width, int height, int bytes_per_pixel)
{
    if (!client->rcSource || bytes_per_pixel != 4 || width <= 0 || height <= 0 || width > 256 || height > 256)
    {
        return;
    }

    uint8_t* rgba = malloc((size_t)width * height * 4);
    for (int i = 0; i < width * height; i++)
    {
        const uint8_t* pixel = client->rcSource + i * 4;
        rgba[i * 4] = pixel[2];
        rgba[i * 4 + 1] = pixel[1];
        rgba[i * 4 + 2] = pixel[0];
        rgba[i * 4 + 3] = client->rcMask && client->rcMask[i] ? 255 : 0;
    }

    video_cursor(session_of(client), hot_x, hot_y, width, height, rgba);
    free(rgba);
}

// SetDesktopSize with one screen that fills the desktop. Written here rather than with libvncclient's
// SendExtDesktopSize, which leaves the screen's position and flags uninitialised - TigerVNC then refuses
// the layout. The server answers with a new desktop size, which is handled like any other.
static void set_desktop_size(rfbClient* client, int width, int height)
{
    rfbSetDesktopSizeMsg message;
    rfbExtDesktopScreen screen;
    memset(&message, 0, sizeof(message));
    memset(&screen, 0, sizeof(screen));
    message.type = rfbSetDesktopSize;
    message.width = rfbClientSwap16IfLE((uint16_t)width);
    message.height = rfbClientSwap16IfLE((uint16_t)height);
    message.numberOfScreens = 1;
    // The id as the server named it (kept as it came off the wire).
    screen.id = client->screen.id;
    screen.width = message.width;
    screen.height = message.height;
    if (WriteToRFBServer(client, (char*)&message, sz_rfbSetDesktopSizeMsg))
    {
        WriteToRFBServer(client, (char*)&screen, sz_rfbExtDesktopScreen);
    }
}

static int vnc_open(Session* s, json_object* params, char* error, size_t error_len)
{
    Vnc* vnc = calloc(1, sizeof(Vnc));
    s->impl = vnc;
    vnc->session = s;
    vnc->username = strdup(json_text(params, "username", ""));
    vnc->password = strdup(json_text(params, "password", ""));

    rfbClient* client = rfbGetClient(8, 3, 4);
    // Blue, green, red, padding in memory - what the encoder takes.
    client->format.redShift = 16;
    client->format.greenShift = 8;
    client->format.blueShift = 0;
    client->format.redMax = 255;
    client->format.greenMax = 255;
    client->format.blueMax = 255;
    client->MallocFrameBuffer = allocate_frame_buffer;
    client->canHandleNewFBSize = TRUE;
    client->GotFrameBufferUpdate = got_update;
    client->GetPassword = get_password;
    client->GetCredential = get_credential;
    client->GotXCutText = got_cut_text;
    client->GotXCutTextUTF8 = got_cut_text_utf8;
    client->GotCursorShape = got_cursor_shape;
    client->appData.useRemoteCursor = TRUE;
    client->appData.encodingsString = "tight zrle ultra copyrect hextile zlib corre rre raw";
    client->appData.compressLevel = 3;
    client->appData.qualityLevel = 8;
    client->serverHost = strdup(json_text(params, "host", ""));
    client->serverPort = (int)json_int(params, "port", 5900);
    rfbClientSetClientData(client, &session_tag, s);
    if (!rfbInitClient(client, NULL, NULL))
    {
        // rfbInitClient has freed the client already.
        snprintf(error, error_len, "The VNC server at %s:%lld could not be reached, or did not accept the login.",
                 json_text(params, "host", ""), (long long)json_int(params, "port", 5900));
        return -1;
    }

    vnc->client = client;
    s->native_width = client->width;
    s->native_height = client->height;
    session_wanted_size(s, &vnc->want_width, &vnc->want_height);
    return 0;
}

static void vnc_run(Session* s)
{
    Vnc* vnc = s->impl;
    rfbClient* client = vnc->client;
    while (s->running)
    {
        int timeout = video_tick(s);
        if (timeout > 50)
        {
            timeout = 50;
        }

        struct pollfd fds[2] = { { .fd = client->sock, .events = POLLIN }, { .fd = s->fd, .events = POLLIN } };
        int ready = client->buffered > 0 ? 1 : poll(fds, 2, timeout);
        if (ready < 0)
        {
            continue;
        }

        if (client->buffered > 0 || (fds[0].revents & (POLLIN | POLLHUP | POLLERR)))
        {
            if (!HandleRFBServerMessage(client))
            {
                session_status(s, "error", "The connection to the VNC server was lost.");
                break;
            }
        }

        if (fds[1].revents & (POLLIN | POLLHUP | POLLERR))
        {
            session_pump(s);
        }

        // The window has another size than the desktop: the server is asked to follow (it can once it has
        // told its screen layout - not every VNC server does). A new size at most every 300 ms, the same one
        // again only after a while.
        if (vnc->want_width > 0 && client->screen.width > 0
            && (client->width != vnc->want_width || client->height != vnc->want_height))
        {
            int fresh = vnc->want_width != vnc->asked_width || vnc->want_height != vnc->asked_height;
            if (now_ms() - vnc->asked_at > (fresh ? 300 : 5000))
            {
                set_desktop_size(client, vnc->want_width, vnc->want_height);
                vnc->asked_width = vnc->want_width;
                vnc->asked_height = vnc->want_height;
                vnc->asked_at = now_ms();
            }
        }
    }
}

static int vnc_frame(Session* s, const uint8_t** pixels, int* stride, int* width, int* height)
{
    Vnc* vnc = s->impl;
    if (!vnc || !vnc->client || !vnc->client->frameBuffer)
    {
        return 0;
    }

    *pixels = vnc->client->frameBuffer;
    *stride = vnc->client->width * 4;
    *width = vnc->client->width;
    *height = vnc->client->height;
    return 1;
}

static void vnc_frame_done(Session* s)
{
    // Everything here happens in one loop: nothing draws while the picture is read.
    (void)s;
}

static void key(Session* s, uint32_t keysym, int down)
{
    Vnc* vnc = s->impl;
    if (down)
    {
        if (s->keysyms_count < 64)
        {
            s->keysyms_down[s->keysyms_count++] = keysym;
        }
    }
    else
    {
        for (int i = 0; i < s->keysyms_count; i++)
        {
            if (s->keysyms_down[i] == keysym)
            {
                s->keysyms_down[i] = s->keysyms_down[--s->keysyms_count];
                break;
            }
        }
    }

    SendKeyEvent(vnc->client, keysym, down ? TRUE : FALSE);
}

static void tap(Session* s, uint32_t keysym)
{
    key(s, keysym, 1);
    key(s, keysym, 0);
}

static void wheel(Vnc* vnc, int value, int up_button, int down_button)
{
    int notches = abs(value) / 120;
    if (notches == 0 && value != 0)
    {
        notches = 1;
    }

    int button = value > 0 ? up_button : down_button;
    for (int i = 0; i < notches && i < 10; i++)
    {
        SendPointerEvent(vnc->client, vnc->x, vnc->y, vnc->buttons | button);
        SendPointerEvent(vnc->client, vnc->x, vnc->y, vnc->buttons);
    }
}

static void vnc_input(Session* s, json_object* data)
{
    Vnc* vnc = s->impl;
    const char* type = json_text(data, "t", "");
    switch (type[0])
    {
        case 'm':
            vnc->x = (int)json_int(data, "x", 0);
            vnc->y = (int)json_int(data, "y", 0);
            SendPointerEvent(vnc->client, vnc->x, vnc->y, vnc->buttons);
            break;
        case 'b':
        {
            int which = (int)json_int(data, "b", 0);
            int mask = which == 0 ? 1 : which == 1 ? 2 : which == 2 ? 4 : 0;
            if (json_flag(data, "d", 0))
            {
                vnc->buttons |= mask;
            }
            else
            {
                vnc->buttons &= ~mask;
            }

            SendPointerEvent(vnc->client, vnc->x, vnc->y, vnc->buttons);
            break;
        }
        case 'w':
            wheel(vnc, (int)json_int(data, "dy", 0), 8, 16);
            wheel(vnc, (int)json_int(data, "dx", 0), 64, 32);
            break;
        case 'k':
        {
            uint32_t keysym = (uint32_t)json_int(data, "ks", 0);
            int down = json_flag(data, "d", 0);
            if (keysym && down && json_flag(data, "ag", 0))
            {
                // A character typed with AltGr: the keysym says it all - the Ctrl+Alt a Windows browser reports
                // for AltGr must not turn it into a shortcut on the server.
                static const uint32_t modifiers[] = { 0xFFE3, 0xFFEA, 0xFE03 };
                for (size_t m = 0; m < 3; m++)
                {
                    for (int i = 0; i < s->keysyms_count; i++)
                    {
                        if (s->keysyms_down[i] == modifiers[m])
                        {
                            key(s, modifiers[m], 0);
                            break;
                        }
                    }
                }
            }

            if (keysym)
            {
                key(s, keysym, down);
            }

            break;
        }
        case 'u':
        {
            size_t units = 0;
            const char* text = json_text(data, "text", "");
            uint16_t* utf16 = utf8_to_utf16(text, strlen(text), &units);
            for (size_t i = 0; i < units; i++)
            {
                uint32_t code = utf16[i];
                tap(s, code < 0x100 ? code : 0x01000000 | code);
            }

            free(utf16);
            break;
        }
        case 'r':
            while (s->keysyms_count > 0)
            {
                key(s, s->keysyms_down[s->keysyms_count - 1], 0);
            }

            vnc->buttons = 0;
            SendPointerEvent(vnc->client, vnc->x, vnc->y, 0);
            break;
    }
}

static void vnc_resize(Session* s, int width, int height)
{
    Vnc* vnc = s->impl;
    vnc->want_width = width;
    vnc->want_height = height;
}

static void vnc_clip(Session* s, const Frame* frame)
{
    Vnc* vnc = s->impl;
    if (strcmp(frame->op, "screen.clip.set") != 0)
    {
        // Files are not part of VNC.
        if (strcmp(frame->op, "screen.clip.begin") == 0)
        {
            session_status(s, "clipfailed", NULL);
        }

        return;
    }

    char* text = malloc(frame->payload_len + 1);
    memcpy(text, frame->payload, frame->payload_len);
    text[frame->payload_len] = 0;
    if (!SendClientCutTextUTF8(vnc->client, text, (int)frame->payload_len))
    {
        SendClientCutText(vnc->client, text, (int)frame->payload_len);
    }

    free(text);
    int paste = json_flag(frame->data, "paste", 0);
    if (paste)
    {
        // Ctrl+V on the remote desktop.
        key(s, 0xFFE3, 1);
        tap(s, 0x76);
        key(s, 0xFFE3, 0);
    }

    json_object* done = json_object_new_object();
    json_object_object_add(done, "paste", json_object_new_boolean(paste));
    wire_event(s, "screen.clip.done", done, NULL, 0);
}

static void vnc_secure_attention(Session* s)
{
    key(s, 0xFFE3, 1);
    key(s, 0xFFE9, 1);
    tap(s, 0xFFFF);
    key(s, 0xFFE9, 0);
    key(s, 0xFFE3, 0);
}

static void vnc_close(Session* s)
{
    Vnc* vnc = s->impl;
    if (!vnc)
    {
        return;
    }

    if (vnc->client)
    {
        rfbClientCleanup(vnc->client);
    }

    free(vnc->username);
    free(vnc->password);
    free(vnc);
    s->impl = NULL;
}

const Backend vnc_backend = {
    .name = "VNC",
    .open = vnc_open,
    .run = vnc_run,
    .frame = vnc_frame,
    .frame_done = vnc_frame_done,
    .input = vnc_input,
    .resize = vnc_resize,
    .clip = vnc_clip,
    .clip_read = NULL,
    .secure_attention = vnc_secure_attention,
    .close = vnc_close,
};
