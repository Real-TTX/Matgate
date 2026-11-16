/*
 * Makes the test vectors of tests/unit/Graphics.Tests with FreeRDP's own codecs.
 *
 * Every vector is: the bytes a server would send, the parameters of the update, and the picture FreeRDP's
 * decoder makes of them. The C# tests decode the same bytes and compare byte for byte.
 *
 * To make the files again, with FreeRDP 3.15 as Debian trixie has it (from the root of the repository, in a
 * POSIX shell; Git Bash on Windows needs MSYS_NO_PATHCONV=1 in front and the path of the folder spelled out):
 *
 *   docker run --rm -v "$PWD/tests/unit/Graphics.Tests:/t" debian:trixie-slim sh -c '
 *     apt-get update -qq &&
 *     apt-get install -y -qq --no-install-recommends build-essential pkg-config freerdp3-dev zlib1g-dev libssl-dev brotli &&
 *     gcc -O2 -Wall -o /tmp/vectors /t/harness/vectors.c $(pkg-config --cflags --libs freerdp3 winpr3) &&
 *     mkdir /tmp/out && /tmp/vectors /tmp/out /t/harness/assets &&
 *     brotli -f -q 11 --lgwin=24 -o /t/TestData/bitmaps.bin.br /tmp/out/bitmaps.bin &&
 *     brotli -f -q 11 --lgwin=24 -o /t/TestData/pointers.bin.br /tmp/out/pointers.bin'
 *
 * The program is deterministic: the same FreeRDP gives the same files. A third argument "soft" keeps FreeRDP
 * from using its SSE code; both ways give the same files.
 *
 * Where the bytes come from:
 *   - FreeRDP's encoders: Interleaved RLE at 15, 16 and 24 bits, RDP 6.0 planar with and without RLE and alpha;
 *   - generators in this file for what FreeRDP cannot encode: RLE at 8 bits and RLE streams that use every order
 *     code in every form; planar streams in the AYCoCg colour space with every colour loss level and with
 *     subsampled chroma; planar streams that use every control byte; the pictures FreeRDP's planar encoder
 *     gives up on; uncompressed bitmaps; pointer masks;
 *   - six planar streams of the IronRDP project (harness/assets, see the notice there).
 * Whatever made the bytes, the expected picture always comes out of FreeRDP's decoder.
 *
 * Three things are this program's and not FreeRDP's:
 *   - 5-6-5 pixels are expanded to eight bits a channel by bit replication. FreeRDP expands green as
 *     (g << 2) + (g >> 3), which is brighter by up to 4. For 16-bit vectors FreeRDP decodes into 5-6-5 and this
 *     program expands; FreeRDP's own expansion is stored next to it as the "reference" picture, and the tests
 *     pin the difference down to exactly the two formulas.
 *   - The X byte of every expected pixel is 0xFF.
 *   - FreeRDP's client turns a red/blue switch on in its planar decoder (see planar_decode). Here it is on for
 *     AYCoCg streams only; main prints what it does to each format.
 *
 * The vector files: "MGFX", the version 1 and the number of vectors as 32-bit numbers, then the vectors as
 * file_add writes them. All numbers little-endian.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include <winpr/crt.h>
#include <winpr/stream.h>
#include <winpr/wlog.h>

#include <freerdp/codec/color.h>
#include <freerdp/codec/bitmap.h>
#include <freerdp/codec/interleaved.h>
#include <freerdp/codec/planar.h>
#include <freerdp/primitives.h>

#define KIND_BITMAP 1
#define KIND_POINTER 2

#define FLAG_COMPRESSED 1
#define FLAG_PALETTE 4
#define FLAG_REFERENCE 8

static void fail(const char* what, const char* name)
{
	fprintf(stderr, "FAILED: %s (%s)\n", what, name ? name : "");
	exit(1);
}

/* ---------------------------------------------------------------------------------------------------------
 * A growing byte buffer and the vector file
 * ------------------------------------------------------------------------------------------------------- */

typedef struct
{
	BYTE* data;
	size_t length;
	size_t capacity;
} Buffer;

static void buffer_reserve(Buffer* b, size_t more)
{
	if (b->length + more <= b->capacity)
		return;

	size_t capacity = b->capacity ? b->capacity * 2 : 4096;
	while (capacity < b->length + more)
		capacity *= 2;

	b->data = realloc(b->data, capacity);
	if (!b->data)
		fail("out of memory", NULL);

	b->capacity = capacity;
}

static void put_bytes(Buffer* b, const void* data, size_t length)
{
	buffer_reserve(b, length);
	if (length > 0)
		memcpy(b->data + b->length, data, length);
	b->length += length;
}

static void put8(Buffer* b, unsigned value)
{
	BYTE byte = (BYTE)value;
	put_bytes(b, &byte, 1);
}

static void put16(Buffer* b, unsigned value)
{
	put8(b, value & 0xFF);
	put8(b, (value >> 8) & 0xFF);
}

static void put32(Buffer* b, uint32_t value)
{
	put16(b, value & 0xFFFF);
	put16(b, (value >> 16) & 0xFFFF);
}

/* A pixel as Interleaved RLE writes it: 1, 2 or 3 bytes, little-endian. */
static void put_pixel(Buffer* b, uint32_t pixel, int bytesPerPixel)
{
	for (int i = 0; i < bytesPerPixel; i++)
		put8(b, (pixel >> (8 * i)) & 0xFF);
}

typedef struct
{
	Buffer bytes;
	uint32_t count;
	const char* path;
} VectorFile;

static void file_begin(VectorFile* f, const char* path)
{
	memset(f, 0, sizeof(*f));
	f->path = path;
	put_bytes(&f->bytes, "MGFX", 4);
	put32(&f->bytes, 1);
	put32(&f->bytes, 0);
}

static void file_end(VectorFile* f)
{
	memcpy(f->bytes.data + 8, &f->count, 4);
	FILE* out = fopen(f->path, "wb");
	if (!out || fwrite(f->bytes.data, 1, f->bytes.length, out) != f->bytes.length)
		fail("cannot write", f->path);

	fclose(out);
	printf("%-28s %5u vectors %9zu bytes\n", f->path, f->count, f->bytes.length);
	free(f->bytes.data);
}

/* palette: 256 entries 0xFFRRGGBB or NULL. reference: a second picture or NULL. */
static void file_add(VectorFile* f, const char* name, int kind, int bitsPerPixel, int flags, int width, int height,
                     int contentWidth, int contentHeight, const BYTE* data, size_t dataLength, const BYTE* mask,
                     size_t maskLength, const uint32_t* palette, const BYTE* expected, size_t expectedLength,
                     const BYTE* reference, size_t referenceLength)
{
	Buffer* b = &f->bytes;
	if (palette)
		flags |= FLAG_PALETTE;
	if (reference)
		flags |= FLAG_REFERENCE;

	put16(b, (unsigned)strlen(name));
	put_bytes(b, name, strlen(name));
	put8(b, kind);
	put8(b, bitsPerPixel);
	put8(b, flags);
	put8(b, 0);
	put16(b, width);
	put16(b, height);
	put16(b, contentWidth);
	put16(b, contentHeight);
	put32(b, (uint32_t)dataLength);
	put_bytes(b, data, dataLength);
	put32(b, (uint32_t)maskLength);
	put_bytes(b, mask, maskLength);
	if (palette)
	{
		for (int i = 0; i < 256; i++)
			put32(b, palette[i]);
	}

	put32(b, (uint32_t)expectedLength);
	put_bytes(b, expected, expectedLength);
	put32(b, (uint32_t)(reference ? referenceLength : 0));
	if (reference)
		put_bytes(b, reference, referenceLength);

	f->count++;
}

/* ---------------------------------------------------------------------------------------------------------
 * Random numbers: the same on every machine
 * ------------------------------------------------------------------------------------------------------- */

typedef struct
{
	uint32_t state;
} Rng;

static uint32_t rng_next(Rng* r)
{
	uint32_t x = r->state;
	x ^= x << 13;
	x ^= x >> 17;
	x ^= x << 5;
	r->state = x;
	return x;
}

static uint32_t rng_below(Rng* r, uint32_t limit)
{
	return limit ? rng_next(r) % limit : 0;
}

/* lowest..highest, inclusive */
static int rng_range(Rng* r, int lowest, int highest)
{
	return lowest + (int)rng_below(r, (uint32_t)(highest - lowest + 1));
}

static void rng_seed(Rng* r, uint32_t seed)
{
	r->state = seed * 2654435761u + 0x9E3779B9u;
	if (r->state == 0)
		r->state = 1;

	for (int i = 0; i < 8; i++)
		rng_next(r);
}

/* ---------------------------------------------------------------------------------------------------------
 * Pictures: BGRX, top-down, 4 bytes a pixel, X = 0xFF
 * ------------------------------------------------------------------------------------------------------- */

static void set_pixel(BYTE* image, int width, int x, int y, uint32_t rgb)
{
	BYTE* p = image + ((size_t)y * width + x) * 4;
	p[0] = rgb & 0xFF;
	p[1] = (rgb >> 8) & 0xFF;
	p[2] = (rgb >> 16) & 0xFF;
	p[3] = 0xFF;
}

static uint32_t get_pixel(const BYTE* image, int width, int x, int y)
{
	const BYTE* p = image + ((size_t)y * width + x) * 4;
	return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16);
}

