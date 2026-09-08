#pragma once

#include <stdlib.h>
#include <stdint.h>

#include <libavutil/frame.h>
#include <libavcodec/avcodec.h>
#include <libswresample/swresample.h>

#define AAC_FRAME_SIZE 1024

typedef void* PVOID;

#if !defined(min)
#define min(a,b) (((a) < (b)) ? (a) : (b))
#endif

#if !defined(max)
#define max(a,b) (((a) > (b)) ? (a) : (b))
#endif

#if defined(_MSC_VER)
//  Microsoft 
#define EXPORT __declspec(dllexport)
#elif defined(__GNUC__)
//  GCC
#define EXPORT __attribute__((visibility("default")))
#else
//  do nothing and hope for the best?
#define EXPORT
#pragma warning Unknown dynamic link import/export semantics.
#endif

typedef struct OutputOptions
{
	int32_t out_sample_rate;
	int32_t out_sample_fmt;
	int32_t out_channels;
} OutputOptions, * POutputOptions;

typedef struct AacDecoder {
    AVCodecContext* context;
    SwrContext* swr_ctx;
    AVPacket* packet;
    AVFrame* frame;
	OutputOptions output_options;
    // Additive decoder API v2 state; handles remain opaque at the exported ABI.
    int32_t drain_state;
    int32_t frame_pending;
    int32_t pending_capacity;
    int32_t terminal_error;
    int32_t input_sample_rate;
    int32_t input_sample_fmt;
    AVChannelLayout input_layout;
}AacDecoder, * PAacDecoder;

typedef struct AacDecoderOptions {
	OutputOptions output_options;
    int32_t asc_size;
    uint8_t* ASC;
}AacDecoderOptions, * PAacDecoderOptions;

typedef struct AacEncoder {
    AVCodecContext* context;
    AVPacket* packet;
    AVFrame* frame;
    int32_t current_frame_nb_samples;
    int32_t sample_size;
}AacEncoder, * PAacEncoder;

typedef struct AacEncoderOptions {
    int64_t bit_rate;
    int32_t global_quality;
    int32_t sample_rate;
    int32_t channels;
    int32_t sample_fmt;
}AacEncoderOptions, * PAacEncoderOptions;

typedef void (*LogCallbackType)(int32_t code, const char* message, size_t messageSize);
static LogCallbackType LogCallback;

#define ERR_SUCCESS 0
#define ERR_INVALID_HANDLE (-1)
#define ERR_BUFF_HANDLE_INVALID (-2)
#define ERR_BUFF_TOO_SMALL (-3)
#define ERR_ALLOC_FAIL (-4)
#define ERR_ASC_INVALID (-5)
#define ERR_AAC_CODEC_NOT_FOUND (-6)
#define ERR_AAC_CODEC_OPEN_FAIL (-7)
#define ERR_SWR_INIT_FAIL (-8)
#define ERR_AVPACKET_INIT_FAIL (-9)
#define ERR_AAC_DECODE_FAIL (-10)
#define ERR_SWR_OUTPUT_CHANNELS_UNSUPPORTED (-11)
#define ERR_SWR_OUTPUT_FORMAT_UNSUPPORTED (-12)
#define ERR_DECODER_INPUT_FORMAT_CHANGED (-13)

/**
* Open an AAC-LC audio encoder instance. Only supports AV_SAMPLE_FMT_FLTP
audio in mono or stereo.
*  
* @param encoder_options options for encoding the audio.
* 
* @return handle to the encoder instance, otherwise a negative error code.
*/
EXPORT PVOID AacEncoder_Open(PAacEncoderOptions encoder_options);

/**
* Get the audio specific config for the current encoder.
*
* @param config encoder handle
*
* @param ascBuffer a pointer to caller-allocated buffer to receive the ASC.
*
* @param pSize a pointer to the size of ascBuffer. On return, contains the size of the ASC blob
*
* @return 0 if ASC was successfully copied to the buffer. Returns the ASC size if the buffer was null or too small.
*/

