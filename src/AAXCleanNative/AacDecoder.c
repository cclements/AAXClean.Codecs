#include "AAXCleanNative.h"
#include <limits.h>

static int32_t init_swr(PAacDecoder pdec, POutputOptions pOptions, AVChannelLayout* pIn_layout, int32_t in_sample_rate) {

    int32_t ret = 0;
    int32_t out_sample_fmt;
    AVChannelLayout out_layout;

    if (pOptions->out_channels < 1 || pOptions->out_channels > 2) {
        ret = ERR_SWR_OUTPUT_CHANNELS_UNSUPPORTED;
        goto failed;
    }

    out_layout
        = pOptions->out_channels == 2
        ? (AVChannelLayout)AV_CHANNEL_LAYOUT_STEREO
        : (AVChannelLayout)AV_CHANNEL_LAYOUT_MONO;

    out_sample_fmt = pOptions->out_sample_fmt;
    if (out_sample_fmt != AV_SAMPLE_FMT_S16 && out_sample_fmt != AV_SAMPLE_FMT_FLT && out_sample_fmt != AV_SAMPLE_FMT_FLTP) {
        ret = ERR_SWR_OUTPUT_FORMAT_UNSUPPORTED;
        goto failed;
    }

    if (swr_alloc_set_opts2(
        &pdec->swr_ctx,
        &out_layout, pOptions->out_sample_fmt, pOptions->out_sample_rate,
        pIn_layout, pdec->context->sample_fmt, in_sample_rate, 0, NULL) < 0) {
        ret = ERR_SWR_INIT_FAIL;
        goto failed;
    }

    if (swr_init(pdec->swr_ctx) < 0) {
        ret = ERR_SWR_INIT_FAIL;
        goto failed;
    }

    return ret;

failed:
    if (pdec->swr_ctx) {
        swr_close(pdec->swr_ctx);
        swr_free(&pdec->swr_ctx);
    }
    return ret;
}

int32_t Decoder_ReceiveDecodedFrame(PAacDecoder config, uint8_t* outBuff0, uint8_t* outBuff1, int32_t numSamples) {
    
    if (!config->frame->nb_samples)
        return 0;

    int32_t required_size = swr_get_out_samples(config->swr_ctx, config->frame->nb_samples);

    if ((!outBuff0 && !numSamples) || required_size > numSamples)
        return required_size;
    else {
        uint8_t* convertedData[2] = { outBuff0 , outBuff1 };
        int32_t decoded = swr_convert(config->swr_ctx,
            (uint8_t* const*)convertedData, numSamples,
            (const uint8_t * const *)config->frame->data, config->frame->nb_samples);

        return decoded;
    }
}

int32_t Decoder_DecodeFlush(PAacDecoder config, uint8_t* outBuff0, uint8_t* outBuff1, uint32_t cbOutBuff)
{
    if (!config || !config->context || !config->swr_ctx)
        return ERR_INVALID_HANDLE;

    int32_t ret;
    uint8_t* convertedData[2] = { outBuff0 , outBuff1 };

    //Null frame flushes buffer
    ret = swr_convert(config->swr_ctx, convertedData, cbOutBuff, NULL, 0);
    return ret;
}

int32_t Decoder_DecodeFrame(PAacDecoder config, uint8_t* pCompressedAudio, uint32_t cbInBufferSize)
{
    if (!config || !config->context || !config->packet || !config->frame)
        return ERR_INVALID_HANDLE;

    if (!pCompressedAudio || !cbInBufferSize || cbInBufferSize >= INT_MAX - AV_INPUT_BUFFER_PADDING_SIZE)
        return AVERROR(EINVAL);

    // The caller's buffer may end at a page boundary and is only borrowed for
    // this call. av_new_packet supplies owned storage and zeroed decoder padding.
    av_packet_unref(config->packet);
    int32_t ret = av_new_packet(config->packet, (int)cbInBufferSize);
    if (ret < 0)
        return ret;
    memcpy(config->packet->data, pCompressedAudio, cbInBufferSize);

    /* send the packet with the compressed data to the decoder */
    ret = avcodec_send_packet(config->context, config->packet);
    // An accepting decoder retains its own reference. On failure, including
    // EAGAIN, the caller still owns the original input and may retry it.
    av_packet_unref(config->packet);
    if (ret < 0)
        return ret;

    ret = avcodec_receive_frame(config->context, config->frame);

    if (ret < 0)
        return ret == AVERROR(EAGAIN) || ret == AVERROR_EOF ? 0 : ret;

    if (!config->swr_ctx) {
        /*Initialize the filter after the first successful frame receipt */
        ret = init_swr(config, &config->output_options, &config->frame->ch_layout, config->frame->sample_rate);
    }

    return ret;
}