static const char* const image_names[] = { "flat", "gradient", "text", "noise", "lines", "speckle", "dither", "mix" };
#define IMAGE_COUNT 8
#define IMAGE_FLAT 0
#define IMAGE_GRADIENT 1
#define IMAGE_TEXT 2
#define IMAGE_NOISE 3
#define IMAGE_LINES 4
#define IMAGE_SPECKLE 5
#define IMAGE_DITHER 6
#define IMAGE_MIX 7

static BYTE* make_image(int kind, int width, int height, uint32_t seed)
{
	BYTE* image = calloc((size_t)width * height, 4);
	Rng rng;
	rng_seed(&rng, seed + (uint32_t)kind * 977u + (uint32_t)width * 31u + (uint32_t)height);

	switch (kind)
	{
		case IMAGE_FLAT:
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					set_pixel(image, width, x, y, 0x3A6EA5);
			break;

		case IMAGE_GRADIENT:
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					uint32_t r = width > 1 ? (uint32_t)x * 255 / (uint32_t)(width - 1) : 128;
					uint32_t g = height > 1 ? (uint32_t)y * 255 / (uint32_t)(height - 1) : 64;
					uint32_t b = 255 - ((r + g) / 2);
					set_pixel(image, width, x, y, (r << 16) | (g << 8) | b);
				}
			}
			break;

		case IMAGE_TEXT:
		{
			/* Black glyphs on white, a few words in blue, one selected line: high contrast, sharp edges. */
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					set_pixel(image, width, x, y, 0xFFFFFF);

			for (int top = 1; top < height; top += 10)
			{
				int selected = rng_below(&rng, 5) == 0;
				if (selected)
				{
					for (int y = top - 1; y < top + 8 && y < height; y++)
						for (int x = 0; x < width; x++)
							set_pixel(image, width, x, y, 0x3399FF);
				}

				for (int left = 2; left < width;)
				{
					int letters = rng_range(&rng, 1, 9);
					uint32_t color = selected ? 0xFFFFFF : (rng_below(&rng, 6) == 0 ? 0x0000EE : 0x000000);
					for (int letter = 0; letter < letters && left < width; letter++, left += 6)
					{
						uint32_t glyph = rng_next(&rng);
						for (int gy = 0; gy < 7; gy++)
						{
							for (int gx = 0; gx < 5; gx++)
							{
								int on = (glyph >> ((gy * 5 + gx) % 31)) & 1;
								if (on && left + gx < width && top + gy < height)
									set_pixel(image, width, left + gx, top + gy, color);
							}
						}
					}

					left += 4;
				}
			}
			break;
		}

		case IMAGE_NOISE:
		{
			/* Every colour there is - except in the one large size, where 64 random colours keep the files
			 * small: noise does not compress, and the smaller sizes have all the values. */
			uint32_t few[64];
			for (int i = 0; i < 64; i++)
				few[i] = rng_next(&rng) & 0xFFFFFF;

			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					set_pixel(image, width, x, y, width * height > 8000 ? few[rng_below(&rng, 64)] : rng_next(&rng) & 0xFFFFFF);
			break;
		}

		case IMAGE_LINES:
		{
			static const uint32_t colors[] = { 0xFF0000, 0x00FF00, 0x0000FF, 0x000000, 0xFFFF00, 0xFF00FF, 0x808080 };
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
					set_pixel(image, width, x, y, 0xF0F0F0);

			for (int y = 0; y < height; y += 7)
				for (int x = 0; x < width; x++)
					set_pixel(image, width, x, y, colors[(y / 7) % 7]);

			for (int x = 0; x < width; x += 11)
				for (int y = 0; y < height; y++)
					set_pixel(image, width, x, y, colors[(x / 11 + 3) % 7]);

			for (int i = 0; i < width && i < height; i++)
				set_pixel(image, width, i, i, 0x202020);

			for (int i = 0; i < width && i < height; i++)
				set_pixel(image, width, width - 1 - i, i, 0xC00000);
			break;
		}

		case IMAGE_SPECKLE:
		{
			/* Few colours in short runs: every kind of run and many short orders. */
			static const uint32_t colors[] = { 0x000000, 0xFFFFFF, 0x3A6EA5, 0xD4D0C8, 0x800000 };
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width;)
				{
					uint32_t color = colors[rng_below(&rng, 5)];
					int run = rng_range(&rng, 1, 12);
					for (; run > 0 && x < width; run--, x++)
						set_pixel(image, width, x, y, color);
				}
			}
			break;
		}

		case IMAGE_DITHER:
			/* Two colours alternating, with the phase and the pair changing in blocks. */
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int block = (x / 24) + (y / 5);
					uint32_t a = (block % 3) == 0 ? 0xFFFFFF : 0x808080;
					uint32_t b = (block % 2) == 0 ? 0x000000 : 0x0000AA;
					int phase = (block % 5) == 0 ? (x + y) & 1 : x & 1;
					set_pixel(image, width, x, y, phase ? a : b);
				}
			}
			break;

		case IMAGE_MIX:
		{
			/* Rows that are the row below them, in stretches XORed with one colour: what the foreground
			 * orders are made for. The data goes bottom-up, so "the scan line before" is the row below. */
			static const uint32_t colors[] = { 0xFFFFFF, 0xFFFFFF, 0x00FF00, 0x123456 };
			for (int x = 0; x < width;)
			{
				uint32_t color = rng_next(&rng) & 0xFFFFFF;
				int run = rng_range(&rng, 1, 20);
				for (; run > 0 && x < width; run--, x++)
					set_pixel(image, width, x, height - 1, color);
			}

			for (int y = height - 2; y >= 0; y--)
			{
				uint32_t color = colors[rng_below(&rng, 4)];
				for (int x = 0; x < width;)
				{
					int mode = (int)rng_below(&rng, 4);
					int run = rng_range(&rng, 1, 40);
					for (; run > 0 && x < width; run--, x++)
					{
						uint32_t below = get_pixel(image, width, x, y + 1);
						uint32_t pixel = below;
						if (mode == 1)
							pixel = below ^ color;
						else if (mode == 2)
							pixel = (rng_next(&rng) & 1) ? below ^ color : below;
						else if (mode == 3 && (x & 7) == 0)
							pixel = rng_next(&rng) & 0xFFFFFF;

						set_pixel(image, width, x, y, pixel);
					}
				}
			}
			break;
		}
	}

	return image;
}

typedef struct
{
	int width;
	int height;
} Size;

static const Size sizes[] = { { 1, 1 }, { 4, 1 }, { 1, 7 }, { 2, 3 }, { 5, 4 }, { 17, 9 }, { 64, 64 }, { 123, 45 }, { 1920, 8 } };
#define SIZE_COUNT 9

/* ---------------------------------------------------------------------------------------------------------
 * Colour conversions of this program
 * ------------------------------------------------------------------------------------------------------- */

