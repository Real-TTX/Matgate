// RDP through FreeRDP 3. FreeRDP's GDI draws the desktop into a BGRX buffer (with the graphics pipeline where
// the server offers it, decoded here - the viewer gets one H.264 stream either way). Keys travel as scan
// codes, so the layout of the remote session applies; the display control channel follows the viewer's
// window without reconnecting; the clipboard carries text, pictures and files in both directions (CLIPRDR
// file streams - Ctrl+V in the browser puts real files into the Explorer window that has the focus).
#include "session.h"

#include <freerdp/addin.h>
#include <freerdp/channels/channels.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/channels/disp.h>
#include <freerdp/client.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/rdpsnd.h>
#include <freerdp/codec/color.h>
#include <freerdp/event.h>
#include <freerdp/freerdp.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/graphics.h>
#include <freerdp/input.h>
#include <freerdp/scancode.h>
#include <freerdp/settings.h>
#include <winpr/synch.h>

#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#define CF_TEXT_UNICODE 13
#define CF_BITMAP_DIB 8
#define FILE_LIST_FORMAT_ID 0xC0D0
#define DESCRIPTOR_SIZE 592
#define READ_CHUNK (256 * 1024)
#define MAX_UPLOADS 4
#define MAX_DOWNLOADS 32

typedef struct
{
    char name[520];
    uint64_t size;
    int folder;
} RemoteFile;

typedef struct
{
    char path[512];
    char name[260];
    uint64_t size;
} LocalFile;

typedef struct
{
    char id[72];
    int image;
    int paste;
    int count;
    LocalFile* files;
} Upload;

typedef struct
{
    UINT32 stream;
    int64_t request;
    int index;
} Download;

typedef struct
{
    rdpClientContext common;
    Session* session;
    CliprdrClientContext* cliprdr;
    DispClientContext* disp;
    int disp_ready;
    int want_width;
    int want_height;
    int asked_width;
    int asked_height;
    int64_t last_layout;
    int64_t resized_at;
    int reactivating;
    UINT16 x;
    UINT16 y;
    int auth_failed;
    int ignore_certificate;

    // FreeRDP draws into the frame buffer on the thread of its graphics channel (and takes the buffer away
    // and makes a new one there when the desktop is resized); the encoder reads it on the session's loop.
    // Whoever touches the buffer holds this. Recursive: a resize may come in the middle of a paint.
    pthread_mutex_t frame_lock;

    pthread_mutex_t clip_lock;
    // The remote clipboard, as offered to the viewer.
    UINT32 file_list_format;
    UINT32 requested_format;
    RemoteFile* remote_files;
    int remote_count;
    char remote_token[40];
    Download downloads[MAX_DOWNLOADS];
    UINT32 next_stream;
    // What this side put on the remote clipboard.
    int local_kind; // 0 nothing, 1 text, 2 picture, 3 files
    uint16_t* local_text;
    size_t local_text_units;
    uint8_t* local_image;
    size_t local_image_len;
    LocalFile* local_files;
    int local_count;
    int paste_pending;
    Upload uploads[MAX_UPLOADS];
    int clip_serial;
} MgContext;

typedef struct
{
    rdpPointer pointer;
    uint8_t* rgba;
} MgPointer;

// One session per process: the sound device finds its session here.
static Session* current_session;

static int64_t now_ms(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static MgContext* mg_of(rdpContext* context)
{
    return (MgContext*)context;
}

// ---- sound: an rdpsnd device of its own that hands PCM to the encoder ---------------------------------

typedef struct
{
    rdpsndDevicePlugin device;
    AUDIO_FORMAT format;
} MgSound;

static BOOL sound_supported(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format)
{
    (void)device;
    return format->wFormatTag == WAVE_FORMAT_PCM && format->wBitsPerSample == 16 && format->nChannels >= 1 && format->nChannels <= 2
        && (format->nSamplesPerSec == 48000 || format->nSamplesPerSec == 44100 || format->nSamplesPerSec == 22050);
}

static BOOL sound_open(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format, UINT32 latency)
{
    (void)latency;
    if (format)
    {
        ((MgSound*)device)->format = *format;
    }

    return TRUE;
}

static UINT32 sound_get_volume(rdpsndDevicePlugin* device)
{
    (void)device;
    return 0xFFFFFFFF;
}

static BOOL sound_set_volume(rdpsndDevicePlugin* device, UINT32 value)
{
    (void)device;
    (void)value;
    return TRUE;
}

static UINT sound_play(rdpsndDevicePlugin* device, const BYTE* data, size_t size)
{
    MgSound* sound = (MgSound*)device;
    int channels = sound->format.nChannels ? sound->format.nChannels : 2;
    if (current_session)
    {
        audio_pcm(current_session, (const int16_t*)data, size / (2 * (size_t)channels), (int)sound->format.nSamplesPerSec, channels);
    }

    return 0;
}

static void sound_close(rdpsndDevicePlugin* device)
{
    (void)device;
}

static void sound_free(rdpsndDevicePlugin* device)
{
    free(device);
}

static UINT VCAPITYPE sound_entry(PFREERDP_RDPSND_DEVICE_ENTRY_POINTS entry_points)
{
    MgSound* sound = calloc(1, sizeof(MgSound));
    if (!sound)
    {
        return CHANNEL_RC_NO_MEMORY;
    }

    sound->device.FormatSupported = sound_supported;
    sound->device.Open = sound_open;
    sound->device.GetVolume = sound_get_volume;
    sound->device.SetVolume = sound_set_volume;
    sound->device.Play = sound_play;
    sound->device.Close = sound_close;
    sound->device.Free = sound_free;
    entry_points->pRegisterRdpsndDevice(entry_points->rdpsnd, &sound->device);
    return CHANNEL_RC_OK;
}

static PVIRTUALCHANNELENTRY addin_provider(LPCSTR name, LPCSTR subsystem, LPCSTR type, DWORD flags)
{
    if (name && subsystem && strcmp(name, "rdpsnd") == 0 && strcmp(subsystem, "matgate") == 0)
    {
        return (PVIRTUALCHANNELENTRY)(void*)sound_entry;
    }

    return freerdp_channels_load_static_addin_entry(name, subsystem, type, flags);
}

// ---- the mouse pointer ---------------------------------------------------------------------------------

static BOOL pointer_new(rdpContext* context, rdpPointer* pointer)
{
    MgPointer* own = (MgPointer*)pointer;
    size_t size = (size_t)pointer->width * pointer->height * 4;
    own->rgba = calloc(1, size ? size : 4);
    if (!own->rgba)
    {
        return FALSE;
    }

    if (!freerdp_image_copy_from_pointer_data(own->rgba, PIXEL_FORMAT_RGBA32, 0, 0, 0, pointer->width, pointer->height,
                                              pointer->xorMaskData, pointer->lengthXorMask, pointer->andMaskData,
                                              pointer->lengthAndMask, pointer->xorBpp, &context->gdi->palette))
    {
        free(own->rgba);
        own->rgba = NULL;
        return FALSE;
    }

    return TRUE;
}

static void pointer_free(rdpContext* context, rdpPointer* pointer)
{
    (void)context;
    MgPointer* own = (MgPointer*)pointer;
    free(own->rgba);
    own->rgba = NULL;
}

static BOOL pointer_set(rdpContext* context, rdpPointer* pointer)
{
    MgPointer* own = (MgPointer*)pointer;
    if (own->rgba && pointer->width > 0 && pointer->height > 0 && pointer->width <= 384 && pointer->height <= 384)
    {
        video_cursor(mg_of(context)->session, (int)pointer->xPos, (int)pointer->yPos, (int)pointer->width, (int)pointer->height, own->rgba);
    }

    return TRUE;
}

static BOOL pointer_set_null(rdpContext* context)
{
    video_cursor_hidden(mg_of(context)->session, 1);
    return TRUE;
}

static BOOL pointer_set_default(rdpContext* context)
{
    video_cursor_default(mg_of(context)->session);
    return TRUE;
}

static BOOL pointer_set_position(rdpContext* context, UINT32 x, UINT32 y)
{
    (void)context;
    (void)x;
    (void)y;
    return TRUE;
}

// ---- painting ------------------------------------------------------------------------------------------

static BOOL begin_paint(rdpContext* context)
{
    pthread_mutex_lock(&mg_of(context)->frame_lock);
    return TRUE;
}

static BOOL end_paint(rdpContext* context)
{
    video_dirty(mg_of(context)->session);
    pthread_mutex_unlock(&mg_of(context)->frame_lock);
    return TRUE;
}

static BOOL desktop_resize(rdpContext* context)
{
    rdpSettings* settings = context->settings;
    pthread_mutex_lock(&mg_of(context)->frame_lock);
    BOOL ok = gdi_resize(context->gdi, freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth),
                         freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight));
    pthread_mutex_unlock(&mg_of(context)->frame_lock);
    mg_of(context)->resized_at = now_ms();
    video_dirty(mg_of(context)->session);
    return ok;
}

