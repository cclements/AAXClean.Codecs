#include "AAXCleanNative.h"

int32_t Decoder_GetInputFormat(PAacDecoder config, int32_t* sample_rate, int32_t* channels, uint64_t* channel_mask) {
    if (!config || !config->context)
        return ERR_INVALID_HANDLE;
    if (!sample_rate || !channels || !channel_mask)
        return ERR_BUFF_HANDLE_INVALID;
    if (config->terminal_error)
        return config->terminal_error;
    if (!config->input_sample_rate)
        return DECODER_NEED_INPUT;
    *sample_rate = config->input_sample_rate;
    *channels = config->input_layout.nb_channels;
    *channel_mask = config->input_layout.order == AV_CHANNEL_ORDER_NATIVE ? config->input_layout.u.mask : 0;
    return ERR_SUCCESS;
}
#include <limits.h>

static int32_t init_swr(PAacDecoder pdec, POutputOptions pOptions, AVChannelLayout* pIn_layout, int32_t in_sample_rate, int32_t in_sample_fmt) {

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
        pIn_layout, in_sample_fmt, in_sample_rate, 0, NULL) < 0) {
        ret = ERR_SWR_INIT_FAIL;
        goto failed;
    }

    if (swr_init(pdec->swr_ctx) < 0) {
        ret = ERR_SWR_INIT_FAIL;
        goto failed;
    }

    if (av_channel_layout_copy(&pdec->input_layout, pIn_layout) < 0) {
        ret = ERR_ALLOC_FAIL;
        goto failed;
    }
    pdec->input_sample_rate = in_sample_rate;
    pdec->input_sample_fmt = in_sample_fmt;
    return ret;

failed:
    av_channel_layout_uninit(&pdec->input_layout);
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
        ret = init_swr(config, &config->output_options, &config->frame->ch_layout, config->frame->sample_rate, config->frame->format);
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
    pdec->drain_state = 0;
    pdec->frame_pending = 0;
    pdec->pending_capacity = 0;
    pdec->terminal_error = 0;
    pdec->input_sample_rate = 0;
    pdec->input_sample_fmt = AV_SAMPLE_FMT_NONE;
    pdec->input_layout = (AVChannelLayout){0};

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
        av_channel_layout_uninit(&pdec->input_layout);
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

int32_t Decoder_GetApiVersion(void) {
    return DECODER_API_VERSION;
}

int32_t Decoder_SubmitPacket(PAacDecoder config, const uint8_t* data, uint32_t size) {
    if (!config || !config->context || !config->packet || !config->frame)
        return ERR_INVALID_HANDLE;
    if ((!data && size) || (data && !size) || size >= INT_MAX - AV_INPUT_BUFFER_PADDING_SIZE)
        return AVERROR(EINVAL);
    if (config->terminal_error)
        return config->terminal_error;
    if (config->drain_state)
        return !data ? DECODER_ACCEPTED : AVERROR_EOF;
    if (config->frame_pending)
        return DECODER_RECEIVE_FIRST;

    int32_t ret;
    if (data) {
        av_packet_unref(config->packet);
        ret = av_new_packet(config->packet, (int)size);
        if (ret < 0)
            return ret;
        memcpy(config->packet->data, data, size);
        ret = avcodec_send_packet(config->context, config->packet);
        av_packet_unref(config->packet);
    } else {
        ret = avcodec_send_packet(config->context, NULL);
    }
    if (ret == AVERROR(EAGAIN))
        return DECODER_RECEIVE_FIRST;
    if (ret < 0)
        return ret;
    if (!data)
        config->drain_state = 1; // Input EOF was actually accepted.
    return DECODER_ACCEPTED;
}

