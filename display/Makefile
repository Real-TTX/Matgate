PACKAGES = freerdp3 freerdp-client3 winpr3 libvncclient x264 opus json-c
CFLAGS ?= -O2 -g
CFLAGS += -Wall -Wextra -Wno-unused-parameter -Wno-deprecated-declarations -std=gnu11 $(shell pkg-config --cflags $(PACKAGES))
LDLIBS = $(shell pkg-config --libs $(PACKAGES)) -lpthread -lm

SOURCES = src/main.c src/wire.c src/video.c src/audio.c src/rdp.c src/vnc.c
OBJECTS = $(SOURCES:.c=.o)

matgate-display: $(OBJECTS)
	$(CC) -o $@ $(OBJECTS) $(LDLIBS)

src/%.o: src/%.c src/session.h
	$(CC) $(CFLAGS) -c -o $@ $<

clean:
	rm -f matgate-display $(OBJECTS)

.PHONY: clean