// ---- clipboard -----------------------------------------------------------------------------------------

static void send_format_list(MgContext* mg)
{
    if (!mg->cliprdr)
    {
        return;
    }

    CLIPRDR_FORMAT formats[1];
    CLIPRDR_FORMAT_LIST list = { 0 };
    list.common.msgType = CB_FORMAT_LIST;
    list.formats = formats;
    switch (mg->local_kind)
    {
        case 1:
            formats[0].formatId = CF_TEXT_UNICODE;
            formats[0].formatName = NULL;
            list.numFormats = 1;
            break;
        case 2:
            formats[0].formatId = CF_BITMAP_DIB;
            formats[0].formatName = NULL;
            list.numFormats = 1;
            break;
        case 3:
            formats[0].formatId = FILE_LIST_FORMAT_ID;
            formats[0].formatName = "FileGroupDescriptorW";
            list.numFormats = 1;
            break;
        default:
            list.numFormats = 0;
            break;
    }

    mg->cliprdr->ClientFormatList(mg->cliprdr, &list);
}

static UINT clip_monitor_ready(CliprdrClientContext* cliprdr, const CLIPRDR_MONITOR_READY* ready)
{
    (void)ready;
    MgContext* mg = cliprdr->custom;
    CLIPRDR_GENERAL_CAPABILITY_SET general = { 0 };
    general.capabilitySetType = CB_CAPSTYPE_GENERAL;
    general.capabilitySetLength = 12;
    general.version = CB_CAPS_VERSION_2;
    general.generalFlags = CB_USE_LONG_FORMAT_NAMES | CB_STREAM_FILECLIP_ENABLED | CB_FILECLIP_NO_FILE_PATHS;
    CLIPRDR_CAPABILITIES capabilities = { 0 };
    capabilities.common.msgType = CB_CLIP_CAPS;
    capabilities.cCapabilitiesSets = 1;
    capabilities.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;
    cliprdr->ClientCapabilities(cliprdr, &capabilities);
    pthread_mutex_lock(&mg->clip_lock);
    send_format_list(mg);
    pthread_mutex_unlock(&mg->clip_lock);
    return CHANNEL_RC_OK;
}

static UINT clip_server_capabilities(CliprdrClientContext* cliprdr, const CLIPRDR_CAPABILITIES* capabilities)
{
    (void)cliprdr;
    (void)capabilities;
    return CHANNEL_RC_OK;
}

// Something was copied on the remote desktop: files first, then text, then a picture.
static UINT clip_server_format_list(CliprdrClientContext* cliprdr, const CLIPRDR_FORMAT_LIST* list)
{
    MgContext* mg = cliprdr->custom;
    CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
    response.common.msgType = CB_FORMAT_LIST_RESPONSE;
    response.common.msgFlags = CB_RESPONSE_OK;
    cliprdr->ClientFormatListResponse(cliprdr, &response);

    UINT32 files = 0;
    int text = 0;
    int image = 0;
    for (UINT32 i = 0; i < list->numFormats; i++)
    {
        const CLIPRDR_FORMAT* format = &list->formats[i];
        if (format->formatName && strcmp(format->formatName, "FileGroupDescriptorW") == 0)
        {
            files = format->formatId;
        }
        else if (format->formatId == CF_TEXT_UNICODE)
        {
            text = 1;
        }
        else if (format->formatId == CF_BITMAP_DIB)
        {
            image = 1;
        }
    }

    pthread_mutex_lock(&mg->clip_lock);
    // The remote side owns the clipboard now.
    mg->local_kind = 0;
    mg->file_list_format = files;
    UINT32 wanted = files ? files : text ? CF_TEXT_UNICODE : image ? CF_BITMAP_DIB : 0;
    mg->requested_format = wanted;
    pthread_mutex_unlock(&mg->clip_lock);
    if (wanted)
    {
        CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
        request.common.msgType = CB_FORMAT_DATA_REQUEST;
        request.requestedFormatId = wanted;
        cliprdr->ClientFormatDataRequest(cliprdr, &request);
    }

    return CHANNEL_RC_OK;
}

static UINT clip_server_format_list_response(CliprdrClientContext* cliprdr, const CLIPRDR_FORMAT_LIST_RESPONSE* response)
{
    MgContext* mg = cliprdr->custom;
    Session* s = mg->session;
    pthread_mutex_lock(&mg->clip_lock);
    int paste = mg->paste_pending;
    mg->paste_pending = 0;
    pthread_mutex_unlock(&mg->clip_lock);
    if (!(response->common.msgFlags & CB_RESPONSE_OK))
    {
        session_status(s, "clipfailed", NULL);
        return CHANNEL_RC_OK;
    }

    if (paste)
    {
        // Ctrl+V where the remote desktop has its focus; it then asks for the data.
        rdpInput* input = mg->common.context.input;
        freerdp_input_send_keyboard_event_ex(input, TRUE, FALSE, RDP_SCANCODE_LCONTROL);
        freerdp_input_send_keyboard_event_ex(input, TRUE, FALSE, RDP_SCANCODE_KEY_V);
        freerdp_input_send_keyboard_event_ex(input, FALSE, FALSE, RDP_SCANCODE_KEY_V);
        freerdp_input_send_keyboard_event_ex(input, FALSE, FALSE, RDP_SCANCODE_LCONTROL);
    }

    json_object* done = json_object_new_object();
    json_object_object_add(done, "paste", json_object_new_boolean(paste));
    wire_event(s, "screen.clip.done", done, NULL, 0);
    return CHANNEL_RC_OK;
}