static uint32_t expand565(unsigned pixel)
{
	uint32_t r = (pixel >> 11) & 0x1F;
	uint32_t g = (pixel >> 5) & 0x3F;
	uint32_t b = pixel & 0x1F;
	return (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
}

/* A 5-6-5 picture (2 bytes a pixel, little-endian) to BGRX with bit replication. */
static BYTE* rgb16_to_bgrx(const BYTE* source, int width, int height)
{
	BYTE* out = malloc((size_t)width * height * 4);
	for (size_t i = 0; i < (size_t)width * height; i++)
	{
		uint32_t rgb = expand565((unsigned)(source[i * 2] | (source[i * 2 + 1] << 8)));
		out[i * 4] = rgb & 0xFF;
		out[i * 4 + 1] = (rgb >> 8) & 0xFF;
		out[i * 4 + 2] = (rgb >> 16) & 0xFF;
		out[i * 4 + 3] = 0xFF;
	}

	return out;
}

static void force_opaque(BYTE* bgrx, int width, int height)
{
	for (size_t i = 0; i < (size_t)width * height; i++)
		bgrx[i * 4 + 3] = 0xFF;
}

/* How FreeRDP and this program differ on a 16-bit picture: red and blue must be the same, green may differ. */
static unsigned long green_only_differences(const BYTE* a, const BYTE* b, int width, int height, const char* name)
{
	unsigned long count = 0;
	for (size_t i = 0; i < (size_t)width * height; i++)
	{
		if (a[i * 4] != b[i * 4] || a[i * 4 + 2] != b[i * 4 + 2] || a[i * 4 + 3] != b[i * 4 + 3])
			fail("16-bit pictures differ in more than green", name);

		/* a is this program's: the six bits and their two highest again. FreeRDP adds the three highest. */
		unsigned green = a[i * 4 + 1] >> 2;
		unsigned freerdp = (green << 2) + (green >> 3);
		if (b[i * 4 + 1] != (freerdp > 255 ? 255 : freerdp))
			fail("16-bit pictures differ in green by more than the two ways of expanding it", name);

		if (a[i * 4 + 1] != b[i * 4 + 1])
			count++;
	}

	return count;
}

static gdiPalette freerdp_palette;
static uint32_t test_palette[256];

static void make_palette(uint32_t seed)
{
	Rng rng;
	rng_seed(&rng, seed);
	freerdp_palette.format = PIXEL_FORMAT_BGRX32;
	for (int i = 0; i < 256; i++)
	{
		uint32_t rgb = rng_next(&rng) & 0xFFFFFF;
		if (i == 0)
			rgb = 0x000000;
		if (i == 255)
			rgb = 0xFFFFFF;
		if (i == 7)
			rgb = 0xFFFFFF; /* a second white and a second black: pointer masks look at the colour, not the index */
		if (i == 9)
			rgb = 0x000000;

		test_palette[i] = 0xFF000000u | rgb;
		freerdp_palette.palette[i] =
		    FreeRDPGetColor(PIXEL_FORMAT_BGRX32, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 0xFF);
	}
}

/* ---------------------------------------------------------------------------------------------------------
 * Interleaved RLE
 * ------------------------------------------------------------------------------------------------------- */

static BITMAP_INTERLEAVED_CONTEXT* interleaved;
static unsigned long green_differences;

/* Decodes with FreeRDP and adds the vector. width/height: the bitmap in the stream. */
static void add_rle_vector(VectorFile* f, const char* name, int bitsPerPixel, int width, int height, int contentWidth,
                           const BYTE* data, size_t length)
{
	BYTE* picture = calloc((size_t)width * height, 4);
	const gdiPalette* palette = bitsPerPixel == 8 ? &freerdp_palette : NULL;
	if (!interleaved_decompress(interleaved, data, (UINT32)length, (UINT32)width, (UINT32)height, (UINT32)bitsPerPixel,
	                            picture, PIXEL_FORMAT_BGRX32, (UINT32)width * 4, 0, 0, (UINT32)width, (UINT32)height,
	                            palette))
		fail("FreeRDP cannot decode the RLE stream", name);

	force_opaque(picture, width, height);
	if (bitsPerPixel == 16)
	{
		BYTE* native = calloc((size_t)width * height, 2);
		if (!interleaved_decompress(interleaved, data, (UINT32)length, (UINT32)width, (UINT32)height, 16, native,
		                            PIXEL_FORMAT_RGB16, (UINT32)width * 2, 0, 0, (UINT32)width, (UINT32)height, NULL))
			fail("FreeRDP cannot decode the RLE stream to 5-6-5", name);

		BYTE* expanded = rgb16_to_bgrx(native, width, height);
		green_differences += green_only_differences(expanded, picture, width, height, name);
		file_add(f, name, KIND_BITMAP, 16, FLAG_COMPRESSED, width, height, contentWidth, height, data, length, NULL, 0,
		         NULL, expanded, (size_t)width * height * 4, picture, (size_t)width * height * 4);
		free(expanded);
		free(native);
	}
	else
	{
		file_add(f, name, KIND_BITMAP, bitsPerPixel, FLAG_COMPRESSED, width, height, contentWidth, height, data,
		         length, NULL, 0, bitsPerPixel == 8 ? test_palette : NULL, picture, (size_t)width * height * 4, NULL,
		         0);
	}

	free(picture);
}

/* Compresses a picture with FreeRDP's encoder, in as many bitmaps as the encoder makes of it. */
static void add_rle_encoded(VectorFile* f, const char* imageName, const BYTE* image, int width, int height,
                            int bitsPerPixel)
{
	int padding = (4 - (width % 4)) % 4;
	int padded = width + padding;
	size_t pixels = (size_t)width * height;
	BYTE* source = NULL;

	if (bitsPerPixel == 24)
	{
		/* The 24-bit encoder reads 32-bit pixels and compares them whole: the fourth byte must be zero for
		 * black to be black. */
		source = malloc(pixels * 4);
		memcpy(source, image, pixels * 4);
		for (size_t i = 0; i < pixels; i++)
			source[i * 4 + 3] = 0;
	}
	else
	{
		source = malloc(pixels * 2);
		if (!freerdp_image_copy_no_overlap(source, bitsPerPixel == 16 ? PIXEL_FORMAT_RGB16 : PIXEL_FORMAT_RGB15,
		                                   (UINT32)width * 2, 0, 0, (UINT32)width, (UINT32)height, image,
		                                   PIXEL_FORMAT_BGRX32, (UINT32)width * 4, 0, 0, NULL, FREERDP_FLIP_NONE))
			fail("cannot convert the picture", imageName);
	}

	int startLine = height - 1;
	int part = 0;
	while (startLine >= 0)
	{
		size_t capacity = (size_t)padded * (size_t)height * 4 + 65536;
		wStream* s = Stream_New(NULL, capacity);
		wStream* temp = Stream_New(NULL, capacity);
		SSIZE_T lines = freerdp_bitmap_compress(source, (UINT32)width, (UINT32)height, s, (UINT32)bitsPerPixel,
		                                        0x7FFFFFFF, (UINT32)startLine, temp, (UINT32)padding);
		if (lines <= 0)
			fail("FreeRDP cannot encode the picture", imageName);

		char name[160];
		if (lines == height)
			snprintf(name, sizeof(name), "rle%d/encoder/%s/%dx%d", bitsPerPixel, imageName, width, height);
		else
			snprintf(name, sizeof(name), "rle%d/encoder/%s/%dx%d/part%d", bitsPerPixel, imageName, width, height,
			         part);

		add_rle_vector(f, name, bitsPerPixel, padded, (int)lines, width, Stream_Buffer(s), Stream_GetPosition(s));
		Stream_Free(s, TRUE);
		Stream_Free(temp, TRUE);
		startLine -= (int)lines;
		part++;
	}

	free(source);
}

static unsigned long order_histogram[4][256];

/* A random stream of orders that fills width x height pixels exactly. Every order code and every form of run
 * length gets used; the pixels are whatever comes out. */
static void make_rle_stream(Rng* rng, int width, int height, int bitsPerPixel, Buffer* out, unsigned long* histogram)
{
	int bytesPerPixel = bitsPerPixel == 8 ? 1 : bitsPerPixel == 24 ? 3 : 2;
	uint32_t pixelMask = bitsPerPixel == 8 ? 0xFF : bitsPerPixel == 24 ? 0xFFFFFF : 0xFFFF;
	long remaining = (long)width * height;
	int afterBackground = 0;

	/* A few colours that repeat, so that XOR and copy orders produce more than noise. */
	uint32_t colors[6];
	for (int i = 0; i < 6; i++)
		colors[i] = rng_next(rng) & pixelMask;

	while (remaining > 0)
	{
		int kind = (int)rng_below(rng, 29);
		long count = 0;
		int header = -1;
		int extended = -1; /* the byte of a MEGA run */
		int megaMega = 0;
		int family = 0; /* 0 background, 1 foreground, 2 set foreground, 3 dithered, 4 colour run, 5 fgbg,
		                   6 set fgbg, 7 colour image, 8 special, 9 white/black */
		switch (kind)
		{
			case 0: /* regular background run */
				count = rng_range(rng, 1, 31);
				header = (0x0 << 5) | (int)count;
				family = 0;
				break;
			case 1:
				count = rng_range(rng, 32, 287);
				header = 0x00;
				extended = (int)count - 32;
				family = 0;
				break;
			case 2:
				count = rng_range(rng, afterBackground ? 1 : 0, 700);
				header = 0xF0;
				megaMega = 1;
				family = 0;
				break;
			case 3: /* regular foreground run */
				count = rng_range(rng, 1, 31);
				header = (0x1 << 5) | (int)count;
				family = 1;
				break;
			case 4:
				count = rng_range(rng, 32, 287);
				header = 0x20;
				extended = (int)count - 32;
				family = 1;
				break;
			case 5:
				count = rng_range(rng, 0, 700);
				header = 0xF1;
				megaMega = 1;
				family = 1;
				break;
			case 6: /* lite set foreground run */
				count = rng_range(rng, 1, 15);
				header = (0xC << 4) | (int)count;
				family = 2;
				break;
			case 7:
				count = rng_range(rng, 16, 271);
				header = 0xC0;
				extended = (int)count - 16;
				family = 2;
				break;
			case 8:
				count = rng_range(rng, 0, 700);
				header = 0xF6;
				megaMega = 1;
				family = 2;
				break;
			case 9: /* lite dithered run: the count is in pairs */
				count = rng_range(rng, 1, 15);
				header = (0xE << 4) | (int)count;
				family = 3;
				break;
			case 10:
				count = rng_range(rng, 16, 271);
				header = 0xE0;
				extended = (int)count - 16;
				family = 3;
				break;
			case 11:
				count = rng_range(rng, 0, 300);
				header = 0xF8;
				megaMega = 1;
				family = 3;
				break;
			case 12: /* regular colour run */
				count = rng_range(rng, 1, 31);
				header = (0x3 << 5) | (int)count;
				family = 4;
				break;
			case 13:
				count = rng_range(rng, 32, 287);
				header = 0x60;
				extended = (int)count - 32;
				family = 4;
				break;
			case 14:
				count = rng_range(rng, 0, 700);
				header = 0xF3;
				megaMega = 1;
				family = 4;
				break;
			case 15: /* regular fgbg image: the header counts in eights */
				count = 8L * rng_range(rng, 1, 31);
				header = (0x2 << 5) | (int)(count / 8);
				family = 5;
				break;
			case 16:
				count = rng_range(rng, 1, 256);
				header = 0x40;
				extended = (int)count - 1;
				family = 5;
				break;
			case 17:
				count = rng_range(rng, 0, 700);
				header = 0xF2;
				megaMega = 1;
				family = 5;
				break;
			case 18: /* lite set fgbg image */
				count = 8L * rng_range(rng, 1, 15);
				header = (0xD << 4) | (int)(count / 8);
				family = 6;
				break;
			case 19:
				count = rng_range(rng, 1, 256);
				header = 0xD0;
				extended = (int)count - 1;
				family = 6;
				break;
			case 20:
				count = rng_range(rng, 0, 700);
				header = 0xF7;
				megaMega = 1;
				family = 6;
				break;
			case 21: /* regular colour image */
				count = rng_range(rng, 1, 31);
				header = (0x4 << 5) | (int)count;
				family = 7;
				break;
			case 22:
				count = rng_range(rng, 32, 287);
				header = 0x80;
				extended = (int)count - 32;
				family = 7;
				break;
			case 23:
				count = rng_range(rng, 0, 400);
				header = 0xF4;
				megaMega = 1;
				family = 7;
				break;
			case 24:
				count = 8;
				header = 0xF9;
				family = 8;
				break;
			case 25:
				count = 8;
				header = 0xFA;
				family = 8;
				break;
			case 26:
				count = 1;
				header = 0xFD;
				family = 9;
				break;
			case 27:
				count = 1;
				header = 0xFE;
				family = 9;
				break;
			default:
				/* More of the short orders, so that small bitmaps get a mixture too. */
				count = rng_range(rng, 1, 4);
				header = (0x0 << 5) | (int)count;
				family = 0;
				break;
		}

		long pixelCount = family == 3 ? count * 2 : count;
		if (pixelCount > remaining)
			continue;

		put8(out, (unsigned)header);
		histogram[header]++;
		if (extended >= 0)
			put8(out, (unsigned)extended);
		if (megaMega)
			put16(out, (unsigned)count);

		switch (family)
		{
			case 2:
			case 6:
				put_pixel(out, rng_below(rng, 3) ? colors[rng_below(rng, 6)] : rng_next(rng) & pixelMask, bytesPerPixel);
				break;
			case 3:
				put_pixel(out, colors[rng_below(rng, 6)], bytesPerPixel);
				put_pixel(out, rng_next(rng) & pixelMask, bytesPerPixel);
				break;
			case 4:
				put_pixel(out, rng_below(rng, 2) ? colors[rng_below(rng, 6)] : rng_next(rng) & pixelMask, bytesPerPixel);
				break;
			default:
				break;
		}

		if (family == 5 || family == 6)
		{
			for (long i = 0; i < (count + 7) / 8; i++)
				put8(out, rng_next(rng));
		}

		if (family == 7)
		{
			for (long i = 0; i < count; i++)
				put_pixel(out, rng_below(rng, 4) ? colors[rng_below(rng, 6)] : rng_next(rng) & pixelMask, bytesPerPixel);
		}

		afterBackground = family == 0;
		remaining -= pixelCount;
	}
}

static void add_rle_order_vectors(VectorFile* f)
{
	static const int allDepths[] = { 8, 15, 16, 24 };
	static const Size streamSizes[] = { { 1, 1 }, { 3, 1 }, { 1, 5 }, { 5, 4 }, { 8, 8 }, { 17, 9 },
		                                { 64, 64 }, { 123, 45 }, { 300, 3 }, { 1000, 2 } };
	for (int depth = 0; depth < 4; depth++)
	{
		for (int size = 0; size < 10; size++)
		{
			/* fewer of the two large sizes: their pictures are what makes the file big */
			for (int number = 0; number < (size == 6 || size == 7 ? 6 : 12); number++)
			{
				Rng rng;
				rng_seed(&rng, (uint32_t)(1000 + depth * 100000 + size * 1000 + number));
				Buffer stream = { 0 };
				make_rle_stream(&rng, streamSizes[size].width, streamSizes[size].height, allDepths[depth], &stream,
				                order_histogram[depth]);
				char name[160];
				snprintf(name, sizeof(name), "rle%d/orders/%dx%d/%d", allDepths[depth], streamSizes[size].width,
				         streamSizes[size].height, number);
				add_rle_vector(f, name, allDepths[depth], streamSizes[size].width, streamSizes[size].height,
				               streamSizes[size].width, stream.data, stream.length);
				free(stream.data);
			}
		}
	}
}

/* ---------------------------------------------------------------------------------------------------------
 * RDP 6.0 planar
 * ------------------------------------------------------------------------------------------------------- */

/* Decodes a planar stream the way FreeRDP's client does for a bitmap update and returns the picture; NULL when
 * FreeRDP rejects the stream. */
static BYTE* planar_try_decode(const BYTE* data, size_t length, int width, int height, BOOL swap)
{
	BITMAP_PLANAR_CONTEXT* decoder = freerdp_bitmap_planar_context_new(0, (UINT32)width, (UINT32)height);
	if (!decoder)
		fail("no planar decoder", NULL);

	freerdp_planar_switch_bgr(decoder, swap);
	BYTE* picture = calloc((size_t)width * height, 4);
	if (!planar_decompress(decoder, data, (UINT32)length, (UINT32)width, (UINT32)height, picture, PIXEL_FORMAT_BGRX32,
	                       (UINT32)width * 4, 0, 0, (UINT32)width, (UINT32)height, TRUE))
	{
		free(picture);
		picture = NULL;
	}
	else
	{
		force_opaque(picture, width, height);
	}

	freerdp_bitmap_planar_context_free(decoder);
	return picture;
}

static BYTE* planar_decode(const BYTE* data, size_t length, int width, int height, const char* name)
{
	/* FreeRDP's client swaps red and blue for streams with an alpha plane when it has announced colour
	 * fidelity (MS-RDPEGDI 3.1.9.1.2: the servers get the AYCoCg transform wrong for bitmaps without alpha).
	 * The switch is on only for AYCoCg streams here: for raw RGB planes with alpha it would swap the colours
	 * although no transform is involved, which FreeRDP's own RLE path does not do either (see main). */
	BYTE* picture = planar_try_decode(data, length, width, height, (data[0] & PLANAR_FORMAT_HEADER_CLL_MASK) != 0);
	if (!picture)
		fail("FreeRDP cannot decode the planar stream", name);

	return picture;
}

static void add_planar_vector(VectorFile* f, const char* name, int width, int height, const BYTE* data, size_t length,
                              const BYTE* reference)
{
	BYTE* picture = planar_decode(data, length, width, height, name);
	file_add(f, name, KIND_BITMAP, 32, FLAG_COMPRESSED, width, height, width, height, data, length, NULL, 0, NULL,
	         picture, (size_t)width * height * 4, reference, (size_t)width * height * 4);
	free(picture);
}

/* A copy of the picture with an alpha channel that is not constant. */
static BYTE* with_alpha(const BYTE* image, int width, int height)
{
	BYTE* copy = malloc((size_t)width * height * 4);
	memcpy(copy, image, (size_t)width * height * 4);
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
			copy[((size_t)y * width + x) * 4 + 3] = (BYTE)(x < width / 2 ? 0xFF : (x * 7 + y * 13) & 0xFF);
	}

	return copy;
}

