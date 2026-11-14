// The wire to Matgate: every message is [4 bytes length, little endian][agent message], and an agent message
// is [4 bytes header length, big endian][JSON header][payload] - the format of Matgate/Agent/AgentWire.cs.
#include "session.h"

#include <errno.h>
#include <poll.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define MAX_MESSAGE (8 * 1024 * 1024)

static int write_all(int fd, const uint8_t* data, size_t length)
{
    while (length > 0)
    {
        ssize_t written = send(fd, data, length, MSG_NOSIGNAL);
        if (written < 0)
        {
            if (errno == EINTR)
            {
                continue;
            }

            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                struct pollfd wait = { .fd = fd, .events = POLLOUT };
                poll(&wait, 1, 1000);
                continue;
            }

            return -1;
        }

        data += written;
        length -= (size_t)written;
    }

    return 0;
}

int wire_send(Session* s, const char* kind, const char* op, int64_t id, json_object* data, const char* error,
              const uint8_t* payload, size_t payload_len)
{
    json_object* header = json_object_new_object();
    if (id != 0)
    {
        json_object_object_add(header, "id", json_object_new_int64(id));
    }

    json_object_object_add(header, "kind", json_object_new_string(kind));
    json_object_object_add(header, "op", json_object_new_string(op));
    if (error)
    {
        json_object_object_add(header, "error", json_object_new_string(error));
    }

    if (!data)
    {
        data = json_object_new_object();
    }

    if (s->id[0] && !json_object_object_get_ex(data, "session", NULL))
    {
        json_object_object_add(data, "session", json_object_new_string(s->id));
    }

    json_object_object_add(header, "data", data);
    size_t header_len = 0;
    const char* text = json_object_to_json_string_length(header, JSON_C_TO_STRING_PLAIN, &header_len);
    size_t total = 4 + header_len + payload_len;
    uint8_t* message = malloc(4 + total);
    if (!message)
    {
        json_object_put(header);
        return -1;
    }

    message[0] = (uint8_t)(total & 0xFF);
    message[1] = (uint8_t)((total >> 8) & 0xFF);
    message[2] = (uint8_t)((total >> 16) & 0xFF);
    message[3] = (uint8_t)((total >> 24) & 0xFF);
    message[4] = (uint8_t)((header_len >> 24) & 0xFF);
    message[5] = (uint8_t)((header_len >> 16) & 0xFF);
    message[6] = (uint8_t)((header_len >> 8) & 0xFF);
    message[7] = (uint8_t)(header_len & 0xFF);
    memcpy(message + 8, text, header_len);
    if (payload_len)
    {
        memcpy(message + 8 + header_len, payload, payload_len);
    }

    json_object_put(header);
    pthread_mutex_lock(&s->send_lock);
    int result = write_all(s->fd, message, 4 + total);
    pthread_mutex_unlock(&s->send_lock);
    free(message);
    if (result < 0)
    {
        s->running = 0;
    }

    return result;
}

int wire_event(Session* s, const char* op, json_object* data, const uint8_t* payload, size_t payload_len)
{
    return wire_send(s, "evt", op, 0, data, NULL, payload, payload_len);
}

int wire_answer(Session* s, const Frame* request, json_object* data, const uint8_t* payload, size_t payload_len)
{
    return wire_send(s, "res", request->op, request->id, data, NULL, payload, payload_len);
}

int wire_fail(Session* s, const Frame* request, const char* error)
{
    return wire_send(s, "res", request->op, request->id, NULL, error, NULL, 0);
}

int wire_receive(Session* s)
{
    for (;;)
    {
        if (s->inbox_cap - s->inbox_len < 65536)
        {
            size_t capacity = s->inbox_cap ? s->inbox_cap * 2 : 262144;
            uint8_t* grown = realloc(s->inbox, capacity);
            if (!grown)
            {
                return -1;
            }

            s->inbox = grown;
            s->inbox_cap = capacity;
        }

        ssize_t read = recv(s->fd, s->inbox + s->inbox_len, s->inbox_cap - s->inbox_len, MSG_DONTWAIT);
        if (read > 0)
        {
            s->inbox_len += (size_t)read;
            continue;
        }

        if (read == 0)
        {
            return -1;
        }

        if (errno == EINTR)
        {
            continue;
        }

        return (errno == EAGAIN || errno == EWOULDBLOCK) ? 0 : -1;
    }
}