static void put_u32(uint8_t* at, uint32_t value)
{
    at[0] = (uint8_t)value;
    at[1] = (uint8_t)(value >> 8);
    at[2] = (uint8_t)(value >> 16);
    at[3] = (uint8_t)(value >> 24);
}

static uint32_t get_u32(const uint8_t* at)
{
    return (uint32_t)at[0] | ((uint32_t)at[1] << 8) | ((uint32_t)at[2] << 16) | ((uint32_t)at[3] << 24);
}

// The remote desktop asks for what this side put on its clipboard.
static UINT clip_server_format_data_request(CliprdrClientContext* cliprdr, const CLIPRDR_FORMAT_DATA_REQUEST* request)
{
    MgContext* mg = cliprdr->custom;
    CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
    response.common.msgType = CB_FORMAT_DATA_RESPONSE;
    uint8_t* owned = NULL;
    pthread_mutex_lock(&mg->clip_lock);
    if (request->requestedFormatId == CF_TEXT_UNICODE && mg->local_kind == 1)
    {
        response.common.dataLen = (UINT32)((mg->local_text_units + 1) * 2);
        response.requestedFormatData = (const BYTE*)mg->local_text;
    }
    else if (request->requestedFormatId == CF_BITMAP_DIB && mg->local_kind == 2)
    {
        response.common.dataLen = (UINT32)mg->local_image_len;
        response.requestedFormatData = mg->local_image;
    }
    else if (request->requestedFormatId == FILE_LIST_FORMAT_ID && mg->local_kind == 3)
    {
        // FILEGROUPDESCRIPTORW: a count, then one FILEDESCRIPTORW of 592 bytes per file.
        size_t length = 4 + (size_t)mg->local_count * DESCRIPTOR_SIZE;
        owned = calloc(1, length);
        put_u32(owned, (uint32_t)mg->local_count);
        for (int i = 0; i < mg->local_count; i++)
        {
            uint8_t* entry = owned + 4 + (size_t)i * DESCRIPTOR_SIZE;
            const LocalFile* file = &mg->local_files[i];
            put_u32(entry, 0x00000004 | 0x00000040 | 0x00004000); // attributes, size, progress UI
            put_u32(entry + 36, 0x00000080);                       // FILE_ATTRIBUTE_NORMAL
            put_u32(entry + 64, (uint32_t)(file->size >> 32));
            put_u32(entry + 68, (uint32_t)(file->size & 0xFFFFFFFF));
            size_t units = 0;
            uint16_t* name = utf8_to_utf16(file->name, strlen(file->name), &units);
            if (units > 259)
            {
                units = 259;
            }

            memcpy(entry + 72, name, units * 2);
            free(name);
        }

        response.common.dataLen = (UINT32)length;
        response.requestedFormatData = owned;
    }

    response.common.msgFlags = response.requestedFormatData ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
    if (!response.requestedFormatData)
    {
        response.common.dataLen = 0;
    }

    cliprdr->ClientFormatDataResponse(cliprdr, &response);
    pthread_mutex_unlock(&mg->clip_lock);
    free(owned);
    return CHANNEL_RC_OK;
}

// A Windows bitmap (CF_DIB) as a .bmp file - what a browser can decode.
static uint8_t* dib_to_bmp(const uint8_t* dib, size_t length, size_t* out_length)
{
    if (length < 40)
    {
        return NULL;
    }

    uint32_t header = get_u32(dib);
    uint16_t bits = (uint16_t)(dib[14] | (dib[15] << 8));
    uint32_t compression = get_u32(dib + 16);
    uint32_t used = get_u32(dib + 32);
    uint32_t palette = used ? used : (bits <= 8 ? (1u << bits) : 0);
    uint32_t masks = (compression == 3 && header == 40) ? 12 : 0;
    size_t total = 14 + length;
    uint8_t* bmp = malloc(total);
    bmp[0] = 'B';
    bmp[1] = 'M';
    put_u32(bmp + 2, (uint32_t)total);
    put_u32(bmp + 6, 0);
    put_u32(bmp + 10, 14 + header + masks + palette * 4);
    memcpy(bmp + 14, dib, length);
    *out_length = total;
    return bmp;
}

static void offer_remote_files(MgContext* mg, const uint8_t* data, size_t length)
{
    Session* s = mg->session;
    if (length < 4)
    {
        return;
    }

    uint32_t count = get_u32(data);
    if (count > 10000 || length < 4 + (size_t)count * DESCRIPTOR_SIZE)
    {
        return;
    }

    pthread_mutex_lock(&mg->clip_lock);
    free(mg->remote_files);
    mg->remote_files = calloc(count ? count : 1, sizeof(RemoteFile));
    mg->remote_count = (int)count;
    snprintf(mg->remote_token, sizeof(mg->remote_token), "%08x%08x%08x", (unsigned)rand(), (unsigned)rand(), (unsigned)++mg->clip_serial);
    json_object* list = json_object_new_array();
    for (uint32_t i = 0; i < count; i++)
    {
        const uint8_t* entry = data + 4 + (size_t)i * DESCRIPTOR_SIZE;
        RemoteFile* file = &mg->remote_files[i];
        uint32_t attributes = get_u32(entry + 36);
        file->size = ((uint64_t)get_u32(entry + 64) << 32) | get_u32(entry + 68);
        file->folder = (attributes & 0x10) != 0;
        char* name = utf16_to_utf8((const uint16_t*)(entry + 72), 260);
        for (char* p = name; *p; p++)
        {
            if (*p == '\\')
            {
                *p = '/';
            }
        }

        snprintf(file->name, sizeof(file->name), "%s", name);
        free(name);
        json_object* item = json_object_new_object();
        json_object_object_add(item, "name", json_object_new_string(file->name));
        json_object_object_add(item, "size", json_object_new_int64((int64_t)file->size));
        json_object_object_add(item, "folder", json_object_new_boolean(file->folder));
        json_object_array_add(list, item);
    }

    json_object* event = json_object_new_object();
    json_object_object_add(event, "kind", json_object_new_string("files"));
    json_object_object_add(event, "token", json_object_new_string(mg->remote_token));
    json_object_object_add(event, "files", list);
    pthread_mutex_unlock(&mg->clip_lock);
    wire_event(s, "screen.clip", event, NULL, 0);
}