EXPORT int32_t AacEncoder_GetExtraData(PAacEncoder config, uint8_t* ascBuffer, int32_t* pSize);



EXPORT int32_t AacEncoder_Close(PAacEncoder config);

/**
* Send AV_SAMPLE_FMT_FLTP audio samples to the encoder up to AAC_FRAME_SIZE samples.
* 
* @param config encoder handle
* 
* @param pDecodedAudio0 a pointer to channel 0 of the of AV_SAMPLE_FMT_FLTP audio.
* 
* @param pDecodedAudio1 if stereo, a pointer to channel 1 of the AV_SAMPLE_FMT_FLTP audio.
* 
* @param nbSamples the number of audio samples being sent to the encoder
* 
* @return 0 if a full frame was sent to the encoder. If the encoder needs more
samples before sending a full frame, returns the number of samples needed.
Otherwise a negative error code.
*/
EXPORT int32_t AacEncoder_EncodeFrame(PAacEncoder config, uint8_t* pDecodedAudio0, uint8_t* pDecodedAudio1, int32_t nbSamples);

/**
* Receive and AAC-encoded audio frame. Must call first with outBuff null and
cbOutBuff 0 to retrieve the size of the encoded frame. Call repeatedly, first
with NULL/0 then with an empty buffer to drain the encoder buffer.
* 
* @param config encoder handle
* 
* @param outBuff The buffer to receive the encoded frame. Can be null.
* 
* @param cbOutBuff The size, in bytes, of outBuff. 
* 
* @return If outBuff is null and cbOutBuff is 0, the size of the encoded frame,
otherwise 0 if success or a negative error code.
* 
*/
EXPORT int32_t AacEncoder_ReceiveEncodedFrame(PAacEncoder config, uint8_t* outBuff, int32_t cbOutBuff);
/**
* Flush all data in the encoder buffer and signal the end of encoding.
* 
* @param config encoder handle
* 
* @return 0 is fuccess, otherwise a negative error code.
*/
EXPORT int32_t AacEncoder_EncodeFlush(PAacEncoder config);

/**
* Open an AAC-LC audio decoder instance.
*
* @param decoder_options options for decoding the audio.
*
* @return owned handle to the decoder instance, otherwise a negative error code.
*/
EXPORT PVOID Decoder_OpenAac(PAacDecoderOptions decoder_options);

/**
* Open an E-AC-3 audio decoder instance.
*
* @param output_options options for decoding the audio.
*
* @return owned handle to the decoder instance, otherwise a negative error code.
*/
EXPORT PVOID Decoder_OpenEC3(POutputOptions output_options);

/**
* Open an AC-4 audio decoder instance.
*
* @param output_options options for decoding the audio.
*
* @return owned handle to the decoder instance, otherwise a negative error code.
*/
EXPORT PVOID Decoder_OpenAC4(POutputOptions output_options);

/** Release a successful open exactly once. NULL is allowed; error codes are not handles. */
EXPORT int32_t Decoder_Close(PAacDecoder config);