static unsigned long planar_encoder_failures;

/* Compresses a picture with FreeRDP's planar encoder. FALSE when what the encoder wrote is not the picture:
 * the RLE encoder of FreeRDP 3.15 writes every negative delta wrongly (it tests an unsigned byte for being
 * below zero; later versions have that fixed), and it has room for four bytes a pixel in all, so that planes
 * RLE cannot shrink end in the middle. A stream that FreeRDP's decoder takes is a vector all the same - of
 * the picture the decoder makes of it, whatever that is. */
static BOOL add_planar_encoded(VectorFile* f, const char* imageName, const BYTE* image, int width, int height,
                               DWORD flags)
{
	BOOL alpha = (flags & PLANAR_FORMAT_HEADER_NA) == 0;
	BOOL usable = FALSE;
	BOOL decodable = FALSE;
	BYTE* source = alpha ? with_alpha(image, width, height) : NULL;
	BITMAP_PLANAR_CONTEXT* encoder = freerdp_bitmap_planar_context_new(flags, (UINT32)width, (UINT32)height);
	UINT32 length = 0;
	BYTE* data = freerdp_bitmap_compress_planar(encoder, alpha ? source : image,
	                                            alpha ? PIXEL_FORMAT_BGRA32 : PIXEL_FORMAT_BGRX32, (UINT32)width,
	                                            (UINT32)height, (UINT32)width * 4, NULL, &length);
	char name[160];
	snprintf(name, sizeof(name), "planar/encoder/%s%s/%s/%dx%d", (flags & PLANAR_FORMAT_HEADER_RLE) ? "rle" : "raw",
	         alpha ? "+alpha" : "", imageName, width, height);
	if (data)
	{
		/* Lossless: the picture must be the one that went in. */
		BYTE* picture = planar_try_decode(data, length, width, height, FALSE);
		if (picture)
		{
			decodable = TRUE;
			usable = TRUE;
			for (size_t i = 0; i < (size_t)width * height; i++)
			{
				if (memcmp(picture + i * 4, image + i * 4, 3) != 0)
					usable = FALSE;
			}

			free(picture);
		}
	}

	if (decodable)
		add_planar_vector(f, name, width, height, data, length, NULL);

	if (!usable)
	{
		planar_encoder_failures++;
		if (getenv("VECTORS_LOG"))
			printf("FreeRDP's planar encoder: %s %s\n", name, decodable ? "a stream of another picture" : "no stream that can be decoded");
	}

	free(data);
	free(source);
	freerdp_bitmap_planar_context_free(encoder);
	return usable;
}

/* One scan line of a plane as RDP 6.0 RLE segments (MS-RDPEGDI 3.1.9.2). style 0 writes it the compact way;
 * other styles break runs and raw stretches up differently, which a decoder must not mind. */