static UINT clip_server_format_data_response(CliprdrClientContext* cliprdr, const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
    MgContext* mg = cliprdr->custom;
    Session* s = mg->session;
    if (!(response->common.msgFlags & CB_RESPONSE_OK) || !response->requestedFormatData)
    {
        return CHANNEL_RC_OK;
    }

    pthread_mutex_lock(&mg->clip_lock);
    UINT32 format = mg->requested_format;
    UINT32 files = mg->file_list_format;
    pthread_mutex_unlock(&mg->clip_lock);
    const uint8_t* data = response->requestedFormatData;
    size_t length = response->common.dataLen;
    if (files && format == files)
    {
        offer_remote_files(mg, data, length);
    }
    else if (format == CF_TEXT_UNICODE)
    {
        char* text = utf16_to_utf8((const uint16_t*)data, length / 2);
        json_object* event = json_object_new_object();
        json_object_object_add(event, "kind", json_object_new_string("text"));
        wire_event(s, "screen.clip", event, (const uint8_t*)text, strlen(text));
        free(text);
    }
    else if (format == CF_BITMAP_DIB)
    {
        size_t bmp_length = 0;
        uint8_t* bmp = dib_to_bmp(data, length, &bmp_length);
        if (bmp)
        {
            json_object* event = json_object_new_object();
            json_object_object_add(event, "kind", json_object_new_string("image"));
            json_object_object_add(event, "format", json_object_new_string("bmp"));
            wire_event(s, "screen.clip", event, bmp, bmp_length);
            free(bmp);
        }
    }

    return CHANNEL_RC_OK;
}

// The remote desktop reads a file this side pasted (Explorer copying it in).
static UINT clip_server_file_contents_request(CliprdrClientContext* cliprdr, const CLIPRDR_FILE_CONTENTS_REQUEST* request)
{
    MgContext* mg = cliprdr->custom;
    CLIPRDR_FILE_CONTENTS_RESPONSE response = { 0 };
    response.common.msgType = CB_FILECONTENTS_RESPONSE;
    response.streamId = request->streamId;
    uint8_t size_answer[8];
    uint8_t* buffer = NULL;
    pthread_mutex_lock(&mg->clip_lock);
    if (mg->local_kind == 3 && request->listIndex < (UINT32)mg->local_count)
    {
        const LocalFile* file = &mg->local_files[request->listIndex];
        if (request->dwFlags & FILECONTENTS_SIZE)
        {
            put_u32(size_answer, (uint32_t)(file->size & 0xFFFFFFFF));
            put_u32(size_answer + 4, (uint32_t)(file->size >> 32));
            response.cbRequested = 8;
            response.requestedData = size_answer;
        }
        else
        {
            uint64_t position = ((uint64_t)request->nPositionHigh << 32) | request->nPositionLow;
            UINT32 wanted = request->cbRequested;
            buffer = malloc(wanted ? wanted : 1);
            int fd = open(file->path, O_RDONLY);
            ssize_t got = fd >= 0 ? pread(fd, buffer, wanted, (off_t)position) : -1;
            if (fd >= 0)
            {
                close(fd);
            }

            if (got >= 0)
            {
                response.cbRequested = (UINT32)got;
                response.requestedData = buffer;
            }
        }
    }

    pthread_mutex_unlock(&mg->clip_lock);
    response.common.msgFlags = response.requestedData ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
    cliprdr->ClientFileContentsResponse(cliprdr, &response);
    free(buffer);
    return CHANNEL_RC_OK;
}

// A piece of a file copied on the remote desktop, for a download in the browser.
static UINT clip_server_file_contents_response(CliprdrClientContext* cliprdr, const CLIPRDR_FILE_CONTENTS_RESPONSE* response)
{
    MgContext* mg = cliprdr->custom;
    Session* s = mg->session;
    Download download = { 0 };
    char name[520] = { 0 };
    uint64_t size = 0;
    pthread_mutex_lock(&mg->clip_lock);
    for (int i = 0; i < MAX_DOWNLOADS; i++)
    {
        if (mg->downloads[i].request && mg->downloads[i].stream == response->streamId)
        {
            download = mg->downloads[i];
            mg->downloads[i].request = 0;
            break;
        }
    }

    if (download.request && download.index < mg->remote_count)
    {
        snprintf(name, sizeof(name), "%s", mg->remote_files[download.index].name);
        size = mg->remote_files[download.index].size;
    }

    pthread_mutex_unlock(&mg->clip_lock);
    if (!download.request)
    {
        return CHANNEL_RC_OK;
    }

    Frame request = { .op = "screen.clip.read", .id = download.request };
    if (!(response->common.msgFlags & CB_RESPONSE_OK))
    {
        wire_fail(s, &request, "This file is no longer on the remote clipboard.");
        return CHANNEL_RC_OK;
    }

    const char* base = strrchr(name, '/');
    json_object* data = json_object_new_object();
    json_object_object_add(data, "size", json_object_new_int64((int64_t)size));
    json_object_object_add(data, "name", json_object_new_string(base ? base + 1 : name));
    wire_answer(s, &request, data, response->requestedData, response->cbRequested);
    return CHANNEL_RC_OK;
}

static UINT clip_lock(CliprdrClientContext* cliprdr, const CLIPRDR_LOCK_CLIPBOARD_DATA* lock)
{
    (void)cliprdr;
    (void)lock;
    return CHANNEL_RC_OK;
}

static UINT clip_unlock(CliprdrClientContext* cliprdr, const CLIPRDR_UNLOCK_CLIPBOARD_DATA* unlock)
{
    (void)cliprdr;
    (void)unlock;
    return CHANNEL_RC_OK;
}

static void free_local_files(MgContext* mg)
{
    for (int i = 0; i < mg->local_count; i++)
    {
        unlink(mg->local_files[i].path);
    }

    free(mg->local_files);
    mg->local_files = NULL;
    mg->local_count = 0;
}