/**
* Send a frame of AAC-LC audio to the decoder.
* 
* @note Only 1 frame may be sent to the decoder before calling aacDecoder_ReceiveDecodedFrame
* 
* @param config decoder handle.
* 
* @param pCompressedAudio A borrowed buffer containing one compressed audio frame.
* The bytes are copied during this call; the caller need not supply decoder padding.
* 
* @param cbInBufferSize The size, in bytes, of the AAC audio frame.
* 
* @return 0 is success, otherwise a negative error code.
* 
*/
EXPORT int32_t Decoder_DecodeFrame(PAacDecoder config, uint8_t* pCompressedAudio, uint32_t cbInBufferSize);
/**
* Receive a decoded audio frame. Must call first with outBuff0 null and cbOutBuff 0
to retrieve the size of the decoded frame. Call repeatedly, first with NULL/0 then
with an empty buffer to drain the encoder buffer.
*
* @param config decoder handle
*
* @param outBuff0 pointer to the buffer to receive the decoded frame. For packet
audio, this buffer receives the full frame (both stereo and mono). For planar audio,
this buffer receives the channel 0 audio. 
*
* @param outBuff1 pointer to the buffer to receive channel 1 of the decoded planar
audio. Unused for packet audio.
* 
* @param numSamples The size of the buffer, in number of audio samples.
*
* @return If outBuff0 is null and numSamples is 0, the maximum size (in number of
audio samples) of the decoded frame. Otherwise the number of samples actually
decoded if positive, or a negative error code.
*
*/
EXPORT int32_t Decoder_ReceiveDecodedFrame(PAacDecoder config, uint8_t* outBuff0, uint8_t* outBuff1, int32_t numSamples);
/**
* Flush all data in the decoder buffer and signal the end of decoding. Call aacDecoder_ReceiveDecodedFrame()
with NULL/0 to get the maximum size of the buffer needed to drain the decoder.
*
* @param config decoder handle
* 
* @param outBuff0 pointer to the buffer to receive the decoded frame. For packet
audio, this buffer receives the full frame (both stereo and mono). For planar audio,
this buffer receives the channel 0 audio.
*
* @param outBuff1 pointer to the buffer to receive channel 1 of the decoded planar
audio. Unused for packet audio.
*
* @return the number of samples decoded, otherwise a negative error code.
*/
EXPORT int32_t Decoder_DecodeFlush(PAacDecoder config, uint8_t* outBuff0, uint8_t* outBuff1, uint32_t cbOutBuff);

/** Decoder API v2. Legacy exports remain available; do not mix protocols on a handle. */
#define DECODER_API_VERSION 2
#define DECODER_ACCEPTED 0
#define DECODER_RECEIVE_FIRST 1
#define DECODER_PCM_CONSUMED 0
#define DECODER_NEED_INPUT 1
#define DECODER_PCM_READY 2
#define DECODER_END_OF_STREAM 3

EXPORT int32_t Decoder_GetApiVersion(void);

/**
 * Submit one borrowed packet, or NULL/0 to finish input. Returns ACCEPTED only
 * when the codec accepted it; RECEIVE_FIRST leaves the packet unaccepted and
 * requires receiving output before retrying the exact same bytes. EOF submission
 * is idempotent after acceptance. Nonempty input after accepted EOF is rejected.
 */
EXPORT int32_t Decoder_SubmitPacket(PAacDecoder config, const uint8_t* data, uint32_t size);

/**
 * Receive every codec frame, then (after codec EOF) drain the resampler.
 * Query with NULL/NULL/0: PCM_READY returns a required per-channel capacity in
 * sample_count and stages one frame/tail; repeat queries do not consume it.
 * Supply buffers with at least that capacity: PCM_CONSUMED returns the actual
 * per-channel count (possibly zero). NEED_INPUT requests another packet.
 * END_OF_STREAM means both codec and resampler ended. Errors are negative.
 * Too-small/invalid output buffers do not consume the staged frame.
 */
EXPORT int32_t Decoder_ReceivePcm(PAacDecoder config, uint8_t* out0, uint8_t* out1,
    int32_t capacity, int32_t* sample_count);

/**
* Set the logging callback
*
* @param callback A logging function with a compatible signature.
*
* @remarks The callback must be thread safe, even if the application does not use
* threads itself as some codecs are multithreaded.
*/
EXPORT void SetLogCallback(LogCallbackType callback);

static void AvLogCallback(void* data, int32_t code, const char* fmt, va_list args) {

    if (!LogCallback || code == AV_LOG_DEBUG)
        return;

    va_list argsCopy;
    va_copy(argsCopy, args);
    size_t requiredSize = vsnprintf(NULL, 0, fmt, argsCopy);

    char* messageBuff = malloc(requiredSize);
    if (!messageBuff)
        return;

    requiredSize = vsnprintf(messageBuff, requiredSize, fmt, args);
    LogCallback(code, messageBuff, requiredSize);
    free(messageBuff);
}
