/* Matching-host contract checks against the actual dynamically loaded payload.
 * Independent signal alignment is covered separately by verify-encoder-signals.py. */
#include "../AAXCleanNative.h"
#include <assert.h>
#include <stdio.h>
#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

static int32_t (*receive_packet)(PAacEncoder, uint8_t *, int32_t);
static int drain(PAacEncoder encoder) {
    int packets = 0, size;
    while ((size = receive_packet(encoder, NULL, 0)) > 0) {
        uint8_t *data = malloc(size);
        assert(data && receive_packet(encoder, data, size) == 0);
        free(data);
        packets++;
    }
    assert(size == 0);
    return packets;
}
int main(int argc, char **argv) {
    assert(argc == 2);
#ifdef _WIN32
    HMODULE library = LoadLibraryA(argv[1]);
    assert(library);
#define LOAD(name, symbol) do { *(FARPROC*)(&name) = GetProcAddress(library, symbol); assert(name); } while (0)
#else
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 2; }
#define LOAD(name, symbol) do { *(void**)(&name) = dlsym(library, symbol); assert(name); } while (0)
#endif
    PVOID (*open_encoder)(PAacEncoderOptions);
    int32_t (*close_encoder)(PAacEncoder), (*flush)(PAacEncoder);
    int32_t (*timing)(PAacEncoder, int32_t *, int32_t *);
    int32_t (*encode)(PAacEncoder, uint8_t *, uint8_t *, int32_t);
    LOAD(open_encoder, "AacEncoder_Open"); LOAD(close_encoder, "AacEncoder_Close");
    LOAD(timing, "AacEncoder_GetTiming"); LOAD(encode, "AacEncoder_EncodeFrame");
    LOAD(flush, "AacEncoder_EncodeFlush"); LOAD(receive_packet, "AacEncoder_ReceiveEncodedFrame");
    int32_t frame = 123, delay = 456;
    assert(timing(NULL, &frame, &delay) == ERR_INVALID_HANDLE && frame == 123 && delay == 456);
    int lengths[] = {1, 128, 1024, 1501, 16001};
    for (int channels = 1; channels <= 2; channels++) {
        for (unsigned i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
            AacEncoderOptions options = {64000 * channels, 0, channels == 1 ? 16000 : 44100, channels, AV_SAMPLE_FMT_S16};
            PAacEncoder encoder = open_encoder(&options);
            assert((intptr_t)encoder > 0);
            frame = 123; delay = 456;
            assert(timing(encoder, NULL, &delay) == ERR_BUFF_HANDLE_INVALID);
            assert(timing(encoder, &frame, NULL) == ERR_BUFF_HANDLE_INVALID && frame == 123 && delay == 456);
            assert(timing(encoder, &frame, &delay) == 0 && frame == 1024 && delay >= 0);
            int padded = ((lengths[i] + frame - 1) / frame) * frame, packets = 0;
            int16_t pcm[AAC_FRAME_SIZE * 2] = {0};
            for (int start = 0; start < padded; start += frame) {
                assert(encode(encoder, (uint8_t *)pcm, NULL, frame) == 0);
                packets += drain(encoder);
            }
            assert(flush(encoder) == 0);
            packets += drain(encoder);
            assert(packets * frame >= delay + lengths[i]);
            assert(close_encoder(encoder) == 0);
            printf("timing channels=%d samples=%d delay=%d media=%d\n", channels, lengths[i], delay, packets * frame);
        }
    }
    puts("PASS: 10 encoder timing/coverage cases and invalid-argument checks");
    return 0;
}