// What the viewer pasted, to the remote clipboard (and Ctrl+V there once the remote side has it).
static void rdp_clip(Session* s, const Frame* frame)
{
    MgContext* mg = s->impl;
    if (!mg->cliprdr)
    {
        session_status(s, "clipfailed", NULL);
        return;
    }

    if (strcmp(frame->op, "screen.clip.set") == 0)
    {
        pthread_mutex_lock(&mg->clip_lock);
        free(mg->local_text);
        mg->local_text = utf8_to_utf16((const char*)frame->payload, frame->payload_len, &mg->local_text_units);
        free_local_files(mg);
        mg->local_kind = 1;
        mg->paste_pending = json_flag(frame->data, "paste", 0);
        send_format_list(mg);
        pthread_mutex_unlock(&mg->clip_lock);
        return;
    }

    const char* id = json_text(frame->data, "id", "");
    Upload* upload = NULL;
    for (int i = 0; i < MAX_UPLOADS; i++)
    {
        if (mg->uploads[i].files && strcmp(mg->uploads[i].id, id) == 0)
        {
            upload = &mg->uploads[i];
        }
    }

    if (strcmp(frame->op, "screen.clip.begin") == 0)
    {
        json_object* files = NULL;
        if (!json_object_object_get_ex(frame->data, "files", &files) || !json_object_is_type(files, json_type_array))
        {
            return;
        }

        int count = (int)json_object_array_length(files);
        Upload* slot = NULL;
        for (int i = 0; i < MAX_UPLOADS && !slot; i++)
        {
            if (!mg->uploads[i].files)
            {
                slot = &mg->uploads[i];
            }
        }

        if (!slot || count <= 0 || count > 500)
        {
            session_status(s, "clipfailed", NULL);
            return;
        }

        char folder[256];
        snprintf(folder, sizeof(folder), "/tmp/matgate-clip-%d-%d", getpid(), ++mg->clip_serial);
        mkdir(folder, 0700);
        snprintf(slot->id, sizeof(slot->id), "%s", id);
        slot->image = strcmp(json_text(frame->data, "kind", "files"), "image") == 0;
        slot->paste = json_flag(frame->data, "paste", 0);
        slot->count = count;
        slot->files = calloc((size_t)count, sizeof(LocalFile));
        for (int i = 0; i < count; i++)
        {
            json_object* item = json_object_array_get_idx(files, (size_t)i);
            const char* name = json_text(item, "name", "Datei");
            const char* slash = strrchr(name, '/');
            const char* backslash = strrchr(name, '\\');
            const char* base = backslash > slash ? backslash + 1 : slash ? slash + 1 : name;
            LocalFile* file = &slot->files[i];
            snprintf(file->name, sizeof(file->name), "%s", *base ? base : "Datei");
            snprintf(file->path, sizeof(file->path), "%s/%d", folder, i);
            file->size = (uint64_t)json_int(item, "size", 0);
            int fd = open(file->path, O_CREAT | O_TRUNC | O_WRONLY, 0600);
            if (fd >= 0)
            {
                close(fd);
            }
        }

        return;
    }

    if (!upload)
    {
        return;
    }

    if (strcmp(frame->op, "screen.clip.chunk") == 0)
    {
        int index = (int)json_int(frame->data, "index", -1);
        if (index >= 0 && index < upload->count)
        {
            int fd = open(upload->files[index].path, O_WRONLY);
            if (fd >= 0)
            {
                ssize_t written = pwrite(fd, frame->payload, frame->payload_len, (off_t)json_int(frame->data, "offset", 0));
                (void)written;
                close(fd);
            }
        }

        return;
    }

    if (strcmp(frame->op, "screen.clip.end") == 0)
    {
        pthread_mutex_lock(&mg->clip_lock);
        free_local_files(mg);
        if (upload->image)
        {
            // A picture arrives as a Windows bitmap already (the page converts it).
            struct stat info;
            free(mg->local_image);
            mg->local_image = NULL;
            mg->local_image_len = 0;
            if (stat(upload->files[0].path, &info) == 0 && info.st_size > 40)
            {
                mg->local_image = malloc((size_t)info.st_size);
                int fd = open(upload->files[0].path, O_RDONLY);
                ssize_t got = fd >= 0 ? read(fd, mg->local_image, (size_t)info.st_size) : -1;
                if (fd >= 0)
                {
                    close(fd);
                }

                mg->local_image_len = got > 0 ? (size_t)got : 0;
            }

            for (int i = 0; i < upload->count; i++)
            {
                unlink(upload->files[i].path);
            }

            free(upload->files);
            mg->local_kind = 2;
        }
        else
        {
            for (int i = 0; i < upload->count; i++)
            {
                struct stat info;
                if (stat(upload->files[i].path, &info) == 0)
                {
                    upload->files[i].size = (uint64_t)info.st_size;
                }
            }

            mg->local_files = upload->files;
            mg->local_count = upload->count;
            mg->local_kind = 3;
        }

        mg->paste_pending = upload->paste;
        upload->files = NULL;
        upload->count = 0;
        upload->id[0] = 0;
        send_format_list(mg);
        pthread_mutex_unlock(&mg->clip_lock);
    }
}

static void rdp_clip_read(Session* s, const Frame* request)
{
    MgContext* mg = s->impl;
    const char* token = json_text(request->data, "token", "");
    int index = (int)json_int(request->data, "index", -1);
    uint64_t offset = (uint64_t)json_int(request->data, "offset", 0);
    pthread_mutex_lock(&mg->clip_lock);
    if (!mg->cliprdr || !token[0] || strcmp(token, mg->remote_token) != 0 || index < 0 || index >= mg->remote_count
        || mg->remote_files[index].folder)
    {
        pthread_mutex_unlock(&mg->clip_lock);
        wire_fail(s, request, "This file is no longer on the remote clipboard.");
        return;
    }

    RemoteFile* file = &mg->remote_files[index];
    if (offset >= file->size)
    {
        json_object* data = json_object_new_object();
        json_object_object_add(data, "size", json_object_new_int64((int64_t)file->size));
        const char* base = strrchr(file->name, '/');
        json_object_object_add(data, "name", json_object_new_string(base ? base + 1 : file->name));
        pthread_mutex_unlock(&mg->clip_lock);
        wire_answer(s, request, data, NULL, 0);
        return;
    }

    Download* slot = NULL;
    for (int i = 0; i < MAX_DOWNLOADS && !slot; i++)
    {
        if (!mg->downloads[i].request)
        {
            slot = &mg->downloads[i];
        }
    }

    if (!slot)
    {
        pthread_mutex_unlock(&mg->clip_lock);
        wire_fail(s, request, "Too many downloads at once.");
        return;
    }

    uint64_t left = file->size - offset;
    slot->stream = ++mg->next_stream;
    slot->request = request->id;
    slot->index = index;
    CLIPRDR_FILE_CONTENTS_REQUEST contents = { 0 };
    contents.common.msgType = CB_FILECONTENTS_REQUEST;
    contents.streamId = slot->stream;
    contents.listIndex = (UINT32)index;
    contents.dwFlags = FILECONTENTS_RANGE;
    contents.nPositionLow = (UINT32)(offset & 0xFFFFFFFF);
    contents.nPositionHigh = (UINT32)(offset >> 32);
    contents.cbRequested = (UINT32)(left < READ_CHUNK ? left : READ_CHUNK);
    pthread_mutex_unlock(&mg->clip_lock);
    mg->cliprdr->ClientFileContentsRequest(mg->cliprdr, &contents);
}

// ---- channels ------------------------------------------------------------------------------------------

static UINT disp_caps(DispClientContext* disp, UINT32 monitors, UINT32 factor_a, UINT32 factor_b)
{
    (void)monitors;
    (void)factor_a;
    (void)factor_b;
    MgContext* mg = disp->custom;
    mg->disp_ready = 1;
    return CHANNEL_RC_OK;
}

static void channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
    MgContext* mg = context;
    if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        CliprdrClientContext* cliprdr = e->pInterface;
        mg->cliprdr = cliprdr;
        cliprdr->custom = mg;
        cliprdr->MonitorReady = clip_monitor_ready;
        cliprdr->ServerCapabilities = clip_server_capabilities;
        cliprdr->ServerFormatList = clip_server_format_list;
        cliprdr->ServerFormatListResponse = clip_server_format_list_response;
        cliprdr->ServerFormatDataRequest = clip_server_format_data_request;
        cliprdr->ServerFormatDataResponse = clip_server_format_data_response;
        cliprdr->ServerFileContentsRequest = clip_server_file_contents_request;
        cliprdr->ServerFileContentsResponse = clip_server_file_contents_response;
        cliprdr->ServerLockClipboardData = clip_lock;
        cliprdr->ServerUnlockClipboardData = clip_unlock;
    }
    else if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        DispClientContext* disp = e->pInterface;
        mg->disp = disp;
        disp->custom = mg;
        disp->DisplayControlCaps = disp_caps;
    }
    else
    {
        freerdp_client_OnChannelConnectedEventHandler(context, e);
    }
}