int wire_next(Session* s, Frame* frame)
{
    memset(frame, 0, sizeof(*frame));
    if (s->inbox_len < 4)
    {
        return 0;
    }

    uint32_t total = (uint32_t)s->inbox[0] | ((uint32_t)s->inbox[1] << 8) | ((uint32_t)s->inbox[2] << 16) | ((uint32_t)s->inbox[3] << 24);
    if (total < 6 || total > MAX_MESSAGE)
    {
        s->running = 0;
        return 0;
    }

    if (s->inbox_len < 4 + (size_t)total)
    {
        return 0;
    }

    uint8_t* message = s->inbox + 4;
    uint32_t header_len = ((uint32_t)message[0] << 24) | ((uint32_t)message[1] << 16) | ((uint32_t)message[2] << 8) | message[3];
    int ok = 0;
    if (header_len >= 2 && header_len <= total - 4)
    {
        json_tokener* tokener = json_tokener_new();
        frame->root = json_tokener_parse_ex(tokener, (const char*)message + 4, (int)header_len);
        json_tokener_free(tokener);
        if (frame->root && json_object_is_type(frame->root, json_type_object))
        {
            frame->kind = json_text(frame->root, "kind", "");
            frame->op = json_text(frame->root, "op", "");
            frame->id = json_int(frame->root, "id", 0);
            json_object_object_get_ex(frame->root, "data", &frame->data);
            size_t payload_len = total - 4 - header_len;
            if (payload_len)
            {
                uint8_t* payload = malloc(payload_len);
                if (payload)
                {
                    memcpy(payload, message + 4 + header_len, payload_len);
                    frame->payload = payload;
                    frame->payload_len = payload_len;
                }
            }

            ok = 1;
        }
    }

    size_t rest = s->inbox_len - 4 - total;
    memmove(s->inbox, s->inbox + 4 + total, rest);
    s->inbox_len = rest;
    if (!ok)
    {
        frame_free(frame);
        // A message that could not be read is skipped; the next one may well be fine.
        return wire_next(s, frame);
    }

    return 1;
}

void frame_free(Frame* frame)
{
    if (frame->root)
    {
        json_object_put(frame->root);
    }

    free((void*)frame->payload);
    memset(frame, 0, sizeof(*frame));
}

static int64_t now_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

int wire_wait_for(Session* s, const char* op, Frame* found, int timeout_ms)
{
    int64_t until = now_ms() + timeout_ms;
    while (s->running)
    {
        Frame frame;
        while (wire_next(s, &frame))
        {
            if (strcmp(frame.op, op) == 0)
            {
                *found = frame;
                return 1;
            }

            session_dispatch(s, &frame);
            frame_free(&frame);
        }

        int64_t left = until - now_ms();
        if (left <= 0)
        {
            return 0;
        }

        struct pollfd wait = { .fd = s->fd, .events = POLLIN };
        if (poll(&wait, 1, (int)(left > 500 ? 500 : left)) > 0 && wire_receive(s) < 0)
        {
            s->running = 0;
        }
    }

    return 0;
}

const char* json_text(json_object* object, const char* name, const char* fallback)
{
    json_object* value = NULL;
    if (object && json_object_object_get_ex(object, name, &value) && value && json_object_is_type(value, json_type_string))
    {
        return json_object_get_string(value);
    }

    return fallback;
}

int64_t json_int(json_object* object, const char* name, int64_t fallback)
{
    json_object* value = NULL;
    if (object && json_object_object_get_ex(object, name, &value) && value
        && (json_object_is_type(value, json_type_int) || json_object_is_type(value, json_type_double)))
    {
        return json_object_get_int64(value);
    }

    return fallback;
}

int json_flag(json_object* object, const char* name, int fallback)
{
    json_object* value = NULL;
    if (object && json_object_object_get_ex(object, name, &value) && value && json_object_is_type(value, json_type_boolean))
    {
        return json_object_get_boolean(value);
    }

    return fallback;
}

void session_log(Session* s, const char* format, ...)
{
    char line[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof(line), format, args);
    va_end(args);
    fprintf(stderr, "[%d %s] %s\n", getpid(), s && s->backend ? s->backend->name : "-", line);
}
