using System;
using System.IO;
using OggVorbisEncoder;

namespace Oxide.Ext.RustQuestsVoice
{
    /// <summary>
    /// Pure audio math: WAV parsing, loudness normalization, and Ogg Vorbis encoding.
    /// No Oxide or network dependencies — the test harness compiles this file verbatim.
    /// </summary>
    public static class VoiceAudio
    {
        /// <summary>Mono float samples plus their rate; the unit passed between pipeline stages.</summary>
        public struct Pcm
        {
            public float[] Samples;
            public int SampleRate;
            public double Seconds => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0;
        }

        /// <summary>Raw headerless 16-bit little-endian mono PCM (the OpenAI "pcm" response format).</summary>
        public static Pcm FromRawPcm16(byte[] data, int sampleRate)
        {
            if (data == null || data.Length < 128)
                throw new InvalidDataException("pcm stream empty/tiny");
            if (sampleRate <= 0) sampleRate = 24000;
            var samples = new float[data.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
            return new Pcm { Samples = samples, SampleRate = sampleRate };
        }

        /// <summary>
        /// Parse a RIFF/WAVE byte stream to mono float PCM. Handles 16/24/32-bit integer and
        /// 32-bit float samples, plain or WAVE_FORMAT_EXTENSIBLE; multi-channel input is
        /// averaged down to mono (the trader speaks from one radio).
        /// </summary>
        public static Pcm ParseWav(byte[] wav)
        {
            if (wav == null || wav.Length < 44 ||
                wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
                wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
                throw new InvalidDataException("not a RIFF/WAVE stream");

            int channels = 0, sampleRate = 0, bits = 0, format = 0;
            int dataOffset = -1, dataLength = 0;

            var pos = 12;
            while (pos + 8 <= wav.Length)
            {
                var chunkId = BitConverter.ToUInt32(wav, pos);
                var chunkLen = BitConverter.ToInt32(wav, pos + 4);
                if (chunkLen < 0 || pos + 8 + chunkLen > wav.Length)
                    chunkLen = wav.Length - pos - 8; // tolerate streamed/lying sizes (a real Kokoro-FastAPI quirk)

                if (chunkId == 0x20746D66) // "fmt "
                {
                    format = BitConverter.ToUInt16(wav, pos + 8);
                    channels = BitConverter.ToUInt16(wav, pos + 10);
                    sampleRate = BitConverter.ToInt32(wav, pos + 12);
                    bits = BitConverter.ToUInt16(wav, pos + 22);
                    if (format == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE: first two bytes of SubFormat GUID
                        format = chunkLen >= 26 ? BitConverter.ToUInt16(wav, pos + 8 + 24) : 1;
                }
                else if (chunkId == 0x61746164) // "data"
                {
                    dataOffset = pos + 8;
                    dataLength = chunkLen;
                }
                pos += 8 + chunkLen + (chunkLen & 1);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0)
                throw new InvalidDataException($"wav missing fmt/data (fmt={format} ch={channels} rate={sampleRate} bits={bits})");
            if (format != 1 && format != 3)
                throw new InvalidDataException($"unsupported wav format tag {format}");
            if (format == 1 && bits != 16 && bits != 24 && bits != 32)
                throw new InvalidDataException($"unsupported PCM bit depth {bits}");
            if (format == 3 && bits != 32)
                throw new InvalidDataException($"unsupported float bit depth {bits}");

            var bytesPerSample = bits / 8;
            var frameSize = bytesPerSample * channels;
            var frames = dataLength / frameSize;
            var mono = new float[frames];

            for (var f = 0; f < frames; f++)
            {
                float acc = 0;
                var frameStart = dataOffset + f * frameSize;
                for (var c = 0; c < channels; c++)
                {
                    var s = frameStart + c * bytesPerSample;
                    float v;
                    if (format == 3)
                        v = BitConverter.ToSingle(wav, s);
                    else if (bits == 16)
                        v = BitConverter.ToInt16(wav, s) / 32768f;
                    else if (bits == 24)
                        v = ((wav[s] << 8 | wav[s + 1] << 16 | wav[s + 2] << 24) >> 8) / 8388608f;
                    else
                        v = BitConverter.ToInt32(wav, s) / 2147483648f;
                    acc += v;
                }
                mono[f] = acc / channels;
            }

            return new Pcm { Samples = mono, SampleRate = sampleRate };
        }

        /// <summary>
        /// Bring the clip up (or down) to a target RMS loudness, soft-limiting any peaks that
        /// would exceed <paramref name="peakDbfs"/>. Not EBU loudnorm — but enough that a trader
        /// line reads at conversational volume from a boombox (the raw PoC audio was too quiet,
        /// and plain peak-capped gain can't fix spiky speech: the full RMS gain must land, with
        /// the few over-ceiling peaks squeezed through a tanh knee instead of blocking the gain).
        /// </summary>
        public static void Normalize(Pcm pcm, float targetRmsDbfs = -16f, float peakDbfs = -1f)
        {
            const float maxBoost = 15.85f; // +24 dB — don't amplify a near-silent clip's noise floor
            var samples = pcm.Samples;
            if (samples == null || samples.Length == 0) return;

            double sumSquares = 0;
            for (var i = 0; i < samples.Length; i++)
                sumSquares += (double)samples[i] * samples[i];
            var rms = (float)Math.Sqrt(sumSquares / samples.Length);
            if (rms <= 1e-6f) return; // silence — nothing to normalize

            var gain = Math.Min((float)Math.Pow(10, targetRmsDbfs / 20.0) / rms, maxBoost);
            var ceiling = (float)Math.Pow(10, peakDbfs / 20.0);
            var knee = ceiling * 0.7f; // transparent below the knee, tanh-compressed above it

            for (var i = 0; i < samples.Length; i++)
            {
                var v = samples[i] * gain;
                var a = Math.Abs(v);
                if (a > knee)
                    v = Math.Sign(v) * (knee + (ceiling - knee) * (float)Math.Tanh((a - knee) / (ceiling - knee)));
                samples[i] = v;
            }
        }

        /// <summary>Encode mono float PCM to an Ogg Vorbis stream (VBR; quality 0.4 ≈ ffmpeg -q:a 4).</summary>
        public static byte[] EncodeOggVorbis(Pcm pcm, float quality = 0.4f, int serial = 0)
        {
            const int writeChunk = 1024;
            var info = VorbisInfo.InitVariableBitRate(1, pcm.SampleRate, quality);
            // Deterministic serial: same text+voice re-render produces byte-identical files,
            // which keeps the plugin's CRC cache stable across rebuilds.
            var oggStream = new OggStream(serial);

            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            var output = new MemoryStream();
            FlushPages(oggStream, output, force: true);

            var processing = ProcessingState.Create(info);
            var channelBuffer = new[] { pcm.Samples };
            for (var read = 0; read < pcm.Samples.Length; read += writeChunk)
            {
                processing.WriteData(channelBuffer, Math.Min(writeChunk, pcm.Samples.Length - read), read);
                DrainPackets(oggStream, processing, output);
            }
            processing.WriteEndOfStream();
            DrainPackets(oggStream, processing, output);
            FlushPages(oggStream, output, force: true);
            return output.ToArray();
        }

        private static void DrainPackets(OggStream oggStream, ProcessingState processing, Stream output)
        {
            OggPacket packet;
            while (!oggStream.Finished && processing.PacketOut(out packet))
            {
                oggStream.PacketIn(packet);
                FlushPages(oggStream, output, force: false);
            }
        }

        private static void FlushPages(OggStream oggStream, Stream output, bool force)
        {
            OggPage page;
            while (oggStream.PageOut(out page, force))
            {
                output.Write(page.Header, 0, page.Header.Length);
                output.Write(page.Body, 0, page.Body.Length);
            }
        }
    }
}