static void planar_rle_line(Buffer* out, const BYTE* line, int width, Rng* rng, int style)
{
	int x = 0;
	int last = 0; /* what a run repeats: the last raw value of the scan line, zero before the first */
	while (x < width)
	{
		/* The raw stretch [x, rawEnd): up to where at least three values repeat the one before them. A run
		 * at the very start of the scan line repeats zero. */
		int rawEnd = x;
		int runLength = 0;
		while (rawEnd < width)
		{
			int previous = rawEnd > x ? line[rawEnd - 1] : last;
			int run = 0;
			while (rawEnd + run < width && line[rawEnd + run] == previous)
				run++;

			if (run >= 3)
			{
				runLength = run;
				break;
			}

			rawEnd++;
		}

		/* A shorter run is as good: what is left of it is found again, as a run or as raw values. */
		if (style != 0 && runLength > 3 && rng_below(rng, 3) == 0)
			runLength = rng_range(rng, 3, runLength);

		int rawCount = rawEnd - x;
		while (rawCount > 15)
		{
			int part = style == 0 ? 15 : rng_range(rng, 1, 15);
			put8(out, (unsigned)(part << 4));
			put_bytes(out, line + x, (size_t)part);
			x += part;
			rawCount -= part;
		}

		if (rawCount > 0)
			last = line[x + rawCount - 1];

		/* The first segment takes the raw values and a run of up to 15. A rest of one or two could not be
		 * written as a run (those lengths mean something else), so the run is cut two short where needed. */
		int first = runLength > 15 ? 15 : runLength;
		if (style != 0 && first >= 6 && rng_below(rng, 2) == 0)
			first = rng_range(rng, 3, first - 3);

		if (runLength - first == 1 || runLength - first == 2)
			first -= 2;

		if (rawCount > 0 || first > 0)
		{
			put8(out, (unsigned)((rawCount << 4) | first));
			put_bytes(out, line + x, (size_t)rawCount);
			x += rawCount + first;
			runLength -= first;
		}

		/* The rest of the run in segments without raw values. */
		while (runLength > 0)
		{
			int part;
			if (runLength < 3)
				fail("planar_rle_line left a run of one or two", NULL);

			if (runLength >= 16 && (style == 0 || rng_below(rng, 3) != 0))
			{
				part = runLength > 47 ? 47 : runLength;
				if (runLength - part == 1 || runLength - part == 2)
					part -= 2;

				put8(out, part >= 32 ? (unsigned)(((part - 32) << 4) | 2) : (unsigned)(((part - 16) << 4) | 1));
			}
			else
			{
				part = runLength > 15 ? 15 : runLength;
				if (runLength - part == 1 || runLength - part == 2)
					part -= 2;

				put8(out, (unsigned)part);
			}

			x += part;
			runLength -= part;
		}
	}
}

/* A plane (first scan line first) as RLE: the first line as it is, the others as deltas to the line before. */
static void planar_rle_plane(Buffer* out, const BYTE* plane, int width, int height, Rng* rng, int style)
{
	BYTE* line = malloc((size_t)width);
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width; x++)
		{
			int value = plane[(size_t)y * width + x];
			if (y > 0)
			{
				int delta = (int)(int8_t)(BYTE)(value - plane[(size_t)(y - 1) * width + x]);
				value = delta >= 0 ? delta << 1 : ((-delta) << 1) - 1;
			}

			line[x] = (BYTE)value;
		}

		planar_rle_line(out, line, width, rng, style);
	}

	free(line);
}

/* Encodes a BGRA picture as an RDP 6.0 planar stream in the AYCoCg colour space (colorLossLevel 1..7) or as
 * plain planes (colorLossLevel 0), the way the specification describes the server side. */
static void planar_encode(Buffer* out, const BYTE* image, int width, int height, int colorLossLevel, BOOL subsample,
                          BOOL rle, BOOL alpha, Rng* rng, int style)
{
	int chromaWidth = subsample ? (width + 1) / 2 : width;
	int chromaHeight = subsample ? (height + 1) / 2 : height;
	size_t planeSize = (size_t)width * height;
	size_t chromaSize = (size_t)chromaWidth * chromaHeight;
	BYTE* alphaPlane = malloc(planeSize);
	BYTE* first = malloc(planeSize);
	int* orange = malloc(planeSize * sizeof(int));
	int* green = malloc(planeSize * sizeof(int));
	BYTE* second = malloc(chromaSize);
	BYTE* third = malloc(chromaSize);

	/* Scan lines go out bottom-up. */
	for (int line = 0; line < height; line++)
	{
		for (int x = 0; x < width; x++)
		{
			const BYTE* p = image + ((size_t)(height - 1 - line) * width + x) * 4;
			int b = p[0];
			int g = p[1];
			int r = p[2];
			size_t i = (size_t)line * width + x;
			alphaPlane[i] = p[3];
			if (colorLossLevel == 0)
			{
				first[i] = (BYTE)r;
				orange[i] = g;
				green[i] = b;
			}
			else
			{
				/* Windows servers take Co the wrong way round for bitmaps without alpha (MS-RDPEGDI
				 * 3.1.9.1.2), and clients are told to swap red and blue for those. Doing the same here
				 * keeps the decoded pictures looking like the ones that went in. */
				first[i] = (BYTE)((r + 2 * g + b) >> 2);
				orange[i] = alpha ? r - b : b - r;
				green[i] = (2 * g - r - b) / 2;
			}
		}
	}

	for (int line = 0; line < chromaHeight; line++)
	{
		for (int x = 0; x < chromaWidth; x++)
		{
			int co;
			int cg;
			if (subsample)
			{
				/* the average of up to four neighbours */
				int sumCo = 0;
				int sumCg = 0;
				int n = 0;
				for (int dy = 0; dy < 2; dy++)
				{
					for (int dx = 0; dx < 2; dx++)
					{
						int sx = x * 2 + dx;
						int sy = line * 2 + dy;
						if (sx < width && sy < height)
						{
							sumCo += orange[(size_t)sy * width + sx];
							sumCg += green[(size_t)sy * width + sx];
							n++;
						}
					}
				}

				co = sumCo / n;
				cg = sumCg / n;
			}
			else
			{
				co = orange[(size_t)line * width + x];
				cg = green[(size_t)line * width + x];
			}

			size_t i = (size_t)line * chromaWidth + x;
			if (colorLossLevel == 0)
			{
				second[i] = (BYTE)co;
				third[i] = (BYTE)cg;
			}
			else
			{
				/* nine bits in two's complement, the low bits dropped */
				second[i] = (BYTE)(((unsigned)co & 0x1FF) >> colorLossLevel);
				third[i] = (BYTE)(((unsigned)cg & 0x1FF) >> colorLossLevel);
			}
		}
	}

	put8(out, (unsigned)(colorLossLevel | (subsample ? PLANAR_FORMAT_HEADER_CS : 0) | (rle ? PLANAR_FORMAT_HEADER_RLE : 0) |
	                     (alpha ? 0 : PLANAR_FORMAT_HEADER_NA)));
	if (rle)
	{
		if (alpha)
			planar_rle_plane(out, alphaPlane, width, height, rng, style);

		planar_rle_plane(out, first, width, height, rng, style);
		planar_rle_plane(out, second, chromaWidth, chromaHeight, rng, style);
		planar_rle_plane(out, third, chromaWidth, chromaHeight, rng, style);
	}
	else
	{
		if (alpha)
			put_bytes(out, alphaPlane, planeSize);

		put_bytes(out, first, planeSize);
		put_bytes(out, second, chromaSize);
		put_bytes(out, third, chromaSize);
		put8(out, 0);
	}

	free(alphaPlane);
	free(first);
	free(orange);
	free(green);
	free(second);
	free(third);
}

static unsigned long planar_worst_error;

static void add_planar_generated(VectorFile* f, const char* group, const char* imageName, const BYTE* image, int width,
                                 int height, int colorLossLevel, BOOL subsample, BOOL rle, BOOL alpha, int style)
{
	Rng rng;
	rng_seed(&rng, (uint32_t)(width * 7919 + height * 104729 + colorLossLevel * 13 + style));
	BYTE* source = alpha ? with_alpha(image, width, height) : NULL;
	Buffer stream = { 0 };
	planar_encode(&stream, alpha ? source : image, width, height, colorLossLevel, subsample, rle, alpha, &rng, style);

	char name[160];
	snprintf(name, sizeof(name), "planar/%s/cll%d%s%s%s%s/%s/%dx%d", group, colorLossLevel, subsample ? "+cs" : "",
	         rle ? "+rle" : "", alpha ? "+alpha" : "", style ? "+split" : "", imageName, width, height);
	add_planar_vector(f, name, width, height, stream.data, stream.length, NULL);

	/* Not lossless, but close: a picture that is far off means this encoder and FreeRDP's decoder disagree
	 * about the format, and then the vector would prove nothing. */
	if (!subsample)
	{
		BYTE* picture = planar_decode(stream.data, stream.length, width, height, name);
		unsigned long tolerance = colorLossLevel == 0 ? 0 : (3ul << colorLossLevel) + 4;
		for (size_t i = 0; i < (size_t)width * height * 4; i++)
		{
			if ((i & 3) == 3)
				continue;

			unsigned long error = (unsigned long)abs((int)picture[i] - (int)image[i]);
			if (error > planar_worst_error)
				planar_worst_error = error;
			if (error > tolerance)
				fail("the generated planar stream does not decode to the picture that went in", name);
		}

		free(picture);
	}

	free(stream.data);
	free(source);
}

static unsigned long segment_histogram[256];