int32_t Decoder_ReceivePcm(PAacDecoder config, uint8_t* out0, uint8_t* out1,
    int32_t capacity, int32_t* sample_count) {
    if (!sample_count)
        return AVERROR(EINVAL);
    *sample_count = 0;
    if (!config || !config->context || !config->frame)
        return ERR_INVALID_HANDLE;
    if (capacity < 0 || (!out0 && (capacity || out1)))
        return AVERROR(EINVAL);
    if (config->terminal_error)
        return config->terminal_error;
    if (config->drain_state == 3)
        return DECODER_END_OF_STREAM;

    int32_t ret;
    if (!config->frame_pending && config->drain_state < 2) {
        av_frame_unref(config->frame);
        ret = avcodec_receive_frame(config->context, config->frame);
        if (ret == AVERROR(EAGAIN)) {
            // The send/receive API forbids EAGAIN after accepted input EOF.
            return config->drain_state ? AVERROR(EINVAL) : DECODER_NEED_INPUT;
        }
        if (ret == AVERROR_EOF) {
            config->drain_state = 2; // Only now may the resampler be drained.
        } else if (ret < 0) {
            return ret;
        } else {
            if (config->frame->nb_samples <= 0) {
                av_frame_unref(config->frame);
                return DECODER_PCM_CONSUMED;
            }
            if (!config->swr_ctx) {
                ret = init_swr(config, &config->output_options,
                    &config->frame->ch_layout, config->frame->sample_rate, config->frame->format);
                if (ret < 0)
                    return ret;
            }
            // The resampler owns a fixed input contract. A changed decoded
            // format must never be interpreted using the previous frame's rate,
            // planes or layout. Persist failure so retry/EOF cannot hide it.
            if (config->frame->sample_rate != config->input_sample_rate ||
                config->frame->format != config->input_sample_fmt ||
                av_channel_layout_compare(&config->frame->ch_layout, &config->input_layout) != 0) {
                config->terminal_error = ERR_DECODER_INPUT_FORMAT_CHANGED;
                av_frame_unref(config->frame);
                return config->terminal_error;
            }
            config->pending_capacity = swr_get_out_samples(config->swr_ctx, config->frame->nb_samples);
            if (config->pending_capacity < 0)
                return config->pending_capacity;
            config->frame_pending = 1;
        }
    }

    if (config->drain_state == 2 && !config->frame_pending) {
        if (!config->swr_ctx) {
            config->drain_state = 3;
            return DECODER_END_OF_STREAM;
        }
        config->pending_capacity = swr_get_out_samples(config->swr_ctx, 0);
        if (config->pending_capacity < 0)
            return config->pending_capacity;
        if (!config->pending_capacity) {
            config->drain_state = 3;
            return DECODER_END_OF_STREAM;
        }
        config->frame_pending = 2; // A resampler tail is staged.
    }

    *sample_count = config->pending_capacity;
    if (!out0)
        return DECODER_PCM_READY;
    if (capacity < config->pending_capacity)
        return ERR_BUFF_TOO_SMALL;
    if (config->output_options.out_sample_fmt == AV_SAMPLE_FMT_FLTP &&
        config->output_options.out_channels == 2 && !out1)
        return ERR_BUFF_HANDLE_INVALID;

    uint8_t* converted[2] = { out0, out1 };
    const int draining = config->frame_pending == 2;
    ret = swr_convert(config->swr_ctx, converted, capacity,
        draining ? NULL : (const uint8_t* const*)config->frame->extended_data,
        draining ? 0 : config->frame->nb_samples);
    if (ret < 0)
        return ret;
    av_frame_unref(config->frame);
    config->frame_pending = 0;
    config->pending_capacity = 0;
    *sample_count = ret;
    if (draining && !ret) {
        config->drain_state = 3;
        return DECODER_END_OF_STREAM;
    }
    return DECODER_PCM_CONSUMED;
}
PVOID Decoder_OpenEC3(POutputOptions output_options) {

    return Decoder_OpenWithStreamDetect(output_options, AV_CODEC_ID_EAC3);
}

void SetLogCallback(LogCallbackType callback) {
    LogCallback = callback;
    av_log_set_callback(LogCallback ? AvLogCallback : av_log_default_callback);
}