static void channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
    MgContext* mg = context;
    if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
    {
        mg->cliprdr = NULL;
    }
    else if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
    {
        mg->disp = NULL;
        mg->disp_ready = 0;
    }
    else
    {
        freerdp_client_OnChannelDisconnectedEventHandler(context, e);
    }
}

// ---- connecting ----------------------------------------------------------------------------------------

static BOOL pre_connect(freerdp* instance)
{
    rdpContext* context = instance->context;
    PubSub_SubscribeChannelConnected(context->pubSub, channel_connected);
    PubSub_SubscribeChannelDisconnected(context->pubSub, channel_disconnected);
    return TRUE;
}

static BOOL post_connect(freerdp* instance)
{
    rdpContext* context = instance->context;
    if (!gdi_init(instance, PIXEL_FORMAT_BGRX32))
    {
        return FALSE;
    }

    rdpPointer pointer = { 0 };
    pointer.size = sizeof(MgPointer);
    pointer.New = pointer_new;
    pointer.Free = pointer_free;
    pointer.Set = pointer_set;
    pointer.SetNull = pointer_set_null;
    pointer.SetDefault = pointer_set_default;
    pointer.SetPosition = pointer_set_position;
    graphics_register_pointer(context->graphics, &pointer);
    context->update->BeginPaint = begin_paint;
    context->update->EndPaint = end_paint;
    context->update->DesktopResize = desktop_resize;
    return TRUE;
}

static void post_disconnect(freerdp* instance)
{
    gdi_free(instance);
}

// Credentials the connection did not bring, or that were refused: the viewer is asked.
static BOOL authenticate(freerdp* instance, char** username, char** password, char** domain, rdp_auth_reason reason)
{
    MgContext* mg = mg_of(instance->context);
    Session* s = mg->session;
    if (reason != AUTH_NLA && reason != AUTH_TLS && reason != AUTH_RDP)
    {
        return FALSE;
    }

    if (!mg->auth_failed && *username && **username && *password && **password)
    {
        return TRUE;
    }

    json_object* data = json_object_new_object();
    json_object* fields = json_object_new_array();
    json_object_array_add(fields, json_object_new_string("username"));
    json_object_array_add(fields, json_object_new_string("password"));
    json_object_array_add(fields, json_object_new_string("domain"));
    json_object_object_add(data, "need", fields);
    json_object_object_add(data, "username", json_object_new_string(*username ? *username : ""));
    json_object_object_add(data, "domain", json_object_new_string(*domain ? *domain : ""));
    json_object_object_add(data, "failed", json_object_new_boolean(mg->auth_failed));
    wire_event(s, "screen.auth", data, NULL, 0);
    Frame answer;
    if (!wire_wait_for(s, "screen.auth", &answer, 180000))
    {
        return FALSE;
    }

    BOOL ok = !json_flag(answer.data, "cancel", 0);
    if (ok)
    {
        free(*username);
        free(*password);
        free(*domain);
        *username = strdup(json_text(answer.data, "username", ""));
        *password = strdup(json_text(answer.data, "password", ""));
        const char* given_domain = json_text(answer.data, "domain", "");
        *domain = *given_domain ? strdup(given_domain) : NULL;
    }

    frame_free(&answer);
    mg->auth_failed = 0;
    return ok;
}

static DWORD verify_certificate(freerdp* instance, const char* host, UINT16 port, const char* common_name, const char* subject,
                                const char* issuer, const char* fingerprint, DWORD flags)
{
    (void)host;
    (void)port;
    (void)common_name;
    (void)subject;
    (void)issuer;
    (void)fingerprint;
    (void)flags;
    // "Ignore certificate" on the connection: accepted for this session only, never stored.
    return mg_of(instance->context)->ignore_certificate ? 2 : 0;
}

static DWORD verify_changed_certificate(freerdp* instance, const char* host, UINT16 port, const char* common_name,
                                        const char* subject, const char* issuer, const char* fingerprint,
                                        const char* old_subject, const char* old_issuer, const char* old_fingerprint,
                                        DWORD flags)
{
    (void)old_subject;
    (void)old_issuer;
    (void)old_fingerprint;
    return verify_certificate(instance, host, port, common_name, subject, issuer, fingerprint, flags);
}

static BOOL client_new(freerdp* instance, rdpContext* context)
{
    (void)context;
    instance->PreConnect = pre_connect;
    instance->PostConnect = post_connect;
    instance->PostDisconnect = post_disconnect;
    instance->AuthenticateEx = authenticate;
    instance->VerifyCertificateEx = verify_certificate;
    instance->VerifyChangedCertificateEx = verify_changed_certificate;
    return TRUE;
}

static void client_free(freerdp* instance, rdpContext* context)
{
    (void)instance;
    (void)context;
}

static const char* explain(UINT32 error)
{
    switch (error)
    {
        case FREERDP_ERROR_AUTHENTICATION_FAILED:
        case FREERDP_ERROR_CONNECT_LOGON_FAILURE:
        case FREERDP_ERROR_CONNECT_WRONG_PASSWORD:
            return "The login was refused - user name or password wrong.";
        case FREERDP_ERROR_CONNECT_ACCOUNT_LOCKED_OUT:
            return "The account is locked.";
        case FREERDP_ERROR_CONNECT_ACCOUNT_DISABLED:
            return "The account is disabled.";
        case FREERDP_ERROR_CONNECT_PASSWORD_EXPIRED:
        case FREERDP_ERROR_CONNECT_PASSWORD_MUST_CHANGE:
            return "The password has expired and has to be changed first.";
        case FREERDP_ERROR_DNS_NAME_NOT_FOUND:
            return "The host name could not be resolved.";
        case FREERDP_ERROR_CONNECT_TRANSPORT_FAILED:
        case FREERDP_ERROR_CONNECT_FAILED:
            return "The RDP server could not be reached.";
        case FREERDP_ERROR_TLS_CONNECT_FAILED:
            return "The secure connection to the RDP server failed (certificate or TLS settings).";
        case FREERDP_ERROR_SECURITY_NEGO_CONNECT_FAILED:
            return "The RDP server and Matgate found no common way to secure the connection.";
        case FREERDP_ERROR_CONNECT_CANCELLED:
            return "The login was cancelled.";
        default:
            return NULL;
    }
}

static int is_login_failure(UINT32 error)
{
    return error == FREERDP_ERROR_AUTHENTICATION_FAILED || error == FREERDP_ERROR_CONNECT_LOGON_FAILURE
        || error == FREERDP_ERROR_CONNECT_WRONG_PASSWORD;
}