/* A plane of random, valid segments: every control byte that can stand in a stream gets used. */
static void make_planar_plane(Rng* rng, int width, int height, Buffer* out)
{
	for (int y = 0; y < height; y++)
	{
		for (int x = 0; x < width;)
		{
			int room = width - x;
			int rawCount = 0;
			int runField = 0;
			int runLength = 0;
			switch (rng_below(rng, 5))
			{
				case 0: /* raw values only */
					rawCount = rng_range(rng, 1, 15);
					break;
				case 1: /* raw values and a run */
					rawCount = rng_range(rng, 1, 15);
					runField = runLength = rng_range(rng, 3, 15);
					break;
				case 2: /* a run only */
					runField = runLength = rng_range(rng, 3, 15);
					break;
				case 3: /* a long run: 16 to 31 */
					runLength = rng_range(rng, 16, 31);
					runField = 1;
					break;
				default: /* a long run: 32 to 47 */
					runLength = rng_range(rng, 32, 47);
					runField = 2;
					break;
			}

			if (rawCount + runLength > room)
			{
				/* what fits: raw values, or a run where three fit */
				rawCount = room < 3 || rng_below(rng, 2) ? (room > 15 ? 15 : room) : 0;
				runField = runLength = rawCount ? 0 : (room > 15 ? 15 : room);
			}

			unsigned control = runField == 1   ? (unsigned)(((runLength - 16) << 4) | 1)
			                   : runField == 2 ? (unsigned)(((runLength - 32) << 4) | 2)
			                                   : (unsigned)((rawCount << 4) | runLength);
			put8(out, control);
			segment_histogram[control]++;
			for (int i = 0; i < rawCount; i++)
			{
				/* mostly small deltas of either sign, sometimes anything */
				put8(out, rng_below(rng, 4) ? rng_below(rng, 8) : rng_next(rng));
			}

			x += rawCount + runLength;
		}
	}
}

static void add_planar_segments(VectorFile* f, int width, int height, int number)
{
	Rng rng;
	rng_seed(&rng, (uint32_t)(500000 + width * 1000 + height * 10 + number));
	int colorLossLevel = number % 2 ? 0 : rng_range(&rng, 1, 7);
	BOOL subsample = colorLossLevel > 0 && rng_below(&rng, 2);
	BOOL alpha = rng_below(&rng, 2);
	int chromaWidth = subsample ? (width + 1) / 2 : width;
	int chromaHeight = subsample ? (height + 1) / 2 : height;

	Buffer stream = { 0 };
	put8(&stream, (unsigned)(colorLossLevel | (subsample ? PLANAR_FORMAT_HEADER_CS : 0) | PLANAR_FORMAT_HEADER_RLE |
	                         (alpha ? 0 : PLANAR_FORMAT_HEADER_NA)));
	if (alpha)
		make_planar_plane(&rng, width, height, &stream);

	make_planar_plane(&rng, width, height, &stream);
	make_planar_plane(&rng, chromaWidth, chromaHeight, &stream);
	make_planar_plane(&rng, chromaWidth, chromaHeight, &stream);

	char name[160];
	snprintf(name, sizeof(name), "planar/segments/cll%d%s%s/%dx%d/%d", colorLossLevel, subsample ? "+cs" : "",
	         alpha ? "+alpha" : "", width, height, number);
	add_planar_vector(f, name, width, height, stream.data, stream.length, NULL);
	free(stream.data);
}

static BYTE* read_file(const char* path, size_t* length)
{
	FILE* in = fopen(path, "rb");
	if (!in)
		return NULL;

	fseek(in, 0, SEEK_END);
	long size = ftell(in);
	fseek(in, 0, SEEK_SET);
	BYTE* data = malloc((size_t)size + 1);
	if (fread(data, 1, (size_t)size, in) != (size_t)size)
		fail("cannot read", path);

	fclose(in);
	*length = (size_t)size;
	return data;
}

/* Streams from the IronRDP project with the pictures its tests expect of them (24-bit .bmp files, the rows in
 * the order of the stream). */
static void add_planar_assets(VectorFile* f, const char* directory)
{
	static const struct
	{
		const char* file;
		int width;
		int height;
	} assets[] = { { "32x64_rgb_raw", 32, 64 },      { "64x24_argb_rle", 64, 24 },     { "64x64_aycocg_rle", 64, 64 },
		           { "64x64_ycocg_rle_ss", 64, 64 }, { "64x35_ycocg_rle_ss", 64, 35 }, { "64x64_ycocg_raw_ss", 64, 64 } };

	for (int i = 0; i < 6; i++)
	{
		char path[512];
		size_t length = 0;
		size_t bmpLength = 0;
		snprintf(path, sizeof(path), "%s/%s.bin", directory, assets[i].file);
		BYTE* data = read_file(path, &length);
		snprintf(path, sizeof(path), "%s/%s.bmp", directory, assets[i].file);
		BYTE* bmp = read_file(path, &bmpLength);
		if (!data || !bmp)
		{
			printf("asset %s not found in %s: left out\n", assets[i].file, directory);
			free(data);
			free(bmp);
			continue;
		}

		int width = assets[i].width;
		int height = assets[i].height;
		uint32_t offset = (uint32_t)bmp[10] | ((uint32_t)bmp[11] << 8) | ((uint32_t)bmp[12] << 16) | ((uint32_t)bmp[13] << 24);
		int32_t bmpWidth = (int32_t)((uint32_t)bmp[18] | ((uint32_t)bmp[19] << 8) | ((uint32_t)bmp[20] << 16) | ((uint32_t)bmp[21] << 24));
		int32_t bmpHeight = (int32_t)((uint32_t)bmp[22] | ((uint32_t)bmp[23] << 8) | ((uint32_t)bmp[24] << 16) | ((uint32_t)bmp[25] << 24));
		int bits = bmp[28] | (bmp[29] << 8);
		if (bmpWidth != width || abs(bmpHeight) != height || bits != 24)
			fail("unexpected .bmp", assets[i].file);

		/* The .bmp shows the scan lines in the order of the stream, first line on top; the bitmap update has
		 * them bottom-up, so the picture on the screen is the .bmp upside down. */
		size_t stride = ((size_t)width * 3 + 3) & ~(size_t)3;
		BYTE* reference = malloc((size_t)width * height * 4);
		for (int y = 0; y < height; y++)
		{
			int fileRow = bmpHeight > 0 ? height - 1 - y : y;
			const BYTE* row = bmp + offset + (size_t)fileRow * stride;
			BYTE* out = reference + (size_t)(height - 1 - y) * width * 4;
			for (int x = 0; x < width; x++)
			{
				out[x * 4] = row[x * 3];
				out[x * 4 + 1] = row[x * 3 + 1];
				out[x * 4 + 2] = row[x * 3 + 2];
				out[x * 4 + 3] = 0xFF;
			}
		}

		char name[160];
		snprintf(name, sizeof(name), "planar/ironrdp/%s", assets[i].file);
		BYTE* picture = planar_decode(data, length, width, height, name);
		int same = memcmp(picture, reference, (size_t)width * height * 4) == 0;
		printf("asset %-22s header 0x%02X: FreeRDP's picture %s the one IronRDP expects\n", assets[i].file, data[0],
		       same ? "is" : "IS NOT");
		add_planar_vector(f, name, width, height, data, length, reference);
		free(picture);
		free(reference);
		free(data);
		free(bmp);
	}
}

static void add_planar_format_vectors(VectorFile* f, const char* assets)
{
	/* Every combination of the format header on a few pictures. */
	static const int kinds[] = { IMAGE_GRADIENT, IMAGE_TEXT, IMAGE_NOISE };
	static const Size formatSizes[] = { { 1, 1 }, { 2, 3 }, { 5, 4 }, { 17, 9 }, { 33, 31 }, { 64, 64 } };
	for (int kind = 0; kind < 3; kind++)
	{
		for (int size = 0; size < 6; size++)
		{
			int width = formatSizes[size].width;
			int height = formatSizes[size].height;

			/* The larger sizes not with every picture: sixty lossy versions of noise do not compress. */
			if ((size == 5 && kinds[kind] != IMAGE_GRADIENT) || (size == 4 && kinds[kind] == IMAGE_NOISE))
				continue;

			BYTE* image = make_image(kinds[kind], width, height, 2);
			for (int colorLossLevel = 0; colorLossLevel <= 7; colorLossLevel++)
			{
				for (int combination = 0; combination < 8; combination++)
				{
					BOOL subsample = (combination & 1) != 0;
					BOOL rle = (combination & 2) != 0;
					BOOL alpha = (combination & 4) != 0;
					if (subsample && colorLossLevel == 0)
						continue;

					add_planar_generated(f, "formats", image_names[kinds[kind]], image, width, height, colorLossLevel, subsample,
					                     rle, alpha, 0);
					if (rle && size >= 3)
						add_planar_generated(f, "formats", image_names[kinds[kind]], image, width, height, colorLossLevel,
						                     subsample, rle, alpha, 1);
				}
			}

			free(image);
		}
	}

	static const Size segmentSizes[] = { { 1, 1 }, { 2, 2 }, { 3, 5 }, { 16, 4 }, { 47, 3 }, { 48, 2 }, { 100, 7 }, { 333, 5 } };
	for (int size = 0; size < 8; size++)
	{
		for (int number = 0; number < 16; number++)
			add_planar_segments(f, segmentSizes[size].width, segmentSizes[size].height, number);
	}

	add_planar_assets(f, assets);
}

/* ---------------------------------------------------------------------------------------------------------
 * Uncompressed bitmaps
 * ------------------------------------------------------------------------------------------------------- */

