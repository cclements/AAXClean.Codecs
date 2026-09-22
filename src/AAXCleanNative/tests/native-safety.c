/* Test-only interposition around production wrappers; codec/library calls are real.
 * Failure switches affect wrapper allocation/open calls, not codec internals. */
#include "../AAXCleanNative.h"
#include <assert.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

static int failure, wrapper_allocs, wrapper_frees, contexts, frames, packets;
static int decoder_closes, encoder_closes, send_calls, check_extra, check_packet, timing_mode;
static const uint8_t *borrowed;
static AVPacket *retained;
static void *checked_malloc(size_t size);
static void checked_free(void *pointer);
static const AVCodec *checked_find_decoder(enum AVCodecID id);
static const AVCodec *checked_find_encoder(enum AVCodecID id);
static AVCodecContext *checked_context(const AVCodec *codec);
static void checked_free_context(AVCodecContext **context);
static void *checked_extra(size_t size);
static int checked_open(AVCodecContext *context, const AVCodec *codec, AVDictionary **options);
static AVFrame *checked_frame(void);
static void checked_free_frame(AVFrame **frame);
static AVPacket *checked_packet(void);
static void checked_free_packet(AVPacket **packet);
static int checked_new_packet(AVPacket *packet, int size);
static int checked_send(AVCodecContext *context, const AVPacket *packet);
int32_t decoder_close_impl(PAacDecoder decoder);
int32_t encoder_close_impl(PAacEncoder encoder);

#define malloc checked_malloc
#define free checked_free
#define avcodec_find_decoder checked_find_decoder
#define avcodec_find_encoder checked_find_encoder
#define avcodec_alloc_context3 checked_context
#define avcodec_free_context checked_free_context
#define av_mallocz checked_extra
#define av_malloc checked_extra
#define avcodec_open2 checked_open
#define av_frame_alloc checked_frame
#define av_frame_free checked_free_frame
#define av_packet_alloc checked_packet
#define av_packet_free checked_free_packet
#define av_new_packet checked_new_packet
#define avcodec_send_packet checked_send
#define Decoder_Close decoder_close_impl
#define AacEncoder_Close encoder_close_impl
#define AacEncoder_GetTiming encoder_timing_impl
#ifndef DECODER_SOURCE
#define DECODER_SOURCE "../AacDecoder.c"
#endif
#include DECODER_SOURCE
#include "../AacEncoder.c"
#undef malloc
#undef free
#undef avcodec_find_decoder
#undef avcodec_find_encoder
#undef avcodec_alloc_context3
#undef avcodec_free_context
#undef av_mallocz
#undef av_malloc
#undef avcodec_open2
#undef av_frame_alloc
#undef av_frame_free
#undef av_packet_alloc
#undef av_packet_free
#undef av_new_packet
#undef avcodec_send_packet
#undef Decoder_Close
#undef AacEncoder_Close
#undef AacEncoder_GetTiming

