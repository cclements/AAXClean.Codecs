#include "../AAXCleanNative.h"
#include <assert.h>
#include <dlfcn.h>
#include <stdio.h>
#include <string.h>

/* Exercise the public ABI from a named, immutable development library. */
static PVOID (*open_aac)(PAacDecoderOptions);
static PVOID (*open_ec3)(POutputOptions);
static int32_t (*close_decoder)(PAacDecoder);
static int32_t (*submit)(PAacDecoder, const uint8_t*, uint32_t);
static int32_t (*receive_pcm)(PAacDecoder, uint8_t*, uint8_t*, int32_t, int32_t*);
static int32_t (*legacy_submit)(PAacDecoder, uint8_t*, uint32_t);
static int32_t (*legacy_receive)(PAacDecoder, uint8_t*, uint8_t*, int32_t);
static int32_t (*legacy_flush)(PAacDecoder, uint8_t*, uint8_t*, uint32_t);
static long total_samples, pcm_frames;
static int receive_first, final_state;

static int receive_available(PAacDecoder decoder, FILE* output) {
    for (int guard = 0; guard < 100000; guard++) {
        int32_t needed = -1;
        int state = receive_pcm(decoder, NULL, NULL, 0, &needed);
        if (state == DECODER_NEED_INPUT || state == DECODER_END_OF_STREAM) return state;
        if (state == DECODER_PCM_CONSUMED) { assert(needed == 0); continue; }
        assert(state == DECODER_PCM_READY && needed > 0);
        int32_t again = -1;
        assert(receive_pcm(decoder, NULL, NULL, 0, &again) == state && again == needed);
        uint8_t* bytes = calloc((size_t)needed, 4);
        assert(bytes);
        assert(receive_pcm(decoder, bytes, NULL, needed - 1, &again) == ERR_BUFF_TOO_SMALL);
        assert(again == needed);
        state = receive_pcm(decoder, bytes, NULL, needed, &again);
        assert((state == DECODER_PCM_CONSUMED || state == DECODER_END_OF_STREAM) && again >= 0 && again <= needed);
        assert(fwrite(bytes, 4, (size_t)again, output) == (size_t)again);
        total_samples += again;
        if (again) pcm_frames++;
        free(bytes);
        if (state == DECODER_END_OF_STREAM) return state;
    }
    assert(!"receive loop failed to terminate");
    return -1;
}

int main(int argc, char** argv) {
    assert(argc == 6); /* library, fixtures, codec, output, v2|legacy */
    void* lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!lib) { fprintf(stderr, "%s\n", dlerror()); return 2; }
#define LOAD(name, symbol) do { *(void**)(&name) = dlsym(lib, symbol); assert(name); } while (0)
    LOAD(open_aac, "Decoder_OpenAac"); LOAD(open_ec3, "Decoder_OpenEC3");
    LOAD(close_decoder, "Decoder_Close");
    const int legacy = strcmp(argv[5], "legacy") == 0;
    if (legacy) {
        LOAD(legacy_submit, "Decoder_DecodeFrame");
        LOAD(legacy_receive, "Decoder_ReceiveDecodedFrame");
        LOAD(legacy_flush, "Decoder_DecodeFlush");
    } else {
        LOAD(submit, "Decoder_SubmitPacket"); LOAD(receive_pcm, "Decoder_ReceivePcm");
        int (*version)(void); LOAD(version, "Decoder_GetApiVersion"); assert(version() == 2);
    }
    char path[4096];
    const int aac = strcmp(argv[3], "aac") == 0;
    OutputOptions format = { aac ? 16000 : 32000, AV_SAMPLE_FMT_S16, 2 };
    PAacDecoder decoder;
    uint8_t asc[2];
    if (aac) {
        snprintf(path, sizeof(path), "%s/aac.asc", argv[2]);
        FILE* config = fopen(path, "rb"); assert(config);
        assert(fread(asc, 1, 2, config) == 2); fclose(config);
        AacDecoderOptions options = {format, 2, asc};
        decoder = open_aac(&options);
    } else decoder = open_ec3(&format);
    assert((intptr_t)decoder > 0);
    snprintf(path, sizeof(path), "%s/%s.packets", argv[2], argv[3]);
    FILE* input = fopen(path, "rb"); assert(input);
    FILE* output = fopen(argv[4], "wb"); assert(output);
    uint32_t size; int packets = 0;
    while (fread(&size, 4, 1, input) == 1) {
        assert(size > 0 && size < 1000000);
        uint8_t* data = malloc(size); assert(data && fread(data, 1, size, input) == size);
        if (legacy) {
            assert(legacy_submit(decoder, data, size) >= 0);
            int needed = legacy_receive(decoder, NULL, NULL, 0); assert(needed >= 0);
            if (needed) {
                uint8_t* bytes = calloc((size_t)needed, 4); assert(bytes);
                int count = legacy_receive(decoder, bytes, NULL, needed); assert(count >= 0 && count <= needed);
                assert(fwrite(bytes, 4, (size_t)count, output) == (size_t)count);
                total_samples += count; free(bytes);
            }
        } else {
            int state = submit(decoder, data, size);
            /* Deliberately queue packets before receiving to exercise real EAGAIN. */
            if (state == DECODER_RECEIVE_FIRST) {
                receive_first++;
                assert(receive_available(decoder, output) == DECODER_NEED_INPUT);
                state = submit(decoder, data, size);
            }
            assert(state == DECODER_ACCEPTED);
        }
        free(data); packets++;
    }
    assert(feof(input)); fclose(input);
    if (legacy) {
        uint8_t bytes[16384]; int count;
        do {
            count = legacy_flush(decoder, bytes, NULL, 4096); assert(count >= 0 && count <= 4096);
            assert(fwrite(bytes, 4, (size_t)count, output) == (size_t)count);
            total_samples += count;
        } while (count);
    } else {
        int state = submit(decoder, NULL, 0);
        if (state == DECODER_RECEIVE_FIRST) {
            receive_first++;
            assert(receive_available(decoder, output) == DECODER_NEED_INPUT);
            state = submit(decoder, NULL, 0);
        }
        assert(state == DECODER_ACCEPTED);
        assert(submit(decoder, NULL, 0) == DECODER_ACCEPTED);
        uint8_t after_eof = 0;
        assert(submit(decoder, &after_eof, 1) < 0);
        final_state = receive_available(decoder, output);
        assert(final_state == DECODER_END_OF_STREAM);
        assert(receive_available(decoder, output) == DECODER_END_OF_STREAM);
    }
    fclose(output); assert(close_decoder(decoder) == 0);
    printf("{\"codec\":\"%s\",\"legacy\":%s,\"packets\":%d,\"samplesPerChannel\":%ld,\"pcmFrames\":%ld,\"receiveFirst\":%d,\"finalState\":%d}\n",
        argv[3], legacy ? "true" : "false", packets, total_samples, pcm_frames, receive_first, final_state);
    dlclose(lib); return 0;
}