static int rdp_open(Session* s, json_object* params, char* error, size_t error_len)
{
    current_session = s;
    RDP_CLIENT_ENTRY_POINTS entry = { 0 };
    entry.Version = RDP_CLIENT_INTERFACE_VERSION;
    entry.Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
    entry.ContextSize = sizeof(MgContext);
    entry.ClientNew = client_new;
    entry.ClientFree = client_free;
    rdpContext* context = freerdp_client_context_new(&entry);
    if (!context)
    {
        snprintf(error, error_len, "The RDP engine could not be started.");
        return -1;
    }

    // The sound device of this process (see sound_entry); everything else as FreeRDP has it built in.
    freerdp_register_addin_provider(addin_provider, 0);
    MgContext* mg = mg_of(context);
    mg->session = s;
    pthread_mutexattr_t recursive;
    pthread_mutexattr_init(&recursive);
    pthread_mutexattr_settype(&recursive, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(&mg->frame_lock, &recursive);
    pthread_mutexattr_destroy(&recursive);
    pthread_mutex_init(&mg->clip_lock, NULL);
    srand((unsigned)time(NULL) ^ (unsigned)getpid());
    s->impl = mg;
    mg->ignore_certificate = json_flag(params, "ignoreCertificate", 1);

    int width = 0;
    int height = 0;
    session_wanted_size(s, &width, &height);
    if (width <= 0)
    {
        width = 1280;
        height = 720;
    }

    s->native_width = width;
    s->native_height = height;
    rdpSettings* settings = context->settings;
    freerdp_settings_set_string(settings, FreeRDP_ServerHostname, json_text(params, "host", ""));
    freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, (UINT32)json_int(params, "port", 3389));
    const char* username = json_text(params, "username", "");
    const char* password = json_text(params, "password", "");
    const char* domain = json_text(params, "domain", "");
    if (*username)
    {
        freerdp_settings_set_string(settings, FreeRDP_Username, username);
    }

    if (*password)
    {
        freerdp_settings_set_string(settings, FreeRDP_Password, password);
    }

    if (*domain)
    {
        freerdp_settings_set_string(settings, FreeRDP_Domain, domain);
    }

    freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, (UINT32)width);
    freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight, (UINT32)height);
    freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32);
    // A server that is busy starting the session may take its time to activate it.
    freerdp_settings_set_uint32(settings, FreeRDP_TcpAckTimeout, 30000);
    freerdp_settings_set_bool(settings, FreeRDP_IgnoreCertificate, mg->ignore_certificate ? TRUE : FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxH264, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, FALSE);
    freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RemoteFxCodec, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_AllowFontSmoothing, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableWallpaper, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableFullWindowDrag, TRUE);
    freerdp_settings_set_bool(settings, FreeRDP_DisableMenuAnims, TRUE);
    freerdp_settings_set_string(settings, FreeRDP_ClientHostname, "Matgate");
    UINT32 layout = (UINT32)json_int(params, "keyboardLayout", 0);
    if (layout)
    {
        freerdp_settings_set_uint32(settings, FreeRDP_KeyboardLayout, layout);
    }

    // Sound through the device of this process; FreeRDP would otherwise look for PulseAudio.
    freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, TRUE);
    const char* sound[] = { "rdpsnd", "sys:matgate" };
    freerdp_client_add_static_channel(settings, 2, sound);

    // The redirected drive "Files", as Matgate prepares it for the session.
    const char* drive = json_text(params, "drivePath", "");
    if (*drive)
    {
        mkdir(drive, 0755);
        const char* device[] = { "drive", "Files", drive };
        freerdp_settings_set_bool(settings, FreeRDP_DeviceRedirection, TRUE);
        freerdp_client_add_device_channel(settings, 3, device);
    }

    freerdp* instance = context->instance;
    for (int attempt = 0; attempt < 3; attempt++)
    {
        if (freerdp_connect(instance))
        {
            return 0;
        }

        UINT32 code = freerdp_get_last_error(context);
        if (!is_login_failure(code) || !s->running)
        {
            const char* sentence = explain(code);
            snprintf(error, error_len, "%s", sentence ? sentence : freerdp_get_last_error_string(code));
            return -1;
        }

        // Wrong password: asked again, with a word that the last one did not work.
        mg->auth_failed = 1;
        freerdp_settings_set_string(settings, FreeRDP_Password, NULL);
    }

    snprintf(error, error_len, "The login was refused - user name or password wrong.");
    return -1;
}

static void send_layout(MgContext* mg)
{
    DISPLAY_CONTROL_MONITOR_LAYOUT layout = { 0 };
    layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
    layout.Width = (UINT32)mg->want_width;
    layout.Height = (UINT32)mg->want_height;
    layout.PhysicalWidth = (UINT32)(mg->want_width * 254 / 960);
    layout.PhysicalHeight = (UINT32)(mg->want_height * 254 / 960);
    layout.Orientation = ORIENTATION_LANDSCAPE;
    layout.DesktopScaleFactor = 100;
    layout.DeviceScaleFactor = 100;
    mg->disp->SendMonitorLayout(mg->disp, 1, &layout);
    mg->last_layout = now_ms();
    mg->asked_width = mg->want_width;
    mg->asked_height = mg->want_height;
    mg->reactivating = 0;
}

static void rdp_run(Session* s)
{
    MgContext* mg = s->impl;
    rdpContext* context = &mg->common.context;
    HANDLE matgate = CreateFileDescriptorEvent(NULL, FALSE, FALSE, s->fd, WINPR_FD_READ);
    while (s->running && !freerdp_shall_disconnect_context(context))
    {
        HANDLE handles[MAXIMUM_WAIT_OBJECTS];
        DWORD count = freerdp_get_event_handles(context, handles, MAXIMUM_WAIT_OBJECTS - 1);
        if (count == 0)
        {
            break;
        }

        handles[count++] = matgate;
        int timeout = video_tick(s);
        if (timeout < 1)
        {
            timeout = 1;
        }
        else if (timeout > 100)
        {
            timeout = 100;
        }

        if (WaitForMultipleObjects(count, handles, FALSE, (DWORD)timeout) == WAIT_FAILED)
        {
            break;
        }

        if (!freerdp_check_event_handles(context))
        {
            break;
        }

        if (WaitForSingleObject(matgate, 0) == WAIT_OBJECT_0 && session_pump(s) < 0)
        {
            break;
        }

        // The window changed size: the remote desktop follows. One request at a time: the server answers a
        // resize by taking the session down and up again (deactivate/reactivate), and a second request in
        // the middle of that leaves the session hanging. So the next one waits until the last has arrived.
        // A server may also drop a request (xrdp does while the login is still running): when it has not
        // even begun to take the session down after three seconds, the request is made again.
        rdpGdi* gdi = context->gdi;
        int active = freerdp_is_active_state(context);
        if (!active)
        {
            mg->reactivating = 1;
        }

        if (mg->disp && mg->disp_ready && mg->want_width > 0 && gdi
            && ((int)gdi->width != mg->want_width || (int)gdi->height != mg->want_height))
        {
            int64_t now = now_ms();
            int in_flight = mg->asked_width > 0 && ((int)gdi->width != mg->asked_width || (int)gdi->height != mg->asked_height)
                && now - mg->last_layout < (mg->reactivating ? 30000 : 3000);
            if (!in_flight && active && now - mg->last_layout > 400 && now - mg->resized_at > 600)
            {
                send_layout(mg);
            }
        }
    }

    if (s->running && freerdp_shall_disconnect_context(context))
    {
        const char* sentence = explain(freerdp_get_last_error(context));
        session_status(s, "error", sentence ? sentence : "The RDP session has ended.");
    }

    CloseHandle(matgate);
}