static void *checked_malloc(size_t size) {
    if (failure == 1) return NULL;
    void *pointer = malloc(size);
    if (pointer) wrapper_allocs++;
    return pointer;
}
static void checked_free(void *pointer) {
    if (pointer) wrapper_frees++;
    free(pointer);
}
static const AVCodec *checked_find_decoder(enum AVCodecID id) {
    return failure == 2 ? NULL : avcodec_find_decoder(id);
}
static const AVCodec *checked_find_encoder(enum AVCodecID id) {
    return failure == 2 ? NULL : avcodec_find_encoder(id);
}
static AVCodecContext *checked_context(const AVCodec *codec) {
    AVCodecContext *context = failure == 3 ? NULL : avcodec_alloc_context3(codec);
    if (context) contexts++;
    return context;
}
static void checked_free_context(AVCodecContext **context) {
    if (*context) contexts--;
    avcodec_free_context(context);
}
static void *checked_extra(size_t size) { return failure == 4 ? NULL : av_mallocz(size); }
static int checked_open(AVCodecContext *context, const AVCodec *codec, AVDictionary **options) {
    if (check_extra && context->extradata) {
        for (int i = 0; i < AV_INPUT_BUFFER_PADDING_SIZE; i++)
            assert(context->extradata[context->extradata_size + i] == 0);
    }
    return failure == 5 ? AVERROR(EINVAL) : avcodec_open2(context, codec, options);
}
static AVFrame *checked_frame(void) {
    AVFrame *frame = failure == 6 ? NULL : av_frame_alloc();
    if (frame) frames++;
    return frame;
}
static void checked_free_frame(AVFrame **frame) {
    if (*frame) frames--;
    av_frame_free(frame);
}
static AVPacket *checked_packet(void) {
    AVPacket *packet = failure == 7 ? NULL : av_packet_alloc();
    if (packet) packets++;
    return packet;
}
static void checked_free_packet(AVPacket **packet) {
    if (*packet) packets--;
    av_packet_free(packet);
}
static int checked_new_packet(AVPacket *packet, int size) {
    return failure == 8 ? AVERROR(ENOMEM) : av_new_packet(packet, size);
}
static int checked_send(AVCodecContext *context, const AVPacket *packet) {
    send_calls++;
    if (check_packet) {
        assert(packet->buf && packet->data != borrowed);
        assert(memcmp(packet->data, borrowed, packet->size) == 0);
        for (int i = 0; i < AV_INPUT_BUFFER_PADDING_SIZE; i++)
            assert(packet->data[packet->size + i] == 0);
        assert(!retained);
        retained = av_packet_clone(packet);
        assert(retained);
    }
    return failure == 9 ? AVERROR(EAGAIN) : avcodec_send_packet(context, packet);
}
EXPORT int32_t Decoder_Close(PAacDecoder decoder) {
    if (decoder) decoder_closes++;
    return decoder_close_impl(decoder);
}
EXPORT int32_t AacEncoder_Close(PAacEncoder encoder) {
    if (encoder) encoder_closes++;
    return encoder_close_impl(encoder);
}
#ifndef OMIT_ENCODER_TIMING
EXPORT int32_t AacEncoder_GetTiming(PAacEncoder encoder, int32_t *frame, int32_t *delay) {
    if (!timing_mode) return encoder_timing_impl(encoder, frame, delay);
    if (timing_mode == 4) return ERR_AAC_CODEC_OPEN_FAIL;
    *frame = timing_mode == 2 ? 512 : 1024;
    *delay = timing_mode == 3 ? -1 : timing_mode == 5 ? INT_MAX : 2112;
    return 0;
}
#endif
EXPORT void Safety_SetTiming(int value) { timing_mode = value; }
EXPORT void Safety_SetFailure(int value) { failure = value; }
EXPORT int Safety_DecoderCloses(void) { return decoder_closes; }
EXPORT int Safety_EncoderCloses(void) { return encoder_closes; }
EXPORT int Safety_LiveWrappers(void) { return wrapper_allocs - wrapper_frees; }
EXPORT int Safety_LiveResources(void) { return contexts + frames + packets; }
EXPORT int Safety_SizeOfOptions(int kind) {
    return kind == 0 ? sizeof(OutputOptions) : kind == 1 ? sizeof(AacDecoderOptions) : sizeof(AacEncoderOptions);
}
EXPORT void Safety_Reset(void) {
    assert(wrapper_allocs == wrapper_frees && !contexts && !frames && !packets && !retained);
    failure = wrapper_allocs = wrapper_frees = decoder_closes = encoder_closes = send_calls = 0;
    check_extra = check_packet = timing_mode = 0;
    borrowed = NULL;
}