static void add_raw_vector(VectorFile* f, const char* imageName, const BYTE* image, int width, int height,
                           int bitsPerPixel, Rng* rng)
{
	/* As servers send them: the width padded to a multiple of four, rows bottom-up. */
	int padded = (width + 3) & ~3;
	int bytesPerPixel = bitsPerPixel == 8 ? 1 : bitsPerPixel == 24 ? 3 : bitsPerPixel == 32 ? 4 : 2;
	size_t length = (size_t)padded * height * (size_t)bytesPerPixel;
	BYTE* data = malloc(length);
	UINT32 format = bitsPerPixel == 8    ? PIXEL_FORMAT_RGB8
	                : bitsPerPixel == 15 ? PIXEL_FORMAT_RGB15
	                : bitsPerPixel == 16 ? PIXEL_FORMAT_RGB16
	                : bitsPerPixel == 24 ? PIXEL_FORMAT_BGR24
	                                     : PIXEL_FORMAT_BGRA32;

	for (int line = 0; line < height; line++)
	{
		for (int x = 0; x < padded; x++)
		{
			uint32_t rgb = x < width ? get_pixel(image, width, x, height - 1 - line) : rng_next(rng) & 0xFFFFFF;
			uint32_t r = (rgb >> 16) & 0xFF;
			uint32_t g = (rgb >> 8) & 0xFF;
			uint32_t b = rgb & 0xFF;
			BYTE* p = data + ((size_t)line * padded + x) * (size_t)bytesPerPixel;
			switch (bitsPerPixel)
			{
				case 8:
					p[0] = (BYTE)((r * 3 + g * 5 + b * 7 + (uint32_t)x) & 0xFF);
					break;
				case 15:
				{
					/* the sixteenth bit means nothing: a decoder must ignore it */
					unsigned value = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3) | ((rng_next(rng) & 1) << 15);
					p[0] = value & 0xFF;
					p[1] = (value >> 8) & 0xFF;
					break;
				}
				case 16:
				{
					unsigned value = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
					p[0] = value & 0xFF;
					p[1] = (value >> 8) & 0xFF;
					break;
				}
				case 24:
					p[0] = (BYTE)b;
					p[1] = (BYTE)g;
					p[2] = (BYTE)r;
					break;
				default:
					p[0] = (BYTE)b;
					p[1] = (BYTE)g;
					p[2] = (BYTE)r;
					p[3] = (BYTE)rng_next(rng);
					break;
			}
		}
	}

	char name[160];
	snprintf(name, sizeof(name), "raw%d/padded/%s/%dx%d", bitsPerPixel, imageName, width, height);

	/* What FreeRDP's client does with an uncompressed bitmap update (gdi_Bitmap_Decompress). */
	BYTE* picture = calloc((size_t)padded * height, 4);
	if (!freerdp_image_copy_no_overlap(picture, PIXEL_FORMAT_BGRX32, (UINT32)padded * 4, 0, 0, (UINT32)padded,
	                                   (UINT32)height, data, format, 0, 0, 0, &freerdp_palette, FREERDP_FLIP_VERTICAL))
		fail("FreeRDP cannot convert the bitmap", name);

	force_opaque(picture, padded, height);
	if (bitsPerPixel == 16)
	{
		BYTE* native = calloc((size_t)padded * height, 2);
		if (!freerdp_image_copy_no_overlap(native, PIXEL_FORMAT_RGB16, (UINT32)padded * 2, 0, 0, (UINT32)padded,
		                                   (UINT32)height, data, format, 0, 0, 0, NULL, FREERDP_FLIP_VERTICAL))
			fail("FreeRDP cannot flip the bitmap", name);

		BYTE* expanded = rgb16_to_bgrx(native, padded, height);
		green_differences += green_only_differences(expanded, picture, padded, height, name);
		file_add(f, name, KIND_BITMAP, 16, 0, padded, height, width, height, data, length, NULL, 0, NULL, expanded,
		         (size_t)padded * height * 4, picture, (size_t)padded * height * 4);
		free(expanded);
		free(native);
	}
	else
	{
		file_add(f, name, KIND_BITMAP, bitsPerPixel, 0, padded, height, width, height, data, length, NULL, 0,
		         bitsPerPixel == 8 ? test_palette : NULL, picture, (size_t)padded * height * 4, NULL, 0);
	}

	free(picture);
	free(data);
}

/* ---------------------------------------------------------------------------------------------------------
 * Every picture in every size through every codec
 * ------------------------------------------------------------------------------------------------------- */

/* The vectors of one picture follow each other in the file: the same picture is then expected many times in a
 * row, which the compression of the file turns into one copy. */
static void add_picture_vectors(VectorFile* f)
{
	static const int rleDepths[] = { 15, 16, 24 };
	static const int rawDepths[] = { 8, 15, 16, 24, 32 };
	static const DWORD encoderFlags[] = { 0, PLANAR_FORMAT_HEADER_RLE, PLANAR_FORMAT_HEADER_NA,
		                                  PLANAR_FORMAT_HEADER_RLE | PLANAR_FORMAT_HEADER_NA };
	Rng rng;
	rng_seed(&rng, 77);
	for (int size = 0; size < SIZE_COUNT; size++)
	{
		for (int kind = 0; kind < IMAGE_COUNT; kind++)
		{
			int width = sizes[size].width;
			int height = sizes[size].height;
			BYTE* image = make_image(kind, width, height, 1);

			for (int depth = 0; depth < 3; depth++)
				add_rle_encoded(f, image_names[kind], image, width, height, rleDepths[depth]);

			for (int flags = 0; flags < 4; flags++)
			{
				/* Where the encoder of FreeRDP gives up, the encoder of this file writes the same format: every
				 * picture is there in all four. */
				if (!add_planar_encoded(f, image_names[kind], image, width, height, encoderFlags[flags]))
					add_planar_generated(f, "generated", image_names[kind], image, width, height, 0, FALSE,
					                     (encoderFlags[flags] & PLANAR_FORMAT_HEADER_RLE) != 0,
					                     (encoderFlags[flags] & PLANAR_FORMAT_HEADER_NA) == 0, 0);
			}

			/* The two formats a Windows server uses when the client allows colour loss. */
			add_planar_generated(f, "generated", image_names[kind], image, width, height, 1, TRUE, TRUE, FALSE, 0);
			add_planar_generated(f, "generated", image_names[kind], image, width, height, 3, FALSE, TRUE, TRUE, 0);

			if (kind == IMAGE_GRADIENT || kind == IMAGE_TEXT || kind == IMAGE_NOISE || kind == IMAGE_LINES)
			{
				for (int depth = 0; depth < 5; depth++)
					add_raw_vector(f, image_names[kind], image, width, height, rawDepths[depth], &rng);
			}

			free(image);
		}
	}
}

/* ---------------------------------------------------------------------------------------------------------
 * Pointers
 * ------------------------------------------------------------------------------------------------------- */

#define SHAPE_ARROW 0   /* a white arrow with a black outline on a transparent ground */
#define SHAPE_BEAM 1    /* an inverting text cursor on a transparent ground */
#define SHAPE_CLEAN 2   /* random colours; under the mask only black and white, as servers send it */
#define SHAPE_NOISE 3   /* random colours and a random mask: any colour under the mask */
static const char* const shape_names[] = { "arrow", "beam", "clean", "noise" };