static int rdp_frame(Session* s, const uint8_t** pixels, int* stride, int* width, int* height)
{
    MgContext* mg = s->impl;
    if (!mg)
    {
        return 0;
    }

    pthread_mutex_lock(&mg->frame_lock);
    rdpGdi* gdi = mg->common.context.gdi;
    if (!gdi || !gdi->primary_buffer)
    {
        pthread_mutex_unlock(&mg->frame_lock);
        return 0;
    }

    *pixels = gdi->primary_buffer;
    *stride = (int)gdi->stride;
    *width = (int)gdi->width;
    *height = (int)gdi->height;
    return 1;
}

static void rdp_frame_done(Session* s)
{
    MgContext* mg = s->impl;
    pthread_mutex_unlock(&mg->frame_lock);
}

static void key(Session* s, int code, int extended, int down)
{
    MgContext* mg = s->impl;
    uint16_t scan = (uint16_t)MAKE_RDP_SCANCODE(code, extended);
    if (down)
    {
        int known = 0;
        for (int i = 0; i < s->keys_count; i++)
        {
            known |= s->keys_down[i] == scan;
        }

        if (!known && s->keys_count < 64)
        {
            s->keys_down[s->keys_count++] = scan;
        }
    }
    else
    {
        for (int i = 0; i < s->keys_count; i++)
        {
            if (s->keys_down[i] == scan)
            {
                s->keys_down[i] = s->keys_down[--s->keys_count];
                break;
            }
        }
    }

    freerdp_input_send_keyboard_event_ex(mg->common.context.input, down ? TRUE : FALSE, FALSE, scan);
}

static void wheel(MgContext* mg, int value, UINT16 axis)
{
    rdpInput* input = mg->common.context.input;
    for (int i = 0; value != 0 && i < 20; i++)
    {
        int step = value > 255 ? 255 : value < -255 ? -255 : value;
        UINT16 flags = axis;
        if (step < 0)
        {
            flags |= PTR_FLAGS_WHEEL_NEGATIVE | (UINT16)((0x100 + step) & 0xFF);
        }
        else
        {
            flags |= (UINT16)(step & 0xFF);
        }

        freerdp_input_send_mouse_event(input, flags, mg->x, mg->y);
        value -= step;
    }
}

static void rdp_input(Session* s, json_object* data)
{
    MgContext* mg = s->impl;
    rdpInput* input = mg->common.context.input;
    const char* type = json_text(data, "t", "");
    switch (type[0])
    {
        case 'm':
            mg->x = (UINT16)json_int(data, "x", 0);
            mg->y = (UINT16)json_int(data, "y", 0);
            freerdp_input_send_mouse_event(input, PTR_FLAGS_MOVE, mg->x, mg->y);
            break;
        case 'b':
        {
            int which = (int)json_int(data, "b", 0);
            int down = json_flag(data, "d", 0);
            if (which == 3 || which == 4)
            {
                UINT16 flags = (which == 3 ? PTR_XFLAGS_BUTTON1 : PTR_XFLAGS_BUTTON2) | (down ? PTR_XFLAGS_DOWN : 0);
                freerdp_input_send_extended_mouse_event(input, flags, mg->x, mg->y);
            }
            else
            {
                UINT16 button = which == 0 ? PTR_FLAGS_BUTTON1 : which == 1 ? PTR_FLAGS_BUTTON3 : PTR_FLAGS_BUTTON2;
                freerdp_input_send_mouse_event(input, (UINT16)(button | (down ? PTR_FLAGS_DOWN : 0)), mg->x, mg->y);
            }

            if (down)
            {
                s->buttons_down |= 1 << which;
            }
            else
            {
                s->buttons_down &= ~(1 << which);
            }

            break;
        }
        case 'w':
            wheel(mg, (int)json_int(data, "dy", 0), PTR_FLAGS_WHEEL);
            wheel(mg, (int)json_int(data, "dx", 0), PTR_FLAGS_HWHEEL);
            break;
        case 'k':
            key(s, (int)json_int(data, "s", 0), json_flag(data, "e", 0), json_flag(data, "d", 0));
            break;
        case 'u':
        {
            const char* text = json_text(data, "text", "");
            size_t units = 0;
            uint16_t* utf16 = utf8_to_utf16(text, strlen(text), &units);
            for (size_t i = 0; i < units; i++)
            {
                freerdp_input_send_unicode_keyboard_event(input, 0, utf16[i]);
                freerdp_input_send_unicode_keyboard_event(input, KBD_FLAGS_RELEASE, utf16[i]);
            }

            free(utf16);
            break;
        }
        case 'r':
            while (s->keys_count > 0)
            {
                uint16_t scan = s->keys_down[--s->keys_count];
                freerdp_input_send_keyboard_event_ex(input, FALSE, FALSE, scan);
            }

            for (int which = 0; which < 5; which++)
            {
                if (s->buttons_down & (1 << which))
                {
                    if (which >= 3)
                    {
                        freerdp_input_send_extended_mouse_event(input, which == 3 ? PTR_XFLAGS_BUTTON1 : PTR_XFLAGS_BUTTON2, mg->x, mg->y);
                    }
                    else
                    {
                        freerdp_input_send_mouse_event(input, which == 0 ? PTR_FLAGS_BUTTON1 : which == 1 ? PTR_FLAGS_BUTTON3 : PTR_FLAGS_BUTTON2, mg->x, mg->y);
                    }
                }
            }

            s->buttons_down = 0;
            break;
    }
}

static void rdp_resize(Session* s, int width, int height)
{
    MgContext* mg = s->impl;
    mg->want_width = width;
    mg->want_height = height;
}

// Ctrl+Alt+Del inside the session: the session's own secure attention (lock, task manager, sign out).
static void rdp_secure_attention(Session* s)
{
    key(s, 0x1D, 0, 1);
    key(s, 0x38, 0, 1);
    key(s, 0x53, 1, 1);
    key(s, 0x53, 1, 0);
    key(s, 0x38, 0, 0);
    key(s, 0x1D, 0, 0);
}

static void rdp_close(Session* s)
{
    MgContext* mg = s->impl;
    if (!mg)
    {
        return;
    }

    rdpContext* context = &mg->common.context;
    freerdp_disconnect(context->instance);
    pthread_mutex_lock(&mg->clip_lock);
    free_local_files(mg);
    free(mg->local_text);
    free(mg->local_image);
    free(mg->remote_files);
    pthread_mutex_unlock(&mg->clip_lock);
    s->impl = NULL;
    current_session = NULL;
    freerdp_client_context_free(context);
}

const Backend rdp_backend = {
    .name = "RDP",
    .open = rdp_open,
    .run = rdp_run,
    .frame = rdp_frame,
    .frame_done = rdp_frame_done,
    .input = rdp_input,
    .resize = rdp_resize,
    .clip = rdp_clip,
    .clip_read = rdp_clip_read,
    .secure_attention = rdp_secure_attention,
    .close = rdp_close,
};