#ifndef BUILD_MANAGED_PROBE
static OutputOptions output = { 44100, AV_SAMPLE_FMT_S16, 2 };
static uint8_t asc[] = { 0x12, 0x10 };
static void *open_decoder(int kind) {
    AacDecoderOptions options = { output, sizeof(asc), asc };
    return kind == 0 ? Decoder_OpenAac(&options) : kind == 1 ? Decoder_OpenEC3(&output) : Decoder_OpenAC4(&output);
}
static uint8_t *page_end(const uint8_t *data, size_t size, void **mapping) {
    size_t page = sysconf(_SC_PAGESIZE);
    assert(size && size <= page);
    *mapping = mmap(NULL, page * 2, PROT_READ | PROT_WRITE, MAP_ANON | MAP_PRIVATE, -1, 0);
    assert(*mapping != MAP_FAILED);
    assert(mprotect((uint8_t *)*mapping + page, page, PROT_NONE) == 0);
    uint8_t *end = (uint8_t *)*mapping + page - size;
    memcpy(end, data, size);
    return end;
}
static void release_pages(void *mapping) { assert(munmap(mapping, sysconf(_SC_PAGESIZE) * 2) == 0); }
static void no_leaks(void) {
    assert(wrapper_allocs == wrapper_frees && !contexts && !frames && !packets);
}
static void failed_opens(void) {
    for (int kind = 0; kind < 3; kind++) {
        for (int point = 1; point <= 7; point++) {
            if (kind && point == 4) continue;
            Safety_Reset();
            failure = point;
            assert((intptr_t)open_decoder(kind) < 0);
            no_leaks();
            printf("PASS failed-open codec=%d point=%d alloc=%d free=%d\n", kind, point, wrapper_allocs, wrapper_frees);
        }
    }
    for (int point = 1; point <= 7; point++) {
        if (point == 4) continue;
        Safety_Reset();
        failure = point;
        AacEncoderOptions options = { 128000, 0, 44100, 2, AV_SAMPLE_FMT_S16 };
        assert((intptr_t)AacEncoder_Open(&options) < 0);
        no_leaks();
        printf("PASS failed-open encoder point=%d alloc=%d free=%d\n", point, wrapper_allocs, wrapper_frees);
    }
}
static void extradata_boundary(void) {
    Safety_Reset();
    check_extra = 1;
    void *mapping;
    AacDecoderOptions options = { output, sizeof(asc), page_end(asc, sizeof(asc), &mapping) };
    PAacDecoder decoder = Decoder_OpenAac(&options);
    assert((intptr_t)decoder > 0);
    release_pages(mapping);
    Decoder_Close(decoder);
    no_leaks();
}
static void packet_boundary(void) {
    Safety_Reset();
    AacEncoderOptions options = { 128000, 0, 44100, 2, AV_SAMPLE_FMT_S16 };
    PAacEncoder encoder = AacEncoder_Open(&options);
    assert((intptr_t)encoder > 0);
    PAacDecoder decoder = open_decoder(0);
    assert((intptr_t)decoder > 0);
    int16_t pcm[AAC_FRAME_SIZE * 2] = { 0 };
    int decoded_samples = 0, decoded_packets = 0;
    for (int frame = 0; frame < 12; frame++) {
        assert(AacEncoder_EncodeFrame(encoder, (uint8_t *)pcm, NULL, AAC_FRAME_SIZE) >= 0);
        int size = AacEncoder_ReceiveEncodedFrame(encoder, NULL, 0);
        if (!size) continue;
        assert(size > 0);
        uint8_t *encoded = malloc(size);
        assert(AacEncoder_ReceiveEncodedFrame(encoder, encoded, size) == 0);
        void *mapping;
        borrowed = page_end(encoded, size, &mapping);
        check_packet = 1;
        assert(Decoder_DecodeFrame(decoder, (uint8_t *)borrowed, size) >= 0);
        check_packet = 0;
        assert(!decoder->packet->buf && !decoder->packet->data);
        release_pages(mapping);
        assert(retained && memcmp(retained->data, encoded, size) == 0);
        av_packet_free(&retained);
        free(encoded);
        int required = Decoder_ReceiveDecodedFrame(decoder, NULL, NULL, 0);
        assert(required > 0);
        int16_t *decoded = calloc(required * 2, sizeof(int16_t));
        int samples = Decoder_ReceiveDecodedFrame(decoder, (uint8_t *)decoded, NULL, required);
        assert(samples >= 0 && samples <= required);
        decoded_samples += samples;
        decoded_packets++;
        free(decoded);
    }
    assert(decoded_packets > 0 && decoded_samples > 0);
    printf("AAC encoded packets=%d decoded samples/channel=%d\n", decoded_packets, decoded_samples);
    Decoder_Close(decoder);
    AacEncoder_Close(encoder);
    no_leaks();
}
static void malformed_packets(void) {
    uint8_t invalid[17];
    memset(invalid, 0xff, sizeof(invalid));
    for (int kind = 0; kind < 3; kind++) {
        for (int size = 1; size <= sizeof(invalid); size++) {
            Safety_Reset();
            PAacDecoder decoder = open_decoder(kind);
            assert((intptr_t)decoder > 0);
            void *mapping;
            borrowed = page_end(invalid, size, &mapping);
            check_packet = 1;
            int result = Decoder_DecodeFrame(decoder, (uint8_t *)borrowed, size);
            assert(result <= 0);
            check_packet = 0;
            release_pages(mapping);
            av_packet_free(&retained);
            assert(!decoder->packet->buf && !decoder->packet->data);
            Decoder_Close(decoder);
            no_leaks();
        }
        printf("PASS malformed-page-boundary codec=%d sizes=1..17\n", kind);
    }
}
static void packet_errors(void) {
    Safety_Reset();
    PAacDecoder decoder = open_decoder(0);
    assert((intptr_t)decoder > 0);
    uint8_t data = 0;
    assert(Decoder_DecodeFrame(decoder, NULL, 1) == AVERROR(EINVAL));
    assert(Decoder_DecodeFrame(decoder, &data, 0) == AVERROR(EINVAL));
    assert(Decoder_DecodeFrame(decoder, &data, INT_MAX - AV_INPUT_BUFFER_PADDING_SIZE) == AVERROR(EINVAL));
    assert(Decoder_DecodeFrame(decoder, &data, UINT_MAX) == AVERROR(EINVAL));
    assert(send_calls == 0);
    failure = 8;
    assert(Decoder_DecodeFrame(decoder, &data, 1) == AVERROR(ENOMEM));
    assert(send_calls == 0 && !decoder->packet->buf);
    failure = 9;
    assert(Decoder_DecodeFrame(decoder, &data, 1) == AVERROR(EAGAIN));
    assert(send_calls == 1 && !decoder->packet->buf && !decoder->packet->data);
    failure = 0;
    Decoder_Close(decoder);
    Decoder_Close(NULL);
    Decoder_Close(NULL);
    AacDecoderOptions options = { output, INT_MAX, &data };
    assert((intptr_t)Decoder_OpenAac(&options) < 0);
    assert((intptr_t)Decoder_OpenAac(NULL) < 0);
    assert((intptr_t)Decoder_OpenEC3(NULL) < 0);
    assert((intptr_t)Decoder_OpenAC4(NULL) < 0);
    no_leaks();
}
static void encoder_timing(void) {
    int32_t frame = 123, delay = 456;
    assert(encoder_timing_impl(NULL, &frame, &delay) == ERR_INVALID_HANDLE);
    AacEncoder dummy = {0};
    assert(encoder_timing_impl(&dummy, &frame, &delay) == ERR_INVALID_HANDLE);
    AacEncoderOptions options = { 128000, 0, 44100, 2, AV_SAMPLE_FMT_S16 };
    PAacEncoder encoder = AacEncoder_Open(&options);
    assert((intptr_t)encoder > 0);
    assert(encoder_timing_impl(encoder, NULL, &delay) == ERR_BUFF_HANDLE_INVALID);
    assert(encoder_timing_impl(encoder, &frame, NULL) == ERR_BUFF_HANDLE_INVALID);
    assert(frame == 123 && delay == 456);
    assert(encoder_timing_impl(encoder, &frame, &delay) == 0);
    assert(frame == 1024 && delay == encoder->context->initial_padding && delay > 0);
    printf("encoder frame=%d initial-padding=%d\n", frame, delay);
    encoder->context->initial_padding = -1;
    assert(encoder_timing_impl(encoder, &frame, &delay) < 0);
    assert(frame == 1024 && delay > 0);
    encoder->context->initial_padding = 0;
    encoder->context->frame_size = 512;
    assert(encoder_timing_impl(encoder, &frame, &delay) < 0);
    AacEncoder_Close(encoder);
    no_leaks();
}
static void abi(void) {
    assert(sizeof(OutputOptions) == 12);
    assert(sizeof(AacDecoderOptions) == 24 && offsetof(AacDecoderOptions, ASC) == 16);
    assert(sizeof(AacEncoderOptions) == 24 && offsetof(AacEncoderOptions, sample_fmt) == 20);
    printf("ABI output=12 decoder=24 ASC-offset=16 encoder=24 format-offset=20 padding=%d\n", AV_INPUT_BUFFER_PADDING_SIZE);
}
int main(int argc, char **argv) {
    assert(argc == 2);
    av_log_set_level(AV_LOG_QUIET);
    if (!strcmp(argv[1], "failed-opens")) failed_opens();
    else if (!strcmp(argv[1], "extradata-boundary")) extradata_boundary();
    else if (!strcmp(argv[1], "packet-boundary")) packet_boundary();
    else if (!strcmp(argv[1], "malformed-packets")) malformed_packets();
    else if (!strcmp(argv[1], "packet-errors")) packet_errors();
    else if (!strcmp(argv[1], "abi")) abi();
    else if (!strcmp(argv[1], "encoder-timing")) encoder_timing();
    else return 2;
    printf("PASS %s\n", argv[1]);
    return 0;
}
#endif