static int32_t init_frame_packet(PAacDecoder pdec) {
    pdec->frame = av_frame_alloc();
    if (!pdec->frame)
        return ERR_ALLOC_FAIL;

    pdec->packet = av_packet_alloc();
    return pdec->packet ? ERR_SUCCESS : ERR_ALLOC_FAIL;
}

static int32_t init_decoder_context(PAacDecoder* ppdec, enum AVCodecID id) {

    int32_t ret = 0;
    const AVCodec* codec;
    PAacDecoder pdec = NULL;

    *ppdec = NULL;
    pdec = malloc(sizeof(AacDecoder));
    if (!pdec) {
        ret = ERR_ALLOC_FAIL;
        *ppdec = NULL;
        goto failed;
    }
    pdec->context = NULL;
    pdec->swr_ctx = NULL;
    pdec->packet = NULL;
    pdec->frame = NULL;

    codec = avcodec_find_decoder(id);

    if (!codec) {
        ret = ERR_AAC_CODEC_NOT_FOUND;
        goto failed;
    }

    /*Initialize the codec context*/
    pdec->context = avcodec_alloc_context3(codec);
    if (!pdec->context) {
        ret = ERR_ALLOC_FAIL;
        goto failed;
    }

    // Publish only after initialization succeeds. The caller owns cleanup from
    // this point onward; a failed initialization leaves no dangling out pointer.
    *ppdec = pdec;
    return ret;

failed:
    Decoder_Close(pdec);
    return ret;
}

int32_t Decoder_Close(PAacDecoder pdec)
{
    if (pdec) {
        if (pdec->context) {
            avcodec_free_context(&pdec->context);
        }
        if (pdec->swr_ctx) {
            swr_close(pdec->swr_ctx);
            swr_free(&pdec->swr_ctx);
        }
        if (pdec->frame) {
            av_frame_free(&pdec->frame);
        }
        if (pdec->packet) {
            av_packet_free(&pdec->packet);
        }
        free(pdec);
    }
    return ERR_SUCCESS;
}

PVOID Decoder_OpenAac(PAacDecoderOptions decoder_options)
{
    intptr_t ret = 0;
    PAacDecoder pdec = NULL;

    if (!decoder_options || !decoder_options->ASC || decoder_options->asc_size < 2 ||
        decoder_options->asc_size >= INT_MAX - AV_INPUT_BUFFER_PADDING_SIZE) {
        ret = ERR_AAC_CODEC_NOT_FOUND;
        goto failed;
    }

    if ((ret = init_decoder_context(&pdec, AV_CODEC_ID_AAC)) != 0) {
        goto failed;
    }

    pdec->output_options = decoder_options->output_options;

    /* AVCodecContext owns extradata, including the required zeroed padding. */
    pdec->context->extradata_size = decoder_options->asc_size;
    pdec->context->extradata = av_mallocz((size_t)pdec->context->extradata_size + AV_INPUT_BUFFER_PADDING_SIZE);
    if (!pdec->context->extradata) {
        ret = ERR_ALLOC_FAIL;
        goto failed;
    }

    memcpy(pdec->context->extradata, decoder_options->ASC, pdec->context->extradata_size);
    if (avcodec_open2(pdec->context, pdec->context->codec, NULL) != 0) {
        ret = ERR_AAC_CODEC_OPEN_FAIL;
        goto failed;
    }

    if ((ret = init_frame_packet(pdec)) != 0) {
        goto failed;
    }

    return pdec;

failed:
    Decoder_Close(pdec);
    return (void*)ret;
}

PVOID Decoder_OpenWithStreamDetect(POutputOptions output_options, enum AVCodecID id) {
    
    PAacDecoder pdec = NULL;
    intptr_t ret = 0;

    if (!output_options) {
        ret = ERR_AAC_CODEC_NOT_FOUND;
        goto failed;
    }

    /*Initialize the decoder context*/
    if ((ret = init_decoder_context(&pdec, id)) != 0) {
        goto failed;
    }

    pdec->output_options = *output_options;

    if (avcodec_open2(pdec->context, pdec->context->codec, NULL) != 0) {
        ret = ERR_AAC_CODEC_OPEN_FAIL;
        goto failed;
    }

    if ((ret = init_frame_packet(pdec)) != 0) {
        goto failed;
    }

    return pdec;

failed:

    Decoder_Close(pdec);
    return (void*)ret;
}

PVOID Decoder_OpenAC4(POutputOptions output_options) {

    return Decoder_OpenWithStreamDetect(output_options, AV_CODEC_ID_AC4);
}
PVOID Decoder_OpenEC3(POutputOptions output_options) {

    return Decoder_OpenWithStreamDetect(output_options, AV_CODEC_ID_EAC3);
}

void SetLogCallback(LogCallbackType callback) {
    LogCallback = callback;
    av_log_set_callback(LogCallback ? AvLogCallback : av_log_default_callback);
}
