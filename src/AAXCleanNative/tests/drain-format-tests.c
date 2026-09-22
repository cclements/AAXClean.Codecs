/* Synthetic decoded frames exercise production format validation and real swr.
 * Only codec receive is interposed, including format changes unavailable from
 * the installed encoders. All frame/layout allocation and conversion is real. */
#include "../AAXCleanNative.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>
static int rate = 48000, format = AV_SAMPLE_FMT_FLTP, channels = 2, receives;
static int receive_frame(AVCodecContext *context, AVFrame *frame) {
    (void)context;
    receives++;
    frame->sample_rate = rate;
    frame->format = format;
    frame->nb_samples = 128;
    av_channel_layout_default(&frame->ch_layout, channels);
    assert(av_frame_get_buffer(frame, 0) == 0);
    assert(av_samples_set_silence(frame->extended_data, 0, frame->nb_samples,
                                 channels, format) == 0);
    return 0;
}
#define avcodec_receive_frame receive_frame
#include "../AacDecoder.c"
#undef avcodec_receive_frame

int main(void) {
    for (int change = 0; change < 3; change++) {
        rate = 48000; format = AV_SAMPLE_FMT_FLTP; channels = 2;
        OutputOptions output = {32000, AV_SAMPLE_FMT_S16, 2};
        PAacDecoder decoder = Decoder_OpenEC3(&output);
        assert((intptr_t)decoder > 0);
        int count;
        int32_t input_rate = 123, input_channels = 456; uint64_t mask = 789;
        assert(Decoder_GetInputFormat(decoder, &input_rate, &input_channels, &mask) == DECODER_NEED_INPUT);
        assert(input_rate == 123 && input_channels == 456 && mask == 789);
        assert(Decoder_ReceivePcm(decoder, NULL, NULL, 0, &count) == DECODER_PCM_READY);
        assert(Decoder_GetInputFormat(decoder, &input_rate, &input_channels, &mask) == 0);
        assert(input_rate == 48000 && input_channels == 2 && mask == AV_CH_LAYOUT_STEREO);
        uint8_t *pcm = calloc(count, 4);
        assert(pcm);
        assert(Decoder_ReceivePcm(decoder, pcm, NULL, count, &count) == DECODER_PCM_CONSUMED);
        free(pcm);
        // Same format is accepted on a distinct owned frame/layout.
        assert(Decoder_ReceivePcm(decoder, NULL, NULL, 0, &count) == DECODER_PCM_READY);
        pcm = calloc(count, 4); assert(pcm);
        assert(Decoder_ReceivePcm(decoder, pcm, NULL, count, &count) == DECODER_PCM_CONSUMED);
        free(pcm);
        if (change == 0) rate = 32000;
        if (change == 1) format = AV_SAMPLE_FMT_S16;
        if (change == 2) channels = 1;
        assert(Decoder_ReceivePcm(decoder, NULL, NULL, 0, &count) == ERR_DECODER_INPUT_FORMAT_CHANGED);
        assert(count == 0);
        assert(Decoder_GetInputFormat(decoder, &input_rate, &input_channels, &mask) == ERR_DECODER_INPUT_FORMAT_CHANGED);
        int before = receives;
        assert(Decoder_ReceivePcm(decoder, NULL, NULL, 0, &count) == ERR_DECODER_INPUT_FORMAT_CHANGED);
        assert(receives == before);
        assert(Decoder_SubmitPacket(decoder, NULL, 0) == ERR_DECODER_INPUT_FORMAT_CHANGED);
        uint8_t input = 0;
        assert(Decoder_SubmitPacket(decoder, &input, 1) == ERR_DECODER_INPUT_FORMAT_CHANGED);
        assert(Decoder_Close(decoder) == 0);
    }
    puts("PASS: rate, sample format and layout changes fail terminally before conversion");
}