static void add_pointer_vector(VectorFile* f, int xorBitsPerPixel, int width, int height, int shape, Rng* rng)
{
	size_t xorStride = (((size_t)width * (size_t)xorBitsPerPixel + 15) / 16) * 2;
	size_t andStride = (((size_t)width + 15) / 16) * 2;
	BYTE* xorMask = calloc(xorStride * height + 1, 1);
	BYTE* andMask = calloc(andStride * height + 1, 1);
	/* The masks of a monochrome pointer go top-down, all others bottom-up. */
	BOOL topDown = xorBitsPerPixel == 1;

	for (int y = 0; y < height; y++)
	{
		int line = topDown ? y : height - 1 - y;
		BYTE* xorLine = xorMask + (size_t)line * xorStride;
		BYTE* andLine = andMask + (size_t)line * andStride;

		/* Padding is not always zero on the wire. */
		for (size_t i = 0; i < xorStride; i++)
			xorLine[i] = shape == SHAPE_NOISE ? (BYTE)rng_next(rng) : 0;
		for (size_t i = 0; i < andStride; i++)
			andLine[i] = shape == SHAPE_NOISE ? (BYTE)rng_next(rng) : 0;

		for (int x = 0; x < width; x++)
		{
			int transparent = 0;
			uint32_t argb = 0xFF000000; /* 0xAARRGGBB */
			switch (shape)
			{
				case SHAPE_ARROW:
				{
					int inside = x <= y && x < (height * 2) / 3 && y < height - 1;
					int edge = x == 0 || x == y || y == height - 2;
					transparent = !inside;
					argb = inside ? (edge ? 0xFF000000 : 0xFFFFFFFF) : 0x00000000;
					if (inside && !edge && xorBitsPerPixel >= 8 && (x + y) % 5 == 0)
						argb = 0xFF2060C0; /* a little colour where the depth can show it */
					if (inside && !edge && xorBitsPerPixel == 32 && x > y / 2)
						argb = (argb & 0x00FFFFFF) | ((uint32_t)(255 - (x * 255 / (width > 1 ? width - 1 : 1))) << 24);
					break;
				}
				case SHAPE_BEAM:
				{
					int middle = width / 2;
					int stem = x == middle;
					int serif = (y == 0 || y == height - 1) && x >= middle - 2 && x <= middle + 2;
					transparent = 1;
					argb = stem || serif ? 0xFFFFFFFF : 0xFF000000; /* white inverts, black keeps the screen */
					break;
				}
				case SHAPE_CLEAN:
					transparent = (int)rng_below(rng, 3) == 0;
					argb = 0xFF000000 | (rng_next(rng) & 0xFFFFFF);
					if (transparent)
						argb = rng_below(rng, 3) == 0 ? 0xFFFFFFFF : 0xFF000000;
					else if (xorBitsPerPixel == 32)
						argb = (argb & 0x00FFFFFF) | (rng_next(rng) & 0xFF000000);
					break;
				default:
					transparent = (int)(rng_next(rng) & 1);
					argb = rng_next(rng);
					if (rng_below(rng, 8) == 0)
						argb = rng_below(rng, 2) ? 0xFFFFFFFF : 0xFF000000;
					break;
			}

			uint32_t r = (argb >> 16) & 0xFF;
			uint32_t g = (argb >> 8) & 0xFF;
			uint32_t b = argb & 0xFF;
			if (transparent)
				andLine[x >> 3] |= (BYTE)(0x80 >> (x & 7));
			else
				andLine[x >> 3] &= (BYTE) ~(0x80 >> (x & 7));

			switch (xorBitsPerPixel)
			{
				case 1:
					if (r >= 0x80)
						xorLine[x >> 3] |= (BYTE)(0x80 >> (x & 7));
					else
						xorLine[x >> 3] &= (BYTE) ~(0x80 >> (x & 7));
					break;
				case 8:
				{
					/* index 0 and 9 are black, 7 and 255 white */
					int index = (int)((r * 3 + g * 5 + b * 7) % 251) + 1;
					if ((argb & 0xFFFFFF) == 0x000000)
						index = (x & 1) ? 0 : 9;
					else if ((argb & 0xFFFFFF) == 0xFFFFFF)
						index = (x & 1) ? 255 : 7;
					else if (index == 7 || index == 9)
						index = 11;

					if (shape == SHAPE_NOISE)
						index = (int)(rng_next(rng) & 0xFF);

					xorLine[x] = (BYTE)index;
					break;
				}
				case 16:
				{
					/* 5-5-5; the sixteenth bit is noise */
					unsigned value = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3) | ((rng_next(rng) & 1) << 15);
					xorLine[x * 2] = value & 0xFF;
					xorLine[x * 2 + 1] = (value >> 8) & 0xFF;
					break;
				}
				case 24:
					xorLine[x * 3] = (BYTE)b;
					xorLine[x * 3 + 1] = (BYTE)g;
					xorLine[x * 3 + 2] = (BYTE)r;
					break;
				default:
					xorLine[x * 4] = (BYTE)b;
					xorLine[x * 4 + 1] = (BYTE)g;
					xorLine[x * 4 + 2] = (BYTE)r;
					xorLine[x * 4 + 3] = (BYTE)(argb >> 24);
					break;
			}
		}
	}

	char name[160];
	snprintf(name, sizeof(name), "pointer%d/%s/%dx%d", xorBitsPerPixel, shape_names[shape], width, height);
	BYTE* picture = calloc((size_t)width * height, 4);
	if (!freerdp_image_copy_from_pointer_data(picture, PIXEL_FORMAT_RGBA32, (UINT32)width * 4, 0, 0, (UINT32)width,
	                                          (UINT32)height, xorMask, (UINT32)(xorStride * height), andMask,
	                                          (UINT32)(andStride * height), (UINT32)xorBitsPerPixel, &freerdp_palette))
		fail("FreeRDP cannot convert the pointer", name);

	file_add(f, name, KIND_POINTER, xorBitsPerPixel, 0, width, height, width, height, xorMask, xorStride * height,
	         andMask, andStride * height, xorBitsPerPixel == 8 ? test_palette : NULL, picture,
	         (size_t)width * height * 4, NULL, 0);
	free(picture);
	free(xorMask);
	free(andMask);
}

static void add_pointer_vectors(VectorFile* f)
{
	static const int depths[] = { 1, 8, 16, 24, 32 };
	static const Size pointerSizes[] = { { 1, 1 }, { 7, 7 }, { 3, 3 }, { 32, 32 }, { 33, 17 }, { 96, 96 }, { 384, 384 } };
	Rng rng;
	rng_seed(&rng, 4711);
	for (int depth = 0; depth < 5; depth++)
	{
		for (int size = 0; size < 7; size++)
		{
			for (int shape = 0; shape < 4; shape++)
			{
				/* The largest size once per depth is enough, and random colours of that size only where they
				 * are small (their pictures do not compress). */
				if (size == 6 && shape != SHAPE_ARROW && !(shape == SHAPE_NOISE && depths[depth] == 1))
					continue;

				add_pointer_vector(f, depths[depth], pointerSizes[size].width, pointerSizes[size].height, shape, &rng);
			}
		}
	}
}

/* --------------------------------------------------------------------------------------------------------- */

/* What FreeRDP's red/blue switch (freerdp_planar_switch_bgr, which its client turns on) does to a stream with
 * an alpha plane, per format: printed so that the choice in planar_decode can be checked against the library
 * that is actually installed. */
static void report_color_switch(void)
{
	static const struct
	{
		const char* what;
		int colorLossLevel;
		BOOL rle;
	} formats[] = { { "RGB planes, raw", 0, FALSE }, { "RGB planes, RLE", 0, TRUE }, { "AYCoCg, raw", 2, FALSE }, { "AYCoCg, RLE", 2, TRUE } };
	int width = 16;
	int height = 8;
	BYTE* image = make_image(IMAGE_GRADIENT, width, height, 9);
	BYTE* source = with_alpha(image, width, height);
	Rng rng;
	rng_seed(&rng, 1);
	for (int i = 0; i < 4; i++)
	{
		Buffer stream = { 0 };
		planar_encode(&stream, source, width, height, formats[i].colorLossLevel, FALSE, formats[i].rle, TRUE, &rng, 0);
		BYTE* off = planar_try_decode(stream.data, stream.length, width, height, FALSE);
		BYTE* on = planar_try_decode(stream.data, stream.length, width, height, TRUE);
		int same = 1;
		int swapped = 1;
		for (size_t p = 0; p < (size_t)width * height; p++)
		{
			if (memcmp(off + p * 4, on + p * 4, 3) != 0)
				same = 0;
			if (off[p * 4] != on[p * 4 + 2] || off[p * 4 + 1] != on[p * 4 + 1] || off[p * 4 + 2] != on[p * 4])
				swapped = 0;
		}

		printf("FreeRDP's red/blue switch on a stream with alpha, %-16s: %s\n", formats[i].what,
		       same ? "no effect" : swapped ? "swaps red and blue" : "something else");
		free(off);
		free(on);
		free(stream.data);
	}

	free(image);
	free(source);
}

int main(int argc, char** argv)
{
	if (argc < 3)
	{
		fprintf(stderr, "usage: vectors <output directory> <directory of the planar assets> [soft]\n");
		return 2;
	}

	/* The decoders log what they reject; nothing here is rejected, and a rejection ends the program anyway. */
	if (!getenv("VECTORS_LOG"))
		WLog_SetLogLevel(WLog_GetRoot(), WLOG_OFF);

	/* "soft" keeps FreeRDP from using its SSE code: a second run to see that both give the same pictures. */
	primitives_set_hints(argc > 3 && strcmp(argv[3], "soft") == 0 ? PRIMITIVES_PURE_SOFT : PRIMITIVES_ONLY_CPU);

	interleaved = bitmap_interleaved_context_new(FALSE);
	make_palette(2026);

	char path[512];
	VectorFile file;

	snprintf(path, sizeof(path), "%s/bitmaps.bin", argv[1]);
	file_begin(&file, path);
	add_picture_vectors(&file);
	add_rle_order_vectors(&file);
	add_planar_format_vectors(&file, argv[2]);
	file_end(&file);

	snprintf(path, sizeof(path), "%s/pointers.bin", argv[1]);
	file_begin(&file, path);
	add_pointer_vectors(&file);
	file_end(&file);

	report_color_switch();

	printf("16-bit pixels where FreeRDP's green differs from bit replication: %lu\n", green_differences);
	printf("largest error of a generated AYCoCg picture without subsampling: %lu\n", planar_worst_error);
	printf("pictures FreeRDP's planar encoder could not write (generated instead): %lu\n", planar_encoder_failures);

	static const int allDepths[] = { 8, 15, 16, 24 };
	static const int codes[] = { 0x00, 0x20, 0x40, 0x60, 0x80, 0xC0, 0xD0, 0xE0, 0xF0, 0xF1, 0xF2, 0xF3,
		                         0xF4, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFD, 0xFE };
	for (int depth = 0; depth < 4; depth++)
	{
		printf("orders at %2d bits:", allDepths[depth]);
		for (int code = 0; code < 20; code++)
		{
			/* a regular or lite code: the header with a run length in it and the MEGA form with none */
			unsigned long inHeader = 0;
			unsigned long mega = order_histogram[depth][codes[code]];
			if (codes[code] < 0xF0)
			{
				int span = codes[code] < 0xC0 ? 32 : 16;
				for (int i = 1; i < span; i++)
					inHeader += order_histogram[depth][codes[code] + i];

				printf(" %02X:%lu+%lu", codes[code], inHeader, mega);
				if (inHeader == 0 || mega == 0)
					fail("an order code was never generated", NULL);
			}
			else
			{
				printf(" %02X:%lu", codes[code], mega);
				if (mega == 0)
					fail("an order code was never generated", NULL);
			}
		}

		printf("\n");
	}

	int unused = 0;
	for (int control = 1; control < 256; control++)
	{
		if (segment_histogram[control] == 0)
			unused++;
	}

	printf("planar control bytes never generated: %d of 255\n", unused);
	bitmap_interleaved_context_free(interleaved);
	return 0;
}
